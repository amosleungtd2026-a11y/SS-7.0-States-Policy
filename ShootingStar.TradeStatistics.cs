using System;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Closed-position and logical-trade accounting and summaries.
    public partial class ShootingStar
    {
        #region Trade Statistics State

        // Aggregate-only telemetry; closed Position objects are not retained.
        private long _tradeStatisticsSequence;
        private long _tradeStatisticsClosedTrades;
        private long _tradeStatisticsWinners;
        private long _tradeStatisticsLosers;
        private long _tradeStatisticsBreakeven;
        private long _tradeStatisticsLongTrades;
        private long _tradeStatisticsShortTrades;
        private long _tradeStatisticsMrTrades;
        private long _tradeStatisticsTfTrades;
        private long _tradeStatisticsInvalidRecords;
        private double _tradeStatisticsGrossProfit;
        private double _tradeStatisticsGrossLoss;
        private double _tradeStatisticsNetProfit;
        private double _tradeStatisticsCommission;
        private double _tradeStatisticsSwap;
        private double _tradeStatisticsPips;
        private double _tradeStatisticsTotalDurationSeconds;
        private double _tradeStatisticsShortestDurationSeconds = double.MaxValue;
        private double _tradeStatisticsLongestDurationSeconds;
        private double _tradeStatisticsLargestWin;
        private double _tradeStatisticsLargestLoss;
        private double _tradeStatisticsWinningNetProfit;
        private double _tradeStatisticsLosingNetProfit;
        private double _tradeStatisticsLongNetProfit;
        private double _tradeStatisticsShortNetProfit;
        private double _tradeStatisticsMrNetProfit;
        private double _tradeStatisticsTfNetProfit;
        private const double TradeStatisticsBreakevenTolerance = 1e-8;
        private long _logicalTradesCompleted;
        private long _logicalTradeSegmentsClosed;
        private long _logicalRolloverSegmentsCarried;
        private double _logicalNetProfit;
        private double _logicalCommission;
        private double _logicalSwap;
        private double _logicalPips;
        private double _logicalActiveDurationSeconds;
        private double _logicalCalendarDurationSeconds;
        private long _logicalMetricsExcludedTrades;

        #endregion

        #region Closed Trade Accounting

        private void FinalizePermanentlySkippedLogicalTrade(PositionSnapshot s)
        {
            FinalizeUnrestoredLogicalTrade(
                s,
                "RolloverPolicySkip",
                "UnspecifiedPermanentSkip");
        }

        private bool FinalizeUnrestoredLogicalTrade(
            PositionSnapshot snapshot,
            string closeReason,
            string fallbackReason)
        {
            if (snapshot == null || snapshot.LogicalTradeFinalized)
                return false;

            snapshot.LogicalTradeFinalized = true;
            DateTime exitUtc = snapshot.ResolvedUtc ?? Server.TimeInUtc;
            DateTime entryUtc = snapshot.LogicalEntryUtc == DateTime.MinValue
                ? exitUtc
                : snapshot.LogicalEntryUtc;
            double activeDurationSeconds = Math.Max(
                0.0, snapshot.CarriedActiveDurationSeconds);
            double calendarDurationSeconds = Math.Max(
                0.0, (exitUtc - entryUtc).TotalSeconds);
            string terminalReason = string.IsNullOrWhiteSpace(snapshot.ResolutionReason)
                ? fallbackReason
                : snapshot.ResolutionReason;

            bool logicalMetricsReliable =
                CanUseForLogicalMetrics(snapshot.Evidence) &&
                IsPartialCloseHistoryReconciled(
                    snapshot.PendingLadderCloseVolume,
                    Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6));
            _logicalTradesCompleted++;
            if (logicalMetricsReliable)
            {
                _logicalNetProfit += snapshot.CarriedNetProfit;
                _logicalCommission += snapshot.CarriedCommission;
                _logicalSwap += snapshot.CarriedSwap;
                _logicalPips += snapshot.CarriedPips;
                _logicalActiveDurationSeconds += activeDurationSeconds;
                _logicalCalendarDurationSeconds += calendarDurationSeconds;
            }
            else
            {
                _logicalMetricsExcludedTrades++;
            }
            ApplyScaleUpOutcome(
                snapshot.Label,
                snapshot.LogicalTradeId,
                snapshot.CarriedNetProfit,
                snapshot.ScaleUpEligible,
                snapshot.TradeType,
                snapshot.EntryPrice,
                snapshot.OriginalTakeProfit ?? 0.0,
                snapshot.OriginalStopLoss ?? 0.0,
                snapshot.LastClosingPrice,
                false,
                snapshot.Evidence);

            LogNormal("TRADE", "LOGICAL_CLOSED",
                "logicalTradeId={0} finalPositionId={1} strategy={2} side={3} " +
                "schemaVersion=4 entryOrigin={24} logicalEntryUtc={4:O} resolutionObservedUtc={5:O} segmentCount={6} restorationCount={7} " +
                "observedActiveDurationSeconds={8:F3} observedCalendarDurationSeconds={9:F3} " +
                "observedRolloverDowntimeSeconds={10:F3} observedNetProfit={12:F2} " +
                "commission=unavailable swap=unavailable financialBreakdown=netOnly pips={15:F2} " +
                "closeReason={16} terminalReason={17} restoredAfterRollover=false logicalMetricsIncluded={18} lifecycleContinuity={19} segmentProvenance={20} positionAttribution={21}" +
                "{22}" +
                " sourcePositionId={23}",
                snapshot.LogicalTradeId,
                snapshot.RestoredObjectId ?? snapshot.SourcePositionId,
                snapshot.Strategy,
                snapshot.TradeType == TradeType.Buy ? "Long" : "Short",
                entryUtc,
                exitUtc,
                snapshot.RolloverSegment + 1,
                snapshot.RestorationCount,
                activeDurationSeconds,
                calendarDurationSeconds,
                Math.Max(0.0, calendarDurationSeconds - activeDurationSeconds),
                snapshot.CarriedGrossProfit,
                snapshot.CarriedNetProfit,
                snapshot.CarriedCommission,
                snapshot.CarriedSwap,
                snapshot.CarriedPips,
                closeReason,
                terminalReason,
                logicalMetricsReliable,
                snapshot.Evidence == null ? "unknown" : snapshot.Evidence.LifecycleContinuity.ToString(),
                snapshot.Evidence == null ? "unknown" : snapshot.Evidence.SegmentProvenance.ToString(),
                snapshot.Evidence == null ? "unknown" : snapshot.Evidence.PositionAttribution.ToString(),
                FormatKnownTradeIdeaField(snapshot.TradeIdeaId),
                snapshot.SourcePositionId,
                snapshot.EntryOrigin);
            RunTelemetry("CompleteTradeIdea", () =>
                CompleteTradeIdea(
                    snapshot.TradeIdeaId,
                    closeReason == "RolloverPolicySkip"
                        ? "rolloverNotReopened"
                        : "rolloverRecoveryFailed",
                    snapshot.CarriedNetProfit,
                    logicalMetricsReliable));
            return true;
        }

        private static string GetTradeIdeaCloseOutcome(PositionCloseReason closeReason)
        {
            if (closeReason == PositionCloseReason.TakeProfit)
                return "takeProfit";
            if (closeReason == PositionCloseReason.StopLoss)
                return "stopLoss";
            return null;
        }

        private static bool IsPartialCloseHistoryReconciled(
            double pendingCloseVolume,
            double volumeTolerance)
        {
            return IsFinite(pendingCloseVolume) &&
                IsFinite(volumeTolerance) &&
                volumeTolerance >= 0.0 &&
                pendingCloseVolume <= volumeTolerance;
        }

        private void RecordClosedPositionStatistics(Position position, PositionCloseReason closeReason)
        {
            DateTime exitUtc = Server.TimeInUtc;
            DateTime entryUtc = position.EntryTime;
            double durationSeconds = Math.Max(0.0, (exitUtc - entryUtc).TotalSeconds);
            double? finalClosingPrice = GetFinalClosingPrice(position.Id);
            double exitPrice = position.CurrentPrice;
            double pips = position.Pips;
            _positionStates.TryGetValue(position.Id, out PositionState logicalState);
            // NetProfit is the only account-currency financial property read here.
            // GrossProfit, Commissions, and Swap each trigger another conversion lookup
            // on affected cTrader symbols. Preserve the exact net result and avoid the
            // three additional conversion-dependent reads in non-critical telemetry.
            double netProfit = position.NetProfit;
            double grossProfit = netProfit;
            double commission = 0.0;
            double swap = 0.0;
            StrategyKind strategy = GetStrategy(position.Label);
            if (!IsFinite(durationSeconds) ||
                !IsFinite(exitPrice) ||
                !IsFinite(pips) ||
                !IsFinite(grossProfit) ||
                !IsFinite(netProfit) ||
                !IsFinite(commission) ||
                !IsFinite(swap))
            {
                _tradeStatisticsInvalidRecords++;
                LogError("TRADE", "STATISTICS_INVALID_VALUE",
                    "schemaVersion=2 positionId={0} durationSeconds={1} priceObservedAtCloseCallback={2} pips={3} netProfit={4} commission=unavailable swap=unavailable financialBreakdown=netOnly",
                    position.Id, durationSeconds, exitPrice, pips, netProfit);
                return;
            }
            long sequence = ++_tradeStatisticsSequence;
            _tradeStatisticsClosedTrades++;
            _tradeStatisticsGrossProfit += Math.Max(0.0, grossProfit);
            _tradeStatisticsGrossLoss += Math.Min(0.0, grossProfit);
            _tradeStatisticsNetProfit += netProfit;
            _tradeStatisticsCommission += commission;
            _tradeStatisticsSwap += swap;
            _tradeStatisticsPips += pips;
            _tradeStatisticsTotalDurationSeconds += durationSeconds;
            _tradeStatisticsShortestDurationSeconds = Math.Min(
                _tradeStatisticsShortestDurationSeconds, durationSeconds);
            _tradeStatisticsLongestDurationSeconds = Math.Max(
                _tradeStatisticsLongestDurationSeconds, durationSeconds);
            if (netProfit > TradeStatisticsBreakevenTolerance)
            {
                _tradeStatisticsWinners++;
                _tradeStatisticsWinningNetProfit += netProfit;
                _tradeStatisticsLargestWin = Math.Max(_tradeStatisticsLargestWin, netProfit);
            }
            else if (netProfit < -TradeStatisticsBreakevenTolerance)
            {
                _tradeStatisticsLosers++;
                _tradeStatisticsLosingNetProfit += netProfit;
                _tradeStatisticsLargestLoss = Math.Min(_tradeStatisticsLargestLoss, netProfit);
            }
            else
            {
                _tradeStatisticsBreakeven++;
            }
            if (position.TradeType == TradeType.Buy)
            {
                _tradeStatisticsLongTrades++;
                _tradeStatisticsLongNetProfit += netProfit;
            }
            else
            {
                _tradeStatisticsShortTrades++;
                _tradeStatisticsShortNetProfit += netProfit;
            }
            if (strategy == StrategyKind.MeanReversion)
            {
                _tradeStatisticsMrTrades++;
                _tradeStatisticsMrNetProfit += netProfit;
            }
            else
            {
                _tradeStatisticsTfTrades++;
                _tradeStatisticsTfNetProfit += netProfit;
            }
            PositionSnapshot rolloverSnapshot = _positionSnapshots.FirstOrDefault(
                snapshot =>
                    (snapshot.SourcePositionId == position.Id ||
                     snapshot.RestoredObjectId == position.Id) &&
                    snapshot.RecoveryStatus != SnapshotRecoveryStatus.Restored &&
                    snapshot.RecoveryStatus != SnapshotRecoveryStatus.PermanentlySkipped);
            if (rolloverSnapshot != null && finalClosingPrice.HasValue)
                rolloverSnapshot.LastClosingPrice = finalClosingPrice;
            bool rolloverSegment = logicalState != null && rolloverSnapshot != null;
            Guid logicalTradeId = logicalState != null
                ? logicalState.LogicalTradeId
                : rolloverSnapshot != null
                    ? rolloverSnapshot.LogicalTradeId
                    : Guid.Empty;
            TradeEvidence logicalEvidence = CombineTradeEvidence(
                logicalState == null ? GetPositionEvidence(position.Id) : logicalState.Evidence,
                rolloverSnapshot == null ? null : rolloverSnapshot.Evidence);
            double pendingCloseVolume = logicalState != null
                ? logicalState.PendingLadderCloseVolume
                : rolloverSnapshot != null
                    ? rolloverSnapshot.PendingLadderCloseVolume
                    : double.PositiveInfinity;
            bool logicalMetricsReliable =
                logicalState != null &&
                CanUseForLogicalMetrics(logicalEvidence) &&
                IsPartialCloseHistoryReconciled(
                    pendingCloseVolume,
                    Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6));
            int rolloverSegmentNumber = logicalState != null
                ? logicalState.RolloverSegment
                : 0;
            _logicalTradeSegmentsClosed++;
            LogVerbose("TRADE", "SEGMENT_CLOSED",
                "schemaVersion=4 logicalTradeId={0} segment={1} positionId={2} rolloverSegment={3} entryUtc={4:O} closeObservedUtc={5:O} activeDurationSeconds={6:F3} netProfit={7:F2} commission=unavailable swap=unavailable financialBreakdown=netOnly pips={10:F2} tradeIdeaId={11} lifecycleContinuity={12} segmentProvenance={13} positionAttribution={14} logicalMetricsIncluded={15} entryOrigin={16}",
                logicalTradeId, rolloverSegmentNumber, position.Id, rolloverSegment,
                entryUtc, exitUtc, durationSeconds, netProfit, commission, swap, pips,
                FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)),
                logicalEvidence == null ? "unknown" : logicalEvidence.LifecycleContinuity.ToString(),
                logicalEvidence == null ? "unknown" : logicalEvidence.SegmentProvenance.ToString(),
                logicalEvidence == null ? "unknown" : logicalEvidence.PositionAttribution.ToString(),
                logicalMetricsReliable,
                logicalState == null
                    ? rolloverSnapshot == null
                        ? GetPositionEntryOrigin(position.Id)
                        : rolloverSnapshot.EntryOrigin
                    : logicalState.EntryOrigin);
            if (rolloverSegment)
            {
                rolloverSnapshot.RolloverSegment = Math.Max(
                    rolloverSnapshot.RolloverSegment,
                    logicalState.RolloverSegment);
                rolloverSnapshot.RestorationCount = Math.Max(
                    rolloverSnapshot.RestorationCount,
                    logicalState.RestorationCount);
                rolloverSnapshot.CarriedGrossProfit += grossProfit;
                rolloverSnapshot.CarriedNetProfit += netProfit;
                rolloverSnapshot.CarriedCommission += commission;
                rolloverSnapshot.CarriedSwap += swap;
                rolloverSnapshot.CarriedPips += pips;
                rolloverSnapshot.CarriedActiveDurationSeconds += durationSeconds;
                _logicalRolloverSegmentsCarried++;
                LogVerbose("TRADE", "ROLLOVER_SEGMENT_CARRIED",
                    "logicalTradeId={0} recoveryId={1} segment={2} carriedNetProfit={3:F2} carriedActiveDurationSeconds={4:F3}",
                    rolloverSnapshot.LogicalTradeId, rolloverSnapshot.RecoveryId,
                    rolloverSnapshot.RolloverSegment,
                    rolloverSnapshot.CarriedNetProfit,
                    rolloverSnapshot.CarriedActiveDurationSeconds);
            }
            else
            {
                PositionSnapshot unresolvedSnapshot = _positionSnapshots.FirstOrDefault(
                    snapshot =>
                        snapshot.SourcePositionId == position.Id &&
                        !snapshot.LogicalTradeFinalized);
                if (unresolvedSnapshot != null)
                {
                    if (finalClosingPrice.HasValue)
                        unresolvedSnapshot.LastClosingPrice = finalClosingPrice;
                    LogError("TRADE", "LOGICAL_CLOSE_DEFERRED",
                        "positionId={0} logicalTradeId={1} recoveryId={2} reason=UnresolvedRolloverSnapshot",
                        position.Id, unresolvedSnapshot.LogicalTradeId,
                        unresolvedSnapshot.RecoveryId);
                    unresolvedSnapshot.CarriedGrossProfit += grossProfit;
                    unresolvedSnapshot.CarriedNetProfit += netProfit;
                    unresolvedSnapshot.CarriedCommission += commission;
                    unresolvedSnapshot.CarriedSwap += swap;
                    unresolvedSnapshot.CarriedPips += pips;
                    unresolvedSnapshot.CarriedActiveDurationSeconds += durationSeconds;
                }
                else
                {
                    double logicalGrossProfit = (logicalState == null ? 0.0 : logicalState.CarriedGrossProfit) + grossProfit;
                    double logicalNetProfit = (logicalState == null ? 0.0 : logicalState.CarriedNetProfit) + netProfit;
                    double logicalCommission = (logicalState == null ? 0.0 : logicalState.CarriedCommission) + commission;
                    double logicalSwap = (logicalState == null ? 0.0 : logicalState.CarriedSwap) + swap;
                    double logicalPips = (logicalState == null ? 0.0 : logicalState.CarriedPips) + pips;
                    double activeDuration = (logicalState == null ? 0.0 : logicalState.CarriedActiveDurationSeconds) + durationSeconds;
                    ApplyScaleUpOutcome(
                        position.Label,
                        logicalTradeId,
                        logicalNetProfit,
                        logicalState != null && logicalState.ScaleUpEligible &&
                            logicalMetricsReliable,
                        position.TradeType,
                        logicalState != null && IsFinitePositive(logicalState.RolloverAnchorEntryPrice)
                            ? logicalState.RolloverAnchorEntryPrice
                            : position.EntryPrice,
                        logicalState != null
                            ? logicalState.OriginalTakeProfit
                            : 0.0,
                        logicalState != null
                            ? logicalState.OriginalStopLoss
                            : 0.0,
                        finalClosingPrice ??
                            (IsFinite(exitPrice) ? (double?)exitPrice : null),
                        closeReason == PositionCloseReason.TakeProfit,
                        logicalEvidence);
                    DateTime logicalEntryUtc = logicalState != null && logicalState.LogicalEntryUtc != DateTime.MinValue
                        ? logicalState.LogicalEntryUtc
                        : entryUtc;
                    double calendarDuration = Math.Max(0.0, (exitUtc - logicalEntryUtc).TotalSeconds);
                    double rolloverDowntime = Math.Max(0.0, calendarDuration - activeDuration);
                    int segmentCount = rolloverSegmentNumber + 1;
                    int restorationCount = logicalState == null ? 0 : logicalState.RestorationCount;
                    _logicalTradesCompleted++;
                    if (logicalMetricsReliable)
                    {
                        _logicalNetProfit += logicalNetProfit;
                        _logicalCommission += logicalCommission;
                        _logicalSwap += logicalSwap;
                        _logicalPips += logicalPips;
                        _logicalActiveDurationSeconds += activeDuration;
                        _logicalCalendarDurationSeconds += calendarDuration;
                    }
                    else
                    {
                        _logicalMetricsExcludedTrades++;
                    }
                    LogNormal("TRADE", "LOGICAL_CLOSED",
                        "schemaVersion=4 logicalTradeId={0} finalPositionId={1} strategy={2} side={3} logicalEntryUtc={4:O} closeObservedUtc={5:O} segmentCount={6} restorationCount={7} observedActiveDurationSeconds={8:F3} observedCalendarDurationSeconds={9:F3} observedRolloverDowntimeSeconds={10:F3} observedNetProfit={12:F2} commission=unavailable swap=unavailable financialBreakdown=netOnly pips={15:F2} closeReason={16} lifecycleContinuity={17} segmentProvenance={18} positionAttribution={19} logicalMetricsIncluded={20} tradeIdeaId={21} entryOrigin={22}",
                        logicalTradeId, position.Id, strategy,
                        position.TradeType == TradeType.Buy ? "Long" : "Short",
                        logicalEntryUtc, exitUtc, segmentCount, restorationCount,
                        activeDuration, calendarDuration, rolloverDowntime,
                        logicalGrossProfit, logicalNetProfit, logicalCommission,
                        logicalSwap, logicalPips, closeReason,
                        logicalEvidence == null ? "unknown" : logicalEvidence.LifecycleContinuity.ToString(),
                        logicalEvidence == null ? "unknown" : logicalEvidence.SegmentProvenance.ToString(),
                        logicalEvidence == null ? "unknown" : logicalEvidence.PositionAttribution.ToString(),
                        logicalMetricsReliable,
                        FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)),
                        logicalState.EntryOrigin);
                    string tradeIdeaCloseOutcome = GetTradeIdeaCloseOutcome(closeReason);
                    if (tradeIdeaCloseOutcome != null)
                    {
                        RunTelemetry("CompleteTradeIdea", () =>
                            CompleteTradeIdea(
                                GetPositionTradeIdeaId(position.Id),
                                tradeIdeaCloseOutcome,
                                logicalNetProfit,
                                logicalMetricsReliable));
                    }
                }
            }
            LogVerbose("TRADE", "PHYSICAL_POSITION_CLOSED",
                "schemaVersion=4 sequence={0} positionId={1} symbol={2} strategy={3} side={4} label={5} entryUtc={6:O} closeObservedUtc={7:O} durationSeconds={8:F3} entryPrice={9} priceObservedAtCloseCallback={10} volume={11:R} pips={12:F2} netProfit={14:F2} commission=unavailable swap=unavailable financialBreakdown=netOnly closeReason={17} tradeIdeaId={18} lifecycleContinuity={19} segmentProvenance={20} positionAttribution={21} logicalMetricsIncluded={22} entryOrigin={23}",
                sequence,
                position.Id,
                position.SymbolName,
                strategy,
                position.TradeType == TradeType.Buy ? "Long" : "Short",
                position.Label,
                entryUtc,
                exitUtc,
                durationSeconds,
                FormatPrice(position.EntryPrice),
                FormatPrice(exitPrice),
                position.VolumeInUnits,
                pips,
                grossProfit,
                netProfit,
                commission,
                swap,
                closeReason,
                FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)),
                logicalEvidence == null ? "unknown" : logicalEvidence.LifecycleContinuity.ToString(),
                logicalEvidence == null ? "unknown" : logicalEvidence.SegmentProvenance.ToString(),
                logicalEvidence == null ? "unknown" : logicalEvidence.PositionAttribution.ToString(),
                logicalMetricsReliable,
                logicalState != null
                    ? logicalState.EntryOrigin
                    : rolloverSnapshot != null
                        ? rolloverSnapshot.EntryOrigin
                        : GetPositionEntryOrigin(position.Id));
        }

        private double? GetFinalClosingPrice(int positionId)
        {
            HistoricalTrade[] closedTrades = History.FindByPositionId(positionId);
            if (closedTrades == null || closedTrades.Length == 0)
                return null;

            HistoricalTrade finalTrade = closedTrades
                .OrderBy(trade => trade.ClosingTime)
                .ThenBy(trade => trade.ClosingDealId)
                .Last();
            return IsFinite(finalTrade.ClosingPrice)
                ? (double?)finalTrade.ClosingPrice
                : null;
        }

        private void LogTradeStatisticsSummary()
        {
            double closedDivisor = _tradeStatisticsClosedTrades > 0
                ? _tradeStatisticsClosedTrades
                : 1.0;
            double winnerDivisor = _tradeStatisticsWinners > 0
                ? _tradeStatisticsWinners
                : 1.0;
            double loserDivisor = _tradeStatisticsLosers > 0
                ? _tradeStatisticsLosers
                : 1.0;
            double grossLossMagnitude = Math.Abs(_tradeStatisticsGrossLoss);
            double profitFactor = grossLossMagnitude > 0.0
                ? _tradeStatisticsGrossProfit / grossLossMagnitude
                : 0.0;
            double shortestDuration = _tradeStatisticsClosedTrades > 0
                ? _tradeStatisticsShortestDurationSeconds
                : 0.0;
            bool outcomeBalanced = _tradeStatisticsClosedTrades ==
                _tradeStatisticsWinners + _tradeStatisticsLosers + _tradeStatisticsBreakeven;
            bool sideBalanced = _tradeStatisticsClosedTrades ==
                _tradeStatisticsLongTrades + _tradeStatisticsShortTrades;
            bool strategyBalanced = _tradeStatisticsClosedTrades ==
                _tradeStatisticsMrTrades + _tradeStatisticsTfTrades;
            LogNormal("TRADE", "STATISTICS_SUMMARY",
                "schemaVersion=2 closedTrades={0} winners={1} losers={2} breakeven={3} winRatePercent={4:F2} positiveNetProfit={5:F2} negativeNetProfit={6:F2} netProfit={7:F2} commission=unavailable swap=unavailable financialBreakdown=netOnly totalPips={10:F2} netOutcomeFactor={11:F4} averageNetProfit={12:F2} averageWinner={13:F2} averageLoser={14:F2} averageDurationSeconds={15:F3} shortestDurationSeconds={16:F3} longestDurationSeconds={17:F3} largestWin={18:F2} largestLoss={19:F2} longTrades={20} longNetProfit={21:F2} shortTrades={22} shortNetProfit={23:F2} mrTrades={24} mrNetProfit={25:F2} tfTrades={26} tfNetProfit={27:F2} invalidRecords={28} outcomeBalanced={29} sideBalanced={30} strategyBalanced={31}",
                _tradeStatisticsClosedTrades,
                _tradeStatisticsWinners,
                _tradeStatisticsLosers,
                _tradeStatisticsBreakeven,
                100.0 * _tradeStatisticsWinners / closedDivisor,
                _tradeStatisticsGrossProfit,
                _tradeStatisticsGrossLoss,
                _tradeStatisticsNetProfit,
                _tradeStatisticsCommission,
                _tradeStatisticsSwap,
                _tradeStatisticsPips,
                profitFactor,
                _tradeStatisticsNetProfit / closedDivisor,
                _tradeStatisticsWinningNetProfit / winnerDivisor,
                _tradeStatisticsLosingNetProfit / loserDivisor,
                _tradeStatisticsTotalDurationSeconds / closedDivisor,
                shortestDuration,
                _tradeStatisticsLongestDurationSeconds,
                _tradeStatisticsLargestWin,
                _tradeStatisticsLargestLoss,
                _tradeStatisticsLongTrades,
                _tradeStatisticsLongNetProfit,
                _tradeStatisticsShortTrades,
                _tradeStatisticsShortNetProfit,
                _tradeStatisticsMrTrades,
                _tradeStatisticsMrNetProfit,
                _tradeStatisticsTfTrades,
                _tradeStatisticsTfNetProfit,
                _tradeStatisticsInvalidRecords,
                outcomeBalanced,
                sideBalanced,
                strategyBalanced);
            LogNormal("TRADE", "LOGICAL_STATISTICS_SUMMARY",
                "schemaVersion=3 completedLogicalTrades={0} closedSegments={1} rolloverSegmentsCarried={2} netProfit={3:F2} commission=unavailable swap=unavailable financialBreakdown=netOnly pips={6:F2} activeDurationSeconds={7:F3} calendarDurationSeconds={8:F3} activeLogicalTrades={9} reliableLogicalTrades={10} incompleteLogicalTradesExcluded={11} aggregationScope=continuousLifecycleExclusiveAttributionKnownSegmentReconciledPartialCloses",
                _logicalTradesCompleted, _logicalTradeSegmentsClosed,
                _logicalRolloverSegmentsCarried, _logicalNetProfit,
                _logicalCommission, _logicalSwap, _logicalPips,
                _logicalActiveDurationSeconds, _logicalCalendarDurationSeconds,
                _positionStates.Count,
                Math.Max(0, _logicalTradesCompleted - _logicalMetricsExcludedTrades),
                _logicalMetricsExcludedTrades);
        }

        #endregion
    }
}
