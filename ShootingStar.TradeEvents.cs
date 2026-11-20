using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Position and pending-order event handling and directional allocation.
    public partial class ShootingStar
    {
        #region Position Events and Fixed Directional Allocation
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            try
            {
                OnPositionOpenedCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPositionOpened", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPositionOpenedCore(PositionOpenedEventArgs args)
        {
            Position position = args.Position;
            if (position.SymbolName == Symbol.Name)
                MarkSafeDistanceSnapshotDirty();

            if (!IsBotPosition(position))
                return;

            RegisterRolloverRestoredPosition(position);
            RunTelemetry("ObservePositionModification", () => ObservePositionModification(position, true));
            LogNormal("TRADE", "OPENED",
                "side={0} positionId={1} entry={2} volume={3:R} sl={4} tp={5} label={6} entryOrigin={7}{8}",
                position.TradeType == TradeType.Buy ? "Long" : "Short",
                position.Id,
                FormatPrice(position.EntryPrice),
                position.VolumeInUnits,
                position.StopLoss.HasValue ? FormatPrice(position.StopLoss.Value) : "none",
                position.TakeProfit.HasValue ? FormatPrice(position.TakeProfit.Value) : "none",
                position.Label,
                GetPositionEntryOrigin(position.Id),
                FormatKnownTradeIdeaField(GetPositionTradeIdeaId(position.Id)));

            PendingOrderTrailingState originatingOrderState =
                TakeOriginatingPendingOrderState(position);
            if (!_positionStates.TryGetValue(position.Id, out PositionState existingState))
            {
                InitializePositionState(position, originatingOrderState, true);
            }
            else
            {
                if (originatingOrderState != null)
                    SetPositionEntryOrigin(
                        position.Id,
                        originatingOrderState.EntryOrigin);
                else if (_ordinaryOrderSubmissionInProgress)
                    SetPositionEntryOrigin(
                        position.Id,
                        _activeOrderSubmissionOrigin);
                if (originatingOrderState?.Evidence != null)
                {
                    AddPositionEvidence(
                        position.Id,
                        AddPositionAttributionEvidence(
                            originatingOrderState.Evidence,
                            GetAccountPositionAttribution()));
                }
                LogVerbose("TRADE", "OPEN_STATE_ALREADY_INITIALIZED",
                    "positionId={0} logicalTradeId={1} rolloverSegment={2} carriedNetProfit={3:F2}",
                    position.Id,
                    existingState.LogicalTradeId,
                    existingState.RolloverSegment,
                    existingState.CarriedNetProfit);
            }
            if (_activePositionRestorationRecoveryId.HasValue &&
                _positionStates.ContainsKey(position.Id))
            {
                PositionSnapshot restorationSnapshot = _positionSnapshots.FirstOrDefault(
                    snapshot =>
                        snapshot.RecoveryId == _activePositionRestorationRecoveryId.Value);
                if (restorationSnapshot != null)
                {
                    restorationSnapshot.RestoredObjectId = position.Id;
                    PreserveRestoredPositionRolloverAnchors(
                        position, restorationSnapshot);
                }
            }
            if (_positionStates.TryGetValue(position.Id, out PositionState openedState))
            {
                RunTelemetry("PositionOpenedLog", () => LogVerbose("TRADE_LIFECYCLE", "POSITION_OPENED",
                    "schemaVersion=3 logicalTradeId={0} positionId={1} segment={2} symbol={3} side={4} entryPrice={5} volume={6:R} tradeIdeaId={7} lifecycleContinuity={8} segmentProvenance={9} positionAttribution={10}",
                    openedState.LogicalTradeId, position.Id, openedState.RolloverSegment,
                    position.SymbolName, position.TradeType, FormatPrice(position.EntryPrice), position.VolumeInUnits,
                    FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)),
                    openedState.Evidence == null ? "unknown" : openedState.Evidence.LifecycleContinuity.ToString(),
                    openedState.Evidence == null ? "unknown" : openedState.Evidence.SegmentProvenance.ToString(),
                    openedState.Evidence == null ? "unknown" : openedState.Evidence.PositionAttribution.ToString()));
            }
            if (_pendingCircuitBreakerFillPositionIds.Remove(position.Id) &&
                _positionStates.TryGetValue(position.Id, out PositionState filledState))
            {
                MarkCircuitBreakerOriginalFill(position, filledState);
                MarkScaleUpOriginalFill(position, filledState);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            try
            {
                OnPositionClosedCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPositionClosed", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPositionClosedCore(PositionClosedEventArgs args)
        {
            Position position = args.Position;
            if (position.SymbolName == Symbol.Name)
                MarkSafeDistanceSnapshotDirty();
            if (!IsBotPosition(position))
                return;

            Position stillOpenPosition = Positions.FindById(position.Id);
            _positionStates.TryGetValue(position.Id, out PositionState partialCloseState);
            double volumeTolerance = Math.Max(1.0, Symbol.VolumeInUnitsStep * 0.5);
            bool remainingVolumeReduced = stillOpenPosition != null &&
                stillOpenPosition.VolumeInUnits <
                    position.VolumeInUnits - volumeTolerance;
            bool ladderClosePending = partialCloseState != null &&
                partialCloseState.PendingLadderCloseVolume > 0.0;
            if (stillOpenPosition != null &&
                (remainingVolumeReduced || ladderClosePending))
            {
                RunTelemetry("FlushPositionPartialClose", () =>
                    FlushModificationTelemetry(_positionModificationTelemetry, position.Id, "position", "partialClose", false));
                if (partialCloseState == null)
                {
                    InitializePositionState(stillOpenPosition);
                    _positionStates.TryGetValue(position.Id, out partialCloseState);
                }
                if (partialCloseState != null)
                {
                    if (partialCloseState.PendingLadderCloseVolume <=
                        Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6))
                    {
                        partialCloseState.PendingLadderCloseVolume +=
                            Math.Max(0.0, position.VolumeInUnits - stillOpenPosition.VolumeInUnits);
                    }
                    ReconcilePositionLadderHistory(stillOpenPosition, partialCloseState);
                }
                LogVerbose("TRADE", "PARTIAL_CLOSE_EVENT",
                    "positionId={0} remainingVolume={1:F0} outcome=PositionStateRetained",
                    position.Id, stillOpenPosition.VolumeInUnits);
                return;
            }

            RunTelemetry("PositionClosedTelemetry", () =>
            {
                FlushModificationTelemetry(_positionModificationTelemetry, position.Id, "position", "closed", true);
                LogVerbose("TRADE_LIFECYCLE", "POSITION_CLOSED",
                    "schemaVersion=2 tradeIdeaId={0} positionId={1} logicalTradeId={2} closeReason={3}",
                    FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)), position.Id,
                    partialCloseState == null ? "notTracked" : partialCloseState.LogicalTradeId.ToString(),
                    args.Reason);
                StartClosedPositionDealObservation(position.Id);
            });

            // Statistics failures are isolated so they can never prevent the existing
            // position, protection-recovery, or rollover cleanup.
            try
            {
                TryTripCircuitBreaker(position, partialCloseState, args.Reason);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException(
                    "OnPositionClosed/TryTripCircuitBreaker",
                    exception,
                    CallbackFailurePolicy.Continue);
            }

            try
            {
                RecordClosedPositionStatistics(position, args.Reason);
            }
            catch (Exception exception)
            {
                _tradeStatisticsInvalidRecords++;
                HandleUnexpectedCallbackException(
                    "OnPositionClosed/RecordClosedPositionStatistics",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
            finally
            {
                if (_positionProtectionRecoveryStates.TryGetValue(
                        position.Id,
                        out PositionProtectionRecoveryState recovery) &&
                    recovery.ClosureOnly)
                {
                    ClearWarning("unprotected-position-close-" + position.Id,
                        "PROTECTION", "UNPROTECTED_POSITION_CLOSE_COMPLETE",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "positionId={0} closeAttempts={1} outcome=PositionClosed",
                            position.Id, recovery.CloseAttempts));
                    recovery.CloseRequestAccepted = false;
                    recovery.CloseRequestAcceptedUtc = DateTime.MinValue;
                }

                _positionStates.Remove(position.Id);
                _positionProtectionRecoveryStates.Remove(position.Id);
                _positionTradeIdeaIds.Remove(position.Id);
                _positionEvidenceById.Remove(position.Id);
                _ambiguousIdeaPositionIds.Remove(position.Id);
                _pendingCircuitBreakerFillPositionIds.Remove(position.Id);
                ForgetRolloverPosition(position.Id);
                ForgetPositionLadderState(position.Id);
                ForgetCircuitBreakerPositionState(position.Id);
                ForgetScaleUpPositionState(position.Id);
            }
        }
        private void LogPendingOrderModificationSummary()
        {
            LogNormal("ORDER", "ORDER_MODIFICATION_SUMMARY",
                "attempts={0} successes={1} failures={2} noChangeSkips={3} duplicateRejectionSkips={4} riskConversionApiCalls={5} riskConversionCoefficientRefreshes={6} conversionMode=BrokerOnly conversionFallbackEnabled=false",
                _pendingOrderModificationAttemptCount,
                _pendingOrderModificationSuccessCount,
                _pendingOrderModificationFailureCount,
                _pendingOrderNoChangeSkipCount,
                _pendingOrderDuplicateRejectionSkipCount,
                _riskConversionApiCallCount,
                _riskConversionCoefficientRefreshCount);
        }

        private void LogSafeSeparationSuppressionSummary()
        {
            LogNormal("ORDER", "SAFE_SEPARATION_SUMMARY",
                "firstBlocks={0} blockedEvaluations={1} duplicatesSuppressed={2} periodicSummaries={3} materialChanges={4} cleared={5} lifecycleEnded={6} stalePrunes={7} activeAtStop={8}",
                _safeSeparationFirstBlockCount,
                _safeSeparationBlockedEvaluationCount,
                _safeSeparationDuplicateSuppressedCount,
                _safeSeparationPeriodicSummaryCount,
                _safeSeparationMaterialChangeCount,
                _safeSeparationClearedCount,
                _safeSeparationLifecycleEndedCount,
                _safeSeparationStatePrunedCount,
                _safeSeparationLogStates.Count);
        }
        private void LogProvisionalSafeDistanceSummary()
        {
            long activeAtStop = _provisionalSafeDistancePendingOrders.Count;
            long resolved = _provisionalSafeDistanceLiveSettlementCount +
                            _provisionalSafeDistanceCancellationCount +
                            _provisionalSafeDistanceStaleCleanupCount +
                            _provisionalSafeDistanceExpiryCount;
            long accounted = resolved + activeAtStop;
            LogNormal("ORDER", "PROVISIONAL_SUMMARY",
                "registrations={0} liveSettlements={1} normalizedSettlements={2} cancellations={3} staleCleanups={4} expiries={5} activeAtStop={6} accounted={7} balanced={8}",
                _provisionalSafeDistanceRegistrationCount,
                _provisionalSafeDistanceLiveSettlementCount,
                _provisionalSafeDistanceNormalizedSettlementCount,
                _provisionalSafeDistanceCancellationCount,
                _provisionalSafeDistanceStaleCleanupCount,
                _provisionalSafeDistanceExpiryCount,
                activeAtStop,
                accounted,
                accounted == _provisionalSafeDistanceRegistrationCount);
        }

        private void OnPendingOrderCreated(PendingOrderCreatedEventArgs args)
        {
            try
            {
                OnPendingOrderCreatedCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPendingOrderCreated", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPendingOrderCreatedCore(PendingOrderCreatedEventArgs args)
        {
            PendingOrder order = args.PendingOrder;
            if (order.SymbolName == Symbol.Name)
            {
                ResolveProvisionalSafeDistancePendingOrder(
                    order.Id,
                    ProvisionalSafeDistanceResolution.CreatedEvent);
                MarkSafeDistanceSnapshotDirty();
            }
            if (IsBotOrder(order))
            {
                RegisterRolloverRestoredOrder(order);
                RunTelemetry("ObserveOrderCreated", () => ObserveOrderModification(order, true));
            }
        }

        private void OnPendingOrderModified(PendingOrderModifiedEventArgs args)
        {
            try
            {
                OnPendingOrderModifiedCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPendingOrderModified", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPendingOrderModifiedCore(PendingOrderModifiedEventArgs args)
        {
            PendingOrder order = args.PendingOrder;
            RunTelemetry("ObserveOrderModified", () => ObserveOrderModification(order, false));
            if (order.SymbolName == Symbol.Name)
            {
                ResolveProvisionalSafeDistancePendingOrder(
                    order.Id,
                    ProvisionalSafeDistanceResolution.ModifiedEvent);
                MarkSafeDistanceSnapshotDirty();
            }
            _pendingOrderNoChangeLogTimes.Remove(order.Id);
            _rejectedPendingOrderModifications.Remove(order.Id);
        }

        private void OnPendingOrderCancelled(PendingOrderCancelledEventArgs args)
        {
            try
            {
                OnPendingOrderCancelledCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPendingOrderCancelled", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPendingOrderCancelledCore(PendingOrderCancelledEventArgs args)
        {
            PendingOrder order = args.PendingOrder;
            CompletePendingOrderCancellationObjective(
                order.Id,
                "Cancelled");
            if (order.SymbolName == Symbol.Name)
            {
                ResolveProvisionalSafeDistancePendingOrder(
                    order.Id,
                    ProvisionalSafeDistanceResolution.CancelledEvent);
                MarkSafeDistanceSnapshotDirty();
            }
            if (!IsBotOrder(order))
                return;

            RunTelemetry("OrderCancelledTelemetry", () =>
            {
                FlushModificationTelemetry(_orderModificationTelemetry, order.Id, "order", "cancelled", true);
                Guid? submissionCorrelationId =
                    GetSubmissionCorrelationId(order.Id);
                string cancelReason = _botCancelReasons.TryGetValue(order.Id, out string botReason)
                    ? botReason : "external";
                LogVerbose("TRADE_LIFECYCLE", "ORDER_CANCELLED",
                    "schemaVersion=4 orderId={0} reason={1} cancelReason={2} submissionCorrelationId={3} outcome=cancelledWithoutFill",
                    order.Id, args.Reason, cancelReason,
                    FormatSubmissionCorrelationId(submissionCorrelationId));
                RecordOrderCancellationDuringSubmission(order.Id);
            });
            _botCancelReasons.Remove(order.Id);
            _pendingOrderTrailingStates.Remove(order.Id);
            _pendingOrderEvidenceById.Remove(order.Id);
            EndSafeSeparationBlock(order.Id, "Cancelled", false);
            _pendingOrderNoChangeLogTimes.Remove(order.Id);
            _rejectedPendingOrderModifications.Remove(order.Id);
            ForgetRolloverOrder(order.Id);
        }

        private void OnPendingOrderFilled(PendingOrderFilledEventArgs args)
        {
            try
            {
                if (!IsBotOrder(args.PendingOrder))
                    return;
                EntryOriginMode entryOrigin =
                    GetPendingOrderEntryOrigin(args.PendingOrder.Id);
                SetPositionEntryOrigin(args.Position.Id, entryOrigin);
                RecordFitnessPendingOrderFill(args.PendingOrder, entryOrigin);
                RemovePendingOrderCancellationRetryState(
                    args.PendingOrder.Id,
                    true,
                    "OrderFilled");
                _botCancelReasons.Remove(args.PendingOrder.Id);
                RegisterRolloverRestoredPositionFromFilledOrder(
                    args.PendingOrder,
                    args.Position);
                TradeEvidence orderEvidence =
                    GetPendingOrderEvidence(args.PendingOrder.Id) ??
                    GetPositionEvidence(args.Position.Id);
                if (orderEvidence == null && _ordinaryOrderSubmissionInProgress)
                {
                    _fillsDuringOrderSubmission[args.PendingOrder.Id] = args.Position.Id;
                }
                else if (orderEvidence != null)
                {
                    AddPositionEvidence(
                        args.Position.Id,
                        AddPositionAttributionEvidence(
                            orderEvidence,
                            GetAccountPositionAttribution()));
                }
                RunTelemetry("OrderFilledTelemetry", () =>
                {
                    Guid? submissionCorrelationId =
                        GetSubmissionCorrelationId(args.PendingOrder.Id);
                    FlushModificationTelemetry(_orderModificationTelemetry, args.PendingOrder.Id, "order", "filled", true);
                    string fillAssociation = RecordTradeIdeaFill(args.PendingOrder.Id, args.Position.Id);
                    string tradeIdeaId = submissionCorrelationId.HasValue
                        ? submissionCorrelationId.Value.ToString()
                        : fillAssociation == "deferredLink"
                            ? "pendingCorrelationResolution"
                            : "untracked";
                    string logicalTradeId = _positionStates.TryGetValue(args.Position.Id, out PositionState state)
                        ? state.LogicalTradeId.ToString()
                        : "notYetCreated";
                    LogVerbose("TRADE_LIFECYCLE", "ORDER_FILLED",
                        "schemaVersion=6 orderId={0} positionId={1} logicalTradeId={2} entryOrigin={3} submissionCorrelationId={4} tradeIdeaId={5} association={6} lifecycleContinuity={7} segmentProvenance={8} positionAttribution={9}",
                        args.PendingOrder.Id,
                        args.Position.Id,
                        logicalTradeId,
                        entryOrigin,
                        FormatSubmissionCorrelationId(submissionCorrelationId),
                        tradeIdeaId,
                        fillAssociation,
                        GetPositionEvidence(args.Position.Id)?.LifecycleContinuity.ToString() ?? "unknown",
                        GetPositionEvidence(args.Position.Id)?.SegmentProvenance.ToString() ?? "unknown",
                        GetPositionEvidence(args.Position.Id)?.PositionAttribution.ToString() ?? "unknown");
                });
                if (_positionStates.TryGetValue(args.Position.Id, out PositionState positionState))
                {
                    MarkCircuitBreakerOriginalFill(args.Position, positionState);
                    MarkScaleUpOriginalFill(args.Position, positionState);
                }
                else
                {
                    _pendingCircuitBreakerFillPositionIds.Add(args.Position.Id);
                    PersistCircuitBreakerFill(args.Position);
                    PersistScaleUpFill(args.Position);
                }
                if (orderEvidence != null)
                    _pendingOrderEvidenceById.Remove(args.PendingOrder.Id);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPendingOrderFilled", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void PersistCircuitBreakerFill(Position position)
        {
            bool hasOriginalStopLoss = position.StopLoss.HasValue &&
                IsFinitePositive(position.StopLoss.Value);
            bool eligible = IsCircuitBreakerEligibleOriginalFill(
                true,
                false,
                hasOriginalStopLoss);
            string value = string.Join(
                "|",
                "1",
                eligible ? "1" : "0",
                eligible ? "1" : "0",
                hasOriginalStopLoss
                    ? position.StopLoss.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "0");
            LocalStorage.SetString(
                GetCircuitBreakerPositionStorageKey(position.Id),
                value,
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void PersistScaleUpFill(Position position)
        {
            bool hasOriginalTakeProfit = position.TakeProfit.HasValue &&
                IsFinitePositive(position.TakeProfit.Value);
            bool hasOriginalStopLoss = position.StopLoss.HasValue &&
                IsFinitePositive(position.StopLoss.Value);
            bool eligible = IsCircuitBreakerEligibleOriginalFill(
                true,
                _rolloverRestoredPositionRecoveryIds.ContainsKey(position.Id),
                hasOriginalTakeProfit && hasOriginalStopLoss);
            string value = string.Join(
                "|",
                "2",
                eligible ? "1" : "0",
                hasOriginalTakeProfit
                    ? position.TakeProfit.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "0",
                hasOriginalStopLoss
                    ? position.StopLoss.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "0");
            LocalStorage.SetString(
                GetScaleUpPositionStorageKey(position.Id),
                value,
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private double GetEffectiveSideAllocationPercent(TradeType tradeType)
        {
            return tradeType == TradeType.Buy
                ? _effectiveDirectionalAllocation
                : 100.0 - _effectiveDirectionalAllocation;
        }

        private bool IsSideAllocated(TradeType tradeType)
        {
            return GetEffectiveSideAllocationPercent(tradeType) > 0.0;
        }

        private double GetAllocationAdjustedVolumeInUnits(TradeType tradeType)
        {
            double sideFraction = GetEffectiveSideAllocationPercent(tradeType) / 100.0;
            if (sideFraction <= 0.0)
                return 0.0;

            double adjustedTotalLots = TotalTradingVolumeInLots / GlobalVolatility / _normalizedAtrRatio;
            return NormalizeVolume(Symbol.QuantityToVolumeInUnits(adjustedTotalLots * sideFraction));
        }
        #endregion


    }
}
