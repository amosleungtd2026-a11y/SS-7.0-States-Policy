using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private readonly Dictionary<int, Guid> _submissionCorrelationIdsByOrderId =
            new Dictionary<int, Guid>();
        private readonly Dictionary<int, Guid> _positionTradeIdeaIds = new Dictionary<int, Guid>();
        private readonly HashSet<int> _ambiguousIdeaPositionIds = new HashSet<int>();
        private readonly Dictionary<int, TradeEvidence> _pendingOrderEvidenceById =
            new Dictionary<int, TradeEvidence>();
        private readonly Dictionary<int, TradeEvidence> _positionEvidenceById =
            new Dictionary<int, TradeEvidence>();
        private readonly Dictionary<int, EntryOriginMode> _positionEntryOriginsById =
            new Dictionary<int, EntryOriginMode>();
        private readonly Dictionary<Guid, EntryOriginMode> _tradeIdeaEntryOrigins =
            new Dictionary<Guid, EntryOriginMode>();
        private readonly Dictionary<int, int> _fillsDuringOrderSubmission = new Dictionary<int, int>();
        private readonly HashSet<int> _cancellationsDuringOrderSubmission = new HashSet<int>();
        private bool _orderSubmissionTrackingInProgress;
        private bool _ordinaryOrderSubmissionInProgress;
        private EntryOriginMode _activeOrderSubmissionOrigin =
            EntryOriginMode.Unattributed;

        private EntryOriginMode GetPendingOrderEntryOrigin(int orderId)
        {
            if (_pendingOrderTrailingStates.TryGetValue(
                orderId, out PendingOrderTrailingState state))
                return state.EntryOrigin;
            return _ordinaryOrderSubmissionInProgress
                ? _activeOrderSubmissionOrigin
                : EntryOriginMode.Unattributed;
        }

        private void SetPendingOrderEntryOrigin(
            int orderId,
            EntryOriginMode entryOrigin)
        {
            if (_pendingOrderTrailingStates.TryGetValue(
                orderId, out PendingOrderTrailingState state))
                state.EntryOrigin = entryOrigin;
        }

        private EntryOriginMode GetPositionEntryOrigin(int positionId)
        {
            if (_positionEntryOriginsById.TryGetValue(
                positionId, out EntryOriginMode entryOrigin))
                return entryOrigin;
            if (_ordinaryOrderSubmissionInProgress)
                return _activeOrderSubmissionOrigin;
            return _positionStates.TryGetValue(
                positionId, out PositionState state)
                ? state.EntryOrigin
                : EntryOriginMode.Unattributed;
        }

        private void SetPositionEntryOrigin(
            int positionId,
            EntryOriginMode entryOrigin)
        {
            _positionEntryOriginsById[positionId] = entryOrigin;
            if (_positionStates.TryGetValue(positionId, out PositionState state))
                state.EntryOrigin = entryOrigin;
        }

        private EntryOriginMode GetTradeIdeaEntryOrigin(Guid tradeIdeaId)
        {
            if (_tradeIdeaEntryOrigins.TryGetValue(
                tradeIdeaId, out EntryOriginMode entryOrigin))
                return entryOrigin;
            return _ordinaryOrderSubmissionInProgress
                ? _activeOrderSubmissionOrigin
                : EntryOriginMode.Unattributed;
        }

        private void SetTradeIdeaEntryOrigin(
            Guid tradeIdeaId,
            EntryOriginMode entryOrigin)
        {
            _tradeIdeaEntryOrigins[tradeIdeaId] = entryOrigin;
        }

        private bool TryGetFitnessEntryOrigin(
            int positionId,
            string comment,
            out EntryOriginMode entryOrigin)
        {
            if (_positionEntryOriginsById.TryGetValue(positionId, out entryOrigin))
                return true;
            if (TryParseLineageComment(comment, out Guid tradeIdeaId) &&
                _tradeIdeaEntryOrigins.TryGetValue(tradeIdeaId, out entryOrigin))
                return true;
            entryOrigin = EntryOriginMode.Unattributed;
            return false;
        }

        private bool ShouldPersistTradeIdeaLineage => RecoverIdeaIdsFromComments;
        private bool ShouldTrackTradeIdeaLineage => true;

        private enum BufferedOrderOutcome
        {
            Active,
            Filled,
            Cancelled
        }

        private static BufferedOrderOutcome GetBufferedOrderOutcome(
            int orderId, Dictionary<int, int> fills, HashSet<int> cancellations,
            out int positionId)
        {
            if (fills.TryGetValue(orderId, out positionId))
                return BufferedOrderOutcome.Filled;
            return cancellations.Contains(orderId)
                ? BufferedOrderOutcome.Cancelled
                : BufferedOrderOutcome.Active;
        }

        private static string FormatTradeIdeaId(Guid? id)
        {
            return id.HasValue ? id.Value.ToString() : "untracked";
        }

        private static string FormatSubmissionCorrelationId(Guid? id)
        {
            return id.HasValue ? id.Value.ToString() : "none";
        }

        // For Normal-level lines: the field is appended only when an idea ID is known,
        // so those lines are unchanged when idea tracking is off.
        private static string FormatKnownTradeIdeaField(Guid? id)
        {
            return id.HasValue ? " tradeIdeaId=" + id.Value.ToString() : string.Empty;
        }

        private Guid? GetSubmissionCorrelationId(int orderId)
        {
            return _submissionCorrelationIdsByOrderId.TryGetValue(orderId, out Guid id)
                ? id
                : (Guid?)null;
        }

        private Guid? GetPositionTradeIdeaId(int positionId)
        {
            return _positionTradeIdeaIds.TryGetValue(positionId, out Guid id) ? id : (Guid?)null;
        }

        private TradeEvidence GetPendingOrderEvidence(int orderId)
        {
            if (_pendingOrderEvidenceById.TryGetValue(orderId, out TradeEvidence evidence))
                return evidence.Copy();
            if (_pendingOrderTrailingStates.TryGetValue(
                orderId, out PendingOrderTrailingState state) &&
                state.Evidence != null)
                return state.Evidence.Copy();
            return null;
        }

        private TradeEvidence GetPositionEvidence(int positionId)
        {
            if (_positionEvidenceById.TryGetValue(positionId, out TradeEvidence evidence))
                return evidence.Copy();
            return _positionStates.TryGetValue(positionId, out PositionState state) &&
                   state.Evidence != null
                ? state.Evidence.Copy()
                : null;
        }

        private void SetPendingOrderEvidence(int orderId, TradeEvidence evidence)
        {
            if (evidence == null)
                _pendingOrderEvidenceById.Remove(orderId);
            else
                _pendingOrderEvidenceById[orderId] = evidence.Copy();

            if (_pendingOrderTrailingStates.TryGetValue(
                orderId, out PendingOrderTrailingState state))
                state.Evidence = evidence == null ? null : evidence.Copy();
        }

        private void AddPositionEvidence(int positionId, TradeEvidence evidence)
        {
            if (evidence == null)
                return;

            TradeEvidence combined = CombineTradeEvidence(
                GetPositionEvidence(positionId), evidence);
            _positionEvidenceById[positionId] = combined.Copy();
            if (_positionStates.TryGetValue(positionId, out PositionState state))
                state.Evidence = combined.Copy();
        }

        private PositionAttributionEvidence GetAccountPositionAttribution()
        {
            if (Account.AccountType == AccountType.Hedged)
                return PositionAttributionEvidence.Exclusive;
            if (Account.AccountType == AccountType.Netted)
                return PositionAttributionEvidence.AmbiguousNetted;
            return PositionAttributionEvidence.Unknown;
        }

        private void InitializeStartupRecoveryEvidence()
        {
            var orders = new List<PendingOrder>();
            var positions = new List<Position>();
            CaptureBotPendingOrders(orders);
            CaptureBotPositions(positions);

            TradeEvidence orderEvidence = CreatePendingOrderEvidence(
                LifecycleContinuityEvidence.RestartGap,
                SegmentProvenanceEvidence.BrokerObserved);
            foreach (PendingOrder order in orders)
                SetPendingOrderEvidence(order.Id, orderEvidence);

            TradeEvidence positionEvidence = AddPositionAttributionEvidence(
                CreatePendingOrderEvidence(
                    LifecycleContinuityEvidence.RestartGap,
                    SegmentProvenanceEvidence.BrokerObserved),
                GetAccountPositionAttribution());
            foreach (Position position in positions)
                AddPositionEvidence(position.Id, positionEvidence);
        }

        private Guid? BeginOrderSubmissionTracking(
            StrategyKind strategy,
            TradeType side,
            double target,
            double volume,
            Guid? preferredSubmissionCorrelationId = null)
        {
            if (_ordinaryOrderSubmissionInProgress)
            {
                LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_OVERLAP",
                    "nested pending-order submission cannot be tracked reliably");
                return null;
            }

            _ordinaryOrderSubmissionInProgress = true;
            if (!ShouldTrackTradeIdeaLineage)
                return null;

            if (_orderSubmissionTrackingInProgress)
            {
                _ordinaryOrderSubmissionInProgress = false;
                LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_OVERLAP",
                    "nested pending-order submission cannot be correlated reliably");
                return null;
            }

            _orderSubmissionTrackingInProgress = true;
            _fillsDuringOrderSubmission.Clear();
            _cancellationsDuringOrderSubmission.Clear();
            Guid submissionCorrelationId =
                preferredSubmissionCorrelationId.HasValue &&
                preferredSubmissionCorrelationId.Value != Guid.Empty
                ? preferredSubmissionCorrelationId.Value
                : Guid.NewGuid();
            LogVerbose("TRADE_LIFECYCLE", "ORDER_SUBMISSION_STARTED",
                "schemaVersion=4 submissionCorrelationId={0} symbol={1} strategy={2} side={3} target={4} volume={5:R}",
                submissionCorrelationId, Symbol.Name, strategy, side,
                FormatPrice(target), volume);
            return submissionCorrelationId;
        }

        private void FinishOrderSubmissionTracking(
            Guid? submissionCorrelationId,
            TradeResult result)
        {
            if (!submissionCorrelationId.HasValue)
            {
                FinishUntrackedOrderSubmissionEvidence(result);
                return;
            }

            try
            {
                if (result == null || !result.IsSuccessful)
                {
                    LogVerbose("TRADE_LIFECYCLE", "ORDER_SUBMISSION_FAILED",
                        "schemaVersion=4 submissionCorrelationId={0} outcome={1} brokerError={2}",
                        submissionCorrelationId.Value, result == null ? "noResult" : "rejected",
                        result == null ? "nullResult" : result.Error.ToString());
                    return;
                }

                if (result.PendingOrder == null)
                {
                    LogVerbose("TRADE_LIFECYCLE", "ORDER_SUBMISSION_UNRESOLVED",
                        "schemaVersion=4 submissionCorrelationId={0} reason=successfulResultWithoutPendingOrder",
                        submissionCorrelationId.Value);
                    return;
                }

                int orderId = result.PendingOrder.Id;

                BufferedOrderOutcome outcome = GetBufferedOrderOutcome(
                    orderId, _fillsDuringOrderSubmission, _cancellationsDuringOrderSubmission, out int positionId);
                if (outcome == BufferedOrderOutcome.Filled)
                {
                    PromoteSubmissionToTradeIdea(
                        orderId,
                        positionId,
                        submissionCorrelationId.Value,
                        "deferredFill");
                    _pendingOrderEvidenceById.Remove(orderId);
                }
                else if (outcome == BufferedOrderOutcome.Cancelled)
                {
                    LogVerbose("TRADE_LIFECYCLE", "ORDER_TERMINAL_LINKED",
                        "schemaVersion=4 submissionCorrelationId={0} orderId={1} outcome=cancelledWithoutFillBeforePlacementReturned",
                        submissionCorrelationId.Value, orderId);
                }
                else
                    _submissionCorrelationIdsByOrderId[orderId] =
                        submissionCorrelationId.Value;
            }
            finally
            {
                _fillsDuringOrderSubmission.Clear();
                _cancellationsDuringOrderSubmission.Clear();
                _orderSubmissionTrackingInProgress = false;
                _ordinaryOrderSubmissionInProgress = false;
            }
        }

        private void FinishUntrackedOrderSubmissionEvidence(TradeResult result)
        {
            try
            {
                if (result == null || !result.IsSuccessful || result.PendingOrder == null)
                    return;

                int orderId = result.PendingOrder.Id;
                TradeEvidence evidence = GetPendingOrderEvidence(orderId) ??
                    CreatePendingOrderEvidence(
                        LifecycleContinuityEvidence.Continuous,
                        SegmentProvenanceEvidence.BrokerObserved);
                SetPendingOrderEvidence(orderId, evidence);
                if (GetBufferedOrderOutcome(
                    orderId, _fillsDuringOrderSubmission, _cancellationsDuringOrderSubmission,
                    out int positionId) == BufferedOrderOutcome.Filled)
                {
                    AddPositionEvidence(
                        positionId,
                        AddPositionAttributionEvidence(
                            evidence, GetAccountPositionAttribution()));
                    RefreshAdaptiveEligibilityForFilledPosition(positionId);
                    _pendingOrderEvidenceById.Remove(orderId);
                }
            }
            finally
            {
                _fillsDuringOrderSubmission.Clear();
                _cancellationsDuringOrderSubmission.Clear();
                _ordinaryOrderSubmissionInProgress = false;
            }
        }

        private void AbortOrderSubmissionTracking(
            Guid? submissionCorrelationId,
            Exception exception)
        {
            if (!submissionCorrelationId.HasValue)
            {
                _ordinaryOrderSubmissionInProgress = false;
                _fillsDuringOrderSubmission.Clear();
                _cancellationsDuringOrderSubmission.Clear();
                return;
            }
            _orderSubmissionTrackingInProgress = false;
            _ordinaryOrderSubmissionInProgress = false;
            _fillsDuringOrderSubmission.Clear();
            _cancellationsDuringOrderSubmission.Clear();
            LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_EXCEPTION",
                "schemaVersion=4 submissionCorrelationId={0} outcome=unknown exceptionType={1} message={2}",
                submissionCorrelationId.Value, exception.GetType().Name, exception.Message);
        }

        private string RecordTradeIdeaFill(int orderId, int positionId)
        {
            if (_ordinaryOrderSubmissionInProgress &&
                GetPendingOrderEvidence(orderId) == null)
            {
                _fillsDuringOrderSubmission[orderId] = positionId;
                return ShouldTrackTradeIdeaLineage
                    ? "deferredLink"
                    : "notLinked";
            }
            if (!ShouldTrackTradeIdeaLineage)
                return "notLinked";
            if (_submissionCorrelationIdsByOrderId.TryGetValue(
                orderId,
                out Guid submissionCorrelationId))
            {
                string association = PromoteSubmissionToTradeIdea(
                    orderId,
                    positionId,
                    submissionCorrelationId,
                    "fillEvent");
                _submissionCorrelationIdsByOrderId.Remove(orderId);
                return association;
            }
            if (_ordinaryOrderSubmissionInProgress)
            {
                _fillsDuringOrderSubmission[orderId] = positionId;
                return "deferredLink";
            }
            return "notLinked";
        }

        private string PromoteSubmissionToTradeIdea(
            int orderId,
            int positionId,
            Guid submissionCorrelationId,
            string source)
        {
            Guid tradeIdeaId = submissionCorrelationId;
            TradeEvidence orderEvidence =
                GetPendingOrderEvidence(orderId) ??
                GetPositionEvidence(positionId);
            if (orderEvidence == null)
            {
                orderEvidence = _ordinaryOrderSubmissionInProgress
                    ? CreatePendingOrderEvidence(
                        LifecycleContinuityEvidence.Continuous,
                        SegmentProvenanceEvidence.BrokerObserved)
                    : CreateUnknownTradeEvidence();
            }
            TradeEvidence positionEvidence = AddPositionAttributionEvidence(
                orderEvidence, GetAccountPositionAttribution());
            AddPositionEvidence(positionId, positionEvidence);
            positionEvidence = GetPositionEvidence(positionId) ?? positionEvidence;

            if (!_tradeIdeaSummaries.ContainsKey(tradeIdeaId))
            {
                StartTradeIdeaSummary(tradeIdeaId, positionEvidence);
                LogVerbose("TRADE_LIFECYCLE", "SUBMISSION_PROMOTED_TO_TRADE_IDEA",
                    "schemaVersion=1 submissionCorrelationId={0} tradeIdeaId={1} orderId={2} positionId={3} source={4}",
                    submissionCorrelationId,
                    tradeIdeaId,
                    orderId,
                    positionId,
                    source);
                LogVerbose("TRADE_LIFECYCLE", "TRADE_IDEA_STARTED",
                    "schemaVersion=4 tradeIdeaId={0} originatingSubmissionCorrelationId={1} orderId={2} positionId={3} source={4}",
                    tradeIdeaId,
                    submissionCorrelationId,
                    orderId,
                    positionId,
                    source);
            }
            CombineTradeIdeaSummaryEvidence(tradeIdeaId, positionEvidence);
            AddIdeaOrder(tradeIdeaId, orderId, false);
            AddIdeaPosition(tradeIdeaId, positionId, false);
            Position filledPosition = Positions.FindById(positionId);
            if (filledPosition != null)
                VerifyLineageComment(
                    tradeIdeaId,
                    filledPosition.Comment,
                    "position",
                    positionId);
            string association = AssociateTradeIdeaWithPosition(
                positionId,
                tradeIdeaId);
            PositionAttributionEvidence associationEvidence =
                association == "exclusive"
                    ? PositionAttributionEvidence.Exclusive
                    : association == "nettedPositionMayBeShared"
                        ? PositionAttributionEvidence.AmbiguousNetted
                        : PositionAttributionEvidence.Unknown;
            AddPositionEvidence(
                positionId,
                AddPositionAttributionEvidence(
                    positionEvidence, associationEvidence));
            CombineTradeIdeaSummaryEvidence(
                tradeIdeaId,
                AddPositionAttributionEvidence(
                    positionEvidence, associationEvidence));
            if (source != "fillEvent")
                RefreshAdaptiveEligibilityForFilledPosition(positionId);
            if (_closingDealObservations.TryGetValue(positionId, out ClosingDealObservation observation))
            {
                observation.Evidence = CombineTradeEvidence(
                    observation.Evidence,
                    positionEvidence);
                RecordTradeIdeaClosingHistory(
                    tradeIdeaId,
                    observation.FirstHistoryObservationUtc,
                    observation.HistoryReadAttempted,
                    observation.HistoryReadSucceeded,
                    observation.HistoryReadFailed);
                if (association == "conflictingIdeas" ||
                    (observation.TradeIdeaId.HasValue &&
                     observation.TradeIdeaId.Value != tradeIdeaId))
                    observation.TradeIdeaId = null;
                else if (Account.AccountType == AccountType.Hedged &&
                    !_ambiguousIdeaPositionIds.Contains(positionId))
                {
                    observation.TradeIdeaId = tradeIdeaId;
                    foreach (int dealId in observation.SeenDealIds)
                        AddIdeaClosingDeal(tradeIdeaId, dealId);
                }
                observation.Evidence = CombineTradeEvidence(
                    observation.Evidence,
                    AddPositionAttributionEvidence(positionEvidence, associationEvidence));
            }
            if (source != "fillEvent")
            {
                string logicalTradeId = _positionStates.TryGetValue(positionId, out PositionState state)
                    ? state.LogicalTradeId.ToString() : "notYetCreated";
                LogVerbose("TRADE_LIFECYCLE", "IDEA_POSITION_LINKED",
                    "schemaVersion=4 tradeIdeaId={0} orderId={1} positionId={2} logicalTradeId={3} source={4} association={5}",
                    tradeIdeaId, orderId, positionId, logicalTradeId, source, association);
            }
            return association;
        }

        private string AssociateTradeIdeaWithPosition(int positionId, Guid ideaId)
        {
            bool live = Positions.FindById(positionId) != null;
            bool exclusive = Account.AccountType == AccountType.Hedged;
            return UpdatePositionIdeaAssociation(
                _positionTradeIdeaIds, _ambiguousIdeaPositionIds, positionId, ideaId, live, exclusive);
        }

        private static string UpdatePositionIdeaAssociation(
            Dictionary<int, Guid> ideas, HashSet<int> ambiguousIds,
            int positionId, Guid ideaId, bool live, bool exclusive)
        {
            string association = !live ? "positionNotLive"
                : !exclusive ? "nettedPositionMayBeShared"
                : "exclusive";
            if (live && exclusive && ambiguousIds.Contains(positionId))
                association = "conflictingIdeas";
            else if (live && exclusive &&
                ideas.TryGetValue(positionId, out Guid existingId) &&
                existingId != ideaId)
            {
                ideas.Remove(positionId);
                ambiguousIds.Add(positionId);
                association = "conflictingIdeas";
            }
            else if (live && exclusive)
                ideas[positionId] = ideaId;
            return association;
        }

        private bool BeginRestoredOrderSubmissionTracking()
        {
            if (!ShouldTrackTradeIdeaLineage)
                return false;
            if (_orderSubmissionTrackingInProgress)
            {
                LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_OVERLAP",
                    "nested rollover order submission cannot be correlated reliably");
                return false;
            }
            _orderSubmissionTrackingInProgress = true;
            _fillsDuringOrderSubmission.Clear();
            _cancellationsDuringOrderSubmission.Clear();
            return true;
        }

        private void FinishRestoredOrderSubmissionTracking(
            PendingOrderSnapshot snapshot, TradeResult result, bool tracking)
        {
            if (!tracking)
                return;
            try
            {
                if (result == null || !result.IsSuccessful || result.PendingOrder == null)
                    return;

                int orderId = result.PendingOrder.Id;
                SetPendingOrderEvidence(
                    orderId,
                    AddRolloverReconstructionEvidence(snapshot.Evidence));
                AddIdeaOrder(snapshot.SubmissionCorrelationId, orderId, true);
                LogVerbose("TRADE_LIFECYCLE", "ROLLOVER_ORDER_LINKED",
                    "schemaVersion=4 submissionCorrelationId={0} recoveryId={1} sourceOrderId={2} restoredOrderId={3}",
                    FormatSubmissionCorrelationId(snapshot.SubmissionCorrelationId), snapshot.RecoveryId,
                    snapshot.SourceOrderId, orderId);

                if (!snapshot.SubmissionCorrelationId.HasValue)
                    return;
                BufferedOrderOutcome outcome = GetBufferedOrderOutcome(
                    orderId, _fillsDuringOrderSubmission, _cancellationsDuringOrderSubmission, out int positionId);
                if (outcome == BufferedOrderOutcome.Filled)
                    PromoteSubmissionToTradeIdea(
                        orderId,
                        positionId,
                        snapshot.SubmissionCorrelationId.Value,
                        "deferredRolloverFill");
                else if (outcome == BufferedOrderOutcome.Cancelled)
                {
                    LogVerbose("TRADE_LIFECYCLE", "ORDER_TERMINAL_LINKED",
                        "schemaVersion=4 submissionCorrelationId={0} orderId={1} outcome=cancelledWithoutFillBeforeRestorationReturned",
                        snapshot.SubmissionCorrelationId.Value, orderId);
                }
                else
                    _submissionCorrelationIdsByOrderId[orderId] =
                        snapshot.SubmissionCorrelationId.Value;
            }
            finally
            {
                _fillsDuringOrderSubmission.Clear();
                _cancellationsDuringOrderSubmission.Clear();
                _orderSubmissionTrackingInProgress = false;
            }
        }

        private void RecordOrderCancellationDuringSubmission(int orderId)
        {
            if (_orderSubmissionTrackingInProgress &&
                !_submissionCorrelationIdsByOrderId.ContainsKey(orderId))
                _cancellationsDuringOrderSubmission.Add(orderId);
            _submissionCorrelationIdsByOrderId.Remove(orderId);
        }
    }
}
