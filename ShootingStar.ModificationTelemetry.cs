using System;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private sealed class ModificationValues
        {
            public double? Target;
            public double? Volume;
            public double? StopLoss;
            public double? TakeProfit;
        }

        private sealed class ModificationTelemetry
        {
            public ModificationValues Initial;
            public ModificationValues Last;
            public DateTime IntervalStartUtc;
            public DateTime? FirstChangeUtc;
            public DateTime? LastChangeUtc;
            public int Requests;
            public int Accepted;
            public int Rejected;
            public int Changes;
            public int TargetChanges;
            public int VolumeChanges;
            public int StopLossChanges;
            public int TakeProfitChanges;
            public int ObservationsWithoutBaseline;
            public bool BaselineKnown;
        }

        private readonly Dictionary<int, ModificationTelemetry> _orderModificationTelemetry =
            new Dictionary<int, ModificationTelemetry>();
        private readonly Dictionary<int, ModificationTelemetry> _positionModificationTelemetry =
            new Dictionary<int, ModificationTelemetry>();
        private static readonly TimeSpan ModificationSummaryInterval = TimeSpan.FromMinutes(30);

        private ModificationValues OrderModificationValues(PendingOrder order)
        {
            return new ModificationValues
            {
                Target = order.TargetPrice,
                Volume = order.VolumeInUnits,
                StopLoss = order.StopLossPips,
                TakeProfit = order.TakeProfitPips
            };
        }

        private ModificationValues PositionModificationValues(Position position)
        {
            return new ModificationValues
            {
                StopLoss = position.StopLoss,
                TakeProfit = position.TakeProfit
            };
        }

        private ModificationTelemetry GetModificationTelemetry(
            Dictionary<int, ModificationTelemetry> states, int id,
            ModificationValues values, bool baselineKnown)
        {
            if (!states.TryGetValue(id, out ModificationTelemetry state))
            {
                state = new ModificationTelemetry
                {
                    Initial = values,
                    Last = values,
                    IntervalStartUtc = Server.TimeInUtc,
                    BaselineKnown = baselineKnown
                };
                states.Add(id, state);
            }
            return state;
        }

        private static void ObserveModification(ModificationTelemetry state, ModificationValues current, DateTime now)
        {
            bool target = state.Last.Target != current.Target;
            bool volume = state.Last.Volume != current.Volume;
            bool stopLoss = state.Last.StopLoss != current.StopLoss;
            bool takeProfit = state.Last.TakeProfit != current.TakeProfit;
            if (!target && !volume && !stopLoss && !takeProfit)
                return;

            if (!state.FirstChangeUtc.HasValue)
                state.FirstChangeUtc = now;
            state.LastChangeUtc = now;
            state.Changes++;
            if (target) state.TargetChanges++;
            if (volume) state.VolumeChanges++;
            if (stopLoss) state.StopLossChanges++;
            if (takeProfit) state.TakeProfitChanges++;
            state.Last = current;
        }

        private ModificationTelemetry BeginOrderModification(PendingOrder order)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return null;
            ModificationTelemetry state = GetModificationTelemetry(
                _orderModificationTelemetry, order.Id, OrderModificationValues(order), true);
            ObserveModification(state, OrderModificationValues(order), Server.TimeInUtc);
            state.Requests++;
            return state;
        }

        private ModificationTelemetry BeginPositionModification(Position position)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return null;
            ModificationTelemetry state = GetModificationTelemetry(
                _positionModificationTelemetry, position.Id, PositionModificationValues(position), true);
            ObserveModification(state, PositionModificationValues(position), Server.TimeInUtc);
            state.Requests++;
            return state;
        }

        private static void CompleteModificationRequest(ModificationTelemetry state, TradeResult result)
        {
            if (state == null)
                return;
            if (result != null && result.IsSuccessful)
                state.Accepted++;
            else
                state.Rejected++;
        }

        private void ObserveOrderModification(PendingOrder order, bool baselineKnown)
        {
            if (!ShouldLog(BotLogLevel.Verbose) || !IsBotOrder(order))
                return;
            ModificationValues current = OrderModificationValues(order);
            bool missingBaseline = !_orderModificationTelemetry.ContainsKey(order.Id) && !baselineKnown;
            ModificationTelemetry state = GetModificationTelemetry(
                _orderModificationTelemetry, order.Id, current, baselineKnown);
            if (missingBaseline)
                state.ObservationsWithoutBaseline++;
            ObserveModification(state, current, Server.TimeInUtc);
        }

        private void ObservePositionModification(Position position, bool baselineKnown)
        {
            if (!ShouldLog(BotLogLevel.Verbose) || !IsBotPosition(position))
                return;
            ModificationValues current = PositionModificationValues(position);
            bool missingBaseline = !_positionModificationTelemetry.ContainsKey(position.Id) && !baselineKnown;
            ModificationTelemetry state = GetModificationTelemetry(
                _positionModificationTelemetry, position.Id, current, baselineKnown);
            if (missingBaseline)
                state.ObservationsWithoutBaseline++;
            ObserveModification(state, current, Server.TimeInUtc);
        }

        // Orders are modified only by trailing, so changes beyond accepted requests are external.
        // Positions are also modified by other bot paths that are not counted, so they are not estimated.
        private static string ExternalChangeEstimate(string kind, ModificationTelemetry state)
        {
            if (kind != "order")
                return "notTracked";
            if (!state.BaselineKnown)
                return "unknownBaseline";
            return Math.Max(0, state.Changes - state.Accepted).ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatTelemetryValue(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "none";
        }

        private void FlushModificationTelemetry(
            Dictionary<int, ModificationTelemetry> states, int id, string kind, string reason, bool remove)
        {
            if (!states.TryGetValue(id, out ModificationTelemetry state))
                return;
            if (state.Requests > 0 || state.Changes > 0 || state.ObservationsWithoutBaseline > 0)
            {
                string logicalTradeId = kind == "order" ? "notApplicable" : "notTracked";
                if (kind == "position" && _positionStates.TryGetValue(id, out PositionState positionState))
                    logicalTradeId = positionState.LogicalTradeId.ToString();
                Guid? lineageId = kind == "position"
                    ? GetPositionTradeIdeaId(id)
                    : GetSubmissionCorrelationId(id);
                string lineageIdField = kind == "position"
                    ? "tradeIdeaId"
                    : "submissionCorrelationId";
                DateTime now = Server.TimeInUtc;
                LogVerbose("TRADE_LIFECYCLE", "MODIFICATION_SUMMARY",
                    "schemaVersion=4 kind={0} objectId={1} logicalTradeId={2} intervalStartUtc={3:O} intervalEndUtc={4:O} flushReason={5} baselineKnown={6} firstChangeUtc={7} lastChangeUtc={8} trailingRequests={9} trailingAccepted={10} trailingRejected={11} observedChanges={12} targetChanges={13} volumeChanges={14} slChanges={15} tpChanges={16} initialTarget={17} finalTarget={18} initialVolume={19} finalVolume={20} initialSl={21} finalSl={22} initialTp={23} finalTp={24} protectionUnits={25} observationsWithoutBaseline={26} {27}={28} modificationSource={29} externalChanges={30}",
                    kind, id, logicalTradeId, state.IntervalStartUtc, now, reason, state.BaselineKnown,
                    state.FirstChangeUtc.HasValue ? state.FirstChangeUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none",
                    state.LastChangeUtc.HasValue ? state.LastChangeUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none",
                    state.Requests, state.Accepted, state.Rejected, state.Changes,
                    state.TargetChanges, state.VolumeChanges, state.StopLossChanges, state.TakeProfitChanges,
                    FormatTelemetryValue(state.Initial.Target), FormatTelemetryValue(state.Last.Target),
                    FormatTelemetryValue(state.Initial.Volume), FormatTelemetryValue(state.Last.Volume),
                    FormatTelemetryValue(state.Initial.StopLoss), FormatTelemetryValue(state.Last.StopLoss),
                    FormatTelemetryValue(state.Initial.TakeProfit), FormatTelemetryValue(state.Last.TakeProfit),
                    kind == "order" ? "pips" : "price", state.ObservationsWithoutBaseline,
                    lineageIdField,
                    kind == "position"
                        ? FormatTradeIdeaId(lineageId)
                        : FormatSubmissionCorrelationId(lineageId),
                    kind == "order" ? "botTrailing" : "botProtection",
                    ExternalChangeEstimate(kind, state));
            }
            if (remove)
            {
                states.Remove(id);
                return;
            }
            state.Initial = state.Last;
            state.IntervalStartUtc = Server.TimeInUtc;
            state.FirstChangeUtc = null;
            state.LastChangeUtc = null;
            state.Requests = state.Accepted = state.Rejected = state.Changes = 0;
            state.TargetChanges = state.VolumeChanges = state.StopLossChanges = state.TakeProfitChanges = 0;
            state.ObservationsWithoutBaseline = 0;
            state.BaselineKnown = true;
        }

        private void FlushPeriodicModificationTelemetry()
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            DateTime now = Server.TimeInUtc;
            if (_orderModificationTelemetry.Count > 0)
                foreach (int id in new List<int>(_orderModificationTelemetry.Keys))
                    if (now - _orderModificationTelemetry[id].IntervalStartUtc >= ModificationSummaryInterval)
                        FlushModificationTelemetry(_orderModificationTelemetry, id, "order", "periodic", false);
            if (_positionModificationTelemetry.Count > 0)
                foreach (int id in new List<int>(_positionModificationTelemetry.Keys))
                    if (now - _positionModificationTelemetry[id].IntervalStartUtc >= ModificationSummaryInterval)
                        FlushModificationTelemetry(_positionModificationTelemetry, id, "position", "periodic", false);
        }

        private void FlushAllModificationTelemetry()
        {
            foreach (int id in new List<int>(_orderModificationTelemetry.Keys))
                FlushModificationTelemetry(_orderModificationTelemetry, id, "order", "shutdown", true);
            foreach (int id in new List<int>(_positionModificationTelemetry.Keys))
                FlushModificationTelemetry(_positionModificationTelemetry, id, "position", "shutdown", true);
        }
    }
}
