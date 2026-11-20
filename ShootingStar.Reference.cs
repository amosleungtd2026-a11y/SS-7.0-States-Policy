using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const int ReferenceWindowEligibleMinutes = 480;
        private const int ReferenceHistoryRetrySeconds = 30;
        private const int ReferenceHistoryTicksPerTimerBatch = 2000;
        private const int ReferenceHistoryBarsPerTimerBatch = 2000;
        private const int ReferenceRefreshSeconds = 60;
        private const int ReferenceNewestBucketMaximumAgeMinutes = 2;
        private const string ActiveReferencePriceDomain = "Bid";
        private const int ReferenceSchemaVersion = 2;

        private sealed class ReferenceMinuteBucket
        {
            public double HighPrice;
            public double LowPrice;
        }

        private Ticks _referenceTicks;
        private Bars _referenceMinuteBars;
        private readonly SortedDictionary<DateTime, ReferenceMinuteBucket> _referenceMinuteBuckets =
            new SortedDictionary<DateTime, ReferenceMinuteBucket>();
        private DateTime _nextReferenceHistoryRetryUtc = DateTime.MinValue;
        private DateTime _nextReferenceMinuteBarsRetryUtc = DateTime.MinValue;
        private DateTime _nextReferenceRefreshUtc = DateTime.MinValue;
        private DateTime _lastReferenceRefreshUtc = DateTime.MinValue;
        private int _referenceHistoryCursor;
        private int _referenceHistoryEnd;
        private int _referenceMinuteBarsCursor;
        private bool _referenceMinuteBarsSeeded;
        private bool _referenceMinuteBarsLoadInProgress;
        private bool _referenceHistoryLoadInProgress;
        private long _referenceHistoryGeneration;
        private long _activeReferenceHistoryGeneration;
        private long _referenceMinuteBarsGeneration;
        private long _activeReferenceMinuteBarsGeneration;
        private bool _referenceRangeReady;
        private double _referenceHigh = double.NaN;
        private double _referenceLow = double.NaN;

        private void InitializeReferenceTickHistory()
        {
            LogNormal("REFERENCE", "REFERENCE_PRICE_DOMAIN",
                "activeDomain={0} historicalSource=BidMinuteBarsWithBidTickFallback referenceSchemaVersion={1} liveSource=BidTicks",
                ActiveReferencePriceDomain,
                ReferenceSchemaVersion);
            _activeReferenceHistoryGeneration = ++_referenceHistoryGeneration;
            _referenceHistoryLoadInProgress = false;
            _referenceTicks = MarketData.GetTicks(Symbol.Name);
            _referenceHistoryCursor = 0;
            _referenceHistoryEnd = _referenceTicks == null ? 0 : _referenceTicks.Count;
            if (_referenceMinuteBars == null)
                InitializeReferenceMinuteBars();
            if (_referenceRangeReady && _referenceHistoryCursor < _referenceHistoryEnd)
                RefreshReferenceRangeIfDue(Server.TimeInUtc, true);
        }

        private void InitializeReferenceMinuteBars()
        {
            _activeReferenceMinuteBarsGeneration = ++_referenceMinuteBarsGeneration;
            _referenceMinuteBarsLoadInProgress = false;
            _referenceMinuteBars = MarketData.GetBars(TimeFrame.Minute, Symbol.Name);
            _referenceMinuteBarsCursor = _referenceMinuteBars == null
                ? -1
                : _referenceMinuteBars.Count - 2;

            if (_referenceMinuteBars != null)
            {
                LogNormal("REF", "MINUTE_BAR_HISTORY_AVAILABLE",
                    "symbol={0} loadedBars={1} source=bid",
                    Symbol.Name, _referenceMinuteBars.Count);
            }
        }

        private void RecordReferenceTick(DateTime utc, double bid, double ask)
        {
            StoreReferenceTick(utc, bid, ask);
            bool forceRefresh = utc < _lastReferenceRefreshUtc ||
                (_referenceRangeReady &&
                 _referenceHistoryCursor < _referenceHistoryEnd &&
                 _referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes) ||
                (!_referenceRangeReady && _referenceMinuteBuckets.Count >= ReferenceWindowEligibleMinutes &&
                 GetReferenceWindowUnavailableReason(utc) == null);
            if (forceRefresh || utc >= _nextReferenceRefreshUtc)
                RefreshReferenceRangeIfDue(utc, forceRefresh);
        }

        private void StoreReferenceTick(DateTime utc, double bid, double ask)
        {
            if (!IsReferenceMarketOpen(utc) ||
                !IsFinitePositive(bid) ||
                !IsFinitePositive(ask) ||
                ask < bid)
            {
                return;
            }

            StoreReferencePriceRange(utc, bid, bid);
        }

        private void StoreReferencePriceRange(DateTime utc, double high, double low)
        {
            if (!IsReferenceMarketOpen(utc) ||
                !IsFinitePositive(high) ||
                !IsFinitePositive(low) ||
                high < low)
            {
                return;
            }

            DateTime minuteUtc = new DateTime(
                utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

            if (!_referenceMinuteBuckets.TryGetValue(minuteUtc, out ReferenceMinuteBucket bucket))
            {
                bucket = new ReferenceMinuteBucket
                {
                    HighPrice = high,
                    LowPrice = low
                };
                _referenceMinuteBuckets.Add(minuteUtc, bucket);
                TrimReferenceMinuteBuckets();
                return;
            }

            bucket.HighPrice = Math.Max(bucket.HighPrice, high);
            bucket.LowPrice = Math.Min(bucket.LowPrice, low);
        }

        private bool IsReferenceMarketOpen(DateTime utc)
        {
            MarketSessionState state = GetMarketSessionState(utc);
            return state == MarketSessionState.Open ||
                   state == MarketSessionState.PostOpenStabilization;
        }

        private void ProcessReferenceHistoryRecovery(DateTime utcNow)
        {
            RefreshReferenceRangeIfDue(utcNow);
            ProcessReferenceMinuteBarsBatch(utcNow);

            if (_referenceMinuteBuckets.Count >= ReferenceWindowEligibleMinutes)
            {
                RefreshReferenceRangeIfDue(utcNow);
                return;
            }

            if (_referenceMinuteBars == null &&
                utcNow >= _nextReferenceMinuteBarsRetryUtc)
            {
                _nextReferenceMinuteBarsRetryUtc =
                    utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
                InitializeReferenceMinuteBars();
                if (_referenceMinuteBars == null)
                {
                    LogActiveWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_UNAVAILABLE",
                        "one-minute bar history is unavailable; tick history remains the fallback");
                }
            }

            if (_referenceTicks == null)
            {
                if (utcNow < _nextReferenceHistoryRetryUtc)
                    return;

                _nextReferenceHistoryRetryUtc = utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
                InitializeReferenceTickHistory();
                if (_referenceTicks == null)
                {
                    LogActiveWarning("reference-tick-history", "REF", "TICK_HISTORY_UNAVAILABLE",
                        "tick history is unavailable; new entries remain disabled until a complete rolling reference range can be initialized");
                    return;
                }
            }

            ClearWarning("reference-tick-history", "REF", "TICK_HISTORY_AVAILABLE",
                "tick history is available for rolling reference initialization");

            if (_referenceHistoryCursor < _referenceHistoryEnd ||
                (_referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes &&
                 utcNow >= _nextReferenceHistoryRetryUtc))
            {
                ProcessReferenceHistoryBatch(utcNow);
            }
        }

        private void ProcessReferenceMinuteBarsBatch(DateTime utcNow)
        {
            if (_referenceMinuteBars == null || _referenceMinuteBarsSeeded)
                return;

            if (_referenceMinuteBuckets.Count >= ReferenceWindowEligibleMinutes)
            {
                _referenceMinuteBarsSeeded = true;
                return;
            }

            int lastCompletedIndex = _referenceMinuteBars.Count - 2;
            if (_referenceMinuteBarsCursor > lastCompletedIndex)
                _referenceMinuteBarsCursor = lastCompletedIndex;

            int processed = 0;
            while (_referenceMinuteBarsCursor >= 0 &&
                   _referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes &&
                   processed < ReferenceHistoryBarsPerTimerBatch)
            {
                int index = _referenceMinuteBarsCursor--;
                DateTime barOpenTime = DateTime.SpecifyKind(
                    _referenceMinuteBars.OpenTimes[index], DateTimeKind.Utc);
                StoreReferencePriceRange(
                    barOpenTime,
                    _referenceMinuteBars.HighPrices[index],
                    _referenceMinuteBars.LowPrices[index]);
                processed++;
            }

            if (_referenceMinuteBuckets.Count >= ReferenceWindowEligibleMinutes)
            {
                MarkReferenceMinuteBarsSeeded(utcNow);
                return;
            }

            if (_referenceMinuteBarsCursor < 0)
                RequestMoreReferenceMinuteBars(utcNow);
        }

        private void MarkReferenceMinuteBarsSeeded(DateTime utcNow)
        {
            if (_referenceMinuteBarsSeeded)
                return;

            _referenceMinuteBarsSeeded = true;
            _referenceMinuteBarsLoadInProgress = false;
            ClearWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_READY",
                "one-minute bars supplied the complete rolling reference seed");
            LogNormal("REF", "MINUTE_BAR_HISTORY_SEEDED",
                "eligibleMinuteBuckets={0} required={1} updatedUtc={2:O}",
                _referenceMinuteBuckets.Count,
                ReferenceWindowEligibleMinutes,
                utcNow);
        }

        private void RequestMoreReferenceMinuteBars(DateTime utcNow)
        {
            if (_referenceMinuteBars == null ||
                _referenceMinuteBarsLoadInProgress ||
                utcNow < _nextReferenceMinuteBarsRetryUtc)
            {
                return;
            }

            _referenceMinuteBarsLoadInProgress = true;
            long requestGeneration = ++_referenceMinuteBarsGeneration;
            _activeReferenceMinuteBarsGeneration = requestGeneration;
            _nextReferenceMinuteBarsRetryUtc =
                utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
            LogActiveWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_INCOMPLETE",
                "eligibleMinuteBuckets={0} required={1}; requesting more completed one-minute bars",
                _referenceMinuteBuckets.Count, ReferenceWindowEligibleMinutes);
            try
            {
                _referenceMinuteBars.LoadMoreHistoryAsync(
                    args => OnReferenceMinuteBarsLoaded(args, requestGeneration));
            }
            catch (Exception exception)
            {
                if (requestGeneration != _activeReferenceMinuteBarsGeneration)
                    return;

                _activeReferenceMinuteBarsGeneration = ++_referenceMinuteBarsGeneration;
                _referenceMinuteBarsLoadInProgress = false;
                _nextReferenceMinuteBarsRetryUtc =
                    utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
                LogActiveWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_LOAD_FAILED",
                    "LoadMoreHistoryAsync threw {0}: {1}; retryUtc={2:O}; eligibleMinuteBuckets={3} required={4}",
                    exception.GetType().Name,
                    exception.Message,
                    _nextReferenceMinuteBarsRetryUtc,
                    _referenceMinuteBuckets.Count,
                    ReferenceWindowEligibleMinutes);
            }
        }

        private void OnReferenceMinuteBarsLoaded(
            BarsHistoryLoadedEventArgs args,
            long requestGeneration)
        {
            if (_startupStopRequested ||
                !_referenceMinuteBarsLoadInProgress ||
                requestGeneration != _activeReferenceMinuteBarsGeneration)
            {
                LogVerbose("REF", "STALE_MINUTE_BAR_HISTORY_CALLBACK_IGNORED",
                    "requestGeneration={0} activeGeneration={1} startupStopRequested={2}",
                    requestGeneration, _activeReferenceMinuteBarsGeneration, _startupStopRequested);
                return;
            }

            _referenceMinuteBarsLoadInProgress = false;
            if (args == null || args.Count <= 0 || _referenceMinuteBars == null)
            {
                _nextReferenceMinuteBarsRetryUtc =
                    Server.TimeInUtc.AddSeconds(ReferenceHistoryRetrySeconds);
                LogActiveWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_INCOMPLETE",
                    "no additional one-minute bars were loaded; eligibleMinuteBuckets={0} required={1}",
                    _referenceMinuteBuckets.Count, ReferenceWindowEligibleMinutes);
                return;
            }

            _referenceMinuteBarsCursor = _referenceMinuteBars.Count - 2;
            _nextReferenceMinuteBarsRetryUtc = Server.TimeInUtc;
            ClearWarning("reference-minute-bars", "REF", "MINUTE_BAR_HISTORY_LOAD_RECOVERED",
                "historical one-minute bar loading resumed; loadedBars=" + args.Count);
        }

        private void RequestMoreReferenceHistory(DateTime utcNow)
        {
            if (_referenceTicks == null ||
                _referenceHistoryLoadInProgress ||
                utcNow < _nextReferenceHistoryRetryUtc)
            {
                return;
            }

            _referenceHistoryLoadInProgress = true;
            long requestGeneration = ++_referenceHistoryGeneration;
            _activeReferenceHistoryGeneration = requestGeneration;
            _nextReferenceHistoryRetryUtc =
                utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
            LogActiveWarning("reference-window-history", "REF", "ROLLING_HISTORY_INCOMPLETE",
                "eligibleMinuteBuckets={0} required={1}; new entries remain disabled while more tick history is requested",
                _referenceMinuteBuckets.Count, ReferenceWindowEligibleMinutes);
            try
            {
                _referenceTicks.LoadMoreHistoryAsync(args => OnReferenceHistoryLoaded(args, requestGeneration));
            }
            catch (Exception exception)
            {
                if (requestGeneration != _activeReferenceHistoryGeneration)
                    return;

                _activeReferenceHistoryGeneration = ++_referenceHistoryGeneration;
                _referenceHistoryLoadInProgress = false;
                _nextReferenceHistoryRetryUtc =
                    utcNow.AddSeconds(ReferenceHistoryRetrySeconds);
                LogActiveWarning("reference-window-history", "REF", "TICK_HISTORY_LOAD_FAILED",
                    "LoadMoreHistoryAsync threw {0}: {1}; retryUtc={2:O}; eligibleMinuteBuckets={3} required={4}",
                    exception.GetType().Name,
                    exception.Message,
                    _nextReferenceHistoryRetryUtc,
                    _referenceMinuteBuckets.Count,
                    ReferenceWindowEligibleMinutes);
            }
        }

        private void OnReferenceHistoryLoaded(TicksHistoryLoadedEventArgs args, long requestGeneration)
        {
            if (_startupStopRequested || !_referenceHistoryLoadInProgress ||
                requestGeneration != _activeReferenceHistoryGeneration)
            {
                LogVerbose("REF", "STALE_HISTORY_CALLBACK_IGNORED",
                    "requestGeneration={0} activeGeneration={1} startupStopRequested={2}",
                    requestGeneration, _activeReferenceHistoryGeneration, _startupStopRequested);
                return;
            }

            _referenceHistoryLoadInProgress = false;

            if (args == null || args.Count <= 0 || _referenceTicks == null)
            {
                _nextReferenceHistoryRetryUtc =
                    Server.TimeInUtc.AddSeconds(ReferenceHistoryRetrySeconds);
                LogActiveWarning("reference-window-history", "REF", "ROLLING_HISTORY_INCOMPLETE",
                    "no additional ticks were loaded; eligibleMinuteBuckets={0} required={1}",
                    _referenceMinuteBuckets.Count, ReferenceWindowEligibleMinutes);
                return;
            }

            _referenceHistoryCursor = 0;
            _referenceHistoryEnd = Math.Min(args.Count, _referenceTicks.Count);
            if (_referenceRangeReady && _referenceHistoryCursor < _referenceHistoryEnd)
                RefreshReferenceRangeIfDue(Server.TimeInUtc, true);
            ClearWarning("reference-window-history", "REF", "TICK_HISTORY_LOAD_RECOVERED",
                "historical tick loading resumed; loadedTicks=" + args.Count);
        }

        private void ProcessReferenceHistoryBatch(DateTime utcNow)
        {
            if (_referenceTicks == null)
                return;

            int batchEnd = Math.Min(
                _referenceHistoryCursor + ReferenceHistoryTicksPerTimerBatch,
                _referenceHistoryEnd);

            for (; _referenceHistoryCursor < batchEnd; _referenceHistoryCursor++)
            {
                Tick tick = _referenceTicks[_referenceHistoryCursor];
                DateTime tickUtc = DateTime.SpecifyKind(tick.Time, DateTimeKind.Utc);
                if (tickUtc <= utcNow)
                    StoreReferenceTick(tickUtc, tick.Bid, tick.Ask);
            }

            if (_referenceHistoryCursor < _referenceHistoryEnd)
                return;

            if (_referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes)
                RequestMoreReferenceHistory(utcNow);

            if (!_referenceRangeReady && _referenceMinuteBuckets.Count >= ReferenceWindowEligibleMinutes &&
                GetReferenceWindowUnavailableReason(utcNow) == null)
                RefreshReferenceRangeIfDue(utcNow, true);
        }

        private void RefreshReferenceRangeIfDue(DateTime utcNow, bool force = false)
        {
            if (!force && utcNow >= _lastReferenceRefreshUtc && utcNow < _nextReferenceRefreshUtc)
                return;

            RefreshReferenceRange(utcNow);
            _lastReferenceRefreshUtc = utcNow;
            DateTime currentMinuteUtc = new DateTime(
                utcNow.Year, utcNow.Month, utcNow.Day,
                utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);
            _nextReferenceRefreshUtc = currentMinuteUtc.AddSeconds(ReferenceRefreshSeconds);
        }

        private void TrimReferenceMinuteBuckets()
        {
            while (_referenceMinuteBuckets.Count > ReferenceWindowEligibleMinutes)
            {
                DateTime oldestMinuteUtc = _referenceMinuteBuckets.Keys.First();
                _referenceMinuteBuckets.Remove(oldestMinuteUtc);
            }
        }

        private string GetReferenceWindowUnavailableReason(DateTime utcNow)
        {
            if (_referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes &&
                _referenceHistoryCursor < _referenceHistoryEnd)
                return "historyBatchPending";

            if (_referenceMinuteBuckets.Count < ReferenceWindowEligibleMinutes)
                return "insufficientCount";

            DateTime currentMinuteUtc = new DateTime(
                utcNow.Year, utcNow.Month, utcNow.Day,
                utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);
            double newestAgeMinutes = (currentMinuteUtc - _referenceMinuteBuckets.Keys.Last()).TotalMinutes;
            return newestAgeMinutes < 0 || newestAgeMinutes > ReferenceNewestBucketMaximumAgeMinutes
                ? "staleNewestBucket"
                : null;
        }

        private void RefreshReferenceRange(DateTime utcNow)
        {
            TrimReferenceMinuteBuckets();
            string unavailableReason = GetReferenceWindowUnavailableReason(utcNow);
            if (unavailableReason != null)
            {
                bool wasReady = _referenceRangeReady;
                _referenceRangeReady = false;
                LogActiveWarningWithoutUpdates("reference-window-current", "REF", "ROLLING_RANGE_NOT_CURRENT",
                    "reason={0} eligibleMinuteBuckets={1} required={2} historyCursor={3} historyEnd={4}; new entries remain disabled",
                    unavailableReason, _referenceMinuteBuckets.Count, ReferenceWindowEligibleMinutes,
                    _referenceHistoryCursor, _referenceHistoryEnd);
                if (wasReady)
                    LogNormal("REF", "ROLLING_RANGE_INVALIDATED", "reason={0} utc={1:O}", unavailableReason, utcNow);
                return;
            }

            ClearWarning("reference-window-current", "REF", "ROLLING_RANGE_CURRENT",
                "the rolling reference range has enough eligible buckets and a current newest bucket");

            bool becameReady = !_referenceRangeReady;
            double previousHigh = _referenceHigh;
            double previousLow = _referenceLow;
            _referenceHigh = _referenceMinuteBuckets.Values.Max(bucket => bucket.HighPrice);
            _referenceLow = _referenceMinuteBuckets.Values.Min(bucket => bucket.LowPrice);
            _referenceRangeReady = true;

            ClearWarning("reference-window-history", "REF", "ROLLING_HISTORY_READY",
                "the complete rolling reference range is initialized");
            ClearWarning("reference-tick-history", "REF", "TICK_HISTORY_READY",
                "tick history is available for rolling reference updates");

            if (becameReady)
            {
                DrawReferencePriceLine();
                LogNormal("REF", "ROLLING_RANGE_READY",
                    "eligibleMinuteBuckets={0} refHi={1} refLo={2} updatedUtc={3:O}",
                    _referenceMinuteBuckets.Count,
                    FormatPrice(_referenceHigh),
                    FormatPrice(_referenceLow),
                    utcNow);
                return;
            }

            if (!IsFinitePositive(previousHigh) ||
                !IsFinitePositive(previousLow) ||
                Math.Abs(previousHigh - _referenceHigh) > _symbolTickSize * 0.5 ||
                Math.Abs(previousLow - _referenceLow) > _symbolTickSize * 0.5)
            {
                DrawReferencePriceLine();
                RecordReferenceRangeUpdate(_referenceHigh, _referenceLow);
            }
        }

        private double GetMovementReferencePrice(StrategyKind strategy, TradeType tradeType)
        {
            bool usesReferenceLow =
                (strategy == StrategyKind.MeanReversion && tradeType == TradeType.Sell) ||
                (strategy == StrategyKind.TrendFollowing && tradeType == TradeType.Buy);
            return usesReferenceLow ? _referenceLow : _referenceHigh;
        }
    }
}
