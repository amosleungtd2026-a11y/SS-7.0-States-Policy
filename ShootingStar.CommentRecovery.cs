using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        // Keep USI1 for existing comments; its GUID is a submission correlation before fill,
        // then the adopted tradeIdeaId after the first fill.
        private const string LineageCommentPrefix = "USI1";

        private static string FormatLineageComment(Guid lineageId)
        {
            return LineageCommentPrefix + lineageId.ToString("N");
        }

        private static bool TryParseLineageComment(string comment, out Guid lineageId)
        {
            lineageId = Guid.Empty;
            int lineageLength = LineageCommentPrefix.Length + 32;
            return comment != null &&
                comment.Length >= lineageLength &&
                comment.StartsWith(LineageCommentPrefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(
                    comment.Substring(LineageCommentPrefix.Length, 32),
                    "N",
                    out lineageId) &&
                lineageId != Guid.Empty &&
                (comment.Length == lineageLength ||
                 (comment[lineageLength] == PersistentCapsuleSeparator &&
                  comment.Length > lineageLength + 1));
        }

        private void VerifyLineageComment(
            Guid? lineageId,
            string actualComment,
            string kind,
            int objectId)
        {
            if (!ShouldPersistTradeIdeaLineage ||
                !lineageId.HasValue ||
                lineageId.Value == Guid.Empty)
                return;
            if (!TryParseLineageComment(actualComment, out Guid parsed) ||
                parsed != lineageId.Value)
            {
                LogActiveWarning("lineage-comment-" + kind + "-" + objectId,
                    "TRADE_LIFECYCLE", "LINEAGE_COMMENT_UNCONFIRMED",
                    "schemaVersion=4 kind={0} objectId={1} lineageId={2} outcome=restartRecoveryNotGuaranteed",
                    kind, objectId, lineageId.Value);
                return;
            }

            ClearWarning("lineage-comment-" + kind + "-" + objectId,
                "TRADE_LIFECYCLE", "LINEAGE_COMMENT_CONFIRMED",
                "broker returned the expected lineage ID");
        }

        private void RecoverLineageFromComments()
        {
            if (Account.AccountType != AccountType.Hedged &&
                Account.AccountType != AccountType.Netted)
            {
                LogError("TRADE_LIFECYCLE", "LINEAGE_COMMENT_RECOVERY_UNAVAILABLE",
                    "accountType={0} outcome=skipped reason=unsupportedAccountType",
                    Account.AccountType);
                return;
            }

            LogNormal("TRADE_LIFECYCLE", "LINEAGE_COMMENT_RECOVERY_STARTED",
                "accountType={0} recoverOrders={1} recoverPositions={2} capsuleRecoveryAlwaysEnabled=true",
                Account.AccountType,
                RecoverIdeaIdsFromComments,
                Account.AccountType == AccountType.Hedged &&
                    RecoverIdeaIdsFromComments);
            try
            {
                RecoverLiveLineageFromComments();
                LogNormal("TRADE_LIFECYCLE", "LINEAGE_COMMENT_RECOVERY_COMPLETED",
                    "accountType={0} outcome=completed",
                    Account.AccountType);
            }
            catch (Exception exception)
            {
                LogError("TRADE_LIFECYCLE", "LINEAGE_COMMENT_RECOVERY_FAILED",
                    "outcome=partialOrUnavailable exceptionType={0} message={1}",
                    exception.GetType().Name, exception.Message);
            }
        }

        private void RecoverLiveLineageFromComments()
        {
            var orders = new List<PendingOrder>();
            var positions = new List<Position>();
            CaptureBotPendingOrders(orders);
            CaptureBotPositions(positions);

            foreach (PendingOrder order in orders)
            {
                if (!RecoverIdeaIdsFromComments &&
                    !HasPersistentCapsuleMarker(order.Comment))
                    continue;

                if (!TryParseLineageComment(
                    order.Comment,
                    out Guid submissionCorrelationId))
                {
                    if (!string.IsNullOrEmpty(order.Comment))
                    {
                        SetPendingOrderEvidence(
                            order.Id, CreateUnknownTradeEvidence());
                    }
                    LogVerbose("TRADE_LIFECYCLE", "SUBMISSION_CORRELATION_RECOVERY_UNAVAILABLE",
                        "schemaVersion=4 kind=order objectId={0} reason=missingOrInvalidSubmissionCorrelationComment",
                        order.Id);
                    continue;
                }
                if (_submissionCorrelationIdsByOrderId.TryGetValue(
                    order.Id,
                    out Guid existing) &&
                    existing != submissionCorrelationId)
                {
                    LogError("TRADE_LIFECYCLE", "SUBMISSION_CORRELATION_RECOVERY_CONFLICT",
                        "schemaVersion=4 kind=order objectId={0} existingSubmissionCorrelationId={1} commentSubmissionCorrelationId={2}",
                        order.Id, existing, submissionCorrelationId);
                    SetPendingOrderEvidence(
                        order.Id, CreateUnknownTradeEvidence());
                    continue;
                }
                _submissionCorrelationIdsByOrderId[order.Id] =
                    submissionCorrelationId;
                SetPendingOrderEvidence(
                    order.Id,
                    CreatePendingOrderEvidence(
                        LifecycleContinuityEvidence.RestartGap,
                        SegmentProvenanceEvidence.BrokerObserved));
                LogVerbose("TRADE_LIFECYCLE", "SUBMISSION_CORRELATION_RECOVERED",
                    "schemaVersion=5 submissionCorrelationId={0} objectId={1} source=brokerComment lifecycleContinuity={2} segmentProvenance={3}",
                    submissionCorrelationId, order.Id,
                    LifecycleContinuityEvidence.RestartGap,
                    SegmentProvenanceEvidence.BrokerObserved);
            }

            foreach (Position position in positions)
            {
                if (!RecoverIdeaIdsFromComments &&
                    !HasPersistentCapsuleMarker(position.Comment))
                    continue;

                if (!TryParseLineageComment(position.Comment, out Guid tradeIdeaId))
                {
                    if (!string.IsNullOrEmpty(position.Comment))
                    {
                        AddPositionEvidence(
                            position.Id, CreateUnknownTradeEvidence());
                    }
                    LogVerbose("TRADE_LIFECYCLE", "IDEA_RECOVERY_UNAVAILABLE",
                        "schemaVersion=2 kind=position objectId={0} reason=missingOrInvalidComment", position.Id);
                    continue;
                }
                if (Account.AccountType != AccountType.Hedged)
                {
                    AddPositionEvidence(
                        position.Id,
                        AddPositionAttributionEvidence(
                            CreatePendingOrderEvidence(
                                LifecycleContinuityEvidence.RestartGap,
                                SegmentProvenanceEvidence.BrokerObserved),
                            PositionAttributionEvidence.AmbiguousNetted));
                    LogVerbose("TRADE_LIFECYCLE", "IDEA_RECOVERY_UNAVAILABLE",
                        "schemaVersion=3 kind=position objectId={0} tradeIdeaId={1} reason=nettedPositionMayBeShared lifecycleContinuity={2} segmentProvenance={3} positionAttribution={4}",
                        position.Id, tradeIdeaId,
                        LifecycleContinuityEvidence.RestartGap,
                        SegmentProvenanceEvidence.BrokerObserved,
                        PositionAttributionEvidence.AmbiguousNetted);
                    continue;
                }
                string association = AssociateTradeIdeaWithPosition(
                    position.Id,
                    tradeIdeaId);
                if (association != "exclusive")
                {
                    AddPositionEvidence(
                        position.Id, CreateUnknownTradeEvidence());
                    LogError("TRADE_LIFECYCLE", "IDEA_RECOVERY_CONFLICT",
                        "kind=position objectId={0} tradeIdeaId={1} association={2}",
                        position.Id, tradeIdeaId, association);
                    continue;
                }
                AddPositionEvidence(
                    position.Id,
                    AddPositionAttributionEvidence(
                        CreatePendingOrderEvidence(
                            LifecycleContinuityEvidence.RestartGap,
                            SegmentProvenanceEvidence.BrokerObserved),
                        PositionAttributionEvidence.Exclusive));
                StartRecoveredTradeIdeaSummary(tradeIdeaId);
                AddIdeaPosition(tradeIdeaId, position.Id, false);
                LogVerbose("TRADE_LIFECYCLE", "IDEA_RECOVERED",
                    "schemaVersion=3 tradeIdeaId={0} kind=position objectId={1} source=brokerComment lineageCompleteness=partialSinceRecovery lifecycleContinuity={2} segmentProvenance={3} positionAttribution={4}",
                    tradeIdeaId, position.Id,
                    LifecycleContinuityEvidence.RestartGap,
                    SegmentProvenanceEvidence.BrokerObserved,
                    PositionAttributionEvidence.Exclusive);
            }
        }
    }
}
