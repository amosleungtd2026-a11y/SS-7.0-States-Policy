using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Restoring captured positions and pending orders.
    public partial class ShootingStar
    {
        #region Rollover Exposure Restoration
        private void ReopenFromSnapshot()
        {
            if (_rolloverContext.Phase == RolloverPhase.WaitingForMarketOpen &&
                HasLiveBotExposure())
            {
                EnterFlatteningPhase();
                LogActiveWarning("rollover-restoration-blocked", "ROLLOVER", "RESTORATION_BLOCKED",
                    "restoration was blocked because live bot exposure still exists");
                return;
            }
            ClearWarning("rollover-restoration-blocked", "ROLLOVER", "RESTORATION_UNBLOCKED",
                "no live pre-rollover bot exposure remains");
            EnterRestoringPhase();
            if (_rolloverContext.Phase != RolloverPhase.Restoring)
                return;
            if (!_rolloverContext.SnapshotContainedTrades)
            {
                _ticksUntilMaintenance = _rolloverContext.SavedTicksUntilMaintenance;
                ClearRolloverSnapshot();
                return;
            }
            if (!IsFinitePositive(_lastClosePrice))
            {
                LogError("ROLLOVER", "INVALID_SNAPSHOT", "last close price is invalid; unresolved rollover snapshots are retained");
                EnterRecoveryCleanupPhase("last close price is invalid");
                return;
            }
            DateTime utcNow = Server.TimeInUtc;
            double openingPrice = (Symbol.Bid + Symbol.Ask) / 2.0;
            double gapPercent = Math.Abs(openingPrice - _lastClosePrice) / _lastClosePrice * 100.0;
            for (int index = _positionSnapshots.Count - 1; index >= 0; index--)
            {
                PositionSnapshot snapshot = _positionSnapshots[index];
                if (utcNow < snapshot.NextRestorationAttemptUtc)
                    continue;
                RestorationOutcome outcome = TryRestorePositionSnapshot(snapshot, openingPrice, gapPercent);
                if (!HandlePositionRestorationOutcome(snapshot, outcome, index))
                    return;
                if (!ValidateRolloverRuntimeInvariants("ReopenFromSnapshot"))
                    return;
            }
            for (int index = _orderSnapshots.Count - 1; index >= 0; index--)
            {
                PendingOrderSnapshot snapshot = _orderSnapshots[index];
                if (utcNow < snapshot.NextRestorationAttemptUtc)
                    continue;
                RestorationOutcome outcome = TryRestorePendingOrderSnapshot(snapshot, openingPrice, gapPercent);
                if (!HandlePendingOrderRestorationOutcome(snapshot, outcome, index))
                    return;
                if (!ValidateRolloverRuntimeInvariants("ReopenFromSnapshot"))
                    return;
            }
            if (_positionSnapshots.Count == 0 && _orderSnapshots.Count == 0)
            {
                LogNormal("ROLLOVER", "RESOLUTION_SUMMARY",
                    "capturedPositions={0} restoredPositions={1} skippedPositions={2} failedPositions={3} finalizedSkippedLogicalTrades={4} capturedOrders={5} restoredOrders={6} skippedOrders={7} failedOrders={8}",
                    _rolloverCapturedPositionSnapshots,
                    _rolloverRestoredPositionSnapshots,
                    _rolloverSkippedPositionSnapshots,
                    _rolloverFailedPositionSnapshots,
                    _rolloverFinalizedSkippedLogicalTrades,
                    _rolloverCapturedOrderSnapshots,
                    _rolloverRestoredOrderSnapshots,
                    _rolloverSkippedOrderSnapshots,
                    _rolloverFailedOrderSnapshots);
                _ticksUntilMaintenance = _rolloverContext.SavedTicksUntilMaintenance;
                ClearRolloverRestorationTracking();
                ClearRolloverSnapshot();
                LogNormal("ROLLOVER", "RESTORATION_COMPLETED", "all rollover snapshots were resolved");
            }
        }
        private OpeningGapDirection GetOpeningGapDirection(TradeType tradeType, double openingPrice)
        {
            double neutralTolerance = Math.Max(_symbolTickSize * 0.5, 1e-12);
            double signedMove = openingPrice - _lastClosePrice;
            if (Math.Abs(signedMove) <= neutralTolerance)
                return OpeningGapDirection.Neutral;
            bool favourable = tradeType == TradeType.Buy ? signedMove > 0.0 : signedMove < 0.0;
            return favourable ? OpeningGapDirection.Favourable : OpeningGapDirection.Unfavourable;
        }

        private double GetOpeningGapLimitPercent(StrategyKind strategy, TradeType tradeType)
        {
            double movePercent = strategy == StrategyKind.MeanReversion
                ? (tradeType == TradeType.Buy ? _actualMrDownMovePercent : _actualMrUpMovePercent)
                : (tradeType == TradeType.Buy ? _actualTfLongMovePercent : _actualTfShortMovePercent);
            return movePercent / 4.0;
        }

        private bool PassesAsymmetricOpeningGapPolicy(StrategyKind strategy, TradeType tradeType,
            double openingPrice, double gapPercent, out OpeningGapDirection gapDirection,
            out double gapLimitPercent)
        {
            gapDirection = GetOpeningGapDirection(tradeType, openingPrice);
            gapLimitPercent = GetOpeningGapLimitPercent(strategy, tradeType);
            return PassesAsymmetricOpeningGapPolicy(strategy, gapDirection, gapPercent, gapLimitPercent);
        }

        private bool PassesAsymmetricOpeningGapPolicy(StrategyKind strategy,
            OpeningGapDirection gapDirection, double gapPercent, double gapLimitPercent)
        {
            bool unrestrictedDirection = strategy == StrategyKind.MeanReversion
                ? gapDirection == OpeningGapDirection.Unfavourable
                : gapDirection == OpeningGapDirection.Favourable;
            return unrestrictedDirection || gapPercent <= gapLimitPercent;
        }

        private RestorationOutcome TryRestorePositionSnapshot(PositionSnapshot snapshot,
            double openingPrice, double gapPercent)
        {
            if (!ReconcileRolloverLadderHistory(snapshot))
            {
                snapshot.LastRestorationFailure =
                    "partial-close history is not yet available for logical trade accounting";
                return RestorationOutcome.OperationalFailure;
            }

            if (!IsSideAllocated(snapshot.TradeType))
            {
                snapshot.ResolutionReason = "SideAllocationRemoved";
                LogNormal("ROLLOVER", "POSITION_RESTORE_EXCLUDED_ALLOCATION", "position snapshot skipped permanently because side {0} has no allocation", snapshot.TradeType);
                return RestorationOutcome.PermanentlySkipped;
            }
            OpeningGapDirection gapDirection = GetOpeningGapDirection(
                snapshot.TradeType,
                openingPrice);
            double gapLimitPercent = snapshot.Capsule == null
                ? GetOpeningGapLimitPercent(snapshot.Strategy, snapshot.TradeType)
                : snapshot.Capsule.MovePercent / 4.0;
            if (!PassesAsymmetricOpeningGapPolicy(
                    snapshot.Strategy,
                    gapDirection,
                    gapPercent,
                    gapLimitPercent))
            {
                snapshot.ResolutionReason = "OpeningGapPolicy";
                LogNormal("ROLLOVER", "POSITION_RESTORE_EXCLUDED_OPENING_GAP",
                    "position snapshot skipped permanently because its {0} opening gap {1:F6}% exceeded the strategy limit {2:F6}%",
                    gapDirection, gapPercent, gapLimitPercent);
                return RestorationOutcome.PermanentlySkipped;
            }
            if (!snapshot.OriginalProtectionKnown)
            {
                snapshot.ResolutionReason = "OriginalProtectionUnavailable";
                LogError("ROLLOVER", "POSITION_RESTORE_EXCLUDED_UNKNOWN_ORIGINAL_PROTECTION",
                    "recoveryId={0} sourcePositionId={1}; original protection cannot be verified; position will not be restored",
                    snapshot.RecoveryId, snapshot.SourcePositionId);
                return RestorationOutcome.PermanentlySkipped;
            }
            if (!snapshot.OriginalStopLoss.HasValue || !snapshot.OriginalTakeProfit.HasValue ||
                !snapshot.StopLoss.HasValue || !snapshot.TakeProfit.HasValue ||
                !snapshot.StopLossPips.HasValue || !snapshot.TakeProfitPips.HasValue)
            {
                snapshot.LastRestorationFailure = "required position protection data is missing";
                LogError("ROLLOVER", "POSITION_RESTORE_FAILED_PROTECTION_DATA",
                    "recoveryId={0} sourcePositionId={1} logicalTradeId={2} reason={3}",
                    snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.LogicalTradeId,
                    snapshot.LastRestorationFailure);
                return RestorationOutcome.PermanentFailure;
            }
            double currentPrice = snapshot.TradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double originalStopLoss = snapshot.OriginalStopLoss.Value;
            double originalTakeProfit = snapshot.OriginalTakeProfit.Value;
            bool geometryValid = snapshot.TradeType == TradeType.Buy
                ? originalStopLoss < snapshot.EntryPrice && originalTakeProfit > snapshot.EntryPrice
                : originalTakeProfit < snapshot.EntryPrice && originalStopLoss > snapshot.EntryPrice;
            if (!geometryValid)
            {
                snapshot.LastRestorationFailure = "original position protection geometry is invalid";
                LogError("ROLLOVER", "POSITION_RESTORE_FAILED_PROTECTION_GEOMETRY",
                    "recoveryId={0} sourcePositionId={1} logicalTradeId={2} reason={3}",
                    snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.LogicalTradeId,
                    snapshot.LastRestorationFailure);
                return RestorationOutcome.PermanentFailure;
            }
            if (IsTakeProfitWithinOriginalQuarter(snapshot.TradeType, snapshot.EntryPrice,
                    originalTakeProfit, snapshot.TakeProfit.Value))
            {
                snapshot.ResolutionReason = "TakeProfitWithinOriginalQuarter";
                LogNormal("ROLLOVER", "POSITION_RESTORE_EXCLUDED_TP_QUARTER",
                    "recoveryId={0} sourcePositionId={1} originalEntry={2} originalTakeProfit={3} currentTakeProfit={4}",
                    snapshot.RecoveryId, snapshot.SourcePositionId,
                    FormatPrice(snapshot.EntryPrice), FormatPrice(originalTakeProfit),
                    FormatPrice(snapshot.TakeProfit.Value));
                return RestorationOutcome.PermanentlySkipped;
            }
            double stopLossPips = NormalizeProtectionPips(snapshot.StopLossPips.Value);
            double takeProfitPips = NormalizeProtectionPips(snapshot.TakeProfitPips.Value);
            if (IsStopLossCrossedProtectionState(snapshot.ProtectionState))
            {
                stopLossPips = NormalizeProtectionPipsUp(
                    Math.Abs(snapshot.EntryPrice - snapshot.OriginalStopLoss.Value) / Symbol.PipSize);
            }
            if (IsTakeProfitCrossedProtectionState(snapshot.ProtectionState) &&
                !IsFinitePositive(takeProfitPips))
            {
                takeProfitPips = NormalizeProtectionPipsUp(
                    Math.Abs(snapshot.OriginalTakeProfit.Value - snapshot.EntryPrice) / Symbol.PipSize);
            }
            if (!IsFinitePositive(stopLossPips) || !IsFinitePositive(takeProfitPips))
            {
                snapshot.LastRestorationFailure = "stored position protection distances are invalid";
                LogError("ROLLOVER", "POSITION_RESTORE_FAILED_PROTECTION_DISTANCE",
                    "recoveryId={0} sourcePositionId={1} logicalTradeId={2} reason={3}",
                    snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.LogicalTradeId,
                    snapshot.LastRestorationFailure);
                return RestorationOutcome.PermanentFailure;
            }
            if (!ValidateRelativeProtectionDistances(currentPrice, stopLossPips, takeProfitPips, "Rollover position restoration"))
            {
                snapshot.LastRestorationFailure = "protection distances are temporarily invalid at the current market price";
                return RestorationOutcome.TransientMarketCondition;
            }
            double restorationVolume = NormalizeVolume(snapshot.VolumeInUnits);
            if (!IsTradableVolume(restorationVolume))
            {
                snapshot.LastRestorationFailure = string.Format(
                    "position volume {0} is outside broker range {1}-{2}",
                    snapshot.VolumeInUnits, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax);
                LogError("ROLLOVER", "POSITION_RESTORE_FAILED_VOLUME",
                    "recoveryId={0} sourcePositionId={1} logicalTradeId={2} reason={3}",
                    snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.LogicalTradeId,
                    snapshot.LastRestorationFailure);
                return RestorationOutcome.PermanentFailure;
            }
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.RestoreInProgress;
            _activePositionRestorationRecoveryId = snapshot.RecoveryId;
            TradeResult result;
            try
            {
                RunTelemetry("RolloverPositionRestoreRequested", () => LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_RESTORE_REQUESTED",
                    "schemaVersion=2 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} volume={4:R}",
                    FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                    snapshot.RecoveryId, snapshot.SourcePositionId, restorationVolume));
                string comment = GetPersistentTradeComment(
                    snapshot.TradeIdeaId,
                    snapshot.Capsule);
                result = comment == null
                    ? ExecuteMarketOrder(
                        snapshot.TradeType,
                        Symbol.Name,
                        restorationVolume,
                        snapshot.Label,
                        stopLossPips,
                        takeProfitPips)
                    : ExecuteMarketOrder(
                        snapshot.TradeType,
                        Symbol.Name,
                        restorationVolume,
                        snapshot.Label,
                        stopLossPips,
                        takeProfitPips,
                        comment);
            }
            finally
            {
                _activePositionRestorationRecoveryId = null;
            }
            if (result == null || !result.IsSuccessful || result.Position == null)
            {
                snapshot.LastRestorationFailure = result == null ? "null result" : result.Error.ToString();
                return RestorationOutcome.OperationalFailure;
            }

            Position restoredPosition = result.Position;
            snapshot.RestoredObjectId = restoredPosition.Id;
            _rolloverRestoredPositionRecoveryIds[restoredPosition.Id] =
                snapshot.RecoveryId;
            string ideaAssociation = "notLinked";
            RunTelemetry("RolloverPositionExecuted", () =>
            {
                VerifyPersistentCapsuleComment(
                    snapshot.TradeIdeaId,
                    snapshot.Capsule,
                    restoredPosition.Comment,
                    "position",
                    restoredPosition.Id);
                AddIdeaPosition(snapshot.TradeIdeaId, restoredPosition.Id, true);
                ideaAssociation = snapshot.TradeIdeaId.HasValue
                    ? AssociateTradeIdeaWithPosition(restoredPosition.Id, snapshot.TradeIdeaId.Value)
                    : "notLinked";
                PositionAttributionEvidence restoredAttribution =
                    ideaAssociation == "exclusive"
                        ? PositionAttributionEvidence.Exclusive
                        : ideaAssociation == "nettedPositionMayBeShared"
                            ? PositionAttributionEvidence.AmbiguousNetted
                            : ideaAssociation == "conflictingIdeas"
                                ? PositionAttributionEvidence.Unknown
                                : PositionAttributionEvidence.NotApplicable;
                if (restoredAttribution != PositionAttributionEvidence.NotApplicable)
                {
                    TradeEvidence restoredEvidence =
                        AddPositionAttributionEvidence(
                            AddRolloverReconstructionEvidence(snapshot.Evidence),
                            restoredAttribution);
                    AddPositionEvidence(restoredPosition.Id, restoredEvidence);
                    if (snapshot.TradeIdeaId.HasValue)
                        CombineTradeIdeaSummaryEvidence(
                            snapshot.TradeIdeaId.Value, restoredEvidence);
                }
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_EXECUTED",
                    "schemaVersion=3 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} restoredPositionId={4} association={5} lifecycleContinuity={6} segmentProvenance={7} positionAttribution={8}",
                    FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                    snapshot.RecoveryId, snapshot.SourcePositionId, restoredPosition.Id, ideaAssociation,
                    snapshot.Evidence == null ? "unknown" : snapshot.Evidence.LifecycleContinuity.ToString(),
                    AddRolloverReconstructionEvidence(snapshot.Evidence).SegmentProvenance,
                    snapshot.Evidence == null ? "unknown" : snapshot.Evidence.PositionAttribution.ToString());
            });
            PreserveRestoredPositionRolloverAnchors(restoredPosition, snapshot);

            if (!result.Position.StopLoss.HasValue || !result.Position.TakeProfit.HasValue)
            {
                snapshot.LastRestorationFailure = "broker did not confirm both requested protections";
                ClosePositionWithoutProtection(
                    result.Position,
                    "rollover restoration did not confirm both requested protections");
                RegisterRolloverRestoredPosition(result.Position, snapshot);
                return RestorationOutcome.Restored;
            }
            bool crossedStopLoss = IsStopLossCrossedProtectionState(snapshot.ProtectionState);
            if (crossedStopLoss)
            {
                double targetStopPrice = NormalizePrice(GetQuarterDistanceStopLoss(
                    snapshot.TradeType, snapshot.EntryPrice, snapshot.OriginalStopLoss.Value,
                    restoredPosition.EntryPrice));
                bool validQuote = IsValidDeferredQuarterStopQuote(
                    Symbol.Bid,
                    Symbol.Ask);
                bool retryWindowOpen = CanAttemptDeferredRolloverQuarterStop(
                    _rolloverContext.Phase == RolloverPhase.Restoring,
                    IsInsideRolloverWindow(),
                    Symbol.Bid,
                    Symbol.Ask);
                double marketPrice = snapshot.TradeType == TradeType.Buy
                    ? Symbol.Bid
                    : Symbol.Ask;
                bool targetProtectionValid = validQuote &&
                    IsQuarterStopOnLossSide(snapshot.TradeType, restoredPosition.EntryPrice, targetStopPrice) &&
                    IsPositionProtectionValid(
                        snapshot.TradeType,
                        marketPrice,
                        targetStopPrice,
                        true,
                        restoredPosition.Id,
                        "rollover quarter stop");
                TradeResult protectionResult = null;
                if (retryWindowOpen && targetProtectionValid)
                {
                    protectionResult = ModifyPosition(
                        restoredPosition,
                        targetStopPrice,
                        restoredPosition.TakeProfit,
                        ProtectionType.Absolute);
                }
                Position confirmedPosition = protectionResult == null
                    ? null
                    : protectionResult.Position;
                double tolerance = Math.Max(Symbol.TickSize * 0.5, 1e-10);
                bool stopLossConfirmed = confirmedPosition != null &&
                     confirmedPosition.StopLoss.HasValue &&
                     Math.Abs(confirmedPosition.StopLoss.Value - targetStopPrice) <= tolerance;
                bool takeProfitConfirmed = confirmedPosition != null &&
                     confirmedPosition.TakeProfit.HasValue &&
                     Math.Abs(confirmedPosition.TakeProfit.Value - restoredPosition.TakeProfit.Value) <= tolerance;
                if (protectionResult == null || !protectionResult.IsSuccessful ||
                    !stopLossConfirmed || !takeProfitConfirmed)
                {
                    string failure = string.Format(
                        CultureInfo.InvariantCulture,
                        "broker did not yet confirm quarter-distance stop; error={0}",
                        protectionResult == null
                            ? (!validQuote
                                ? "invalid live quote"
                                : (!retryWindowOpen
                                    ? "rollover guard closed"
                                    : (!targetProtectionValid
                                        ? "protection guard rejected quarter-distance stop"
                                        : "null result")))
                            : protectionResult.Error.ToString());

                    Position livePosition =
                        Positions.FindById(restoredPosition.Id) ??
                        (protectionResult == null
                            ? restoredPosition
                            : protectionResult.Position ?? restoredPosition);
                    bool conventionalProtectionConfirmed =
                        livePosition.StopLoss.HasValue &&
                        livePosition.TakeProfit.HasValue &&
                        livePosition.SymbolName == Symbol.Name &&
                        HasValidRolloverPositionProtectionGeometry(
                            livePosition.TradeType,
                            livePosition.EntryPrice,
                            livePosition.StopLoss.Value,
                            livePosition.TakeProfit.Value,
                            false,
                            false,
                            GetProtectionComparisonTolerance());
                    if (!conventionalProtectionConfirmed)
                    {
                        snapshot.LastRestorationFailure =
                            failure + "; conventional protection was not confirmed";
                        ClosePositionWithoutProtection(
                            livePosition,
                            "rollover quarter stop did not leave broker-confirmed conventional protection");
                    }
                    else
                    {
                        restoredPosition = livePosition;
                        ScheduleDeferredRolloverQuarterStop(
                            restoredPosition,
                            snapshot,
                            failure);
                    }
                }
                else
                {
                    restoredPosition = confirmedPosition;
                    LogNormal("ROLLOVER", "POSITION_QUARTER_STOP_APPLIED",
                        "recoveryId={0} positionId={1} targetStopLoss={2}",
                        snapshot.RecoveryId, restoredPosition.Id, FormatPrice(targetStopPrice));
                }
            }
            RegisterRolloverRestoredPosition(restoredPosition, snapshot);
            PreserveRestoredPositionRolloverAnchors(restoredPosition, snapshot);
            RunTelemetry("RolloverPositionLinked", () => LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_LINKED",
                "schemaVersion=3 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} restoredPositionId={4} sourceSegment={5} restoredSegment={6} association={7} lifecycleContinuity={8} segmentProvenance={9} positionAttribution={10}",
                FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                snapshot.RecoveryId, snapshot.SourcePositionId, restoredPosition.Id,
                snapshot.RolloverSegment, snapshot.RolloverSegment + 1, ideaAssociation,
                GetPositionEvidence(restoredPosition.Id)?.LifecycleContinuity.ToString() ?? "unknown",
                GetPositionEvidence(restoredPosition.Id)?.SegmentProvenance.ToString() ?? "unknown",
                GetPositionEvidence(restoredPosition.Id)?.PositionAttribution.ToString() ?? "unknown"));
            LogNormal("ROLLOVER", "POSITION_RESTORED", "positionId={0} side={1} stopLossPips={2} takeProfitPips={3}",
                restoredPosition.Id, snapshot.TradeType, stopLossPips, takeProfitPips);
            return RestorationOutcome.Restored;
        }
        private void PreserveRestoredPositionRolloverAnchors(Position position, PositionSnapshot snapshot)
        {
            if (!_positionStates.TryGetValue(position.Id, out PositionState state))
            {
                InitializePositionState(position);
                if (!_positionStates.TryGetValue(position.Id, out state))
                {
                    LogError("ROLLOVER", "STATE_MISSING",
                        "restored position {0} has no PositionState; historical rollover anchors could not be preserved",
                        position.Id);
                    return;
                }
            }
            state.RolloverAnchorEntryPrice = snapshot.EntryPrice;
            state.EntryOrigin = snapshot.EntryOrigin;
            SetPositionEntryOrigin(position.Id, snapshot.EntryOrigin);
            state.Evidence = AddRolloverReconstructionEvidence(
                CombineTradeEvidence(state.Evidence, snapshot.Evidence));
            AddPositionEvidence(position.Id, state.Evidence);
            if (snapshot.TradeIdeaId.HasValue)
                CombineTradeIdeaSummaryEvidence(
                    snapshot.TradeIdeaId.Value,
                    state.Evidence);
            state.LogicalTradeId = snapshot.LogicalTradeId;
            state.LogicalEntryUtc = snapshot.LogicalEntryUtc;
            state.RolloverSegment = snapshot.RolloverSegment + 1;
            state.RestorationCount = snapshot.RestorationCount + 1;
            state.CarriedGrossProfit = snapshot.CarriedGrossProfit;
            state.CarriedNetProfit = snapshot.CarriedNetProfit;
            state.CarriedCommission = snapshot.CarriedCommission;
            state.CarriedSwap = snapshot.CarriedSwap;
            state.CarriedPips = snapshot.CarriedPips;
            state.CarriedActiveDurationSeconds = snapshot.CarriedActiveDurationSeconds;
            state.CompletedTakeProfitLevels = snapshot.CompletedTakeProfitLevels;
            state.CompletedStopLossLevels = snapshot.CompletedStopLossLevels;
            state.PendingLadderCloseVolume = 0.0;
            state.LadderRecoveryAmbiguous = snapshot.LadderRecoveryAmbiguous;
            PersistPositionLadderState(position.Id, state);
            state.ScaleUpEligible = snapshot.ScaleUpEligible;
            state.CircuitBreakerEligible = false;
            state.CircuitBreakerOriginalStopUnchanged = false;
            PersistCircuitBreakerPositionState(position.Id, state);
            LogVerbose("TRADE", "ROLLOVER_SEGMENT_RESTORED",
                "logicalTradeId={0} recoveryId={1} positionId={2} segment={3} restorationCount={4} carriedNetProfit={5:F2}",
                state.LogicalTradeId, snapshot.RecoveryId, position.Id,
                state.RolloverSegment, state.RestorationCount, state.CarriedNetProfit);
            state.OriginalStopLoss = snapshot.OriginalStopLoss.Value;
            state.OriginalTakeProfit = snapshot.OriginalTakeProfit.Value;
            state.OriginalProtectionKnown = snapshot.OriginalProtectionKnown;
            PersistScaleUpPositionState(position.Id, state);
            // Keep state.EntryPrice unchanged: live trailing must use the actual entry
            // of the newly restored position, not the historical rollover anchor.
        }
        private RestorationOutcome TryRestorePendingOrderSnapshot(PendingOrderSnapshot snapshot,
            double openingPrice, double gapPercent)
        {
            if (snapshot.CapsuleRecoveryFailed)
            {
                snapshot.ResolutionReason = "PersistentCapsuleUnavailable";
                LogError("ROLLOVER", "ORDER_RESTORE_EXCLUDED_INVALID_CAPSULE",
                    "recoveryId={0} sourceOrderId={1} reason={2}",
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId,
                    snapshot.ResolutionReason);
                return RestorationOutcome.PermanentlySkipped;
            }

            if (snapshot.OrderType != PendingOrderType.Limit &&
                snapshot.OrderType != PendingOrderType.Stop)
            {
                snapshot.ResolutionReason =
                    "UnsupportedPendingOrderType:" + snapshot.OrderType;
                LogError("ROLLOVER", "ORDER_RESTORE_EXCLUDED_UNSUPPORTED_TYPE",
                    "recoveryId={0} sourceOrderId={1} orderType={2} outcome=skipped",
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId,
                    snapshot.OrderType);
                return RestorationOutcome.PermanentlySkipped;
            }

            if (snapshot.OrderType == PendingOrderType.Stop &&
                snapshot.Capsule == null)
            {
                snapshot.ResolutionReason =
                    "FrozenStopEntryGeometryUnavailable";
                LogError("ROLLOVER", "STOP_ORDER_RESTORE_EXCLUDED_NO_GEOMETRY",
                    "recoveryId={0} sourceOrderId={1} outcome=skipped",
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId);
                return RestorationOutcome.PermanentlySkipped;
            }

            if (!IsSideAllocated(snapshot.TradeType))
            {
                snapshot.ResolutionReason = "SideAllocationRemoved";
                LogNormal("ROLLOVER", "ORDER_RESTORE_EXCLUDED_ALLOCATION",
                    "recoveryId={0} side={1} reason={2}",
                    snapshot.RecoveryId, snapshot.TradeType, snapshot.ResolutionReason);
                return RestorationOutcome.PermanentlySkipped;
            }
            if (snapshot.ExpirationTime.HasValue && snapshot.ExpirationTime.Value <= Server.TimeInUtc)
            {
                snapshot.ResolutionReason = "Expired";
                LogNormal("ROLLOVER", "ORDER_RESTORE_EXCLUDED_EXPIRED",
                    "recoveryId={0} expirationUtc={1:yyyy-MM-dd HH:mm:ss} reason={2}",
                    snapshot.RecoveryId, snapshot.ExpirationTime.Value, snapshot.ResolutionReason);
                return RestorationOutcome.PermanentlySkipped;
            }
            OpeningGapDirection gapDirection = GetOpeningGapDirection(
                snapshot.TradeType,
                openingPrice);
            double gapLimitPercent = snapshot.Capsule == null
                ? GetOpeningGapLimitPercent(snapshot.Strategy, snapshot.TradeType)
                : snapshot.Capsule.MovePercent / 4.0;
            if (!PassesAsymmetricOpeningGapPolicy(
                    snapshot.Strategy,
                    gapDirection,
                    gapPercent,
                    gapLimitPercent))
            {
                snapshot.ResolutionReason = "OpeningGapPolicy";
                LogNormal("ROLLOVER", "ORDER_RESTORE_EXCLUDED_OPENING_GAP",
                    "recoveryId={0} gapDirection={1} gapPercent={2:F6} gapLimitPercent={3:F6} reason={4}",
                    snapshot.RecoveryId, gapDirection, gapPercent, gapLimitPercent,
                    snapshot.ResolutionReason);
                return RestorationOutcome.PermanentlySkipped;
            }
            double movePercent = snapshot.Strategy == StrategyKind.MeanReversion
                ? (snapshot.TradeType == TradeType.Buy ? _actualMrDownMovePercent : _actualMrUpMovePercent)
                : (snapshot.TradeType == TradeType.Buy ? _actualTfLongMovePercent : _actualTfShortMovePercent);
            double hourlyVolatilityFactor = GetCurrentHourlyVolatilityFactor();
            double limitDistancePercent = snapshot.Strategy == StrategyKind.MeanReversion
                ? (snapshot.TradeType == TradeType.Buy
                    ? _actualMrBuyLimitDistancePercent
                    : _actualMrSellLimitDistancePercent)
                : (snapshot.TradeType == TradeType.Buy
                    ? _actualTfBuyLimitDistancePercent
                    : _actualTfSellLimitDistancePercent);
            if (snapshot.OrderType == PendingOrderType.Stop &&
                snapshot.Capsule != null)
            {
                limitDistancePercent =
                    snapshot.Capsule.LimitDistancePercent;
            }
            double targetReferencePrice = snapshot.TradeType == TradeType.Buy
                ? Symbol.Ask
                : Symbol.Bid;
            double targetDistancePercent =
                snapshot.OrderType == PendingOrderType.Stop &&
                snapshot.Capsule != null
                    ? snapshot.Capsule.LimitDistancePercent
                    : limitDistancePercent * hourlyVolatilityFactor;
            double targetPrice = NormalizePrice(
                snapshot.OrderType == PendingOrderType.Stop
                    ? (snapshot.TradeType == TradeType.Buy
                        ? targetReferencePrice * (1.0 + targetDistancePercent / 100.0)
                        : targetReferencePrice * (1.0 - targetDistancePercent / 100.0))
                    : (snapshot.TradeType == TradeType.Buy
                        ? targetReferencePrice * (1.0 - targetDistancePercent / 100.0)
                        : targetReferencePrice * (1.0 + targetDistancePercent / 100.0)));
            if (!IsPendingTargetDistanceValid(
                    snapshot.TradeType,
                    snapshot.OrderType == PendingOrderType.Stop
                        ? V2OrderKind.Stop
                        : V2OrderKind.Limit,
                    targetPrice,
                    "Rollover pending-order restoration"))
            {
                snapshot.LastRestorationFailure = "target is temporarily on the wrong market side or inside the broker minimum distance";
                return RestorationOutcome.TransientMarketCondition;
            }
            if (!PassesSafeDistanceCheck(targetPrice, SafeDistanceMultiplier * movePercent))
            {
                snapshot.LastRestorationFailure = "safe-separation check is not currently satisfied";
                return RestorationOutcome.TransientMarketCondition;
            }
            if (!snapshot.StopLossPips.HasValue || !snapshot.TakeProfitPips.HasValue)
            {
                snapshot.LastRestorationFailure = "pending-order snapshot is missing relative SL or TP protection";
                LogError("ROLLOVER", "ORDER_RESTORE_FAILED_PROTECTION_DATA",
                    "pending-order snapshot restoration failed permanently because required relative protection data is missing");
                return RestorationOutcome.PermanentFailure;
            }
            double stopLossPips = NormalizeProtectionPips(snapshot.StopLossPips.Value);
            double takeProfitPips = NormalizeProtectionPips(snapshot.TakeProfitPips.Value);
            if (!IsFinitePositive(stopLossPips) || !IsFinitePositive(takeProfitPips))
            {
                snapshot.LastRestorationFailure = "pending-order relative protection distances are invalid";
                return RestorationOutcome.PermanentFailure;
            }
            double? stopLossPrice = NormalizePrice(snapshot.TradeType == TradeType.Buy
                ? targetPrice - stopLossPips * Symbol.PipSize
                : targetPrice + stopLossPips * Symbol.PipSize);
            double? takeProfitPrice = NormalizePrice(snapshot.TradeType == TradeType.Buy
                ? targetPrice + takeProfitPips * Symbol.PipSize
                : targetPrice - takeProfitPips * Symbol.PipSize);
            string protectionGeometryError;
            if (!ValidatePendingProtectionGeometry(snapshot.TradeType, targetPrice,
                    stopLossPrice, takeProfitPrice, "Rollover pending-order restoration",
                    out protectionGeometryError))
            {
                snapshot.LastRestorationFailure = protectionGeometryError;
                return RestorationOutcome.PermanentFailure;
            }
            if (!ValidateRelativeProtectionDistances(targetPrice, stopLossPips, takeProfitPips,
                    "Rollover pending-order restoration"))
            {
                snapshot.LastRestorationFailure = "pending protection distances are temporarily invalid";
                return RestorationOutcome.TransientMarketCondition;
            }
            double restorationVolume = NormalizeVolume(snapshot.VolumeInUnits);
            if (!IsTradableVolume(restorationVolume))
            {
                snapshot.LastRestorationFailure = string.Format(
                    "pending-order volume {0} is outside broker range {1}-{2}",
                    snapshot.VolumeInUnits, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax);
                LogError("ROLLOVER", "ORDER_RESTORE_FAILED_VOLUME",
                    "recoveryId={0} reason={1}",
                    snapshot.RecoveryId, snapshot.LastRestorationFailure);
                return RestorationOutcome.PermanentFailure;
            }
            double restorationStopLossPips = stopLossPips;
            PendingOrderRiskCapDecision riskCapDecision;
            if (!TryCapPendingOrderVolumeByEquityRisk(
                    restorationVolume,
                    restorationStopLossPips,
                    "Rollover pending-order restoration",
                    out restorationVolume,
                    out riskCapDecision))
            {
                snapshot.LastRestorationFailure =
                    "the per-trade 2% current-equity risk cap currently allows no tradable pending-order volume";
                return RestorationOutcome.TransientMarketCondition;
            }

            snapshot.RecoveryStatus = SnapshotRecoveryStatus.RestoreInProgress;
            _activeOrderRestorationRecoveryId = snapshot.RecoveryId;
            bool trackSubmissionCorrelation =
                BeginRestoredOrderSubmissionTracking();
            TradeResult result = null;
            try
            {
                LogRiskCapDecision(
                    "Rollover pending-order restoration",
                    riskCapDecision);
                RunTelemetry("RolloverOrderRestoreRequested", () => LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_RESTORE_REQUESTED",
                    "schemaVersion=4 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} target={3} volume={4:R}",
                    FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId), snapshot.RecoveryId,
                    snapshot.SourceOrderId, FormatPrice(targetPrice), restorationVolume));
                string comment = GetPersistentTradeComment(
                    snapshot.SubmissionCorrelationId,
                    snapshot.Capsule);
                if (snapshot.OrderType == PendingOrderType.Stop)
                {
                    result = comment == null
                        ? PlaceStopOrder(snapshot.TradeType, Symbol.Name, restorationVolume,
                            targetPrice, snapshot.Label, stopLossPips, takeProfitPips,
                            ProtectionType.Relative, snapshot.ExpirationTime, null, false)
                        : PlaceStopOrder(snapshot.TradeType, Symbol.Name, restorationVolume,
                            targetPrice, snapshot.Label, stopLossPips, takeProfitPips,
                            ProtectionType.Relative, snapshot.ExpirationTime, comment, false);
                }
                else
                {
                    result = comment == null
                        ? PlaceLimitOrder(snapshot.TradeType, Symbol.Name, restorationVolume,
                            targetPrice, snapshot.Label, stopLossPips, takeProfitPips,
                            ProtectionType.Relative, snapshot.ExpirationTime)
                        : PlaceLimitOrder(snapshot.TradeType, Symbol.Name, restorationVolume,
                            targetPrice, snapshot.Label, stopLossPips, takeProfitPips,
                            ProtectionType.Relative, snapshot.ExpirationTime, comment);
                }
            }
            finally
            {
                _activeOrderRestorationRecoveryId = null;
                RunTelemetry(
                    "FinishRestoredOrderSubmissionTracking",
                    () => FinishRestoredOrderSubmissionTracking(
                        snapshot,
                        result,
                        trackSubmissionCorrelation));
            }
            if (result == null || !result.IsSuccessful || result.PendingOrder == null)
            {
                snapshot.LastRestorationFailure = result == null ? "null result" : result.Error.ToString();
                return RestorationOutcome.OperationalFailure;
            }
            RunTelemetry("VerifyRestoredOrderComment", () =>
                VerifyPersistentCapsuleComment(
                    snapshot.SubmissionCorrelationId,
                    snapshot.Capsule,
                    result.PendingOrder.Comment,
                    "order",
                    result.PendingOrder.Id));
            snapshot.TargetPrice = result.PendingOrder.TargetPrice;
            RegisterRolloverRestoredOrder(result.PendingOrder, snapshot);
            RegisterAndReconcileProvisionalSafeDistancePendingOrder(result.PendingOrder);
            RegisterPendingOrderTrailingState(
                result.PendingOrder,
                snapshot.Capsule == null
                    ? snapshot.OriginalTargetPrice
                    : snapshot.Capsule.OriginalTargetPrice,
                snapshot.MovementReferencePrice, snapshot.Strategy,
                GetUtcHourKey(Server.TimeInUtc),
                GetCurrentHourlyVolatilityFactor(),
                AddRolloverReconstructionEvidence(snapshot.Evidence),
                snapshot.EntryOrigin,
                snapshot.Capsule);
            TradeEvidence restoredOrderEvidence = GetPendingOrderEvidence(result.PendingOrder.Id);
            LogNormal("ROLLOVER", "PENDING_ORDER_RESTORED",
                "orderId={0} side={1} target={2} lifecycleContinuity={3} segmentProvenance={4}",
                result.PendingOrder.Id, snapshot.TradeType, FormatPrice(snapshot.TargetPrice),
                restoredOrderEvidence == null
                    ? "unknown"
                    : restoredOrderEvidence.LifecycleContinuity.ToString(),
                restoredOrderEvidence == null
                    ? "unknown"
                    : restoredOrderEvidence.SegmentProvenance.ToString());
            return RestorationOutcome.Restored;
        }
        private bool HandlePositionRestorationOutcome(
            PositionSnapshot snapshot,
            RestorationOutcome outcome,
            int snapshotIndex)
        {
            string warningKey = GetTransientPositionWarningKey(snapshot);
            if (outcome == RestorationOutcome.Restored || outcome == RestorationOutcome.PermanentlySkipped)
            {
                ClearWarning(warningKey, "ROLLOVER", "ELIGIBILITY_RESOLVED",
                    "position snapshot restoration eligibility resolved");
                if (outcome == RestorationOutcome.Restored)
                {
                    _rolloverRestoredPositionSnapshots++;
                }
                else
                {
                    snapshot.RecoveryStatus = SnapshotRecoveryStatus.PermanentlySkipped;
                    snapshot.ResolvedUtc = Server.TimeInUtc;
                    _rolloverSkippedPositionSnapshots++;
                    LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_SKIPPED",
                        "schemaVersion=2 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} reason={4}",
                        FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                        snapshot.RecoveryId, snapshot.SourcePositionId, snapshot.ResolutionReason ?? "unspecified");
                    FinalizePermanentlySkippedLogicalTrade(snapshot);
                    if (snapshot.LogicalTradeFinalized)
                        _rolloverFinalizedSkippedLogicalTrades++;
                }
                _positionSnapshots.RemoveAt(snapshotIndex);
                return true;
            }
            if (outcome == RestorationOutcome.PermanentFailure)
            {
                return FailPositionRestoration(
                    snapshot,
                    snapshot.LastRestorationFailure ?? "unspecified",
                    "permanentFailure");
            }

            snapshot.RestorationAttempts++;
            if (!HasPositionRestorationAttemptsRemaining(snapshot.RestorationAttempts))
            {
                string failure = string.Format(CultureInfo.InvariantCulture,
                    "exhausted {0} position restoration attempts. Last failure: {1}",
                    RolloverPositionRestorationMaxAttempts,
                    snapshot.LastRestorationFailure ?? "unspecified");
                return FailPositionRestoration(snapshot, failure, "attemptsExhausted");
            }

            return outcome == RestorationOutcome.TransientMarketCondition
                ? ScheduleTransientRestorationRetry(snapshot, "position")
                : ScheduleOperationalRestorationRetry(snapshot, "position");
        }

        private bool FailPositionRestoration(
            PositionSnapshot snapshot,
            string failure,
            string outcome)
        {
            TerminateTransientEligibilityWait(snapshot, outcome);
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.Failed;
            snapshot.ResolvedUtc = Server.TimeInUtc;
            snapshot.LastRestorationFailure = failure;
            if (!snapshot.FailureCounted)
            {
                snapshot.FailureCounted = true;
                _rolloverFailedPositionSnapshots++;
            }

            LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_POSITION_FAILED",
                "schemaVersion=2 tradeIdeaId={0} logicalTradeId={1} recoveryId={2} sourcePositionId={3} restoredPositionId={4} failedStep={5} reason={6} outcome={7} failedAttempts={8} maxAttempts={9}",
                FormatTradeIdeaId(snapshot.TradeIdeaId), snapshot.LogicalTradeId,
                snapshot.RecoveryId, snapshot.SourcePositionId,
                snapshot.RestoredObjectId.HasValue ? snapshot.RestoredObjectId.Value.ToString(CultureInfo.InvariantCulture) : "none",
                snapshot.RestoredObjectId.HasValue ? "protection" : "execution",
                snapshot.LastRestorationFailure,
                outcome,
                snapshot.RestorationAttempts,
                RolloverPositionRestorationMaxAttempts);
            return FailRestoration("position", snapshot.LastRestorationFailure);
        }

        private bool HandlePendingOrderRestorationOutcome(
            PendingOrderSnapshot snapshot,
            RestorationOutcome outcome,
            int snapshotIndex)
        {
            string warningKey = GetTransientOrderWarningKey(snapshot);
            if (outcome == RestorationOutcome.Restored || outcome == RestorationOutcome.PermanentlySkipped)
            {
                ClearWarning(warningKey, "ROLLOVER", "ELIGIBILITY_RESOLVED",
                    "pending-order snapshot restoration eligibility resolved");
                if (outcome == RestorationOutcome.Restored)
                {
                    _rolloverRestoredOrderSnapshots++;
                }
                else
                {
                    snapshot.RecoveryStatus = SnapshotRecoveryStatus.PermanentlySkipped;
                    snapshot.ResolvedUtc = Server.TimeInUtc;
                    _rolloverSkippedOrderSnapshots++;
                    LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_SKIPPED",
                        "schemaVersion=4 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} reason={3}",
                        FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId), snapshot.RecoveryId,
                        snapshot.SourceOrderId, snapshot.ResolutionReason ?? "unspecified");
                }
                _orderSnapshots.RemoveAt(snapshotIndex);
                return true;
            }
            if (outcome == RestorationOutcome.PermanentFailure)
            {
                TerminateTransientEligibilityWait(snapshot, "PermanentFailure");
                snapshot.RecoveryStatus = SnapshotRecoveryStatus.Failed;
                snapshot.FailureCounted = true;
                _rolloverFailedOrderSnapshots++;
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_FAILED",
                    "schemaVersion=4 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} outcome=permanentFailure",
                    FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId),
                    snapshot.RecoveryId,
                    snapshot.SourceOrderId);
                return FailRestoration("pending order", snapshot.LastRestorationFailure);
            }
            return outcome == RestorationOutcome.TransientMarketCondition
                ? ScheduleTransientRestorationRetry(snapshot, "pending order")
                : ScheduleOperationalRestorationRetry(snapshot, "pending order");
        }

        private string GetTransientPositionWarningKey(PositionSnapshot snapshot)
        {
            return "rollover-transient-position-" + snapshot.RecoveryId;
        }

        private string GetTransientOrderWarningKey(PendingOrderSnapshot snapshot)
        {
            return "rollover-transient-order-" + snapshot.RecoveryId;
        }

        private void TerminateTransientEligibilityWait(
            PositionSnapshot snapshot,
            string completionReason)
        {
            if (snapshot == null)
                return;
            ClearWarning(GetTransientPositionWarningKey(snapshot),
                "ROLLOVER", "ELIGIBILITY_TERMINATED",
                string.Format(CultureInfo.InvariantCulture,
                    "snapshotType=position recoveryId={0} sourceId={1} completionReason={2} lastFailure={3}",
                    snapshot.RecoveryId, snapshot.SourcePositionId,
                    completionReason,
                    snapshot.LastRestorationFailure ?? "unspecified"));
        }

        private void TerminateTransientEligibilityWait(
            PendingOrderSnapshot snapshot,
            string completionReason)
        {
            if (snapshot == null)
                return;
            ClearWarning(GetTransientOrderWarningKey(snapshot),
                "ROLLOVER", "ELIGIBILITY_TERMINATED",
                string.Format(CultureInfo.InvariantCulture,
                    "snapshotType=pendingOrder recoveryId={0} completionReason={1} lastFailure={2}",
                    snapshot.RecoveryId, completionReason,
                    snapshot.LastRestorationFailure ?? "unspecified"));
        }

        private bool ScheduleTransientRestorationRetry(PositionSnapshot snapshot, string snapshotType)
        {
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.WaitingForEligibility;
            DateTime now = Server.TimeInUtc;
            snapshot.NextRestorationAttemptUtc = now.AddSeconds(RolloverPositionRestorationRetrySeconds);
            LogActiveWarning(GetTransientPositionWarningKey(snapshot), "ROLLOVER", "WAITING_FOR_ELIGIBILITY",
                "{0} restoration attempt {1}/{2} failed; next attempt scheduled at {3:yyyy-MM-dd HH:mm:ss} UTC ({4} seconds). Reason: {5}",
                snapshotType, snapshot.RestorationAttempts,
                RolloverPositionRestorationMaxAttempts,
                snapshot.NextRestorationAttemptUtc,
                RolloverPositionRestorationRetrySeconds,
                snapshot.LastRestorationFailure ?? "unspecified");
            return true;
        }

        private bool ScheduleTransientRestorationRetry(PendingOrderSnapshot snapshot, string snapshotType)
        {
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.WaitingForEligibility;
            DateTime now = Server.TimeInUtc;
            if (snapshot.FirstRestorationAttemptUtc == DateTime.MinValue)
                snapshot.FirstRestorationAttemptUtc = now;
            if ((now - snapshot.FirstRestorationAttemptUtc).TotalMinutes >= RolloverTransientMaxWaitMinutes)
            {
                TerminateTransientEligibilityWait(snapshot, "RestorationDeadline");
                snapshot.RecoveryStatus = SnapshotRecoveryStatus.Failed;
                snapshot.ResolvedUtc = now;
                return FailRestoration(snapshotType,
                    "transient eligibility did not recover before the restoration deadline. Last failure: " +
                    (snapshot.LastRestorationFailure ?? "unspecified"));
            }
            snapshot.NextRestorationAttemptUtc = now.AddSeconds(RolloverTransientRetrySeconds);
            LogActiveWarning(GetTransientOrderWarningKey(snapshot), "ROLLOVER", "WAITING_FOR_ELIGIBILITY",
                "{0} restoration is waiting for market eligibility; retry in {1} seconds, deadline={2:yyyy-MM-dd HH:mm:ss} UTC. Reason: {3}",
                snapshotType, RolloverTransientRetrySeconds,
                snapshot.FirstRestorationAttemptUtc.AddMinutes(RolloverTransientMaxWaitMinutes),
                snapshot.LastRestorationFailure ?? "unspecified");
            return true;
        }

        private bool ScheduleOperationalRestorationRetry(PositionSnapshot snapshot, string snapshotType)
        {
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.Captured;
            snapshot.NextRestorationAttemptUtc = Server.TimeInUtc.AddSeconds(RolloverPositionRestorationRetrySeconds);
            LogNormal("ROLLOVER", "OPERATION_RETRY_SCHEDULED",
                "snapshotType={0} failedAttempt={1} nextAttempt={2} maxAttempts={3} retryScheduled=true retryDelaySeconds={4} reason={5}",
                snapshotType,
                snapshot.RestorationAttempts,
                snapshot.RestorationAttempts + 1,
                RolloverPositionRestorationMaxAttempts,
                RolloverPositionRestorationRetrySeconds,
                snapshot.LastRestorationFailure ?? "unspecified");
            return true;
        }

        private bool ScheduleOperationalRestorationRetry(PendingOrderSnapshot snapshot, string snapshotType)
        {
            snapshot.RecoveryStatus = SnapshotRecoveryStatus.Captured;
            snapshot.RestorationAttempts++;
            snapshot.NextRestorationAttemptUtc = Server.TimeInUtc.AddSeconds(RolloverRestorationRetrySeconds);
            return HandleOperationalRestorationRetry(snapshot.RestorationAttempts,
                snapshot.LastRestorationFailure, snapshotType);
        }

        private bool HandleOperationalRestorationRetry(int attempts, string failure, string snapshotType)
        {
            if (attempts >= RolloverRestorationMaxAttempts)
                return FailRestoration(snapshotType,
                    string.Format(CultureInfo.InvariantCulture,
                        "exhausted {0} broker-operation attempts. Last failure: {1}",
                        RolloverRestorationMaxAttempts, failure ?? "unspecified"));
            LogNormal("ROLLOVER", "OPERATION_RETRY_SCHEDULED",
                "snapshotType={0} attempt={1} maxAttempts={2} retryScheduled=true retryDelaySeconds={3} reason={4}",
                snapshotType, attempts, RolloverRestorationMaxAttempts,
                RolloverRestorationRetrySeconds, failure ?? "unspecified");
            return true;
        }

        private bool FailRestoration(string snapshotType, string failure)
        {
            EnterRecoveryCleanupPhase(snapshotType + " restoration failed");
            LogError("ROLLOVER", "RESTORATION_FAILED",
                "snapshotType={0} outcome=Failed cleanupScheduled=true reason={1}",
                snapshotType, failure ?? "unspecified");
            return false;
        }

        #endregion

    }
}
