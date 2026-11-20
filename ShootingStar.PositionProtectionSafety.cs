using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Fail-safe closes and manual protection reconciliation.
    public partial class ShootingStar
    {
        #region Unprotected Position Safety and Manual Changes
        private double GetExpectedProtectionPrice(
            Position position, double distance, bool isStopLoss)
        {
            bool isBuy = position.TradeType == TradeType.Buy;
            double price = isStopLoss
                ? (isBuy ? position.EntryPrice - distance : position.EntryPrice + distance)
                : (isBuy ? position.EntryPrice + distance : position.EntryPrice - distance);
            return NormalizePrice(price);
        }

        private void ClosePositionWithoutProtection(Position position, string reason)
        {
            if (position == null)
                return;

            if (!_positionProtectionRecoveryStates.TryGetValue(
                    position.Id,
                    out PositionProtectionRecoveryState recovery))
            {
                recovery = new PositionProtectionRecoveryState
                {
                    PositionId = position.Id,
                    StartedUtc = Server.TimeInUtc
                };
                _positionProtectionRecoveryStates[position.Id] = recovery;
            }

            if (!recovery.ClosureOnly)
            {
                recovery.ClosureOnly = true;
                recovery.ClosureStartedUtc = Server.TimeInUtc;
                recovery.NextCloseAttemptUtc = Server.TimeInUtc;
                recovery.CloseAttempts = 0;
                recovery.LastCloseFailure = null;
                recovery.TerminalCloseFailureLogged = false;
                _runtimeFaulted = true;

                LogError("PROTECTION", "UNPROTECTED_POSITION_CLOSE_STARTED",
                    "positionId={0} reason={1}; protection recovery ended, closure-only emergency handling started, and new entries are disabled",
                    position.Id, reason);
            }

            if (Server.TimeInUtc >= recovery.NextCloseAttemptUtc)
                ProcessUnprotectedPositionCloseAttempt(position, recovery);
        }

        private void ProcessUnprotectedPositionCloseAttempt(
            Position position,
            PositionProtectionRecoveryState recovery)
        {
            if (position == null || recovery == null || !recovery.ClosureOnly)
                return;

            if (!HasExecutableMarketQuote(Server.TimeInUtc))
            {
                recovery.NextCloseAttemptUtc = Server.TimeInUtc.AddSeconds(1);
                LogActiveWarning("unprotected-position-awaiting-market-" + position.Id,
                    "PROTECTION", "CLOSE_WAITING_FOR_EXECUTABLE_MARKET",
                    "positionId={0}; emergency closure is deferred until a valid quote is observed in the active market session",
                    position.Id);
                return;
            }

            ClearWarning("unprotected-position-awaiting-market-" + position.Id,
                "PROTECTION", "EXECUTABLE_MARKET_AVAILABLE",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "positionId={0}; emergency closure can resume",
                    position.Id));

            if (recovery.CloseRequestAccepted)
            {
                if (Positions.FindById(position.Id) == null)
                {
                    recovery.CloseRequestAccepted = false;
                    recovery.CloseRequestAcceptedUtc = DateTime.MinValue;
                    return;
                }

                if (ShouldWaitForAcceptedCloseConfirmation(
                        true,
                        true,
                        recovery.CloseRequestAcceptedUtc,
                        Server.TimeInUtc))
                {
                    recovery.NextCloseAttemptUtc =
                        recovery.CloseRequestAcceptedUtc.AddSeconds(
                            AcceptedCloseConfirmationSeconds);
                    return;
                }

                recovery.CloseRequestAccepted = false;
            }

            recovery.CloseAttempts++;
            TradeResult closeResult = ClosePosition(position);
            if (closeResult != null && closeResult.IsSuccessful)
            {
                recovery.LastCloseFailure = null;
                recovery.CloseRequestAccepted = true;
                recovery.CloseRequestAcceptedUtc = Server.TimeInUtc;
                recovery.NextCloseAttemptUtc = Server.TimeInUtc.AddSeconds(
                    AcceptedCloseConfirmationSeconds);
                LogNormal("PROTECTION", "UNPROTECTED_POSITION_CLOSE_ACCEPTED",
                    "positionId={0} attempt={1} outcome=BrokerAccepted; awaiting position-closed confirmation",
                    position.Id, recovery.CloseAttempts);
                return;
            }

            recovery.CloseRequestAccepted = false;
            recovery.CloseRequestAcceptedUtc = DateTime.MinValue;
            recovery.LastCloseFailure = closeResult == null
                ? "null result"
                : closeResult.Error.ToString();

            double elapsedSeconds = Math.Max(
                0.0,
                (Server.TimeInUtc - recovery.ClosureStartedUtc).TotalSeconds);
            bool terminalThresholdReached =
                recovery.CloseAttempts >= UnprotectedPositionCloseMaxAttempts ||
                elapsedSeconds >= UnprotectedPositionCloseDeadlineSeconds;

            if (terminalThresholdReached)
            {
                _runtimeFaulted = true;
                if (!recovery.TerminalCloseFailureLogged)
                {
                    recovery.TerminalCloseFailureLogged = true;
                    LogError("PROTECTION", "UNPROTECTED_POSITION_CLOSE_FAILED",
                        "positionId={0} attempts={1} elapsedSeconds={2:F1} brokerError={3} outcome=RuntimeFaulted; new entries remain disabled and emergency close retries continue",
                        position.Id, recovery.CloseAttempts, elapsedSeconds,
                        recovery.LastCloseFailure);
                }
                else
                {
                    LogActiveWarning(
                        "unprotected-position-close-" + position.Id,
                        "PROTECTION", "UNPROTECTED_POSITION_CLOSE_RETRY_FAILED",
                        "positionId={0} attempt={1} elapsedSeconds={2:F1} brokerError={3}; runtime remains faulted",
                        position.Id, recovery.CloseAttempts, elapsedSeconds,
                        recovery.LastCloseFailure);
                }

                recovery.NextCloseAttemptUtc = Server.TimeInUtc.AddSeconds(
                    UnprotectedPositionCloseTerminalRetrySeconds);
                return;
            }

            LogActiveWarning(
                "unprotected-position-close-" + position.Id,
                "PROTECTION", "UNPROTECTED_POSITION_CLOSE_ATTEMPT_FAILED",
                "positionId={0} attempt={1}/{2} elapsedSeconds={3:F1}/{4} brokerError={5}",
                position.Id, recovery.CloseAttempts,
                UnprotectedPositionCloseMaxAttempts, elapsedSeconds,
                UnprotectedPositionCloseDeadlineSeconds,
                recovery.LastCloseFailure);
            recovery.NextCloseAttemptUtc = Server.TimeInUtc.AddSeconds(
                UnprotectedPositionCloseRetrySeconds);
        }

        private static bool ShouldWaitForAcceptedCloseConfirmation(
            bool requestAccepted,
            bool positionStillOpen,
            DateTime acceptedUtc,
            DateTime nowUtc)
        {
            return requestAccepted &&
                   positionStillOpen &&
                   (nowUtc - acceptedUtc).TotalSeconds <
                       AcceptedCloseConfirmationSeconds;
        }

        private double GetProtectionComparisonTolerance()
        {
            // Broker-side price normalisation may move the effective price by one tick.
            return Math.Max(Symbol.TickSize * 1.01, 1e-10);
        }

        private bool ProtectionPriceChanged(double? livePrice, double? referencePrice)
        {
            if (livePrice.HasValue != referencePrice.HasValue)
                return true;

            if (!livePrice.HasValue)
                return false;

            return Math.Abs(livePrice.Value - referencePrice.Value) >
                   GetProtectionComparisonTolerance();
        }

        private double GetLiveProtectionDistance(
            Position position,
            double protectionPrice,
            bool isStopLoss,
            double currentPrice)
        {
            bool isBuy = position.TradeType == TradeType.Buy;

            if (isStopLoss)
                return isBuy
                    ? currentPrice - protectionPrice
                    : protectionPrice - currentPrice;

            return isBuy
                ? protectionPrice - currentPrice
                : currentPrice - protectionPrice;
        }

        private void AdoptManualProtectionChanges(
            Position position,
            PositionState state,
            double currentPrice)
        {
            double? expectedTp = state.LastRequestedTakeProfit;
            double? expectedSl = state.LastRequestedStopLoss;

            if (ProtectionPriceChanged(position.TakeProfit, expectedTp))
            {
                if (position.TakeProfit.HasValue)
                {
                    double liveDistance = GetLiveProtectionDistance(
                        position, position.TakeProfit.Value, false, currentPrice);

                    if (liveDistance > 0.0)
                    {
                        state.TakeProfitDistance = liveDistance;
                        LogNormal("PROTECTION", "MANUAL_TARGET_ADOPTED", "Position {0}: external/manual TP change detected. " +
                              "Live TP={1}, expected TP={2}, adopted live market distance={3} pips.",
                            position.Id,
                            position.TakeProfit.Value,
                            expectedTp.HasValue ? expectedTp.Value.ToString() : "none",
                            state.TakeProfitDistance / Symbol.PipSize);
                    }
                    else
                    {
                        LogNormal("PROTECTION", "MANUAL_TARGET_REJECTED", "Position {0}: external/manual TP change detected, but its " +
                              "distance from the current market is invalid and was not adopted.",
                            position.Id);
                    }
                }
            }

            // Whether changed or merely normalised by the broker, the live value is now
            // the comparison baseline. A removed protection remains null.
            state.LastRequestedTakeProfit = position.TakeProfit;

            if (ProtectionPriceChanged(position.StopLoss, expectedSl))
            {
                if (position.StopLoss.HasValue)
                {
                    double liveDistance = GetLiveProtectionDistance(
                        position, position.StopLoss.Value, true, currentPrice);

                    if (liveDistance > 0.0)
                    {
                        state.StopLossDistance = liveDistance;
                        LogNormal("PROTECTION", "MANUAL_STOP_ADOPTED", "Position {0}: external/manual SL change detected. " +
                              "Live SL={1}, expected SL={2}, adopted live market distance={3} pips.",
                            position.Id,
                            position.StopLoss.Value,
                            expectedSl.HasValue ? expectedSl.Value.ToString() : "none",
                            state.StopLossDistance / Symbol.PipSize);
                    }
                    else
                    {
                        LogNormal("PROTECTION", "MANUAL_STOP_REJECTED", "Position {0}: external/manual SL change detected, but its " +
                              "distance from the current market is invalid and was not adopted.",
                            position.Id);
                    }
                }
            }

            state.LastRequestedStopLoss = position.StopLoss;
        }

        #endregion

    }
}
