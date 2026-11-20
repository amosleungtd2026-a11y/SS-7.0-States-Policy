using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private sealed class TradeIdeaSummary
        {
            public Guid Id;
            public DateTime StartedUtc;
            public EntryOriginMode EntryOrigin;
            public TradeEvidence Evidence;
            public readonly List<int> OrderIds = new List<int>();
            public readonly List<int> PositionIds = new List<int>();
            public readonly HashSet<int> ClosingDealIds = new HashSet<int>();
            public int PositionRestorations;
            public int OrderRestorations;
            public string TerminalOutcome;
            public DateTime? TerminalUtc;
            public double? ReportedLogicalNetProfit;
            public DateTime? ClosingHistoryObservedFromUtc;
            public bool ClosingHistoryReadAttempted;
            public bool ClosingHistoryReadSucceeded;
            public bool ClosingHistoryReadFailed;
        }

        private readonly Dictionary<Guid, TradeIdeaSummary> _tradeIdeaSummaries =
            new Dictionary<Guid, TradeIdeaSummary>();

        private void StartTradeIdeaSummary(Guid id, TradeEvidence evidence = null)
        {
            _tradeIdeaSummaries.Add(id, new TradeIdeaSummary
            {
                Id = id,
                StartedUtc = Server.TimeInUtc,
                EntryOrigin = GetTradeIdeaEntryOrigin(id),
                Evidence = evidence == null ? CreateUnknownTradeEvidence() : evidence.Copy()
            });
            SetTradeIdeaEntryOrigin(
                id,
                _tradeIdeaSummaries[id].EntryOrigin);
        }

        private void StartRecoveredTradeIdeaSummary(Guid id)
        {
            if (!_tradeIdeaSummaries.ContainsKey(id))
                _tradeIdeaSummaries.Add(id, new TradeIdeaSummary
                {
                    Id = id,
                    StartedUtc = Server.TimeInUtc,
                    EntryOrigin = GetTradeIdeaEntryOrigin(id),
                    Evidence = new TradeEvidence
                    {
                        LifecycleContinuity = LifecycleContinuityEvidence.RestartGap,
                        SegmentProvenance = SegmentProvenanceEvidence.BrokerObserved,
                        PositionAttribution = PositionAttributionEvidence.Exclusive
                    }
                });
        }

        private void CombineTradeIdeaSummaryEvidence(Guid id, TradeEvidence evidence)
        {
            if (evidence != null &&
                _tradeIdeaSummaries.TryGetValue(id, out TradeIdeaSummary summary))
                summary.Evidence = CombineTradeEvidence(summary.Evidence, evidence);
        }

        private void RecordTradeIdeaClosingHistory(
            Guid? ideaId,
            DateTime observedFromUtc,
            bool readAttempted,
            bool readSucceeded,
            bool readFailed)
        {
            if (!ideaId.HasValue ||
                !_tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary))
                return;

            if (!summary.ClosingHistoryObservedFromUtc.HasValue ||
                observedFromUtc < summary.ClosingHistoryObservedFromUtc.Value)
                summary.ClosingHistoryObservedFromUtc = observedFromUtc;
            summary.ClosingHistoryReadAttempted |= readAttempted;
            summary.ClosingHistoryReadSucceeded |= readSucceeded;
            summary.ClosingHistoryReadFailed |= readFailed;
        }

        private void AddIdeaOrder(Guid? ideaId, int orderId, bool restored)
        {
            if (!ideaId.HasValue || !_tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary))
                return;
            if (!summary.OrderIds.Contains(orderId))
            {
                summary.OrderIds.Add(orderId);
                if (restored)
                    summary.OrderRestorations++;
            }
        }

        private void AddIdeaPosition(Guid? ideaId, int positionId, bool restored)
        {
            if (!ideaId.HasValue || !_tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary))
                return;
            if (!summary.PositionIds.Contains(positionId))
            {
                summary.PositionIds.Add(positionId);
                if (restored)
                    summary.PositionRestorations++;
            }
        }

        private void AddIdeaClosingDeal(Guid? ideaId, int closingDealId)
        {
            if (ideaId.HasValue && _tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary))
                summary.ClosingDealIds.Add(closingDealId);
        }

        private void CompleteTradeIdea(
            Guid? ideaId,
            string outcome,
            double? reportedLogicalNetProfit = null,
            bool logicalNetProfitEvidenceComplete = true)
        {
            if (!ideaId.HasValue || !_tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary))
                return;
            if (summary.TerminalOutcome != null)
                return;
            summary.TerminalOutcome = outcome;
            summary.TerminalUtc = Server.TimeInUtc;
            summary.ReportedLogicalNetProfit =
                logicalNetProfitEvidenceComplete &&
                CanUseForLogicalMetrics(summary.Evidence)
                    ? reportedLogicalNetProfit
                    : null;
            if (_closingDealObservations.Values.Any(observation =>
                    observation.TradeIdeaId == ideaId && observation.PositionClosed))
                return;
            LogTradeIdeaSummary(summary, "terminal");
            _tradeIdeaSummaries.Remove(ideaId.Value);
        }

        private void LogTradeIdeaSummary(TradeIdeaSummary summary, string status)
        {
            int pendingObservations = _closingDealObservations.Values.Where(entry =>
                entry.TradeIdeaId == summary.Id && entry.PositionClosed).Count();
            LogVerbose("TRADE_LIFECYCLE", "TRADE_IDEA_SUMMARY",
                "schemaVersion=7 tradeIdeaId={0} entryOrigin={1} status={2} outcome={3} startedUtc={4:O} terminalUtc={5} orderIds={6} positionIds={7} orderRestorations={8} positionRestorations={9} observedClosingDealIds={10} pendingDealObservations={11} historyCompleteness=unverified closingHistoryObservedFromUtc={12} closingHistoryStatus={13} closingHistoryReadFailed={14} lifecycleContinuity={15} segmentProvenance={16} positionAttribution={17} reportedLogicalNetProfit={18} logicalNetProfitReliable={19} profitSource={20} lineageCompleteness={21} startedUtcBasis={22}",
                summary.Id, summary.EntryOrigin, status, summary.TerminalOutcome ?? "unresolved", summary.StartedUtc,
                summary.TerminalUtc.HasValue ? summary.TerminalUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none",
                summary.OrderIds.Count == 0 ? "none" : string.Join(",", summary.OrderIds),
                summary.PositionIds.Count == 0 ? "none" : string.Join(",", summary.PositionIds),
                summary.OrderRestorations, summary.PositionRestorations,
                summary.ClosingDealIds.Count == 0 ? "none" : string.Join(",", summary.ClosingDealIds.OrderBy(id => id)),
                pendingObservations,
                summary.ClosingHistoryObservedFromUtc.HasValue
                    ? summary.ClosingHistoryObservedFromUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                    : "notObserved",
                !summary.ClosingHistoryReadAttempted
                    ? "notObserved"
                    : summary.ClosingHistoryReadSucceeded
                        ? "observedUnverified"
                        : "unavailable",
                summary.ClosingHistoryReadFailed,
                summary.Evidence == null ? "unknown" : summary.Evidence.LifecycleContinuity.ToString(),
                summary.Evidence == null ? "unknown" : summary.Evidence.SegmentProvenance.ToString(),
                summary.Evidence == null ? "unknown" : summary.Evidence.PositionAttribution.ToString(),
                summary.ReportedLogicalNetProfit.HasValue
                    ? summary.ReportedLogicalNetProfit.Value.ToString("R", CultureInfo.InvariantCulture) : "unknown",
                summary.ReportedLogicalNetProfit.HasValue,
                summary.ReportedLogicalNetProfit.HasValue ? "existingLogicalStatistics" : "unavailable",
                GetLifecycleCompletenessLabel(summary.Evidence),
                summary.Evidence != null &&
                    summary.Evidence.LifecycleContinuity == LifecycleContinuityEvidence.RestartGap
                        ? "recoveryObservation"
                        : summary.Evidence != null &&
                          summary.Evidence.LifecycleContinuity == LifecycleContinuityEvidence.Continuous
                            ? "fill"
                            : "unknown");
        }

        private void TryFinishTradeIdeaSummary(Guid? ideaId)
        {
            if (!ideaId.HasValue || !_tradeIdeaSummaries.TryGetValue(ideaId.Value, out TradeIdeaSummary summary) ||
                summary.TerminalOutcome == null)
                return;
            if (_closingDealObservations.Values.Any(observation =>
                    observation.TradeIdeaId == ideaId && observation.PositionClosed))
                return;
            LogTradeIdeaSummary(summary, "dealObservationEnded");
            _tradeIdeaSummaries.Remove(ideaId.Value);
        }

        private void LogIncompleteTradeIdeasOnStop()
        {
            foreach (TradeIdeaSummary summary in _tradeIdeaSummaries.Values)
                LogTradeIdeaSummary(summary, summary.TerminalOutcome == null ? "incompleteAtStop" : "stoppedAfterTerminal");
        }
    }
}
