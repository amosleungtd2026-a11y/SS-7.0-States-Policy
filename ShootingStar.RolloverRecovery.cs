using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Rollover retries, failure cleanup, and recovery completion.
    public partial class ShootingStar
    {
        #region Rollover Recovery and Cleanup
        private bool ProcessRolloverRecovery()
        {
            if (_rolloverContext.Phase == RolloverPhase.FatalStop)
                return true;

            if (_rolloverContext.Phase == RolloverPhase.RecoveryCooldown)
            {
                if (Server.TimeInUtc < _rolloverRecoveryCooldownUntilUtc)
                    return true;

                if (!CanResumeNormalTradingAfterRecovery())
                {
                    EnterRecoveryCleanupPhase("recovery cooldown validation failed");
                    return true;
                }

                ResolveFailedRolloverSnapshots();
                ClearRolloverRestorationTracking();
                _positionSnapshots.Clear();
                _orderSnapshots.Clear();
                _rolloverContext.SnapshotContainedTrades = false;
                _lastClosePrice = 0.0;
                _rolloverRecoveryReason = null;
                _rolloverRecoveryStartedUtc = DateTime.MinValue;
                _rolloverRecoveryCleanupAttempts = 0;
                _nextRolloverRecoveryCleanupAttemptUtc = DateTime.MinValue;
                _ticksUntilMaintenance = _rolloverContext.SavedTicksUntilMaintenance;
                MarkSafeDistanceSnapshotDirty();
                TryTransitionRolloverPhase(RolloverPhase.Normal,
                    "recovery cleanup and cooldown completed");
                LogNormal("ROLLOVER", "RECOVERY_COMPLETE",
                    "controlled recovery completed; the rolling reference range remained continuous");
                return true;
            }

            if (_rolloverContext.Phase != RolloverPhase.RecoveryCleanup)
                return false;

            if (Server.TimeInUtc < _nextRolloverRecoveryCleanupAttemptUtc)
                return true;

            _nextRolloverRecoveryCleanupAttemptUtc = Server.TimeInUtc.AddSeconds(2.0);
            _rolloverRecoveryCleanupAttempts++;
            bool operationFailed = false;

            CaptureRolloverPendingOrders(_rolloverRecoveryPendingOrderBuffer);
            foreach (PendingOrder order in _rolloverRecoveryPendingOrderBuffer)
            {
                PendingOrderCancellationAttempt cancelAttempt = CancelPendingOrderWithRetry(order, "rollover-recovery-cleanup");
                if (cancelAttempt.Outcome == PendingOrderCancellationOutcome.Failed)
                    operationFailed = true;
            }

            CaptureRolloverPositions(_rolloverRecoveryPositionBuffer);
            foreach (Position position in _rolloverRecoveryPositionBuffer)
            {
                RecoveryExposureClassification classification = ClassifyRolloverPosition(position);
                if (classification == RecoveryExposureClassification.ExpectedAndProtected)
                    continue;

                TradeResult closeResult = ClosePosition(position);
                if (closeResult == null || !closeResult.IsSuccessful)
                {
                    operationFailed = true;
                    LogActiveWarning("rollover-recovery-position-" + position.Id,
                        "ROLLOVER", "RECOVERY_CLOSE_PENDING",
                        "position {0} classification={1} could not be closed. Error: {2}",
                        position.Id, classification,
                        closeResult == null ? "null result" : closeResult.Error.ToString());
                }
            }

            PruneRolloverRestoredObjectIds();
            bool unsafeExposureRemains = HasUnsafeOrUnexpectedRecoveryExposure();
            if (operationFailed || unsafeExposureRemains)
            {
                if (_rolloverRecoveryCleanupAttempts >= RolloverRecoveryMaxCleanupAttempts)
                {
                    EnterFatalStopPhase("recovery cleanup exhausted its bounded retry limit");
                }
                return true;
            }

            int quarantinedPositions = _positionSnapshots.Where(snapshot =>
                snapshot.RecoveryStatus == SnapshotRecoveryStatus.Quarantined ||
                snapshot.RecoveryStatus == SnapshotRecoveryStatus.Failed).Count();
            int quarantinedOrders = _orderSnapshots.Where(snapshot =>
                snapshot.RecoveryStatus == SnapshotRecoveryStatus.Quarantined ||
                snapshot.RecoveryStatus == SnapshotRecoveryStatus.Failed).Count();
            _rolloverRecoveryCooldownUntilUtc = Server.TimeInUtc.AddSeconds(RolloverRecoveryCooldownSeconds);
            TryTransitionRolloverPhase(RolloverPhase.RecoveryCooldown,
                "unsafe and unexplained exposure removed or absent");
            LogNormal("ROLLOVER", "RECOVERY_COOLDOWN_STARTED",
                "cooldownSeconds={0} quarantinedPositionSnapshots={1} quarantinedOrderSnapshots={2} reason={3}",
                RolloverRecoveryCooldownSeconds, quarantinedPositions, quarantinedOrders,
                _rolloverRecoveryReason ?? "unspecified");
            return true;
        }

        private void ResolveFailedRolloverSnapshots()
        {
            DateTime resolvedUtc = Server.TimeInUtc;

            foreach (PositionSnapshot snapshot in _positionSnapshots)
            {
                snapshot.RecoveryStatus = SnapshotRecoveryStatus.Failed;
                if (!snapshot.FailureCounted)
                {
                    snapshot.FailureCounted = true;
                    _rolloverFailedPositionSnapshots++;
                }

                snapshot.ResolvedUtc = resolvedUtc;
                if (string.IsNullOrWhiteSpace(snapshot.ResolutionReason))
                    snapshot.ResolutionReason = "ControlledRecovery";

                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_RESOLVED",
                    "schemaVersion=2 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} restoredPositionId={4} failureReason={5} outcome=controlledRecoveryFailure",
                    FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                    snapshot.RecoveryId, snapshot.SourcePositionId,
                    snapshot.RestoredObjectId.HasValue ? snapshot.RestoredObjectId.Value.ToString(CultureInfo.InvariantCulture) : "none",
                    snapshot.LastRestorationFailure ?? "unspecified");
                TerminateTransientEligibilityWait(snapshot, "ControlledRecovery");
                if (FinalizeUnrestoredLogicalTrade(
                        snapshot,
                        "RolloverRecoveryFailure",
                        "ControlledRecovery"))
                {
                    _rolloverFinalizedFailedLogicalTrades++;
                }
            }

            foreach (PendingOrderSnapshot snapshot in _orderSnapshots)
            {
                snapshot.RecoveryStatus = SnapshotRecoveryStatus.Failed;
                if (!snapshot.FailureCounted)
                {
                    snapshot.FailureCounted = true;
                    _rolloverFailedOrderSnapshots++;
                }

                snapshot.ResolvedUtc = resolvedUtc;
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_RESOLVED",
                    "schemaVersion=4 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} restoredOrderId={3} outcome=controlledRecoveryFailure",
                    FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId),
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId,
                    snapshot.RestoredObjectId.HasValue ? snapshot.RestoredObjectId.Value.ToString(CultureInfo.InvariantCulture) : "none");
                TerminateTransientEligibilityWait(snapshot, "ControlledRecovery");
            }

            LogNormal("ROLLOVER", "RECOVERY_RESOLUTION_SUMMARY",
                "capturedPositions={0} restoredPositions={1} skippedPositions={2} " +
                "failedPositions={3} finalizedSkippedLogicalTrades={4} " +
                "finalizedFailedLogicalTrades={5} capturedOrders={6} restoredOrders={7} " +
                "skippedOrders={8} failedOrders={9} reason={10}",
                _rolloverCapturedPositionSnapshots,
                _rolloverRestoredPositionSnapshots,
                _rolloverSkippedPositionSnapshots,
                _rolloverFailedPositionSnapshots,
                _rolloverFinalizedSkippedLogicalTrades,
                _rolloverFinalizedFailedLogicalTrades,
                _rolloverCapturedOrderSnapshots,
                _rolloverRestoredOrderSnapshots,
                _rolloverSkippedOrderSnapshots,
                _rolloverFailedOrderSnapshots,
                _rolloverRecoveryReason ?? "unspecified");
        }

        private bool HasUnsafeOrUnexpectedRecoveryExposure()
        {
            CaptureRolloverPositions(_rolloverRecoveryPositionBuffer);
            foreach (Position position in _rolloverRecoveryPositionBuffer)
            {
                if (ClassifyRolloverPosition(position) != RecoveryExposureClassification.ExpectedAndProtected)
                    return true;
            }

            CaptureRolloverPendingOrders(_rolloverRecoveryPendingOrderBuffer);
            return _rolloverRecoveryPendingOrderBuffer.Count > 0;
        }

        private bool CanResumeNormalTradingAfterRecovery()
        {
            if (_rolloverContext.Phase != RolloverPhase.RecoveryCooldown)
                return false;
            if (HasUnsafeOrUnexpectedRecoveryExposure())
                return false;

            // HasUnsafeOrUnexpectedRecoveryExposure populated this dedicated position
            // snapshot and performed no broker operation, so it remains valid here.
            foreach (Position position in _rolloverRecoveryPositionBuffer)
            {
                if (!position.StopLoss.HasValue || !position.TakeProfit.HasValue)
                    return false;
            }
            return true;
        }

        private void ClearRolloverSnapshot()
        {
            _positionSnapshots.Clear();
            _orderSnapshots.Clear();
            CompleteRolloverPhase();
            _rolloverContext.SnapshotContainedTrades = false;
            _lastClosePrice = 0;
        }

        #endregion
    }
}
