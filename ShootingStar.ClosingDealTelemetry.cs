using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private sealed class ClosingDealObservation
        {
            public readonly HashSet<int> SeenDealIds = new HashSet<int>();
            public Guid? TradeIdeaId;
            public Guid? LogicalTradeId;
            public TradeEvidence Evidence;
            public DateTime FirstHistoryObservationUtc;
            public DateTime ClosedUtc;
            public DateTime NextCheckUtc;
            public DateTime DeadlineUtc;
            public bool PositionClosed;
            public bool HistoryReadAttempted;
            public bool HistoryReadSucceeded;
            public bool HistoryReadFailed;
        }

        private readonly Dictionary<int, ClosingDealObservation> _closingDealObservations =
            new Dictionary<int, ClosingDealObservation>();
        private static readonly TimeSpan ClosingDealCheckInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ClosingDealObservationWindow = TimeSpan.FromMinutes(2);

        private enum ClosingDealRegistration
        {
            New,
            Duplicate,
            Invalid
        }

        private enum ClosingDealObservationAction
        {
            Wait,
            Poll,
            PollAndEnd
        }

        private static ClosingDealObservationAction GetClosingDealObservationAction(
            bool closed, DateTime now, DateTime nextCheckUtc, DateTime deadlineUtc)
        {
            if (!closed || now < nextCheckUtc)
                return ClosingDealObservationAction.Wait;
            return now >= deadlineUtc
                ? ClosingDealObservationAction.PollAndEnd
                : ClosingDealObservationAction.Poll;
        }

        private static ClosingDealRegistration RegisterClosingDeal(
            HashSet<int> seen, int positionId, int historyPositionId, int dealId)
        {
            if (historyPositionId != positionId || dealId <= 0)
                return ClosingDealRegistration.Invalid;
            return seen.Add(dealId) ? ClosingDealRegistration.New : ClosingDealRegistration.Duplicate;
        }

        private ClosingDealObservation GetClosingDealObservation(int positionId)
        {
            if (!_closingDealObservations.TryGetValue(positionId, out ClosingDealObservation observation))
            {
                observation = new ClosingDealObservation();
                _closingDealObservations.Add(positionId, observation);
            }

            Guid? ideaId = GetPositionTradeIdeaId(positionId);
            if (ideaId.HasValue)
                observation.TradeIdeaId = ideaId;
            if (_positionStates.TryGetValue(positionId, out PositionState positionState))
                observation.LogicalTradeId = positionState.LogicalTradeId;
            observation.Evidence = CombineTradeEvidence(
                observation.Evidence,
                GetPositionEvidence(positionId));
            if (observation.FirstHistoryObservationUtc == DateTime.MinValue)
                observation.FirstHistoryObservationUtc = Server.TimeInUtc;
            RecordTradeIdeaClosingHistory(
                observation.TradeIdeaId,
                observation.FirstHistoryObservationUtc,
                observation.HistoryReadAttempted,
                observation.HistoryReadSucceeded,
                observation.HistoryReadFailed);
            return observation;
        }

        private void ObserveClosingDeals(int positionId, ClosingDealObservation observation, HistoricalTrade[] trades)
        {
            if (trades == null)
            {
                observation.HistoryReadAttempted = true;
                observation.HistoryReadFailed = true;
                RecordTradeIdeaClosingHistory(
                    observation.TradeIdeaId,
                    observation.FirstHistoryObservationUtc,
                    true,
                    false,
                    true);
                LogActiveWarning("closing-deal-history-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_HISTORY_UNAVAILABLE",
                    "positionId={0} tradeIdeaId={1} retryScheduled={2}",
                    positionId, FormatTradeIdeaId(observation.TradeIdeaId), observation.PositionClosed);
                return;
            }

            observation.HistoryReadAttempted = true;
            observation.HistoryReadSucceeded = true;
            RecordTradeIdeaClosingHistory(
                observation.TradeIdeaId,
                observation.FirstHistoryObservationUtc,
                true,
                true,
                observation.HistoryReadFailed);
            ClearWarning("closing-deal-history-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_HISTORY_AVAILABLE",
                "position history is available");
            foreach (HistoricalTrade trade in trades.OrderBy(item => item.ClosingTime).ThenBy(item => item.ClosingDealId))
            {
                ClosingDealRegistration registration = RegisterClosingDeal(
                    observation.SeenDealIds, positionId, trade.PositionId, trade.ClosingDealId);
                if (registration == ClosingDealRegistration.Invalid)
                {
                    LogActiveWarning("closing-deal-invalid-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_ID_INVALID",
                        "positionId={0} historyPositionId={1} closingDealId={2}",
                        positionId, trade.PositionId, trade.ClosingDealId);
                    continue;
                }
                if (registration == ClosingDealRegistration.Duplicate)
                    continue;

                AddIdeaClosingDeal(observation.TradeIdeaId, trade.ClosingDealId);
                LogVerbose("TRADE_LIFECYCLE", "CLOSING_DEAL_OBSERVED",
                    "schemaVersion=2 tradeIdeaId={0} logicalTradeId={1} positionId={2} closingDealId={3} closingUtc={4:O}",
                    FormatTradeIdeaId(observation.TradeIdeaId),
                    FormatTradeIdeaId(observation.LogicalTradeId), positionId,
                    trade.ClosingDealId, trade.ClosingTime);
            }
        }

        private void ObserveOpenPositionClosingDeals(int positionId, HistoricalTrade[] trades)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            if ((trades == null || trades.Length == 0) &&
                !_closingDealObservations.ContainsKey(positionId))
                return;
            try
            {
                ObserveClosingDeals(positionId, GetClosingDealObservation(positionId), trades);
            }
            catch (Exception exception)
            {
                LogActiveWarning("closing-deal-telemetry-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_OBSERVATION_FAILED",
                    "positionId={0} exceptionType={1} message={2}", positionId, exception.GetType().Name, exception.Message);
            }
        }

        private void TryReadClosingDeals(int positionId, ClosingDealObservation observation)
        {
            observation.HistoryReadAttempted = true;
            try
            {
                ObserveClosingDeals(positionId, observation, History.FindByPositionId(positionId));
                ClearWarning("closing-deal-telemetry-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_OBSERVATION_RECOVERED",
                    "closing deal history observation succeeded");
            }
            catch (Exception exception)
            {
                observation.HistoryReadFailed = true;
                RecordTradeIdeaClosingHistory(
                    observation.TradeIdeaId,
                    observation.FirstHistoryObservationUtc,
                    true,
                    observation.HistoryReadSucceeded,
                    true);
                LogActiveWarning("closing-deal-telemetry-" + positionId, "TRADE_LIFECYCLE", "CLOSING_DEAL_OBSERVATION_FAILED",
                    "positionId={0} exceptionType={1} message={2} retryScheduled=true",
                    positionId, exception.GetType().Name, exception.Message);
            }
        }

        private void StartClosedPositionDealObservation(int positionId)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            ClosingDealObservation observation = GetClosingDealObservation(positionId);
            DateTime now = Server.TimeInUtc;
            observation.PositionClosed = true;
            observation.ClosedUtc = now;
            observation.NextCheckUtc = now.Add(ClosingDealCheckInterval);
            observation.DeadlineUtc = now.Add(ClosingDealObservationWindow);
            TryReadClosingDeals(positionId, observation);
        }

        private void ProcessClosedPositionDealObservations()
        {
            if (!ShouldLog(BotLogLevel.Verbose) || _closingDealObservations.Count == 0)
                return;
            DateTime now = Server.TimeInUtc;
            foreach (int positionId in new List<int>(_closingDealObservations.Keys))
            {
                ClosingDealObservation observation = _closingDealObservations[positionId];
                ClosingDealObservationAction action = GetClosingDealObservationAction(
                    observation.PositionClosed, now, observation.NextCheckUtc, observation.DeadlineUtc);
                if (action == ClosingDealObservationAction.Wait)
                    continue;

                TryReadClosingDeals(positionId, observation);
                if (action == ClosingDealObservationAction.PollAndEnd)
                {
                    LogClosingDealObservationEnd(positionId, observation, "observationWindowElapsed");
                    _closingDealObservations.Remove(positionId);
                    TryFinishTradeIdeaSummary(observation.TradeIdeaId);
                }
                else
                    observation.NextCheckUtc = now.Add(ClosingDealCheckInterval);
            }
        }

        private void LogClosingDealObservationEnd(
            int positionId, ClosingDealObservation observation, string reason)
        {
            LogVerbose("TRADE_LIFECYCLE", "CLOSING_DEAL_OBSERVATION_ENDED",
                "schemaVersion=4 tradeIdeaId={0} logicalTradeId={1} positionId={2} closedUtc={3} observedClosingDealIds={4} historyCompleteness=unverified historyObservedFromUtc={5} historyReadAttempted={6} historyReadSucceeded={7} historyReadFailed={8} lifecycleContinuity={9} segmentProvenance={10} positionAttribution={11} reason={12}",
                FormatTradeIdeaId(observation.TradeIdeaId),
                FormatTradeIdeaId(observation.LogicalTradeId), positionId,
                observation.PositionClosed
                    ? observation.ClosedUtc.ToString("O", CultureInfo.InvariantCulture)
                    : "notClosed",
                observation.SeenDealIds.Count == 0 ? "none" : string.Join(",", observation.SeenDealIds.OrderBy(id => id)),
                observation.FirstHistoryObservationUtc == DateTime.MinValue
                    ? "notObserved"
                    : observation.FirstHistoryObservationUtc.ToString("O", CultureInfo.InvariantCulture),
                observation.HistoryReadAttempted,
                observation.HistoryReadSucceeded,
                observation.HistoryReadFailed,
                observation.Evidence == null ? "unknown" : observation.Evidence.LifecycleContinuity.ToString(),
                observation.Evidence == null ? "unknown" : observation.Evidence.SegmentProvenance.ToString(),
                observation.Evidence == null ? "unknown" : observation.Evidence.PositionAttribution.ToString(),
                reason);
        }

        private void EndClosingDealObservationsOnStop()
        {
            foreach (KeyValuePair<int, ClosingDealObservation> entry in _closingDealObservations)
                LogClosingDealObservationEnd(entry.Key, entry.Value, "botStopped");
            _closingDealObservations.Clear();
        }
    }
}
