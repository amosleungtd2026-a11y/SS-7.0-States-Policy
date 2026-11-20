using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        [Parameter("Target Fills / Week", DefaultValue = 4.5, MinValue = 0.1, Step = 0.1, Group = "Optimisation Fitness")]
        public double TargetFillsPerWeek { get; set; }

        [Parameter("Fills / Week Tolerance", DefaultValue = 1.5, MinValue = 0.1, Step = 0.1, Group = "Optimisation Fitness")]
        public double FillsPerWeekTolerance { get; set; }

        private const double MinFrequencyWeight = 0.30;
        private const int FrequencyScoringStartDays = 7;
        private const int FrequencyFullWeightDays = 28;

        private DateTime _fitnessStartTimeUtc;
        private DateTime? _strategyOperationalStartUtc;
        private int _filledPendingOrdersCount;
        private bool _fitnessTrackingInitialized;
        private readonly HashSet<int> _fitnessCountedPendingOrderIds = new HashSet<int>();
        private readonly HashSet<int> _fitnessExcludedRolloverPendingOrderIds = new HashSet<int>();

        private void InitializeFitnessTracking()
        {
            _fitnessStartTimeUtc = Server.TimeInUtc;
            _strategyOperationalStartUtc = null;
            _filledPendingOrdersCount = 0;
            _fitnessCountedPendingOrderIds.Clear();
            _fitnessExcludedRolloverPendingOrderIds.Clear();
            _fitnessTrackingInitialized = true;
        }

        private void RecordStrategyOperationalStartForFitness(DateTime utcNow)
        {
            if (_strategyOperationalStartUtc.HasValue)
                return;

            _strategyOperationalStartUtc = utcNow;
            LogNormal("OPTIMISATION", "STRATEGY_OPERATIONAL_START",
                "utc={0:O}", utcNow);
        }

        private void RecordFitnessPendingOrderFill(
            PendingOrder order,
            EntryOriginMode entryOrigin)
        {
            if (!_fitnessTrackingInitialized ||
                order == null ||
                !IsBotOrder(order))
            {
                return;
            }

            if (_rolloverContext.Phase == RolloverPhase.Restoring ||
                _activeOrderRestorationRecoveryId.HasValue ||
                _rolloverRestoredOrderRecoveryIds.ContainsKey(order.Id))
            {
                _fitnessExcludedRolloverPendingOrderIds.Add(order.Id);
                return;
            }

            if (!IsEligibleForOptimizationFitness(entryOrigin))
                return;

            if (_fitnessCountedPendingOrderIds.Add(order.Id))
                _filledPendingOrdersCount++;
        }

        private bool IsEligibleForOptimizationFitness(EntryOriginMode entryOrigin)
        {
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                return entryOrigin == EntryOriginMode.ProbabilisticAuto;
            if (_effectiveTradingMode == TradingMode.RuleMappedAuto)
                return entryOrigin == EntryOriginMode.RuleMappedAuto;
            return false;
        }

        private void ValidateOptimizationFitnessParameters()
        {
            if (!IsFinite(TargetFillsPerWeek) || TargetFillsPerWeek < 0.1)
            {
                throw new ArgumentOutOfRangeException(nameof(TargetFillsPerWeek),
                    "Target Fills / Week must be a finite value of at least 0.1.");
            }

            if (!IsFinite(FillsPerWeekTolerance) || FillsPerWeekTolerance < 0.1)
            {
                throw new ArgumentOutOfRangeException(nameof(FillsPerWeekTolerance),
                    "Fills / Week Tolerance must be a finite value of at least 0.1.");
            }

        }

        #region Optimisation Fitness

        protected override double GetFitness(GetFitnessArgs args)
        {
            double longNetProfit = 0.0;
            double shortNetProfit = 0.0;
            double longTradeLargestLoss = 0.0;
            double longTradeLargestWin = 0.0;
            double shortTradeLargestLoss = 0.0;
            double shortTradeLargestWin = 0.0;
            double filteredNetProfit = 0.0;
            bool needsFallbackCount = !_fitnessTrackingInitialized;

            HashSet<Guid> distinctTradeIdeaIds = needsFallbackCount
                ? new HashSet<Guid>()
                : null;
            HashSet<int> distinctPositionIdsWithoutIdea = needsFallbackCount
                ? new HashSet<int>()
                : null;
            HashSet<int> positionIdsWithIdea = needsFallbackCount
                ? new HashSet<int>()
                : null;

            foreach (HistoricalTrade trade in args.History)
            {
                if (trade.SymbolName != SymbolName || !IsKnownLabel(trade.Label))
                    continue;

                if (!TryGetFitnessEntryOrigin(
                        trade.PositionId,
                        trade.Comment,
                        out EntryOriginMode entryOrigin) ||
                    !IsEligibleForOptimizationFitness(entryOrigin))
                    continue;

                if (needsFallbackCount)
                {
                    AddFitnessEntryIdentity(
                        trade.PositionId,
                        trade.Comment,
                        distinctTradeIdeaIds,
                        distinctPositionIdsWithoutIdea,
                        positionIdsWithIdea);
                }

                if (trade.TradeType == TradeType.Buy)
                {
                    longNetProfit += trade.NetProfit;
                    filteredNetProfit += trade.NetProfit;

                    if (trade.NetProfit > 0.0)
                    {
                        longTradeLargestWin = Math.Max(
                            longTradeLargestWin,
                            trade.NetProfit);
                    }
                    else if (trade.NetProfit < 0.0)
                    {
                        longTradeLargestLoss = Math.Max(
                            longTradeLargestLoss,
                            Math.Abs(trade.NetProfit));
                    }
                }
                else if (trade.TradeType == TradeType.Sell)
                {
                    shortNetProfit += trade.NetProfit;
                    filteredNetProfit += trade.NetProfit;

                    if (trade.NetProfit > 0.0)
                    {
                        shortTradeLargestWin = Math.Max(
                            shortTradeLargestWin,
                            trade.NetProfit);
                    }
                    else if (trade.NetProfit < 0.0)
                    {
                        shortTradeLargestLoss = Math.Max(
                            shortTradeLargestLoss,
                            Math.Abs(trade.NetProfit));
                    }
                }
            }

            if (!IsFinite(args.NetProfit) || !IsFinite(filteredNetProfit))
                return double.MinValue;

            // Reject an overall result that is not profitable.
            if (filteredNetProfit <= 0.0)
                return 3.0 * filteredNetProfit;

            double minimumSideNetProfit = Math.Min(longNetProfit, shortNetProfit);
            double maximumEquityDrawdownAmount = args.MaxEquityDrawdown;

            if (!IsFinite(maximumEquityDrawdownAmount) ||
                longTradeLargestWin <= 0.0 ||
                shortTradeLargestWin <= 0.0 ||
                maximumEquityDrawdownAmount <= 0.0)
            {
                return double.MinValue;
            }

            // Fallback count includes open positions and groups rollover segments by trade-idea comment.
            if (needsFallbackCount)
            {
                foreach (Position position in args.Positions)
                {
                    if (position.SymbolName != SymbolName || !IsKnownLabel(position.Label))
                        continue;

                    if (!TryGetFitnessEntryOrigin(
                            position.Id,
                            position.Comment,
                            out EntryOriginMode entryOrigin) ||
                        !IsEligibleForOptimizationFitness(entryOrigin))
                        continue;

                    AddFitnessEntryIdentity(
                        position.Id,
                        position.Comment,
                        distinctTradeIdeaIds,
                        distinctPositionIdsWithoutIdea,
                        positionIdsWithIdea);
                }
            }

            int totalFilledOrders = needsFallbackCount
                ? distinctTradeIdeaIds.Count + distinctPositionIdsWithoutIdea.Count
                : _filledPendingOrdersCount;

            DateTime endUtc = Server.TimeInUtc;
            DateTime observationStartUtc = _fitnessStartTimeUtc != default(DateTime)
                ? _fitnessStartTimeUtc
                : endUtc;
            DateTime frequencyStartUtc = _strategyOperationalStartUtc.HasValue
                ? _strategyOperationalStartUtc.Value
                : observationStartUtc;

            double elapsedDays;
            double elapsedWeeks;
            double fillsPerWeek;
            double rawFrequencyWeight;
            double frequencyConfidence;
            double frequencyWeight = CalculateFrequencyFitnessWeight(
                totalFilledOrders,
                frequencyStartUtc,
                endUtc,
                out elapsedDays,
                out elapsedWeeks,
                out fillsPerWeek,
                out rawFrequencyWeight,
                out frequencyConfidence);

            double totalBacktestDays = (endUtc - observationStartUtc).TotalDays;
            double strategyOperationalDays =
                _strategyOperationalStartUtc.HasValue
                    ? elapsedDays
                    : 0.0;

            LogNormal("OPTIMISATION", "FREQUENCY_FITNESS",
                "totalBacktestDays={0:F3} strategyOperationalDays={1:F3} operationalStartCaptured={2} elapsedWeeksUsed={3:F6} filledPendingOrders={4} rolloverRestorationFillsExcluded={5} rolloverExclusionCountKnown={6} fillsPerWeek={7:F4} targetFillsPerWeek={8:F4} toleranceFillsPerWeek={9:F4} minFrequencyWeight={10:F4} rawFrequencyWeight={11:F4} frequencyConfidence={12:F4} effectiveFrequencyWeight={13:F4} frequencyScoringStartDays={14} frequencyFullWeightDays={15}",
                totalBacktestDays,
                strategyOperationalDays,
                _strategyOperationalStartUtc.HasValue,
                elapsedWeeks,
                totalFilledOrders,
                _fitnessExcludedRolloverPendingOrderIds.Count,
                _fitnessTrackingInitialized,
                fillsPerWeek,
                TargetFillsPerWeek,
                FillsPerWeekTolerance,
                MinFrequencyWeight,
                rawFrequencyWeight,
                frequencyConfidence,
                frequencyWeight,
                FrequencyScoringStartDays,
                FrequencyFullWeightDays);

            double longTradeBoundedScore =
                longTradeLargestWin /
                (longTradeLargestWin + longTradeLargestLoss);

            double shortTradeBoundedScore =
                shortTradeLargestWin /
                (shortTradeLargestWin + shortTradeLargestLoss);

            double minimumBoundedScore = Math.Min(
                longTradeBoundedScore,
                shortTradeBoundedScore);

            double baseFitness = minimumBoundedScore *
                                 (0.6 * minimumSideNetProfit + filteredNetProfit) /
                                 maximumEquityDrawdownAmount;
            if (!IsFinite(baseFitness))
                return double.MinValue;

            // Keep every profitable pass at or above zero so losing passes cannot outrank it.
            return Math.Max(0.0, baseFitness) * frequencyWeight;
        }

        private double CalculateFrequencyFitnessWeight(
            int filledPendingOrderCount,
            DateTime startUtc,
            DateTime endUtc,
            out double elapsedDays,
            out double elapsedWeeks,
            out double fillsPerWeek,
            out double rawFrequencyWeight,
            out double frequencyConfidence)
        {
            elapsedDays = (endUtc - startUtc).TotalDays;
            elapsedWeeks = 0.0;
            fillsPerWeek = 0.0;
            rawFrequencyWeight = 1.0;
            frequencyConfidence = 0.0;

            if (endUtc < startUtc ||
                !IsFinite(elapsedDays) ||
                filledPendingOrderCount < 0 ||
                !IsFinite(TargetFillsPerWeek) ||
                TargetFillsPerWeek < 0.0 ||
                !IsFinite(FillsPerWeekTolerance) ||
                FillsPerWeekTolerance <= 0.0 ||
                !IsFinite(MinFrequencyWeight) ||
                MinFrequencyWeight < 0.0 ||
                MinFrequencyWeight > 1.0 ||
                FrequencyScoringStartDays < 0 ||
                FrequencyFullWeightDays < 1 ||
                FrequencyFullWeightDays < FrequencyScoringStartDays)
            {
                LogError("OPTIMISATION", "INVALID_FREQUENCY_INPUT",
                    "startUtc={0:O} endUtc={1:O} elapsedDays={2:R} filledPendingOrderCount={3} targetFillsPerWeek={4:R} toleranceFillsPerWeek={5:R} minFrequencyWeight={6:R}; neutral frequency weight applied",
                    startUtc,
                    endUtc,
                    elapsedDays,
                    filledPendingOrderCount,
                    TargetFillsPerWeek,
                    FillsPerWeekTolerance,
                    MinFrequencyWeight);
                return 1.0;
            }

            double elapsedDaysForRate = Math.Max(
                elapsedDays,
                1.0 / 1440.0);
            elapsedWeeks = elapsedDaysForRate / 7.0;
            fillsPerWeek = filledPendingOrderCount / elapsedWeeks;
            if (!IsFinite(elapsedWeeks) ||
                !IsFinite(fillsPerWeek))
            {
                LogError("OPTIMISATION", "INVALID_FREQUENCY_RATE",
                    "elapsedWeeks={0:R} fillsPerWeek={1:R} filledPendingOrderCount={2}; neutral frequency weight applied",
                    elapsedWeeks,
                    fillsPerWeek,
                    filledPendingOrderCount);
                elapsedWeeks = 0.0;
                fillsPerWeek = 0.0;
                return 1.0;
            }

            double deviation =
                (fillsPerWeek - TargetFillsPerWeek) /
                FillsPerWeekTolerance;
            double gaussian = Math.Exp(-0.5 * deviation * deviation);
            rawFrequencyWeight =
                MinFrequencyWeight +
                (1.0 - MinFrequencyWeight) * gaussian;
            frequencyConfidence = CalculateFrequencyConfidence(elapsedDays);

            return 1.0 -
                   frequencyConfidence * (1.0 - rawFrequencyWeight);
        }

        private double CalculateFrequencyConfidence(double elapsedDays)
        {
            double startDays = FrequencyScoringStartDays;
            double fullDays = FrequencyFullWeightDays;

            if (fullDays == startDays)
                return elapsedDays >= fullDays ? 1.0 : 0.0;

            if (elapsedDays <= startDays)
                return 0.0;

            if (elapsedDays >= fullDays)
                return 1.0;

            return (elapsedDays - startDays) /
                   (fullDays - startDays);
        }

        private static void AddFitnessEntryIdentity(
            int positionId,
            string comment,
            HashSet<Guid> distinctTradeIdeaIds,
            HashSet<int> distinctPositionIdsWithoutIdea,
            HashSet<int> positionIdsWithIdea)
        {
            if (TryParseLineageComment(comment, out Guid tradeIdeaId))
            {
                distinctTradeIdeaIds.Add(tradeIdeaId);
                positionIdsWithIdea.Add(positionId);
                distinctPositionIdsWithoutIdea.Remove(positionId);
            }
            else if (!positionIdsWithIdea.Contains(positionId))
            {
                distinctPositionIdsWithoutIdea.Add(positionId);
            }
        }

        #endregion
    }
}
