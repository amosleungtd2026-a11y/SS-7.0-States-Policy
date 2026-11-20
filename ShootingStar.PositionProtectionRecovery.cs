using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Position protection recovery retries and state management.
    public partial class ShootingStar
    {
        #region Position Protection Recovery
        private bool TryRestoreMissingProtectionOnce(
            Position position,
            PositionState state,
            PositionProtectionRecoveryState recovery)
        {
            double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double? stopLoss = position.StopLoss;
            double? takeProfit = position.TakeProfit;

            if (!stopLoss.HasValue)
            {
                stopLoss = NormalizePrice(position.TradeType == TradeType.Buy
                    ? currentPrice - state.StopLossDistance
                    : currentPrice + state.StopLossDistance);
            }
            if (!takeProfit.HasValue)
            {
                takeProfit = NormalizePrice(position.TradeType == TradeType.Buy
                    ? currentPrice + state.TakeProfitDistance
                    : currentPrice - state.TakeProfitDistance);
            }

            recovery.Attempts++;
            if (!stopLoss.HasValue || !takeProfit.HasValue ||
                !IsPositionProtectionValid(position.TradeType, currentPrice,
                    stopLoss.Value, true, position.Id, "reconstructed") ||
                !IsPositionProtectionValid(position.TradeType, currentPrice,
                    takeProfit.Value, false, position.Id, "reconstructed"))
            {
                recovery.LastFailure = "reconstructed protection is invalid at the current market price";
                return false;
            }

            TradeResult result = ModifyPosition(position, stopLoss, takeProfit, ProtectionType.Absolute);
            Position confirmed = result == null ? null : result.Position;
            double? confirmedStopLoss = confirmed == null ? position.StopLoss : confirmed.StopLoss;
            double? confirmedTakeProfit = confirmed == null ? position.TakeProfit : confirmed.TakeProfit;
            if (result != null && result.IsSuccessful &&
                confirmedStopLoss.HasValue && confirmedTakeProfit.HasValue)
            {
                state.LastRequestedStopLoss = confirmedStopLoss;
                state.LastRequestedTakeProfit = confirmedTakeProfit;
                MarkCircuitBreakerStopLossModified(confirmed, state);
                state.LastProtectionModificationTime = Server.TimeInUtc;
                recovery.LastFailure = null;
                LogNormal("PROTECTION", "RECOVERED",
                    "positionId={0} attempt={1}/{2} stopLoss={3} takeProfit={4}",
                    position.Id, recovery.Attempts, PositionProtectionRecoveryMaxAttempts,
                    FormatPrice(confirmedStopLoss.Value), FormatPrice(confirmedTakeProfit.Value));
                return true;
            }

            recovery.LastFailure = result == null ? "null result" : result.Error.ToString();
            LogActiveWarning("position-protection-recovery-" + position.Id,
                "PROTECTION", "RECOVERY_ATTEMPT_FAILED",
                "positionId={0} attempt={1}/{2} brokerError={3}",
                position.Id, recovery.Attempts,
                PositionProtectionRecoveryMaxAttempts,
                recovery.LastFailure);
            return false;
        }

        private void StartOrContinuePositionProtectionRecovery(
            Position position,
            PositionState state,
            bool attemptImmediately)
        {
            if (position == null || state == null)
                return;

            if (position.StopLoss.HasValue && position.TakeProfit.HasValue)
            {
                CompletePositionProtectionRecovery(position.Id);
                return;
            }

            if (!_positionProtectionRecoveryStates.TryGetValue(
                    position.Id,
                    out PositionProtectionRecoveryState recovery))
            {
                recovery = new PositionProtectionRecoveryState
                {
                    PositionId = position.Id,
                    StartedUtc = Server.TimeInUtc,
                    NextAttemptUtc = Server.TimeInUtc,
                    Attempts = 0
                };
                _positionProtectionRecoveryStates[position.Id] = recovery;
                LogError("PROTECTION", "RECOVERY_STARTED",
                    "positionId={0} missingProtection={1}; bounded timer-driven protection recovery started",
                    position.Id,
                    !position.StopLoss.HasValue && !position.TakeProfit.HasValue
                        ? "SL and TP"
                        : (!position.StopLoss.HasValue ? "SL" : "TP"));
            }

            if (recovery.ClosureOnly)
            {
                if (attemptImmediately && Server.TimeInUtc >= recovery.NextCloseAttemptUtc)
                    ProcessUnprotectedPositionCloseAttempt(position, recovery);
                return;
            }

            if (attemptImmediately && Server.TimeInUtc >= recovery.NextAttemptUtc)
                ProcessPositionProtectionRecoveryAttempt(position, state, recovery);
        }

        private void ProcessPositionProtectionRecoveryAttempt(
            Position position,
            PositionState state,
            PositionProtectionRecoveryState recovery)
        {
            if (TryRestoreMissingProtectionOnce(position, state, recovery))
            {
                CompletePositionProtectionRecovery(position.Id);
                return;
            }

            double elapsedSeconds = Math.Max(
                0.0,
                (Server.TimeInUtc - recovery.StartedUtc).TotalSeconds);
            if (recovery.Attempts >= PositionProtectionRecoveryMaxAttempts ||
                elapsedSeconds >= PositionProtectionRecoveryDeadlineSeconds)
            {
                ClosePositionWithoutProtection(position,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "protection recovery failed after {0} attempts and {1:F1} seconds; last failure={2}",
                        recovery.Attempts,
                        elapsedSeconds,
                        recovery.LastFailure ?? "unspecified"));
                return;
            }

            recovery.NextAttemptUtc = Server.TimeInUtc.AddSeconds(
                PositionProtectionRecoveryRetrySeconds);
        }

        private void CompletePositionProtectionRecovery(long positionId)
        {
            if (!_positionProtectionRecoveryStates.Remove(positionId))
                return;

            ClearWarning("position-protection-recovery-" + positionId,
                "PROTECTION", "RECOVERY_COMPLETE",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "positionId={0} outcome=ProtectionRestored; timer-driven recovery state cleared",
                    positionId),
                true);
        }

        private void ProcessPositionProtectionRecoveries()
        {
            if (_positionProtectionRecoveryStates.Count == 0)
                return;

            _positionProtectionRecoveryIdBuffer.Clear();
            foreach (long positionId in _positionProtectionRecoveryStates.Keys)
                _positionProtectionRecoveryIdBuffer.Add(positionId);

            CaptureBotPositions(_protectionRecoveryPositionBuffer);
            _stalePositionProtectionRecoveryIds.Clear();
            foreach (long positionId in _positionProtectionRecoveryIdBuffer)
            {
                Position position = null;
                foreach (Position candidate in _protectionRecoveryPositionBuffer)
                {
                    if (candidate.Id == positionId)
                    {
                        position = candidate;
                        break;
                    }
                }

                if (position == null)
                {
                    _stalePositionProtectionRecoveryIds.Add(positionId);
                    continue;
                }

                PositionProtectionRecoveryState recovery;
                if (!_positionProtectionRecoveryStates.TryGetValue(positionId, out recovery))
                    continue;

                if (recovery.ClosureOnly)
                {
                    if (Server.TimeInUtc >= recovery.NextCloseAttemptUtc)
                        ProcessUnprotectedPositionCloseAttempt(position, recovery);
                    continue;
                }

                if (position.StopLoss.HasValue && position.TakeProfit.HasValue)
                {
                    _stalePositionProtectionRecoveryIds.Add(positionId);
                    ClearWarning("position-protection-recovery-" + positionId,
                        "PROTECTION", "RECOVERY_COMPLETE",
                        "position protection is complete; timer-driven recovery state cleared");
                    continue;
                }

                if (!_positionStates.TryGetValue(positionId, out PositionState state))
                {
                    InitializePositionState(position);
                    if (!_positionStates.TryGetValue(positionId, out state))
                        continue;
                }

                if (Server.TimeInUtc >= recovery.NextAttemptUtc)
                    ProcessPositionProtectionRecoveryAttempt(position, state, recovery);
            }

            foreach (long positionId in _stalePositionProtectionRecoveryIds)
                _positionProtectionRecoveryStates.Remove(positionId);
        }

        #endregion

    }
}
