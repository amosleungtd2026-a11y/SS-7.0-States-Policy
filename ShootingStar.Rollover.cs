using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Rollover window and phase-state coordination.
    public partial class ShootingStar
    {
        #region Rollover State

        private DateTime _cachedRolloverStartUtc = DateTime.MinValue;
        private DateTime _cachedRolloverEndUtc = DateTime.MinValue;
        private DateTime _lastRolloverQueryUtc = DateTime.MinValue;

        private readonly List<PositionSnapshot> _positionSnapshots = new List<PositionSnapshot>();
        private readonly List<PendingOrderSnapshot> _orderSnapshots = new List<PendingOrderSnapshot>();
        private readonly RolloverContext _rolloverContext = new RolloverContext();

        // Objects created by the active restoration cycle are valid exposure while
        // other snapshots remain unresolved. Track them separately from exposure
        // that unexpectedly appears during restoration.
        private readonly Dictionary<int, Guid> _rolloverRestoredPositionRecoveryIds =
            new Dictionary<int, Guid>();
        private readonly Dictionary<int, Guid> _rolloverRestoredOrderRecoveryIds =
            new Dictionary<int, Guid>();
        private readonly Dictionary<Guid, PositionSnapshot> _restoredPositionSnapshotsByRecoveryId =
            new Dictionary<Guid, PositionSnapshot>();
        private readonly Dictionary<Guid, PendingOrderSnapshot> _restoredOrderSnapshotsByRecoveryId =
            new Dictionary<Guid, PendingOrderSnapshot>();
        private readonly Dictionary<int, int> _unregisteredRolloverOrderFills =
            new Dictionary<int, int>();
        private sealed class DeferredRolloverQuarterStop
        {
            public Guid RecoveryId;
            public int PositionId;
            public double TargetStopLoss;
            public DateTime StartedUtc;
            public DateTime NextAttemptUtc;
            public int Attempts;
            public bool Expired;
        }

        private readonly Dictionary<int, DeferredRolloverQuarterStop>
            _deferredRolloverQuarterStops =
                new Dictionary<int, DeferredRolloverQuarterStop>();
        private const int DeferredQuarterStopRetrySeconds = 2;
        private const int DeferredQuarterStopDeadlineSeconds = 300;

        // Dedicated rollover snapshots. These lists are never shared with ordinary
        // maintenance buffers or repopulated while their caller is enumerating them.
        private readonly List<PendingOrder> _rolloverRecoveryPendingOrderBuffer =
            new List<PendingOrder>();
        private readonly List<Position> _rolloverRecoveryPositionBuffer =
            new List<Position>();
        private readonly List<int> _rolloverStalePositionIdBuffer = new List<int>();
        private readonly List<int> _rolloverStaleOrderIdBuffer = new List<int>();
        private readonly HashSet<int> _rolloverLivePositionIds = new HashSet<int>();
        private readonly HashSet<int> _rolloverLiveOrderIds = new HashSet<int>();
        private Guid? _activePositionRestorationRecoveryId;
        private Guid? _activeOrderRestorationRecoveryId;
        private DateTime _rolloverRecoveryCooldownUntilUtc = DateTime.MinValue;
        private DateTime _rolloverRecoveryStartedUtc = DateTime.MinValue;
        private string _rolloverRecoveryReason;
        private int _rolloverRecoveryCleanupAttempts;
        private DateTime _nextRolloverRecoveryCleanupAttemptUtc = DateTime.MinValue;
        private bool _rolloverFatalStopIssued;
        private int _rolloverCapturedPositionSnapshots;
        private int _rolloverCapturedOrderSnapshots;
        private int _rolloverRestoredPositionSnapshots;
        private int _rolloverRestoredOrderSnapshots;
        private int _rolloverSkippedPositionSnapshots;
        private int _rolloverSkippedOrderSnapshots;
        private int _rolloverFailedPositionSnapshots;
        private int _rolloverFailedOrderSnapshots;
        private int _rolloverFinalizedSkippedLogicalTrades;
        private int _rolloverFinalizedFailedLogicalTrades;
        private const int RolloverRecoveryCooldownSeconds = 30;
        private const int RolloverRecoveryMaxCleanupAttempts = 5;

        #endregion

        #region Broker Rollover Guard

        private bool IsInsideRolloverWindow()
        {
            DateTime utc = DateTime.SpecifyKind(Server.TimeInUtc, DateTimeKind.Utc);
            LogMarketSessionState(utc);
            if (RolloverCacheNeedsRefresh(utc))
                RefreshRolloverWindowCache(utc);

            _lastRolloverQueryUtc = utc;
            return utc >= _cachedRolloverStartUtc && utc < _cachedRolloverEndUtc;
        }

        private bool RolloverCacheNeedsRefresh(DateTime utc)
        {
            bool cacheNotInitialized = _cachedRolloverStartUtc == DateTime.MinValue;
            bool cachedWindowHasEnded = utc >= _cachedRolloverEndUtc;
            bool serverTimeMovedBackUnexpectedly =
                _lastRolloverQueryUtc != DateTime.MinValue &&
                utc < _lastRolloverQueryUtc;
            return cacheNotInitialized || cachedWindowHasEnded || serverTimeMovedBackUnexpectedly;
        }

        private void RefreshRolloverWindowCache(DateTime utc)
        {
            if (!TryGetRolloverWindow(utc, out DateTime selectedStart, out DateTime selectedEnd))
            {
                throw new InvalidOperationException(
                    "The next New York rollover window could not be calculated.");
            }

            _cachedRolloverStartUtc = DateTime.SpecifyKind(selectedStart, DateTimeKind.Utc);
            _cachedRolloverEndUtc = DateTime.SpecifyKind(selectedEnd, DateTimeKind.Utc);
        }

        private bool IsAllowedRolloverTransition(RolloverPhase from, RolloverPhase to)
        {
            if (from == to)
                return true;
            switch (from)
            {
                case RolloverPhase.Normal:
                    return to == RolloverPhase.Flattening ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.Flattening:
                    return to == RolloverPhase.WaitingForMarketOpen ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.WaitingForMarketOpen:
                    return to == RolloverPhase.Flattening ||
                           to == RolloverPhase.Restoring ||
                           to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.Restoring:
                    return to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.RecoveryCleanup:
                    return to == RolloverPhase.RecoveryCooldown ||
                           to == RolloverPhase.FatalStop;
                case RolloverPhase.RecoveryCooldown:
                    return to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup ||
                           to == RolloverPhase.FatalStop;
                case RolloverPhase.FatalStop:
                    return false;
                default:
                    return false;
            }
        }
        private bool TryTransitionRolloverPhase(RolloverPhase target, string reason)
        {
            RolloverPhase current = _rolloverContext.Phase;
            if (!IsAllowedRolloverTransition(current, target))
            {
                LogError("ROLLOVER", "INVALID_TRANSITION",
                    "invalid phase transition {0} -> {1}. Reason: {2}", current, target, reason);
                if (current != RolloverPhase.FatalStop)
                    _rolloverContext.Phase = RolloverPhase.RecoveryCleanup;
                return false;
            }
            if (current != target)
            {
                LogNormal("ROLLOVER", "PHASE_CHANGED",
                    "from={0} to={1} reason={2} utc={3:O} positions={4} pendingOrders={5}",
                    current, target, reason, Server.TimeInUtc,
                    Positions.Where(IsBotPosition).Count(), PendingOrders.Where(IsBotOrder).Count());
            }
            _rolloverContext.Phase = target;
            return true;
        }
        private void EnterFlatteningPhase()
        {
            TryTransitionRolloverPhase(RolloverPhase.Flattening, "pre-rollover exposure must be removed");
        }
        private void EnterWaitingForMarketOpenPhase()
        {
            if (HasLiveBotExposure())
            {
                LogError("ROLLOVER", "WAIT_BLOCKED",
                    "cannot enter WaitingForMarketOpen while bot exposure remains");
                return;
            }
            TryTransitionRolloverPhase(RolloverPhase.WaitingForMarketOpen,
                "flattening is complete; wait for the first eligible market-open tick");
        }
        private void EnterRestoringPhase()
        {
            // Restoration can span multiple ticks when one snapshot is temporarily
            // ineligible. Exposure already created and tracked by this restoration
            // cycle is therefore valid and must not be treated as pre-existing exposure.
            if (_rolloverContext.Phase == RolloverPhase.Restoring)
                return;

            if (_rolloverContext.Phase != RolloverPhase.WaitingForMarketOpen)
            {
                EnterRecoveryCleanupPhase(
                    "restoration requested from an invalid rollover phase");
                return;
            }

            if (IsInsideRolloverWindow() || HasLiveBotExposure())
            {
                EnterRecoveryCleanupPhase(
                    "initial restoration requested inside the rollover window or while pre-existing bot exposure remains");
                return;
            }

            TryTransitionRolloverPhase(RolloverPhase.Restoring,
                "first eligible tick after market open");
        }
        private void CompleteRolloverPhase()
        {
            if (_positionSnapshots.Count != 0 || _orderSnapshots.Count != 0)
            {
                EnterRecoveryCleanupPhase("rollover completion requested with unresolved snapshots");
                return;
            }
            TryTransitionRolloverPhase(RolloverPhase.Normal,
                "all rollover snapshots have been resolved");
        }
        private void EnterRecoveryCleanupPhase(string reason)
        {
            if (_rolloverContext.Phase == RolloverPhase.RecoveryCleanup)
                return;

            if (!TryTransitionRolloverPhase(RolloverPhase.RecoveryCleanup, reason))
                return;

            _rolloverRecoveryStartedUtc = Server.TimeInUtc;
            _rolloverRecoveryReason = reason;
            _rolloverRecoveryCleanupAttempts = 0;
            _nextRolloverRecoveryCleanupAttemptUtc = Server.TimeInUtc;
            foreach (PositionSnapshot waitingSnapshot in _positionSnapshots)
            {
                if (waitingSnapshot.RecoveryStatus == SnapshotRecoveryStatus.WaitingForEligibility)
                    TerminateTransientEligibilityWait(waitingSnapshot, "RecoveryCleanup");
            }
            foreach (PendingOrderSnapshot waitingSnapshot in _orderSnapshots)
            {
                if (waitingSnapshot.RecoveryStatus == SnapshotRecoveryStatus.WaitingForEligibility)
                    TerminateTransientEligibilityWait(waitingSnapshot, "RecoveryCleanup");
            }
            foreach (PositionSnapshot snapshot in _positionSnapshots)
            {
                if (snapshot.RecoveryStatus != SnapshotRecoveryStatus.Restored &&
                    snapshot.RecoveryStatus != SnapshotRecoveryStatus.PermanentlySkipped)
                {
                    snapshot.RecoveryStatus = SnapshotRecoveryStatus.Quarantined;
                }
            }
            foreach (PendingOrderSnapshot snapshot in _orderSnapshots)
            {
                if (snapshot.RecoveryStatus != SnapshotRecoveryStatus.Restored &&
                    snapshot.RecoveryStatus != SnapshotRecoveryStatus.PermanentlySkipped)
                {
                    snapshot.RecoveryStatus = SnapshotRecoveryStatus.Quarantined;
                }
            }
            LogError("ROLLOVER", "RECOVERY_STARTED",
                "controlled recovery started; reason={0} positions={1} pendingOrders={2} positionSnapshots={3} orderSnapshots={4}",
                reason, Positions.Where(IsBotPosition).Count(), PendingOrders.Where(IsBotOrder).Count(),
                _positionSnapshots.Count, _orderSnapshots.Count);
        }

        private void EnterFatalStopPhase(string reason)
        {
            if (_rolloverContext.Phase != RolloverPhase.FatalStop)
                TryTransitionRolloverPhase(RolloverPhase.FatalStop, reason);

            if (_rolloverFatalStopIssued)
                return;

            _rolloverFatalStopIssued = true;
            LogError("ROLLOVER", "FATAL_STOP",
                "capital safety could not be established; cBot will stop. Reason: {0}", reason);
            Stop();
        }
        private bool RunRolloverStateMachineSelfTests()
        {
            // These production startup tests are intentionally always enabled. They are
            // pure and deterministic: no live rollover state or trading collection is mutated.
            RolloverPhase[] phases =
            {
                RolloverPhase.Normal,
                RolloverPhase.Flattening,
                RolloverPhase.WaitingForMarketOpen,
                RolloverPhase.Restoring,
                RolloverPhase.RecoveryCleanup,
                RolloverPhase.RecoveryCooldown,
                RolloverPhase.FatalStop
            };

            foreach (RolloverPhase from in phases)
            {
                foreach (RolloverPhase to in phases)
                {
                    bool expected = IsExpectedRolloverTransition(from, to);
                    bool actual = IsAllowedRolloverTransition(from, to);
                    if (actual == expected)
                        continue;

                    LogError("STARTUP", "ROLLOVER_SELF_TEST_FAILED",
                        "transition assertion failed: from={0} to={1} expected={2} actual={3}",
                        from, to, expected, actual);
                    return false;
                }
            }

            if (!AreRolloverRetryConstantsValid(
                RolloverRestorationMaxAttempts,
                RolloverRestorationRetrySeconds,
                RolloverTransientRetrySeconds,
                RolloverTransientMaxWaitMinutes,
                RolloverPositionRestorationMaxAttempts,
                RolloverPositionRestorationRetrySeconds))
            {
                LogError("STARTUP", "ROLLOVER_SELF_TEST_FAILED",
                    "retry invariant failed: maxAttempts={0} restorationRetrySeconds={1} transientRetrySeconds={2} transientMaxWaitMinutes={3} positionMaxAttempts={4} positionRetrySeconds={5}",
                    RolloverRestorationMaxAttempts,
                    RolloverRestorationRetrySeconds,
                    RolloverTransientRetrySeconds,
                    RolloverTransientMaxWaitMinutes,
                    RolloverPositionRestorationMaxAttempts,
                    RolloverPositionRestorationRetrySeconds);
                return false;
            }

            if (!RunMarketSessionSelfTests())
                return false;

            if (!RunRolloverRestorationPolicySelfTests()) return false;
            LogNormal("STARTUP", "ROLLOVER_SELF_TEST",
                "all {0} transition assertions and retry invariants passed; production startup self-tests remain always enabled",
                phases.Length * phases.Length);
            return true;
        }

        private static bool AreRolloverRetryConstantsValid(
            int maxAttempts,
            int restorationRetrySeconds,
            int transientRetrySeconds,
            int transientMaxWaitMinutes,
            int positionMaxAttempts,
            int positionRetrySeconds)
        {
            return maxAttempts > 0 &&
                restorationRetrySeconds > 0 &&
                transientRetrySeconds > 0 &&
                transientMaxWaitMinutes > 0 &&
                positionMaxAttempts > 0 &&
                positionRetrySeconds > 0;
        }

        private static bool HasPositionRestorationAttemptsRemaining(int failedAttempts)
        {
            return failedAttempts < RolloverPositionRestorationMaxAttempts;
        }

        private bool IsExpectedRolloverTransition(RolloverPhase from, RolloverPhase to)
        {
            if (from == to)
                return true;

            switch (from)
            {
                case RolloverPhase.Normal:
                    return to == RolloverPhase.Flattening ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.Flattening:
                    return to == RolloverPhase.WaitingForMarketOpen ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.WaitingForMarketOpen:
                    return to == RolloverPhase.Flattening ||
                           to == RolloverPhase.Restoring ||
                           to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.Restoring:
                    return to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup;
                case RolloverPhase.RecoveryCleanup:
                    return to == RolloverPhase.RecoveryCooldown ||
                           to == RolloverPhase.FatalStop;
                case RolloverPhase.RecoveryCooldown:
                    return to == RolloverPhase.Normal ||
                           to == RolloverPhase.RecoveryCleanup ||
                           to == RolloverPhase.FatalStop;
                case RolloverPhase.FatalStop:
                    return false;
                default:
                    return false;
            }
        }

        private bool ValidateRolloverRuntimeInvariants(string caller)
        {
            RolloverPhase phase = _rolloverContext.Phase;
            bool invalid = false;
            string reason = null;
            if (phase == RolloverPhase.RecoveryCleanup ||
                phase == RolloverPhase.RecoveryCooldown)
            {
                return true;
            }
            if (phase == RolloverPhase.FatalStop)
                return false;
            if (phase == RolloverPhase.Normal &&
                (_positionSnapshots.Count != 0 || _orderSnapshots.Count != 0))
            {
                invalid = true;
                reason = "Normal phase contains unresolved rollover snapshots";
            }
            else if (phase == RolloverPhase.WaitingForMarketOpen && HasLiveBotExposure())
            {
                invalid = true;
                reason = "WaitingForMarketOpen phase contains live bot exposure";
            }
            else if (phase == RolloverPhase.Restoring && HasUnsafeBotExposureDuringRestoration())
            {
                invalid = true;
                reason = "Restoring phase contains bot exposure that is not expected and protected";
            }
            else if (phase == RolloverPhase.Restoring && IsInsideRolloverWindow())
            {
                invalid = true;
                reason = "Restoring phase is active inside the rollover window";
            }
            if (!invalid)
                return true;
            LogError("ROLLOVER", "INVARIANT_FAILED", "{0}: {1}", caller, reason);
            EnterRecoveryCleanupPhase(reason);
            return false;
        }

        private void ProcessRolloverGuard()
        {
            if (_rolloverContext.Phase == RolloverPhase.Normal)
            {
                ClearRolloverRestorationTracking();
                CaptureRolloverSnapshot();
                EnterFlatteningPhase();
            }
            else if (_rolloverContext.Phase != RolloverPhase.Flattening &&
                     _rolloverContext.Phase != RolloverPhase.WaitingForMarketOpen)
            {
                EnterRecoveryCleanupPhase("rollover guard entered from invalid phase " + _rolloverContext.Phase);
                return;
            }

            // Stable reusable snapshot required: successful cancellation mutates PendingOrders.
            CaptureBotPendingOrders(_rolloverPendingOrderSnapshot);
            foreach (var order in _rolloverPendingOrderSnapshot)
            {
                CancelPendingOrderWithRetry(order, "rollover-flattening");
            }

            // Stable reusable snapshot required: successful closure mutates Positions.
            CaptureBotPositions(_rolloverPositionSnapshot);
            bool canClosePositions = HasExecutableMarketQuote(Server.TimeInUtc);
            if (_rolloverPositionSnapshot.Count > 0 && !canClosePositions)
            {
                LogActiveWarning("rollover-awaiting-executable-market", "ROLLOVER",
                    "WAITING_FOR_EXECUTABLE_MARKET",
                    "position flattening is deferred until a valid quote is observed in the active market session");
            }
            else
            {
                ClearWarning("rollover-awaiting-executable-market", "ROLLOVER",
                    "EXECUTABLE_MARKET_AVAILABLE",
                    "a valid quote is available in the active market session");
            }

            foreach (var position in _rolloverPositionSnapshot)
            {
                if (!canClosePositions)
                    continue;

                RunTelemetry("FlushPositionRollover", () =>
                    FlushModificationTelemetry(_positionModificationTelemetry, position.Id, "position", "rollover", false));
                var result = ClosePosition(position);
                if (result == null || !result.IsSuccessful)
                    LogError("ROLLOVER", "FLATTEN_CLOSE_FAILED", "to close position {0}. Error: {1}", position.Id, result == null ? "null result" : result.Error.ToString());
            }

            if (!HasLiveBotExposure())
                EnterWaitingForMarketOpenPhase();
        }

        private RolloverProtectionState ClassifyRolloverProtectionState(TradeType side, double entry, double? sl, double? tp, double tolerance)
        {
            if (!IsFinitePositive(entry) || !IsFinitePositive(tolerance))
                return RolloverProtectionState.InvalidProtection;

            if (!sl.HasValue || !tp.HasValue)
                return RolloverProtectionState.MissingProtection;

            if (!IsFinitePositive(sl.Value) || !IsFinitePositive(tp.Value))
                return RolloverProtectionState.InvalidProtection;

            bool slCrossed = side == TradeType.Buy ? sl.Value >= entry : sl.Value <= entry;
            bool tpCrossed = side == TradeType.Buy ? tp.Value <= entry + tolerance : tp.Value >= entry - tolerance;
            if (slCrossed && tpCrossed)
                return RolloverProtectionState.BothCrossedEntry;

            if (slCrossed)
                return RolloverProtectionState.StopLossCrossedEntry;

            if (tpCrossed)
                return RolloverProtectionState.TakeProfitCrossedEntry;

            return RolloverProtectionState.Conventional;
        }

        private bool IsStopLossCrossedProtectionState(RolloverProtectionState state)
        {
            return state == RolloverProtectionState.StopLossCrossedEntry ||
                   state == RolloverProtectionState.BothCrossedEntry;
        }
        private bool IsTakeProfitCrossedProtectionState(RolloverProtectionState state)
        {
            return state == RolloverProtectionState.TakeProfitCrossedEntry ||
                   state == RolloverProtectionState.BothCrossedEntry;
        }
        private bool IsCrossedProtectionState(RolloverProtectionState state)
        {
            return IsStopLossCrossedProtectionState(state) ||
                   IsTakeProfitCrossedProtectionState(state);
        }

        private static bool IsTakeProfitWithinOriginalQuarter(
            TradeType side, double originalEntry, double originalTakeProfit, double currentTakeProfit)
        {
            double originalDistance = side == TradeType.Buy
                ? originalTakeProfit - originalEntry : originalEntry - originalTakeProfit;
            double remainingDistance = side == TradeType.Buy
                ? currentTakeProfit - originalEntry : originalEntry - currentTakeProfit;
            return remainingDistance <= originalDistance * 0.25;
        }

        private static double GetQuarterDistanceStopLoss(
            TradeType side, double originalEntry, double originalStopLoss, double restoredEntry)
        {
            double distance = Math.Abs(originalEntry - originalStopLoss) * 0.25;
            return side == TradeType.Buy ? restoredEntry - distance : restoredEntry + distance;
        }

        private static bool IsQuarterStopOnLossSide(TradeType side, double entry, double stop)
        {
            return IsFinitePositive(stop) &&
                (side == TradeType.Buy ? stop < entry : stop > entry);
        }
        private bool RunRolloverRestorationPolicySelfTests()
        {
            double e = 100.0, t = 0.01;
            bool ok =
                HasPositionRestorationAttemptsRemaining(0) &&
                HasPositionRestorationAttemptsRemaining(RolloverPositionRestorationMaxAttempts - 1) &&
                !HasPositionRestorationAttemptsRemaining(RolloverPositionRestorationMaxAttempts) &&
                ClassifyRolloverProtectionState(TradeType.Buy, e, 90, 110, t) == RolloverProtectionState.Conventional &&
                ClassifyRolloverProtectionState(TradeType.Buy, e, 100, 110, t) == RolloverProtectionState.StopLossCrossedEntry &&
                ClassifyRolloverProtectionState(TradeType.Buy, e, 90, 100, t) == RolloverProtectionState.TakeProfitCrossedEntry &&
                ClassifyRolloverProtectionState(TradeType.Sell, e, 110, 90, t) == RolloverProtectionState.Conventional &&
                ClassifyRolloverProtectionState(TradeType.Sell, e, 100, 90, t) == RolloverProtectionState.StopLossCrossedEntry &&
                ClassifyRolloverProtectionState(TradeType.Sell, e, 110, 100, t) == RolloverProtectionState.TakeProfitCrossedEntry &&
                IsStopLossCrossedProtectionState(RolloverProtectionState.StopLossCrossedEntry) &&
                IsStopLossCrossedProtectionState(RolloverProtectionState.BothCrossedEntry) &&
                !IsStopLossCrossedProtectionState(RolloverProtectionState.TakeProfitCrossedEntry) &&
                IsTakeProfitCrossedProtectionState(RolloverProtectionState.TakeProfitCrossedEntry) &&
                IsTakeProfitCrossedProtectionState(RolloverProtectionState.BothCrossedEntry) &&
                !IsTakeProfitCrossedProtectionState(RolloverProtectionState.StopLossCrossedEntry) &&
                HasValidRolloverPositionProtectionGeometry(
                    TradeType.Buy, e, e, 110, true, false, t) &&
                HasValidRolloverPositionProtectionGeometry(
                    TradeType.Buy, e, 90, e, false, true, t) &&
                HasValidRolloverPositionProtectionGeometry(
                    TradeType.Sell, e, e, 90, true, false, t) &&
                HasValidRolloverPositionProtectionGeometry(
                    TradeType.Sell, e, 110, e, false, true, t) &&
                !HasValidRolloverPositionProtectionGeometry(
                    TradeType.Buy, e, e + 2 * t, 110, true, false, t) &&
                !HasValidRolloverPositionProtectionGeometry(
                    TradeType.Buy, e, e, 90, true, false, t) &&
                !HasValidRolloverPositionProtectionGeometry(
                    TradeType.Buy, e, e, 110, false, false, t) &&
                PassesAsymmetricOpeningGapPolicy(StrategyKind.MeanReversion, OpeningGapDirection.Unfavourable, 10.0, 1.0) &&
                PassesAsymmetricOpeningGapPolicy(StrategyKind.MeanReversion, OpeningGapDirection.Favourable, 1.0, 1.0) &&
                !PassesAsymmetricOpeningGapPolicy(StrategyKind.MeanReversion, OpeningGapDirection.Favourable, 1.0001, 1.0) &&
                PassesAsymmetricOpeningGapPolicy(StrategyKind.TrendFollowing, OpeningGapDirection.Favourable, 10.0, 1.0) &&
                PassesAsymmetricOpeningGapPolicy(StrategyKind.TrendFollowing, OpeningGapDirection.Unfavourable, 1.0, 1.0) &&
                !PassesAsymmetricOpeningGapPolicy(StrategyKind.TrendFollowing, OpeningGapDirection.Unfavourable, 1.0001, 1.0) &&
                PassesAsymmetricOpeningGapPolicy(StrategyKind.MeanReversion, OpeningGapDirection.Neutral, 0.0, 1.0);
            if (!ok) LogError("STARTUP", "ROLLOVER_SELF_TEST_FAILED", "restoration policy assertion failed");
            return ok;
        }

        #endregion

    }
}
