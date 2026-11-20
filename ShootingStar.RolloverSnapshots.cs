using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Rollover exposure snapshots and restoration tracking.
    public partial class ShootingStar
    {
        #region Rollover Snapshot Capture and Tracking
        private void CaptureRolloverSnapshot()
        {
            _positionSnapshots.Clear();
            _orderSnapshots.Clear();
            _rolloverContext.SavedTicksUntilMaintenance = _ticksUntilMaintenance;
            _lastClosePrice = (Symbol.Bid + Symbol.Ask) / 2.0;

            CaptureBotPositions(_rolloverPositionSnapshot);
            foreach (var position in _rolloverPositionSnapshot)
            {
                // PositionState preserves the opening SL and TP even after the live
                // protection levels have been moved by trailing or manual modification.
                _positionStates.TryGetValue(position.Id, out PositionState openingState);
                if (openingState != null)
                    ReconcilePositionLadderHistory(position, openingState);
                double originalEntry = openingState != null && IsFinitePositive(openingState.RolloverAnchorEntryPrice)
                    ? openingState.RolloverAnchorEntryPrice : position.EntryPrice;
                RolloverProtectionState protectionState = ClassifyRolloverProtectionState(position.TradeType, originalEntry, position.StopLoss, position.TakeProfit, GetProtectionComparisonTolerance());

                PositionSnapshot snapshot = new PositionSnapshot
                {
                    Strategy = openingState == null
                        ? GetStrategy(position.Label)
                        : openingState.Strategy,
                    Capsule = openingState?.Capsule == null
                        ? null
                        : openingState.Capsule.Copy(),
                    CapsuleRecoveryFailed = openingState != null &&
                        openingState.CapsuleRecoveryFailed,
                    EntryOrigin = openingState != null
                        ? openingState.EntryOrigin
                        : GetPositionEntryOrigin(position.Id),
                    TradeType = position.TradeType,
                    SourcePositionId = position.Id,
                    TradeIdeaId = GetPositionTradeIdeaId(position.Id),
                    Evidence = openingState != null && openingState.Evidence != null
                        ? openingState.Evidence.Copy()
                        : GetPositionEvidence(position.Id) ?? CreateUnknownTradeEvidence(),
                    LogicalTradeId = openingState != null ? openingState.LogicalTradeId : Guid.NewGuid(),
                    LogicalEntryUtc = openingState != null && openingState.LogicalEntryUtc != DateTime.MinValue
                        ? openingState.LogicalEntryUtc
                        : position.EntryTime,
                    RolloverSegment = openingState != null ? openingState.RolloverSegment : 0,
                    RestorationCount = openingState != null ? openingState.RestorationCount : 0,
                    CarriedGrossProfit = openingState != null ? openingState.CarriedGrossProfit : 0.0,
                    CarriedNetProfit = openingState != null ? openingState.CarriedNetProfit : 0.0,
                    CarriedCommission = openingState != null ? openingState.CarriedCommission : 0.0,
                    CarriedSwap = openingState != null ? openingState.CarriedSwap : 0.0,
                    CarriedPips = openingState != null ? openingState.CarriedPips : 0.0,
                    CarriedActiveDurationSeconds = openingState != null ? openingState.CarriedActiveDurationSeconds : 0.0,
                    CompletedTakeProfitLevels = openingState != null
                        ? openingState.CompletedTakeProfitLevels
                        : 0,
                    CompletedStopLossLevels = openingState != null
                        ? openingState.CompletedStopLossLevels
                        : 0,
                    PendingLadderCloseVolume = openingState != null
                        ? openingState.PendingLadderCloseVolume
                        : 0.0,
                    LadderRecoveryAmbiguous = openingState != null &&
                        openingState.LadderRecoveryAmbiguous,
                    ScaleUpEligible = openingState != null && openingState.ScaleUpEligible,
                    VolumeInUnits = position.VolumeInUnits,
                    Label = position.Label,
                    EntryPrice = openingState != null && IsFinitePositive(openingState.RolloverAnchorEntryPrice)
                        ? openingState.RolloverAnchorEntryPrice
                        : position.EntryPrice,
                    PhysicalEntryPrice = position.EntryPrice,
                    ProtectionState = protectionState,
                    OriginalProtectionKnown = openingState != null && openingState.OriginalProtectionKnown,
                    OriginalStopLoss = openingState != null && IsFinitePositive(openingState.OriginalStopLoss)
                        ? openingState.OriginalStopLoss
                        : (double?)null,
                    OriginalTakeProfit = openingState != null && IsFinitePositive(openingState.OriginalTakeProfit)
                        ? openingState.OriginalTakeProfit
                        : (double?)null,
                    StopLoss = position.StopLoss,
                    TakeProfit = position.TakeProfit,
                    StopLossPips = position.StopLoss.HasValue
                        ? NormalizeProtectionPips(Math.Abs(position.EntryPrice - position.StopLoss.Value) / Symbol.PipSize)
                        : (double?)null,
                    TakeProfitPips = position.TakeProfit.HasValue
                        ? NormalizeProtectionPips(Math.Abs(position.TakeProfit.Value - position.EntryPrice) / Symbol.PipSize)
                        : (double?)null
                };
                if (openingState != null)
                {
                    foreach (int closingDealId in openingState.AccountedClosingDealIds)
                        snapshot.AccountedClosingDealIds.Add(closingDealId);
                }
                _positionSnapshots.Add(snapshot);
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_CAPTURED",
                    "schemaVersion=3 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} segment={4} lifecycleContinuity={5} segmentProvenance={6} positionAttribution={7}",
                    FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                    snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.RolloverSegment,
                    snapshot.Evidence.LifecycleContinuity,
                    snapshot.Evidence.SegmentProvenance,
                    snapshot.Evidence.PositionAttribution);
                if (IsCrossedProtectionState(protectionState))
                    LogNormal("ROLLOVER", "CROSSED_PROTECTION_CAPTURED", "positionId={0} side={1} state={2} entry={3} SL={4} TP={5}", position.Id, position.TradeType, protectionState, FormatPrice(position.EntryPrice), position.StopLoss.HasValue ? FormatPrice(position.StopLoss.Value) : "none", position.TakeProfit.HasValue ? FormatPrice(position.TakeProfit.Value) : "none");
            }

            CaptureBotPendingOrders(_rolloverPendingOrderSnapshot);
            foreach (var order in _rolloverPendingOrderSnapshot)
            {
                RunTelemetry("FlushOrderRollover", () =>
                    FlushModificationTelemetry(_orderModificationTelemetry, order.Id, "order", "rollover", false));
                PendingOrderTrailingState pendingState;
                bool hasPendingState = _pendingOrderTrailingStates.TryGetValue(order.Id, out pendingState);
                PersistentTradeCapsule capsule = hasPendingState
                    ? pendingState.Capsule
                    : null;
                bool capsuleRecoveryFailed = hasPendingState &&
                    pendingState.CapsuleRecoveryFailed;
                if (capsule == null && !capsuleRecoveryFailed)
                {
                    bool capsuleRecovered = TryGetCapsuleFromOrder(order, out capsule);
                    capsuleRecoveryFailed = !capsuleRecovered &&
                        HasPersistentCapsuleMarker(order.Comment);
                }
                PendingOrderSnapshot snapshot = new PendingOrderSnapshot
                {
                    Strategy = capsule != null
                        ? capsule.Strategy
                        : hasPendingState
                            ? pendingState.Strategy
                            : GetStrategy(order.Label),
                    Capsule = capsule == null ? null : capsule.Copy(),
                    CapsuleRecoveryFailed = capsuleRecoveryFailed,
                    EntryOrigin = hasPendingState ? pendingState.EntryOrigin : GetPendingOrderEntryOrigin(order.Id),
                    TradeType = order.TradeType,
                    OrderType = order.OrderType,
                    SourceOrderId = order.Id,
                    Evidence = hasPendingState && pendingState.Evidence != null
                        ? pendingState.Evidence.Copy()
                        : GetPendingOrderEvidence(order.Id) ?? CreateUnknownTradeEvidence(),
                    SubmissionCorrelationId = RunTelemetry(
                        "GetSubmissionCorrelationId",
                        () => GetSubmissionCorrelationId(order.Id),
                        (Guid?)null),
                    VolumeInUnits = order.VolumeInUnits,
                    TargetPrice = order.TargetPrice,
                    OriginalTargetPrice = capsule != null
                        ? capsule.OriginalTargetPrice
                        : hasPendingState
                            ? pendingState.OriginalTargetPrice
                            : order.TargetPrice,
                    MovementReferencePrice = capsule != null
                        ? capsule.MovementReferencePrice
                        : hasPendingState
                            ? pendingState.MovementReferencePrice
                            : GetMovementReferencePrice(GetStrategy(order.Label), order.TradeType),
                    Label = order.Label,
                    StopLoss = GetPendingStopLossPrice(order),
                    TakeProfit = GetPendingTakeProfitPrice(order),
                    StopLossPips = order.StopLossPips.HasValue
                        ? NormalizeProtectionPips(order.StopLossPips.Value)
                        : (double?)null,
                    TakeProfitPips = order.TakeProfitPips.HasValue
                        ? NormalizeProtectionPips(order.TakeProfitPips.Value)
                        : (double?)null,
                    ExpirationTime = order.ExpirationTime
                };
                _orderSnapshots.Add(snapshot);
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_CAPTURED",
                    "schemaVersion=5 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} lifecycleContinuity={3} segmentProvenance={4}",
                    FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId),
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId,
                    snapshot.Evidence.LifecycleContinuity,
                    snapshot.Evidence.SegmentProvenance);
            }

            _rolloverCapturedPositionSnapshots = _positionSnapshots.Count;
            _rolloverCapturedOrderSnapshots = _orderSnapshots.Count;
            _rolloverRestoredPositionSnapshots = 0;
            _rolloverRestoredOrderSnapshots = 0;
            _rolloverSkippedPositionSnapshots = 0;
            _rolloverSkippedOrderSnapshots = 0;
            _rolloverFailedPositionSnapshots = 0;
            _rolloverFailedOrderSnapshots = 0;
            _rolloverFinalizedSkippedLogicalTrades = 0;
            _rolloverFinalizedFailedLogicalTrades = 0;

            _rolloverContext.SnapshotContainedTrades = _positionSnapshots.Count > 0 || _orderSnapshots.Count > 0;
            if (_rolloverContext.SnapshotContainedTrades)
            {
                LogNormal("ROLLOVER", "SNAPSHOT_CAPTURED", "positions={0} orders={1} windowEnd={2:yyyy-MM-dd HH:mm} UTC",
                    _positionSnapshots.Count, _orderSnapshots.Count, _cachedRolloverEndUtc);
            }
            else
            {
                LogVerbose("ROLLOVER", "NOOP", "no bot trades to flatten");
            }
        }

        private void RegisterRolloverRestoredPosition(Position position)
        {
            if (position == null ||
                _rolloverContext.Phase != RolloverPhase.Restoring ||
                !IsBotPosition(position))
            {
                return;
            }

            Guid? recoveryId = _activePositionRestorationRecoveryId;
            if (recoveryId.HasValue)
            {
                _rolloverRestoredPositionRecoveryIds[position.Id] = recoveryId.Value;
                PositionSnapshot activeSnapshot = _positionSnapshots.FirstOrDefault(
                    snapshot => snapshot.RecoveryId == recoveryId.Value);
                if (activeSnapshot != null)
                {
                    activeSnapshot.RestoredObjectId = position.Id;
                    AddPositionEvidence(
                        position.Id,
                        AddRolloverReconstructionEvidence(activeSnapshot.Evidence));
                }
                else
                {
                    AddPositionEvidence(position.Id, CreateUnknownTradeEvidence());
                }
            }
        }

        private void RegisterRolloverRestoredPositionFromFilledOrder(
            PendingOrder sourceOrder,
            Position resultingPosition)
        {
            if (sourceOrder == null ||
                resultingPosition == null ||
                !IsBotOrder(sourceOrder) ||
                !IsBotPosition(resultingPosition))
            {
                return;
            }

            if (!_rolloverRestoredOrderRecoveryIds.TryGetValue(
                    sourceOrder.Id,
                    out Guid recoveryId))
            {
                if (_rolloverContext.Phase == RolloverPhase.Restoring)
                    _unregisteredRolloverOrderFills[sourceOrder.Id] = resultingPosition.Id;
                return;
            }

            if (!_restoredOrderSnapshotsByRecoveryId.TryGetValue(
                    recoveryId,
                    out PendingOrderSnapshot snapshot) ||
                !IsRestoredPendingOrderIdMatch(
                    sourceOrder.Id,
                    snapshot.RestoredObjectId))
            {
                AddPositionEvidence(resultingPosition.Id, CreateUnknownTradeEvidence());
                LogError("ROLLOVER", "FILLED_ORDER_LINK_MISSING",
                    "orderId={0} positionId={1} recoveryId={2}",
                    sourceOrder.Id,
                    resultingPosition.Id,
                    recoveryId);
                return;
            }

            _rolloverRestoredPositionRecoveryIds[resultingPosition.Id] =
                recoveryId;
            AddPositionEvidence(
                resultingPosition.Id,
                AddRolloverReconstructionEvidence(snapshot.Evidence));
            LogNormal("ROLLOVER", "FILLED_ORDER_POSITION_LINKED",
                "orderId={0} positionId={1} recoveryId={2} idLink=pendingOrderFilledEvent",
                sourceOrder.Id,
                resultingPosition.Id,
                recoveryId);
        }

        private void RegisterRolloverRestoredPosition(Position position, PositionSnapshot snapshot)
        {
            if (position == null || snapshot == null || !IsBotPosition(position))
                return;

            snapshot.RestoredObjectId = position.Id;
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.Restored;
            snapshot.ResolvedUtc = Server.TimeInUtc;
            _rolloverRestoredPositionRecoveryIds[position.Id] = snapshot.RecoveryId;
            _restoredPositionSnapshotsByRecoveryId[snapshot.RecoveryId] = snapshot;
            AddPositionEvidence(
                position.Id,
                AddRolloverReconstructionEvidence(snapshot.Evidence));
            if (snapshot.TradeIdeaId.HasValue)
                CombineTradeIdeaSummaryEvidence(
                    snapshot.TradeIdeaId.Value,
                    AddRolloverReconstructionEvidence(snapshot.Evidence));
        }

        private void RegisterRolloverRestoredOrder(PendingOrder order)
        {
            if (order == null ||
                _rolloverContext.Phase != RolloverPhase.Restoring ||
                !IsBotOrder(order))
            {
                return;
            }

            Guid? recoveryId = _activeOrderRestorationRecoveryId;
            if (!recoveryId.HasValue)
                return;

            _rolloverRestoredOrderRecoveryIds[order.Id] = recoveryId.Value;
            PendingOrderSnapshot snapshot = _orderSnapshots.FirstOrDefault(
                candidate => candidate.RecoveryId == recoveryId.Value);
            if (snapshot != null)
            {
                snapshot.RestoredObjectId = order.Id;
                _restoredOrderSnapshotsByRecoveryId[recoveryId.Value] =
                    snapshot;
                SetPendingOrderEvidence(
                    order.Id,
                    AddRolloverReconstructionEvidence(snapshot.Evidence));
                LinkBufferedRolloverOrderFill(order);
            }
            else
            {
                SetPendingOrderEvidence(order.Id, CreateUnknownTradeEvidence());
            }
        }

        private void RegisterRolloverRestoredOrder(PendingOrder order, PendingOrderSnapshot snapshot)
        {
            if (order == null || snapshot == null || !IsBotOrder(order))
                return;

            snapshot.RestoredObjectId = order.Id;
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.Restored;
            snapshot.ResolvedUtc = Server.TimeInUtc;
            _rolloverRestoredOrderRecoveryIds[order.Id] = snapshot.RecoveryId;
            _restoredOrderSnapshotsByRecoveryId[snapshot.RecoveryId] = snapshot;
            SetPendingOrderEvidence(
                order.Id,
                AddRolloverReconstructionEvidence(snapshot.Evidence));
            LinkBufferedRolloverOrderFill(order);
        }

        private void LinkBufferedRolloverOrderFill(PendingOrder order)
        {
            if (!TryTakeBufferedRolloverOrderFill(
                    _unregisteredRolloverOrderFills, order.Id, out int positionId))
                return;

            Position position = Positions.FindById(positionId);
            if (position != null)
                RegisterRolloverRestoredPositionFromFilledOrder(order, position);
        }

        private static bool TryTakeBufferedRolloverOrderFill(
            Dictionary<int, int> fills, int orderId, out int positionId)
        {
            if (!fills.TryGetValue(orderId, out positionId))
                return false;
            fills.Remove(orderId);
            return true;
        }

        private void ForgetRolloverPosition(int positionId)
        {
            _deferredRolloverQuarterStops.Remove(positionId);
            ClearWarning("rollover-quarter-stop-" + positionId,
                "ROLLOVER", "QUARTER_STOP_ENDED",
                "position no longer exists");
            if (!_rolloverRestoredPositionRecoveryIds.TryGetValue(positionId, out Guid recoveryId))
                return;

            _rolloverRestoredPositionRecoveryIds.Remove(positionId);
        }

        private void ForgetRolloverOrder(int orderId)
        {
            if (!_rolloverRestoredOrderRecoveryIds.TryGetValue(orderId, out Guid recoveryId))
                return;

            _rolloverRestoredOrderRecoveryIds.Remove(orderId);
        }

        private void CaptureRolloverPendingOrders(List<PendingOrder> destination)
        {
            destination.Clear();
            foreach (PendingOrder order in PendingOrders)
            {
                if (IsBotOrder(order))
                    destination.Add(order);
            }
        }

        private void CaptureRolloverPositions(List<Position> destination)
        {
            destination.Clear();
            foreach (Position position in Positions)
            {
                if (IsBotPosition(position))
                    destination.Add(position);
            }
        }

        private void PruneRolloverRestoredObjectIds()
        {
            _rolloverLivePositionIds.Clear();
            foreach (Position position in Positions)
            {
                if (IsBotPosition(position))
                    _rolloverLivePositionIds.Add(position.Id);
            }

            _rolloverStalePositionIdBuffer.Clear();
            foreach (int id in _rolloverRestoredPositionRecoveryIds.Keys)
            {
                if (!_rolloverLivePositionIds.Contains(id))
                    _rolloverStalePositionIdBuffer.Add(id);
            }
            foreach (int id in _rolloverStalePositionIdBuffer)
                ForgetRolloverPosition(id);

            _rolloverLiveOrderIds.Clear();
            foreach (PendingOrder order in PendingOrders)
            {
                if (IsBotOrder(order))
                    _rolloverLiveOrderIds.Add(order.Id);
            }

            _rolloverStaleOrderIdBuffer.Clear();
            foreach (int id in _rolloverRestoredOrderRecoveryIds.Keys)
            {
                if (!_rolloverLiveOrderIds.Contains(id))
                    _rolloverStaleOrderIdBuffer.Add(id);
            }
            // A filled order can disappear from PendingOrders before its fill event.
            // Retain its identity until restoration tracking is cleared.
            if (_rolloverContext.Phase != RolloverPhase.Restoring)
            {
                foreach (int id in _rolloverStaleOrderIdBuffer)
                    ForgetRolloverOrder(id);
            }
        }

        private RecoveryExposureClassification ClassifyRolloverPosition(Position position)
        {
            if (position == null || !IsBotPosition(position) ||
                !_rolloverRestoredPositionRecoveryIds.TryGetValue(position.Id, out Guid recoveryId))
            {
                return RecoveryExposureClassification.UnexpectedBotExposure;
            }

            bool matchesPositionSnapshot =
                _restoredPositionSnapshotsByRecoveryId.TryGetValue(recoveryId, out PositionSnapshot positionSnapshot) &&
                positionSnapshot.RestoredObjectId == position.Id &&
                position.TradeType == positionSnapshot.TradeType &&
                string.Equals(position.Label, positionSnapshot.Label, StringComparison.Ordinal);
            bool matchesFilledOrderSnapshot =
                _restoredOrderSnapshotsByRecoveryId.TryGetValue(recoveryId, out PendingOrderSnapshot orderSnapshot) &&
                position.TradeType == orderSnapshot.TradeType &&
                string.Equals(position.Label, orderSnapshot.Label, StringComparison.Ordinal);
            if (!matchesPositionSnapshot && !matchesFilledOrderSnapshot)
                return RecoveryExposureClassification.UnexpectedBotExposure;

            if (!position.StopLoss.HasValue || !position.TakeProfit.HasValue)
                return RecoveryExposureClassification.ExpectedButUnprotected;

            if (_deferredRolloverQuarterStops.ContainsKey(position.Id))
            {
                bool deferredGeometryValid =
                    IsDeferredRolloverQuarterStopGeometryValid(position);
                return deferredGeometryValid &&
                       position.SymbolName == Symbol.Name
                    ? RecoveryExposureClassification.ExpectedAndProtected
                    : RecoveryExposureClassification.ExpectedButInvalid;
            }

            bool geometryValid = HasValidRolloverPositionProtectionGeometry(
                position.TradeType,
                position.EntryPrice,
                position.StopLoss.Value,
                position.TakeProfit.Value,
                false,
                false,
                GetProtectionComparisonTolerance());
            bool identityValid = position.SymbolName == Symbol.Name;
            return geometryValid && identityValid
                ? RecoveryExposureClassification.ExpectedAndProtected
                : RecoveryExposureClassification.ExpectedButInvalid;
        }

        private bool IsDeferredRolloverQuarterStopGeometryValid(
            Position position)
        {
            return HasDeferredRolloverQuarterStopGeometry(
                position.TradeType,
                position.EntryPrice,
                position.StopLoss.Value,
                position.TakeProfit.Value);
        }

        private static bool HasDeferredRolloverQuarterStopGeometry(
            TradeType tradeType,
            double entryPrice,
            double stopLoss,
            double takeProfit)
        {
            bool stopValid = tradeType == TradeType.Buy
                    ? stopLoss < entryPrice
                    : stopLoss > entryPrice;
            bool takeValid = tradeType == TradeType.Buy
                    ? takeProfit > entryPrice
                    : takeProfit < entryPrice;
            return stopValid && takeValid;
        }

        private void ScheduleDeferredRolloverQuarterStop(
            Position position,
            PositionSnapshot snapshot,
            string reason)
        {
            if (!_deferredRolloverQuarterStops.TryGetValue(
                    position.Id,
                    out DeferredRolloverQuarterStop state))
            {
                DateTime now = Server.TimeInUtc;
                state = new DeferredRolloverQuarterStop
                {
                    RecoveryId = snapshot.RecoveryId,
                    PositionId = position.Id,
                    TargetStopLoss = NormalizePrice(GetQuarterDistanceStopLoss(
                        position.TradeType, snapshot.EntryPrice,
                        snapshot.OriginalStopLoss.Value, position.EntryPrice)),
                    StartedUtc = now,
                    NextAttemptUtc = now.AddSeconds(
                        DeferredQuarterStopRetrySeconds)
                };
                _deferredRolloverQuarterStops[position.Id] = state;
            }

            LogActiveWarning(
                "rollover-quarter-stop-" + position.Id,
                "ROLLOVER",
                "QUARTER_STOP_DEFERRED",
                "positionId={0} recoveryId={1} targetStopLoss={2} retrySeconds={3} deadlineSeconds={4} reason={5}",
                position.Id,
                state.RecoveryId,
                FormatPrice(state.TargetStopLoss),
                DeferredQuarterStopRetrySeconds,
                DeferredQuarterStopDeadlineSeconds,
                reason);
        }

        private static bool IsValidDeferredQuarterStopQuote(
            double bid,
            double ask)
        {
            return IsFinitePositive(bid) &&
                   IsFinitePositive(ask) &&
                   ask > bid;
        }

        private static bool CanAttemptDeferredRolloverQuarterStop(
            bool rolloverPhaseAllowsAttempt,
            bool insideRolloverWindow,
            double bid,
            double ask)
        {
            return rolloverPhaseAllowsAttempt &&
                   !insideRolloverWindow &&
                   IsValidDeferredQuarterStopQuote(bid, ask);
        }

        private static bool IsRestoredPendingOrderIdMatch(
            int fillCallbackOrderId,
            long? restoredOrderId)
        {
            return restoredOrderId.HasValue &&
                   fillCallbackOrderId == restoredOrderId.Value;
        }

        private static bool IsDeferredQuarterStopDeadlineExpired(
            DateTime startedUtc,
            DateTime nowUtc)
        {
            return (nowUtc - startedUtc).TotalSeconds >=
                   DeferredQuarterStopDeadlineSeconds;
        }

        private void ProcessDeferredRolloverQuarterStops()
        {
            if (_deferredRolloverQuarterStops.Count == 0)
                return;

            int[] positionIds =
                _deferredRolloverQuarterStops.Keys.ToArray();
            DateTime now = Server.TimeInUtc;
            bool insideRolloverWindow = IsInsideRolloverWindow();
            bool validQuote = IsValidDeferredQuarterStopQuote(
                Symbol.Bid,
                Symbol.Ask);
            bool retryWindowOpen = CanAttemptDeferredRolloverQuarterStop(
                _rolloverContext.Phase == RolloverPhase.Normal ||
                _rolloverContext.Phase == RolloverPhase.Restoring,
                insideRolloverWindow,
                Symbol.Bid,
                Symbol.Ask);

            foreach (int positionId in positionIds)
            {
                if (!_deferredRolloverQuarterStops.TryGetValue(
                        positionId,
                        out DeferredRolloverQuarterStop state))
                    continue;

                Position position = Positions.FindById(positionId);
                if (position == null)
                {
                    _deferredRolloverQuarterStops.Remove(positionId);
                    ClearWarning(
                        "rollover-quarter-stop-" + positionId,
                        "ROLLOVER",
                        "QUARTER_STOP_ENDED",
                        "position no longer exists");
                    continue;
                }

                if (!position.StopLoss.HasValue ||
                    !position.TakeProfit.HasValue ||
                    !IsDeferredRolloverQuarterStopGeometryValid(position))
                {
                    _deferredRolloverQuarterStops.Remove(positionId);
                    ClearWarning(
                        "rollover-quarter-stop-" + positionId,
                        "ROLLOVER",
                        "QUARTER_STOP_PROTECTION_LOST",
                        "live protection is missing or has invalid geometry; closure-only protection recovery started");
                    ClosePositionWithoutProtection(
                        position,
                        "deferred rollover quarter stop lost broker-confirmed protection");
                    continue;
                }

                double confirmationTolerance = Math.Max(Symbol.TickSize * 0.5, 1e-10);
                if (!state.Expired &&
                    IsQuarterStopOnLossSide(position.TradeType, position.EntryPrice, state.TargetStopLoss) &&
                    Math.Abs(position.StopLoss.Value - state.TargetStopLoss) <= confirmationTolerance)
                {
                    _deferredRolloverQuarterStops.Remove(positionId);
                    ClearWarning("rollover-quarter-stop-" + positionId, "ROLLOVER",
                        "QUARTER_STOP_APPLIED",
                        string.Format(CultureInfo.InvariantCulture,
                            "positionId={0} recoveryId={1} confirmedBy=livePosition",
                            positionId, state.RecoveryId));
                    continue;
                }

                if (state.Expired || IsDeferredQuarterStopDeadlineExpired(
                        state.StartedUtc,
                        now))
                {
                    if (!state.Expired)
                    {
                        state.Expired = true;
                        LogError("ROLLOVER", "QUARTER_STOP_RETRY_EXPIRED",
                            "positionId={0} recoveryId={1} attempts={2} targetStopLoss={3}; closing restored position",
                            positionId, state.RecoveryId, state.Attempts, FormatPrice(state.TargetStopLoss));
                    }
                    ClosePositionWithoutProtection(position,
                        "quarter-distance rollover stop was not confirmed within five minutes");
                    continue;
                }

                if (!retryWindowOpen || !validQuote ||
                    now < state.NextAttemptUtc)
                    continue;

                double marketPrice = position.TradeType == TradeType.Buy
                    ? Symbol.Bid
                    : Symbol.Ask;
                if (!IsPositionProtectionValid(
                        position.TradeType,
                        marketPrice,
                        position.StopLoss.Value,
                        true,
                        position.Id,
                        "deferred rollover quarter stop guard") ||
                    !IsPositionProtectionValid(
                        position.TradeType,
                        marketPrice,
                        position.TakeProfit.Value,
                        false,
                        position.Id,
                        "deferred rollover quarter stop guard"))
                {
                    _deferredRolloverQuarterStops.Remove(positionId);
                    ClosePositionWithoutProtection(
                        position,
                        "deferred rollover quarter stop protection guard rejected live protection");
                    continue;
                }

                double targetStopPrice = state.TargetStopLoss;
                if (!IsQuarterStopOnLossSide(position.TradeType, position.EntryPrice, targetStopPrice) ||
                    !IsPositionProtectionValid(
                         position.TradeType,
                         marketPrice,
                         targetStopPrice,
                         true,
                         position.Id,
                         "deferred rollover quarter stop"))
                {
                    state.NextAttemptUtc = now.AddSeconds(
                        DeferredQuarterStopRetrySeconds);
                    continue;
                }

                state.Attempts++;
                state.NextAttemptUtc = now.AddSeconds(
                    DeferredQuarterStopRetrySeconds);
                TradeResult result = ModifyPosition(
                    position,
                    targetStopPrice,
                    position.TakeProfit,
                    ProtectionType.Absolute);
                Position confirmed = result == null
                    ? null
                    : result.Position;
                double tolerance = Math.Max(Symbol.TickSize * 0.5, 1e-10);
                bool stopConfirmed = confirmed != null &&
                     confirmed.StopLoss.HasValue &&
                     Math.Abs(confirmed.StopLoss.Value -
                         targetStopPrice) <= tolerance;
                bool takeConfirmed = confirmed != null &&
                     confirmed.TakeProfit.HasValue &&
                     Math.Abs(confirmed.TakeProfit.Value -
                         position.TakeProfit.Value) <= tolerance;

                if (result != null &&
                    result.IsSuccessful &&
                    stopConfirmed &&
                    takeConfirmed)
                {
                    _deferredRolloverQuarterStops.Remove(positionId);
                    ClearWarning(
                        "rollover-quarter-stop-" + positionId,
                        "ROLLOVER",
                        "QUARTER_STOP_APPLIED",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "positionId={0} recoveryId={1} attempts={2}",
                            positionId,
                            state.RecoveryId,
                            state.Attempts));
                }
            }
        }

        private bool HasValidRolloverPositionProtectionGeometry(
            TradeType tradeType,
            double entryPrice,
            double stopLoss,
            double takeProfit,
            bool stopLossAtBreakeven,
            bool takeProfitAtBreakeven,
            double tolerance)
        {
            bool stopLossValid = stopLossAtBreakeven
                ? Math.Abs(stopLoss - entryPrice) <= tolerance
                : (tradeType == TradeType.Buy
                    ? stopLoss < entryPrice
                    : stopLoss > entryPrice);
            bool takeProfitValid = takeProfitAtBreakeven
                ? Math.Abs(takeProfit - entryPrice) <= tolerance
                : (tradeType == TradeType.Buy
                    ? takeProfit > entryPrice
                    : takeProfit < entryPrice);
            return stopLossValid && takeProfitValid;
        }

        private RecoveryExposureClassification ClassifyRolloverOrder(PendingOrder order)
        {
            if (order == null || !IsBotOrder(order) ||
                !_rolloverRestoredOrderRecoveryIds.TryGetValue(order.Id, out Guid recoveryId) ||
                !_restoredOrderSnapshotsByRecoveryId.TryGetValue(recoveryId, out PendingOrderSnapshot snapshot) ||
                snapshot.RestoredObjectId != order.Id)
            {
                return RecoveryExposureClassification.UnexpectedBotExposure;
            }

            if (!order.StopLoss.HasValue || !order.TakeProfit.HasValue)
                return RecoveryExposureClassification.ExpectedButUnprotected;

            bool geometryValid = order.TradeType == TradeType.Buy
                ? order.StopLoss.Value < order.TargetPrice && order.TakeProfit.Value > order.TargetPrice
                : order.StopLoss.Value > order.TargetPrice && order.TakeProfit.Value < order.TargetPrice;
            bool identityValid = order.SymbolName == Symbol.Name &&
                                 order.TradeType == snapshot.TradeType &&
                                 string.Equals(order.Label, snapshot.Label, StringComparison.Ordinal);
            return geometryValid && identityValid
                ? RecoveryExposureClassification.ExpectedAndProtected
                : RecoveryExposureClassification.ExpectedButInvalid;
        }

        private bool HasUnsafeBotExposureDuringRestoration()
        {
            PruneRolloverRestoredObjectIds();

            foreach (Position position in Positions)
            {
                if (IsBotPosition(position) &&
                    ClassifyRolloverPosition(position) != RecoveryExposureClassification.ExpectedAndProtected)
                {
                    return true;
                }
            }

            foreach (PendingOrder order in PendingOrders)
            {
                if (IsBotOrder(order) &&
                    ClassifyRolloverOrder(order) != RecoveryExposureClassification.ExpectedAndProtected)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearRolloverRestorationTracking()
        {
            _rolloverRestoredPositionRecoveryIds.Clear();
            _rolloverRestoredOrderRecoveryIds.Clear();
            _restoredPositionSnapshotsByRecoveryId.Clear();
            _restoredOrderSnapshotsByRecoveryId.Clear();
            _unregisteredRolloverOrderFills.Clear();
            _activePositionRestorationRecoveryId = null;
            _activeOrderRestorationRecoveryId = null;
        }

        private bool HasLiveBotExposure()
        {
            foreach (var position in Positions)
            {
                if (IsBotPosition(position))
                    return true;
            }
            foreach (var order in PendingOrders)
            {
                if (IsBotOrder(order))
                    return true;
            }
            return false;
        }
        #endregion

    }
}
