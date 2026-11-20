using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Pending-order state, persistence, retries, and cancellation.
    public partial class ShootingStar
    {
        #region Pending Order Tracking State

        private readonly Dictionary<int, PendingOrderTrailingState> _pendingOrderTrailingStates = new Dictionary<int, PendingOrderTrailingState>();
        private readonly Dictionary<int, PendingOrderCancellationRetryState> _pendingOrderCancellationRetries = new Dictionary<int, PendingOrderCancellationRetryState>();
        private static readonly TimeSpan PendingOrderCancellationRetryDelay = TimeSpan.FromSeconds(2.0);
        private static readonly TimeSpan CircuitBreakerPause = TimeSpan.FromHours(24.0);
        private const LocalStorageScope CircuitBreakerStorageScope = LocalStorageScope.Instance;
        private const int ScaleUpMaximumLevel = 10;
        private const double ScaleUpIncreasePerLevel = 0.05;
        private readonly Dictionary<string, int> _scaleUpLevels =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> _scaleUpLastAppliedLogicalTradeIds =
            new Dictionary<string, Guid>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _circuitBreakerUntilUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<long> _pendingCircuitBreakerFillPositionIds =
            new HashSet<long>();

        private static string BuildLocalStorageKey(string prefix, params string[] components)
        {
            var identity = new System.Text.StringBuilder();
            foreach (string component in components)
            {
                string value = component ?? string.Empty;
                identity.Append(value.Length.ToString(CultureInfo.InvariantCulture));
                identity.Append(':');
                identity.Append(value);
            }

            byte[] identityBytes = System.Text.Encoding.UTF8.GetBytes(identity.ToString());
            byte[] hash;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                hash = sha256.ComputeHash(identityBytes);

            var suffix = new System.Text.StringBuilder(32);
            for (int index = 0; index < 16; index++)
                suffix.Append(hash[index].ToString("X2", CultureInfo.InvariantCulture));
            return prefix + suffix;
        }

        private string GetCircuitBreakerStorageKey(string label)
        {
            return BuildLocalStorageKey(
                "USCB1",
                Account.Number.ToString(CultureInfo.InvariantCulture),
                Symbol.Name,
                BotLabel,
                label);
        }

        private string GetCircuitBreakerPositionStorageKey(long positionId)
        {
            return BuildLocalStorageKey(
                "USCBP1",
                Account.Number.ToString(CultureInfo.InvariantCulture),
                Symbol.Name,
                BotLabel,
                positionId.ToString(CultureInfo.InvariantCulture));
        }

        private string GetScaleUpStorageKey(string label)
        {
            return BuildLocalStorageKey(
                "USSU1",
                Account.Number.ToString(CultureInfo.InvariantCulture),
                Symbol.Name,
                BotLabel,
                label);
        }

        private string GetScaleUpPositionStorageKey(long positionId)
        {
            return BuildLocalStorageKey(
                "USSUP1",
                Account.Number.ToString(CultureInfo.InvariantCulture),
                Symbol.Name,
                BotLabel,
                positionId.ToString(CultureInfo.InvariantCulture));
        }

        private string GetPositionLadderStorageKey(long positionId)
        {
            return BuildLocalStorageKey(
                "USLAD1",
                Account.Number.ToString(CultureInfo.InvariantCulture),
                Symbol.Name,
                BotLabel,
                positionId.ToString(CultureInfo.InvariantCulture));
        }

        private string[] GetCircuitBreakerLabels()
        {
            return new[] { _mrLongLabel, _mrShortLabel, _tfLongLabel, _tfShortLabel };
        }

        private void LoadScaleUpState()
        {
            foreach (string label in GetCircuitBreakerLabels())
            {
                string value = LocalStorage.GetString(
                    GetScaleUpStorageKey(label),
                    CircuitBreakerStorageScope);
                if (string.IsNullOrEmpty(value))
                    continue;

                string[] fields = value.Split('|');
                int level;
                Guid lastAppliedLogicalTradeId = Guid.Empty;
                if (fields.Length != 3 ||
                    fields[0] != "1" ||
                    !int.TryParse(fields[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out level) ||
                    level < 0 ||
                    level > ScaleUpMaximumLevel ||
                    (fields[2].Length > 0 &&
                     !Guid.TryParse(fields[2], out lastAppliedLogicalTradeId)))
                {
                    LogError("SCALE_UP", "INVALID_STORED_STATE",
                        "label={0} outcome=RewardLevelReset", label);
                    PersistScaleUpOutcome(label, 0, Guid.Empty);
                    continue;
                }

                _scaleUpLevels[label] = level;
                if (fields[2].Length > 0)
                {
                    Guid.TryParse(fields[2], out lastAppliedLogicalTradeId);
                    _scaleUpLastAppliedLogicalTradeIds[label] = lastAppliedLogicalTradeId;
                }
                LogNormal("SCALE_UP", "REWARD_LEVEL_RESTORED",
                    "label={0} level={1}", label, level);
            }
        }

        private int GetScaleUpLevel(string label)
        {
            return _scaleUpLevels.TryGetValue(label, out int level)
                ? Math.Max(0, Math.Min(ScaleUpMaximumLevel, level))
                : 0;
        }

        private double GetScaleUpMultiplier(string label)
        {
            return GetScaleUpMultiplierForLevel(GetScaleUpLevel(label));
        }

        private static double GetScaleUpMultiplierForLevel(int level)
        {
            int boundedLevel = Math.Max(0, Math.Min(ScaleUpMaximumLevel, level));
            return 1.0 + ScaleUpIncreasePerLevel * boundedLevel;
        }

        private void PersistScaleUpOutcome(string label, int level, Guid logicalTradeId)
        {
            int boundedLevel = Math.Max(0, Math.Min(ScaleUpMaximumLevel, level));
            _scaleUpLevels[label] = boundedLevel;
            if (logicalTradeId != Guid.Empty)
                _scaleUpLastAppliedLogicalTradeIds[label] = logicalTradeId;
            else
                _scaleUpLastAppliedLogicalTradeIds.Remove(label);

            string lastApplied = logicalTradeId == Guid.Empty
                ? string.Empty
                : logicalTradeId.ToString();
            LocalStorage.SetString(
                GetScaleUpStorageKey(label),
                string.Join("|", "1", boundedLevel.ToString(CultureInfo.InvariantCulture), lastApplied),
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void PersistScaleUpPositionState(long positionId, PositionState state)
        {
            string value = string.Join(
                "|",
                "2",
                state.ScaleUpEligible ? "1" : "0",
                state.OriginalTakeProfit.ToString("R", CultureInfo.InvariantCulture),
                state.OriginalStopLoss.ToString("R", CultureInfo.InvariantCulture));
            LocalStorage.SetString(
                GetScaleUpPositionStorageKey(positionId),
                value,
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void RestoreScaleUpPositionState(long positionId, PositionState state)
        {
            string value = LocalStorage.GetString(
                GetScaleUpPositionStorageKey(positionId),
                CircuitBreakerStorageScope);
            if (string.IsNullOrEmpty(value))
                return;

            string[] fields = value.Split('|');
            bool versionOne = fields.Length == 5 && fields[0] == "1";
            bool versionTwo = fields.Length == 4 && fields[0] == "2";
            bool eligible = (versionOne || versionTwo) && fields[1] == "1";
            double originalTakeProfit;
            double originalStopLoss = state.OriginalStopLoss;
            if ((!versionOne && !versionTwo) ||
                (fields[1] != "0" && fields[1] != "1") ||
                (versionOne &&
                 ((fields[2] != "0" && fields[2] != "1") ||
                  (fields[3] != "0" && fields[3] != "1"))) ||
                !double.TryParse(fields[versionOne ? 4 : 2], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out originalTakeProfit) ||
                (versionTwo &&
                 !double.TryParse(fields[3], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out originalStopLoss)) ||
                (eligible &&
                 (!IsFinitePositive(originalTakeProfit) ||
                  !IsFinitePositive(originalStopLoss))))
            {
                LogError("SCALE_UP", "INVALID_STORED_POSITION_STATE",
                    "positionId={0} outcome=PositionMarkedIneligible", positionId);
                state.ScaleUpEligible = false;
                return;
            }

            state.ScaleUpEligible = eligible &&
                IsFinitePositive(originalTakeProfit) &&
                IsFinitePositive(originalStopLoss);
            if (IsFinitePositive(originalTakeProfit))
                state.OriginalTakeProfit = originalTakeProfit;
            if (IsFinitePositive(originalStopLoss))
                state.OriginalStopLoss = originalStopLoss;
        }

        private void MarkScaleUpOriginalFill(Position position, PositionState state)
        {
            bool eligible = IsCircuitBreakerEligibleOriginalFill(
                true,
                _rolloverRestoredPositionRecoveryIds.ContainsKey(position.Id),
                position.TakeProfit.HasValue && IsFinitePositive(position.TakeProfit.Value) &&
                position.StopLoss.HasValue && IsFinitePositive(position.StopLoss.Value)) &&
                CanUseForAdaptiveOutcomeControls(state.Evidence);
            state.ScaleUpEligible = eligible;
            if (eligible)
            {
                state.OriginalTakeProfit = position.TakeProfit.Value;
                state.OriginalStopLoss = position.StopLoss.Value;
            }
            PersistScaleUpPositionState(position.Id, state);
        }

        private void RefreshAdaptiveEligibilityForFilledPosition(int positionId)
        {
            Position position = Positions.FindById(positionId);
            if (position == null ||
                !_positionStates.TryGetValue(positionId, out PositionState state))
                return;

            MarkCircuitBreakerOriginalFill(position, state);
            MarkScaleUpOriginalFill(position, state);
        }

        private void ApplyScaleUpOutcome(
            string label,
            Guid logicalTradeId,
            double logicalNetProfit,
            bool eligible,
            TradeType tradeType,
            double entryPrice,
            double originalTakeProfit,
            double originalStopLoss,
            double? closingPrice,
            bool takeProfitClose,
            TradeEvidence evidence)
        {
            if (!eligible ||
                !CanUseForAdaptiveOutcomeControls(evidence) ||
                logicalTradeId == Guid.Empty ||
                (_scaleUpLastAppliedLogicalTradeIds.TryGetValue(label, out Guid lastApplied) &&
                 lastApplied == logicalTradeId))
            {
                return;
            }

            int currentLevel = GetScaleUpLevel(label);
            int nextLevel = GetNextScaleUpLevel(
                currentLevel,
                logicalNetProfit,
                eligible,
                tradeType,
                entryPrice,
                originalTakeProfit,
                originalStopLoss,
                closingPrice,
                takeProfitClose,
                TradeStatisticsBreakevenTolerance,
                Math.Max(Symbol.TickSize * 0.5, 1e-10));

            PersistScaleUpOutcome(label, nextLevel, logicalTradeId);
            LogNormal("SCALE_UP", "REWARD_LEVEL_UPDATED",
                "label={0} priorLevel={1} level={2} logicalTradeId={3} netProfit={4:F2}",
                label, currentLevel, nextLevel, logicalTradeId, logicalNetProfit);
        }

        private static int GetNextScaleUpLevel(
            int currentLevel,
            double logicalNetProfit,
            bool eligible,
            TradeType tradeType,
            double entryPrice,
            double originalTakeProfit,
            double originalStopLoss,
            double? closingPrice,
            bool takeProfitClose,
            double breakevenTolerance,
            double priceTolerance)
        {
            int boundedLevel = Math.Max(0, Math.Min(ScaleUpMaximumLevel, currentLevel));
            if (!eligible || !IsFinite(entryPrice))
                return boundedLevel;

            if (logicalNetProfit < -breakevenTolerance &&
                (!closingPrice.HasValue || !IsFinite(closingPrice.Value)))
            {
                return Math.Max(0, boundedLevel - 1);
            }

            if (logicalNetProfit < -breakevenTolerance &&
                closingPrice.HasValue &&
                IsFinite(closingPrice.Value) &&
                IsFinitePositive(originalStopLoss))
            {
                double adverseClosingDistance = tradeType == TradeType.Buy
                    ? entryPrice - closingPrice.Value
                    : closingPrice.Value - entryPrice;
                double originalStopLossDistance =
                    Math.Abs(originalStopLoss - entryPrice);
                if (!IsFinitePositive(originalStopLossDistance))
                    return boundedLevel;
                if (adverseClosingDistance >= -priceTolerance &&
                    adverseClosingDistance <= originalStopLossDistance / 3.0 + priceTolerance)
                {
                    return Math.Max(0, boundedLevel - 1);
                }

                if (adverseClosingDistance >
                    originalStopLossDistance / 3.0 + priceTolerance)
                {
                    return Math.Max(0, boundedLevel - 2);
                }

                return boundedLevel;
            }

            if (logicalNetProfit <= breakevenTolerance ||
                !takeProfitClose ||
                !closingPrice.HasValue ||
                !IsFinite(closingPrice.Value))
            {
                return boundedLevel;
            }

            double originalTakeProfitDistance = Math.Abs(originalTakeProfit - entryPrice);
            if (!IsFinitePositive(originalTakeProfitDistance))
                return boundedLevel;

            double favorableClosingDistance = tradeType == TradeType.Buy
                ? closingPrice.Value - entryPrice
                : entryPrice - closingPrice.Value;
            if (favorableClosingDistance < -priceTolerance)
                return boundedLevel;
            if (favorableClosingDistance + priceTolerance >=
                originalTakeProfitDistance)
            {
                return Math.Min(ScaleUpMaximumLevel, boundedLevel + 2);
            }

            if (favorableClosingDistance + priceTolerance <
                originalTakeProfitDistance * 0.8)
                return boundedLevel;

            return Math.Min(ScaleUpMaximumLevel, boundedLevel + 1);
        }

        private void ForgetScaleUpPositionState(long positionId)
        {
            LocalStorage.Remove(
                GetScaleUpPositionStorageKey(positionId),
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void LoadCircuitBreakerState()
        {
            DateTime now = Server.TimeInUtc;
            foreach (string label in GetCircuitBreakerLabels())
            {
                string key = GetCircuitBreakerStorageKey(label);
                string storedTicks = LocalStorage.GetString(key, CircuitBreakerStorageScope);
                if (string.IsNullOrEmpty(storedTicks))
                    continue;

                long ticks;
                if (!long.TryParse(storedTicks, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out ticks) ||
                    ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                {
                    LogError("CIRCUIT_BREAKER", "INVALID_STORED_EXPIRY",
                        "label={0} outcome=StoredPauseRemoved", label);
                    LocalStorage.Remove(key, CircuitBreakerStorageScope);
                    LocalStorage.Flush(CircuitBreakerStorageScope);
                    continue;
                }

                DateTime expiresUtc = new DateTime(ticks, DateTimeKind.Utc);
                if (expiresUtc <= now)
                {
                    LocalStorage.Remove(key, CircuitBreakerStorageScope);
                    continue;
                }

                _circuitBreakerUntilUtc[label] = expiresUtc;
                LogNormal("CIRCUIT_BREAKER", "PAUSE_RESTORED",
                    "label={0} expiresUtc={1:O}", label, expiresUtc);
            }
        }

        private void PersistCircuitBreakerPause(string label, DateTime expiresUtc)
        {
            LocalStorage.SetString(
                GetCircuitBreakerStorageKey(label),
                expiresUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void PersistCircuitBreakerPositionState(long positionId, PositionState state)
        {
            string value = string.Join(
                "|",
                "1",
                state.CircuitBreakerEligible ? "1" : "0",
                state.CircuitBreakerOriginalStopUnchanged ? "1" : "0",
                state.OriginalStopLoss.ToString("R", CultureInfo.InvariantCulture));
            LocalStorage.SetString(
                GetCircuitBreakerPositionStorageKey(positionId),
                value,
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void RestoreCircuitBreakerPositionState(long positionId, PositionState state)
        {
            string value = LocalStorage.GetString(
                GetCircuitBreakerPositionStorageKey(positionId),
                CircuitBreakerStorageScope);
            if (string.IsNullOrEmpty(value))
                return;

            string[] fields = value.Split('|');
            double originalStopLoss;
            bool eligible = fields.Length == 4 && fields[1] == "1";
            if (fields.Length != 4 ||
                fields[0] != "1" ||
                (fields[1] != "0" && fields[1] != "1") ||
                (fields[2] != "0" && fields[2] != "1") ||
                !double.TryParse(fields[3], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out originalStopLoss) ||
                (eligible && !IsFinitePositive(originalStopLoss)))
            {
                LogError("CIRCUIT_BREAKER", "INVALID_STORED_POSITION_STATE",
                    "positionId={0} outcome=PositionMarkedIneligible", positionId);
                state.CircuitBreakerEligible = false;
                state.CircuitBreakerOriginalStopUnchanged = false;
                return;
            }

            state.CircuitBreakerEligible = fields[1] == "1";
            state.CircuitBreakerOriginalStopUnchanged =
                state.CircuitBreakerEligible && fields[2] == "1";
            if (IsFinitePositive(originalStopLoss))
                state.OriginalStopLoss = originalStopLoss;
        }

        private void MarkCircuitBreakerOriginalFill(Position position, PositionState state)
        {
            state.CircuitBreakerEligible = IsCircuitBreakerEligibleOriginalFill(
                true,
                false,
                position.StopLoss.HasValue && IsFinitePositive(position.StopLoss.Value)) &&
                CanUseForAdaptiveOutcomeControls(state.Evidence);
            state.CircuitBreakerOriginalStopUnchanged = state.CircuitBreakerEligible;
            if (state.CircuitBreakerEligible)
                state.OriginalStopLoss = position.StopLoss.Value;
            PersistCircuitBreakerPositionState(position.Id, state);
        }

        private static bool IsCircuitBreakerEligibleOriginalFill(
            bool originatedFromPendingOrder,
            bool isRolloverRestoredPosition,
            bool hasOriginalStopLoss)
        {
            return originatedFromPendingOrder &&
                !isRolloverRestoredPosition &&
                hasOriginalStopLoss;
        }

        private void MarkCircuitBreakerStopLossModified(Position position, PositionState state)
        {
            double tolerance = Math.Max(Symbol.TickSize * 0.5, 1e-10);
            if (!state.CircuitBreakerEligible ||
                !state.CircuitBreakerOriginalStopUnchanged ||
                (position.StopLoss.HasValue &&
                 Math.Abs(position.StopLoss.Value - state.OriginalStopLoss) <= tolerance))
            {
                return;
            }

            state.CircuitBreakerOriginalStopUnchanged = false;
            PersistCircuitBreakerPositionState(position.Id, state);
        }

        private bool IsCircuitBreakerPaused(string label, DateTime nowUtc, out DateTime expiresUtc)
        {
            if (_circuitBreakerUntilUtc.TryGetValue(label, out expiresUtc))
            {
                if (IsCircuitBreakerPauseActive(expiresUtc, nowUtc))
                    return true;

                _circuitBreakerUntilUtc.Remove(label);
                LocalStorage.Remove(GetCircuitBreakerStorageKey(label), CircuitBreakerStorageScope);
                LocalStorage.Flush(CircuitBreakerStorageScope);
            }

            expiresUtc = DateTime.MinValue;
            return false;
        }

        private static bool IsCircuitBreakerPauseActive(DateTime expiresUtc, DateTime nowUtc)
        {
            return nowUtc < expiresUtc;
        }

        private static bool ShouldTripCircuitBreaker(
            bool eligible,
            bool originalStopUnchanged,
            TradeType tradeType,
            double originalStopLoss,
            double? closedStopLoss,
            double? actualClosingPrice,
            PositionCloseReason closeReason,
            double tolerance)
        {
            if (!eligible ||
                !originalStopUnchanged ||
                !IsFinite(originalStopLoss) ||
                closeReason != PositionCloseReason.StopLoss)
            {
                return false;
            }

            if (closedStopLoss.HasValue)
                return IsFinite(closedStopLoss.Value) &&
                    Math.Abs(closedStopLoss.Value - originalStopLoss) <= tolerance;

            if (!actualClosingPrice.HasValue || !IsFinite(actualClosingPrice.Value))
                return false;

            return tradeType == TradeType.Buy
                ? actualClosingPrice.Value <= originalStopLoss + tolerance
                : actualClosingPrice.Value >= originalStopLoss - tolerance;
        }

        private void TryTripCircuitBreaker(Position position, PositionState state, PositionCloseReason closeReason)
        {
            if (state == null ||
                !CanUseForAdaptiveOutcomeControls(state.Evidence) ||
                !ShouldTripCircuitBreaker(
                    state.CircuitBreakerEligible,
                    state.CircuitBreakerOriginalStopUnchanged,
                    position.TradeType,
                    state.OriginalStopLoss,
                    position.StopLoss,
                    GetFinalClosingPrice(position.Id),
                    closeReason,
                    Math.Max(Symbol.TickSize * 0.5, 1e-10)))
            {
                return;
            }

            DateTime expiresUtc = Server.TimeInUtc + CircuitBreakerPause;
            _circuitBreakerUntilUtc[position.Label] = expiresUtc;
            PersistCircuitBreakerPause(position.Label, expiresUtc);
            PersistScaleUpOutcome(position.Label, 0, state.LogicalTradeId);
            LogNormal("CIRCUIT_BREAKER", "STRATEGY_PAUSED",
                "label={0} positionId={1} expiresUtc={2:O} reason=OriginalStopLoss",
                position.Label, position.Id, expiresUtc);
        }

        private void ForgetCircuitBreakerPositionState(long positionId)
        {
            LocalStorage.Remove(
                GetCircuitBreakerPositionStorageKey(positionId),
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private enum PendingOrderCancellationOutcome
        {
            Successful,
            Failed,
            CoolingDown
        }

        private sealed class PendingOrderCancellationAttempt
        {
            public PendingOrderCancellationOutcome Outcome { get; set; }
            public TradeResult TradeResult { get; set; }
        }

        private readonly Dictionary<int, string> _botCancelReasons = new Dictionary<int, string>();

        private PendingOrderCancellationAttempt CancelPendingOrderWithRetry(PendingOrder order, string reason)
        {
            DateTime now = Server.TimeInUtc;
            PendingOrderCancellationRetryState state;
            if (!_pendingOrderCancellationRetries.TryGetValue(order.Id, out state))
            {
                state = new PendingOrderCancellationRetryState
                {
                    StartedUtc = now,
                    InitialReason = reason
                };
                _pendingOrderCancellationRetries[order.Id] = state;
            }

            state.LatestReason = reason;
            state.ReasonsSeen.Add(reason);
            if (now < state.NextEligibleAttemptUtc)
            {
                return new PendingOrderCancellationAttempt
                {
                    Outcome = PendingOrderCancellationOutcome.CoolingDown
                };
            }

            state.AttemptCount++;
            _botCancelReasons[order.Id] = reason;
            TradeResult result = CancelPendingOrder(order);
            if (result == null || !result.IsSuccessful)
                _botCancelReasons.Remove(order.Id);
            if (result != null && result.IsSuccessful)
            {
                // The cancellation callback is the authoritative finalizer. If it ran
                // synchronously, the state is already gone and must not be recreated.
                PendingOrderCancellationRetryState liveState;
                if (_pendingOrderCancellationRetries.TryGetValue(order.Id, out liveState))
                {
                    liveState.SuccessfulAttemptCount++;
                    liveState.NextEligibleAttemptUtc = now.Add(PendingOrderCancellationRetryDelay);
                }
                return new PendingOrderCancellationAttempt
                {
                    Outcome = PendingOrderCancellationOutcome.Successful,
                    TradeResult = result
                };
            }

            state.FailedAttemptCount++;
            state.NextEligibleAttemptUtc = now.Add(PendingOrderCancellationRetryDelay);
            state.LastFailureSignature = result == null ? "null result" : result.Error.ToString();
            LogActiveWarning("cancel-" + order.Id, "ORDER", "CANCEL_RETRY_PENDING",
                "orderId={0} initialReason={1} latestReason={2} reasonsSeen={3} attempt={4} retryScheduled=true nextAttemptUtc={5:O} brokerError={6}",
                order.Id, state.InitialReason, state.LatestReason,
                GetPendingOrderCancellationReasons(state), state.AttemptCount,
                state.NextEligibleAttemptUtc, state.LastFailureSignature);
            return new PendingOrderCancellationAttempt
            {
                Outcome = PendingOrderCancellationOutcome.Failed,
                TradeResult = result
            };
        }

        private static string GetPendingOrderCancellationReasons(
            PendingOrderCancellationRetryState state)
        {
            return string.Join(
                ",",
                state.ReasonsSeen.OrderBy(reason => reason, StringComparer.Ordinal));
        }

        private void RemovePendingOrderCancellationRetryState(
            int pendingOrderId,
            bool clearWarnings = false,
            string outcome = null)
        {
            if (_pendingOrderCancellationRetries.TryGetValue(
                    pendingOrderId,
                    out PendingOrderCancellationRetryState state))
            {
                if (clearWarnings)
                {
                    ClearWarning("cancel-" + pendingOrderId, "ORDER", "CANCEL_RECOVERED",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "orderId={0} initialReason={1} latestReason={2} reasonsSeen={3} outcome={4}",
                            pendingOrderId, state.InitialReason, state.LatestReason,
                            GetPendingOrderCancellationReasons(state),
                            outcome ?? "OrderNoLongerLive"));
                }
                _pendingOrderCancellationRetries.Remove(pendingOrderId);
            }
        }

        private void CompletePendingOrderCancellationObjective(
            int pendingOrderId,
            string completionReason)
        {
            if (!_pendingOrderCancellationRetries.TryGetValue(
                    pendingOrderId,
                    out PendingOrderCancellationRetryState state))
            {
                return;
            }

            if (string.Equals(completionReason, "Cancelled", StringComparison.Ordinal) &&
                state.SuccessfulAttemptCount == 0)
            {
                state.SuccessfulAttemptCount = 1;
            }

            ClearWarning(
                "cancel-" + pendingOrderId,
                "ORDER",
                "CANCEL_RETRY_RESOLVED",
                string.Format(CultureInfo.InvariantCulture,
                    "orderId={0} initialReason={1} latestReason={2} reasonsSeen={3} completionReason={4}",
                    pendingOrderId, state.InitialReason, state.LatestReason,
                    GetPendingOrderCancellationReasons(state), completionReason));

            LogNormal("ORDER", "CANCELLATION_COMPLETED",
                "orderId={0} initialReason={1} latestReason={2} reasonsSeen={3} startedUtc={4:O} attempts={5} successful={6} failed={7} completionReason={8}",
                pendingOrderId, state.InitialReason, state.LatestReason,
                GetPendingOrderCancellationReasons(state), state.StartedUtc,
                state.AttemptCount, state.SuccessfulAttemptCount,
                state.FailedAttemptCount, completionReason);

            _pendingOrderCancellationRetries.Remove(pendingOrderId);
        }
        private long _riskConversionApiCallCount;
        private long _riskConversionCoefficientRefreshCount;
        private bool _lastRiskCapFailureWasConversionTimeout;
        private readonly Dictionary<int, DateTime> _pendingOrderNoChangeLogTimes =
            new Dictionary<int, DateTime>();
        private const int PendingOrderNoChangeLogIntervalSeconds = 60;
        private readonly Dictionary<int, RejectedPendingOrderModification> _rejectedPendingOrderModifications =
            new Dictionary<int, RejectedPendingOrderModification>();
        private const int RejectedPendingOrderRetrySeconds = 30;
        private const int SafeSeparationHeartbeatIntervalSeconds = 600;
        private const int SafeSeparationMaterialTargetChangeTicks = 10;
        private const double SafeSeparationMaterialTargetChangePercent = 0.02;
        private const string SafeSeparationFootprintReason = "OrderFootprintViolatesSafeSeparation";
        private readonly Dictionary<int, SafeSeparationLogState> _safeSeparationLogStates =
            new Dictionary<int, SafeSeparationLogState>();
        private long _safeSeparationFirstBlockCount;
        private long _safeSeparationBlockedEvaluationCount;
        private long _safeSeparationDuplicateSuppressedCount;
        private long _safeSeparationPeriodicSummaryCount;
        private long _safeSeparationMaterialChangeCount;
        private long _safeSeparationClearedCount;
        private long _safeSeparationLifecycleEndedCount;
        private long _safeSeparationStatePrunedCount;
        private long _pendingOrderNoChangeSkipCount;
        private long _pendingOrderDuplicateRejectionSkipCount;
        private long _pendingOrderModificationAttemptCount;
        private long _pendingOrderModificationSuccessCount;
        private long _pendingOrderModificationFailureCount;
        private long _provisionalSafeDistanceRegistrationCount;
        private long _provisionalSafeDistanceLiveSettlementCount;
        private long _provisionalSafeDistanceNormalizedSettlementCount;
        private long _provisionalSafeDistanceCancellationCount;
        private long _provisionalSafeDistanceStaleCleanupCount;
        private long _provisionalSafeDistanceExpiryCount;
        // Reused only by stale-state cleanup to avoid rebuilding temporary LINQ collections.
        private readonly HashSet<int> _activeBotPendingOrderIds = new HashSet<int>();
        private readonly List<int> _stalePendingOrderStateIds = new List<int>();
        // Reusable stable snapshots preserve safe iteration when trade operations can
        // mutate the live cTrader collections, without allocating LINQ arrays each cycle.
        private readonly List<Position> _botPositionSnapshot = new List<Position>(16);
        private readonly List<Position> _rolloverPositionSnapshot = new List<Position>(16);
        private readonly List<PendingOrder> _pendingOrderTrailingSnapshot = new List<PendingOrder>(16);
        private readonly List<PendingOrder> _rolloverPendingOrderSnapshot = new List<PendingOrder>(16);
        // Lazily rebuilt account-wide, same-symbol cache used by safe-separation checks.
        // This intentionally includes manual and other-bot trades on the current symbol.
        private readonly List<SafeDistanceLevelSnapshot> _safeDistancePositionSnapshot = new List<SafeDistanceLevelSnapshot>(16);
        private readonly List<SafeDistanceLevelSnapshot> _safeDistancePendingOrderSnapshot = new List<SafeDistanceLevelSnapshot>(16);
        private readonly Dictionary<int, ProvisionalSafeDistancePendingOrder> _provisionalSafeDistancePendingOrders =
            new Dictionary<int, ProvisionalSafeDistancePendingOrder>();
        private readonly List<int> _settledProvisionalSafeDistancePendingOrderIds = new List<int>();
        private const int ProvisionalSafeDistanceSettlementTimeoutSeconds = 120;
        private enum ProvisionalSafeDistanceResolution
        {
            LiveCollection,
            LiveCollectionNormalized,
            CreatedEvent,
            ModifiedEvent,
            CancelledEvent,
            StaleCleanup,
            Expired
        }
        private bool _safeDistanceSnapshotDirty = true;

        private double _lastClosePrice;
        private string _mrLongLabel;
        private string _mrShortLabel;
        private string _tfLongLabel;
        private string _tfShortLabel;
        private double _effectiveDirectionalAllocation = DefaultDirectionalAllocation;
        private TradingMode _effectiveTradingMode;
        private readonly ManualSideStrategy[] _effectiveManualLongStrategies =
        {
            ManualSideStrategy.TrendFollowing,
            ManualSideStrategy.MeanReversion,
            ManualSideStrategy.TrendFollowing
        };
        private readonly ManualSideStrategy[] _effectiveManualShortStrategies =
        {
            ManualSideStrategy.MeanReversion,
            ManualSideStrategy.TrendFollowing,
            ManualSideStrategy.TrendFollowing
        };
        private const int HourlyStandardDeviationLength = 21;
        private sealed class ActiveWarningState
        {
            public string Category;
            public string EventName;
            public string Signature;
            public DateTime ActivatedUtc;
            public DateTime LastOccurrenceUtc;
            public long Occurrences;
            public long SuppressedOccurrences;
            public bool OpeningRecordVisible;
        }

        private readonly Dictionary<string, ActiveWarningState> _activeWarningStates =
            new Dictionary<string, ActiveWarningState>();
        private long _warningActivations;
        private long _warningRecoveries;
        private long _warningSuppressedOccurrences;

        #endregion



    }
}
