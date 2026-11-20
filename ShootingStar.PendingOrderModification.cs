using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Pending-order modification and placement handling.
    public partial class ShootingStar
    {
        #region Pending Order Modification and Placement
        private void ModifyTrailingOrder(
            PendingOrder order,
            double target,
            double stopLossPips,
            double takeProfitPips,
            double volumeInUnits,
            TickSnapshot tick)
        {
            target = NormalizePrice(target);
            stopLossPips = NormalizeProtectionPipsUp(stopLossPips);
            takeProfitPips = NormalizeProtectionPipsUp(takeProfitPips);
            volumeInUnits = NormalizeVolume(volumeInUnits);
            if (!IsTradableVolume(volumeInUnits))
            {
                LogVerbose("ORDER", "MODIFICATION_SKIPPED_INVALID_VOLUME", "Pending order {0} modification skipped: calculated volume is outside broker range.", order.Id);
                return;
            }

            StrategyKind strategy = GetStrategy(order.Label);
            double movePercent;
            if (_pendingOrderTrailingStates.TryGetValue(
                    order.Id,
                    out PendingOrderTrailingState existingState) &&
                existingState.Capsule != null)
            {
                movePercent = existingState.Capsule.MovePercent;
            }
            else
            {
                movePercent = strategy == StrategyKind.MeanReversion
                    ? (order.TradeType == TradeType.Buy ? _actualMrDownMovePercent : _actualMrUpMovePercent)
                    : (order.TradeType == TradeType.Buy ? _actualTfLongMovePercent : _actualTfShortMovePercent);
            }
            double requiredPercent = SafeDistanceMultiplier * movePercent;

            double safeSeparationMarketPrice = order.TradeType == TradeType.Buy
                ? tick.Ask
                : tick.Bid;
            if (!PassesOrderFootprintSafeDistanceCheck(
                    order.TradeType,
                    target,
                    stopLossPips,
                    takeProfitPips,
                    requiredPercent,
                    order.Id))
            {
                double effectiveMinimumTrailStepPrice = Math.Max(
                    GetEffectiveMinimumPositionTrailStepPips(tick) * Symbol.PipSize,
                    Symbol.TickSize * 0.5);
                RecordSafeSeparationBlock(
                    order,
                    SafeSeparationFootprintReason,
                    target,
                    safeSeparationMarketPrice,
                    requiredPercent,
                    effectiveMinimumTrailStepPrice);
                return;
            }
            ClearSafeSeparationBlock(order.Id, "SafetyCheckPassed");

            if (!IsPendingTargetDistanceValid(
                    order.TradeType,
                    target,
                    tick,
                    "Pending order " + order.Id + " snapshot modification validation"))
                return;

            if (!ValidateRelativeProtectionDistances(
                    target,
                    stopLossPips,
                    takeProfitPips,
                    "Pending order " + order.Id + " modification"))
            {
                return;
            }

            double requestedModificationVolumeInUnits = volumeInUnits;
            string riskCapContext = "Pending order " + order.Id + " modification";
            PendingOrderRiskCapDecision riskCapDecision;
            bool riskCapSucceeded = TryCapPendingOrderVolumeByEquityRisk(
                requestedModificationVolumeInUnits,
                stopLossPips,
                riskCapContext,
                out volumeInUnits,
                out riskCapDecision);
            if (!riskCapSucceeded)
            {
                PendingOrderCancellationAttempt cancelAttempt =
                    CancelPendingOrderWithRetry(order, "risk-cap-not-met");
                LogNormal("ORDER", "MODIFICATION_BLOCKED_BY_RISK_CAP",
                    "orderId={0} requestedVolume={1} stopLossPips={2} cancellationOutcome={3}",
                    order.Id, requestedModificationVolumeInUnits, stopLossPips,
                    cancelAttempt.Outcome);
                return;
            }

            // Compare final broker-ready values only after risk capping and normalization.
            target = NormalizePrice(target);
            stopLossPips = NormalizeProtectionPipsUp(stopLossPips);
            takeProfitPips = NormalizeProtectionPipsUp(takeProfitPips);
            volumeInUnits = NormalizeVolume(volumeInUnits);
            if (PendingOrderModificationMatchesLiveOrder(
                    order,
                    target,
                    stopLossPips,
                    takeProfitPips,
                    volumeInUnits))
            {
                _pendingOrderNoChangeSkipCount++;
                if (ShouldLogPendingOrderNoChange(order.Id))
                {
                    LogVerbose("ORDER", "MODIFICATION_SKIPPED_NO_CHANGE",
                        "orderId={0} target={1} volume={2} slPips={3} tpPips={4}",
                        order.Id,
                        FormatPrice(target),
                        volumeInUnits,
                        stopLossPips,
                        takeProfitPips);
                }
                return;
            }

            // Refresh market geometry immediately before making the broker request.
            if (!IsPendingTargetDistanceValid(
                    order.TradeType,
                    target,
                    "Pending order " + order.Id + " final live modification validation"))
                return;

            PendingOrderModificationFingerprint fingerprint =
                CreatePendingOrderModificationFingerprint(
                    target,
                    volumeInUnits,
                    stopLossPips,
                    takeProfitPips);
            bool shouldLogDuplicateRejection;
            string rejectedError;
            if (ShouldSuppressRejectedPendingOrderModification(
                    order.Id,
                    fingerprint,
                    out shouldLogDuplicateRejection,
                    out rejectedError))
            {
                if (shouldLogDuplicateRejection)
                {
                    LogVerbose("ORDER", "MODIFICATION_SKIPPED_DUPLICATE_REJECTION",
                        "orderId={0} target={1} volume={2} slPips={3} tpPips={4} priorError={5} duplicateSkips={6}",
                        order.Id,
                        FormatPrice(target),
                        volumeInUnits,
                        stopLossPips,
                        takeProfitPips,
                        rejectedError,
                        _pendingOrderDuplicateRejectionSkipCount);
                }
                return;
            }

            LogRiskCapDecision(riskCapContext, riskCapDecision);
            ProtectionType protectionType = ProtectionType.Relative;
            _pendingOrderModificationAttemptCount++;
            ModificationTelemetry modificationTelemetry = RunTelemetry(
                "BeginOrderModification", () => BeginOrderModification(order), null);
            var result = ModifyPendingOrder(
                order,
                target,
                stopLossPips,
                takeProfitPips,
                protectionType,
                order.ExpirationTime,
                volumeInUnits);
            RunTelemetry("CompleteModificationRequest",
                () => CompleteModificationRequest(modificationTelemetry, result));

            if (result == null || !result.IsSuccessful)
            {
                _pendingOrderModificationFailureCount++;
                string modificationError = result == null
                    ? "null result"
                    : result.Error.ToString();
                RememberRejectedPendingOrderModification(
                    order.Id,
                    fingerprint,
                    modificationError);
                LogError("ORDER", "TRAIL_MODIFICATION_FAILED",
                    "orderId={0} operation=ModifyPendingOrder target={1} volume={2} stopLossPips={3} takeProfitPips={4} brokerError={5} retryScheduled=false",
                    order.Id,
                    target,
                    volumeInUnits,
                    stopLossPips,
                    takeProfitPips,
                    modificationError);
                return;
            }

            _pendingOrderModificationSuccessCount++;
            _rejectedPendingOrderModifications.Remove(order.Id);

            // Local state changes occur only after broker-confirmed success.
            // The modified event and live PendingOrders collection are authoritative;
            // successful modifications must not create provisional placement state.
            MarkSafeDistanceSnapshotDirty();
        }

        private bool HandlePlacementResult(
            TradeResult result,
            string side,
            double target,
            double movementReferencePrice,
            StrategyKind strategy,
            TickSnapshot tick,
            Guid? submissionCorrelationId,
            EntryOriginMode entryOrigin,
            PersistentTradeCapsule capsule)
        {
            if (result == null || !result.IsSuccessful)
            {
                LogError("ORDER", "PLACEMENT_FAILED",
                    "operation=PlaceLimitOrder side={0} target={1} brokerError={2}",
                    side,
                    target,
                    result == null ? "null result" : result.Error.ToString());
                return false;
            }
            if (result.PendingOrder == null)
            {
                LogError("ORDER", "PLACEMENT_UNCONFIRMED",
                    "{0} placement reported success but returned no pending order; the entry allowance remains available",
                    side);
                return false;
            }

            return HandleAcceptedPendingOrder(
                result.PendingOrder,
                side,
                target,
                movementReferencePrice,
                strategy,
                tick,
                submissionCorrelationId,
                "placementResult",
                entryOrigin,
                capsule);
        }

        private bool HandleAcceptedPendingOrder(
            PendingOrder pendingOrder,
            string side,
            double target,
            double movementReferencePrice,
            StrategyKind strategy,
            TickSnapshot tick,
            Guid? submissionCorrelationId,
            string idLink,
            EntryOriginMode entryOrigin,
            PersistentTradeCapsule capsule)
        {
            RegisterAndReconcileProvisionalSafeDistancePendingOrder(pendingOrder);
            RunTelemetry("OrderAccepted", () =>
            {
                VerifyPersistentCapsuleComment(
                    submissionCorrelationId,
                    capsule,
                    pendingOrder.Comment,
                    "order",
                    pendingOrder.Id);
                if (PendingOrders.Any(order => order.Id == pendingOrder.Id))
                    ObserveOrderModification(pendingOrder, true);
                LogVerbose("TRADE_LIFECYCLE", "ORDER_ACCEPTED",
                    "schemaVersion=5 orderId={0} symbol={1} side={2} target={3} volume={4:R} label={5} entryOrigin={6} submissionCorrelationId={7} movementReference={8} dTrail={9} idLink={10}",
                    pendingOrder.Id, pendingOrder.SymbolName, pendingOrder.TradeType,
                    FormatPrice(pendingOrder.TargetPrice), pendingOrder.VolumeInUnits,
                    pendingOrder.Label,
                    entryOrigin,
                    FormatSubmissionCorrelationId(submissionCorrelationId),
                    FormatPrice(movementReferencePrice), FormatPrice(Math.Abs(movementReferencePrice - target)),
                    idLink);
            });

            if (submissionCorrelationId.HasValue)
                SetTradeIdeaEntryOrigin(
                    submissionCorrelationId.Value,
                    entryOrigin);
            LogNormal("ORDER", "PLACEMENT_ACCEPTED",
                "orderId={0} side={1} label={2} entryOrigin={3}",
                pendingOrder.Id,
                pendingOrder.TradeType,
                pendingOrder.Label,
                entryOrigin);
            RegisterPendingOrderTrailingState(
                pendingOrder,
                target,
                movementReferencePrice,
                strategy,
                tick.UtcHourKey,
                tick.VolatilityFactor,
                CreatePendingOrderEvidence(
                    LifecycleContinuityEvidence.Continuous,
                    SegmentProvenanceEvidence.BrokerObserved),
                entryOrigin,
                capsule);

            return true;
        }
        private void RegisterPendingOrderTrailingState(
            PendingOrder order,
            double originalTargetPrice,
            double movementReferencePrice,
            StrategyKind strategy)
        {
            DateTime utcNow = Server.TimeInUtc;
            RegisterPendingOrderTrailingState(
                order,
                originalTargetPrice,
                movementReferencePrice,
                strategy,
                GetUtcHourKey(utcNow),
                _utcHourlyVolatilityFactors[utcNow.Hour]);
        }
        private void RegisterPendingOrderTrailingState(
            PendingOrder order,
            double originalTargetPrice,
            double movementReferencePrice,
            StrategyKind strategy,
            int utcHourKey,
            double volatilityFactor,
            TradeEvidence evidence = null,
            EntryOriginMode entryOrigin = EntryOriginMode.Unattributed,
            PersistentTradeCapsule capsule = null)
        {
            bool capsuleRecovered = capsule != null ||
                TryGetCapsuleFromOrder(order, out capsule);
            bool capsuleRecoveryFailed = !capsuleRecovered &&
                HasPersistentCapsuleMarker(order.Comment);
            TradeEvidence resolvedEvidence = evidence ??
                GetPendingOrderEvidence(order.Id) ??
                CreateUnknownTradeEvidence();
            SetPendingOrderEvidence(order.Id, resolvedEvidence);
            _pendingOrderTrailingStates[order.Id] = new PendingOrderTrailingState
            {
                Strategy = capsule == null ? strategy : capsule.Strategy,
                Capsule = capsule == null ? null : capsule.Copy(),
                CapsuleRecoveryFailed = capsuleRecoveryFailed,
                EntryOrigin = entryOrigin,
                Evidence = resolvedEvidence.Copy(),
                LastUtcHourKey = utcHourKey,
                LastVolatilityFactor = volatilityFactor,
                OriginalVolatilityFactor = volatilityFactor,
                OriginalStopLossPips = capsule != null
                    ? capsule.OriginalStopLossPips
                    : order.StopLossPips.HasValue
                        ? NormalizeProtectionPips(order.StopLossPips.Value)
                        : double.NaN,
                OriginalTakeProfitPips = capsule != null
                    ? capsule.OriginalTakeProfitPips
                    : order.TakeProfitPips.HasValue
                        ? NormalizeProtectionPips(order.TakeProfitPips.Value)
                        : double.NaN,
                TradeType = order.TradeType,
                VolumeInUnits = order.VolumeInUnits,
                Label = order.Label,
                OriginalTargetPrice = originalTargetPrice,
                MovementReferencePrice = movementReferencePrice
            };
            SetPendingOrderEntryOrigin(order.Id, entryOrigin);
        }

        #endregion


    }
}
