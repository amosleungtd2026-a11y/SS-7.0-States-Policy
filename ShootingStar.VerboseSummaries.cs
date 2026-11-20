using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private static readonly TimeSpan VerboseSummaryInterval = TimeSpan.FromHours(1);

        private DateTime? _verboseSummaryWindowStartUtc;
        private DateTime _nextVerboseSummaryUtc;

        private int _rangeUpdateCount;
        private double _rangeMinHigh, _rangeMaxHigh, _rangeMinLow, _rangeMaxLow;
        private double _rangeLastHigh, _rangeLastLow;

        private sealed class EntrySkipSummary
        {
            public int Count;
            public DateTime FirstUtc;
            public DateTime LastUtc;
        }

        private sealed class MevNoEntrySummary
        {
            public HourlyMevSignal HourlyState;
            public DailyMevContext DailyContext;
            public string Reason;
            public long Count;
            public DateTime FirstUtc;
            public DateTime LastUtc;
            public double MinHourlyMev;
            public double MaxHourlyMev;
            public double MinDailyMev;
            public double MaxDailyMev;
            public double LastHourlyMev;
            public double LastDailyMev;
        }

        private sealed class DaughterContextCandidateSummary
        {
            public DailyMevContext CommittedContext;
            public DailyMevContext CandidateContext;
            public long Count;
            public DateTime FirstUtc;
            public DateTime LastUtc;
            public double MinDailyMev;
            public double MaxDailyMev;
            public double LastDailyMev;
        }

        private readonly Dictionary<string, EntrySkipSummary> _entrySkipsByReason =
            new Dictionary<string, EntrySkipSummary>(StringComparer.Ordinal);
        private long _entryEvaluationPasses;
        private long _entryEvaluationPassesWithCandidates;
        private long _entryCandidateEvaluations;
        private long _entryAcceptedOrders;
        private long _entryLimitReachedPasses;
        private readonly Dictionary<string, MevNoEntrySummary> _mevNoEntries =
            new Dictionary<string, MevNoEntrySummary>(StringComparer.Ordinal);
        private readonly Dictionary<string, DaughterContextCandidateSummary> _daughterContextCandidates =
            new Dictionary<string, DaughterContextCandidateSummary>(StringComparer.Ordinal);

        private void StartVerboseSummaryWindowIfNeeded()
        {
            if (_verboseSummaryWindowStartUtc.HasValue)
                return;
            _verboseSummaryWindowStartUtc = Server.TimeInUtc;
            _nextVerboseSummaryUtc = _verboseSummaryWindowStartUtc.Value + VerboseSummaryInterval;
        }

        private void RecordReferenceRangeUpdate(double high, double low)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            StartVerboseSummaryWindowIfNeeded();
            if (_rangeUpdateCount == 0)
            {
                _rangeMinHigh = _rangeMaxHigh = high;
                _rangeMinLow = _rangeMaxLow = low;
            }
            else
            {
                _rangeMinHigh = Math.Min(_rangeMinHigh, high);
                _rangeMaxHigh = Math.Max(_rangeMaxHigh, high);
                _rangeMinLow = Math.Min(_rangeMinLow, low);
                _rangeMaxLow = Math.Max(_rangeMaxLow, low);
            }
            _rangeLastHigh = high;
            _rangeLastLow = low;
            _rangeUpdateCount++;
        }

        private void RecordEntrySkip(string reason)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            StartVerboseSummaryWindowIfNeeded();
            DateTime now = Server.TimeInUtc;
            EntrySkipSummary summary;
            if (!_entrySkipsByReason.TryGetValue(reason, out summary))
            {
                summary = new EntrySkipSummary { FirstUtc = now };
                _entrySkipsByReason.Add(reason, summary);
            }
            summary.LastUtc = now;
            summary.Count++;
        }

        private void RecordEntryEvaluationPass(EntryEvaluationPass pass)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;

            StartVerboseSummaryWindowIfNeeded();
            _entryEvaluationPasses++;
            _entryCandidateEvaluations += pass.CandidateEvaluations;
            if (pass.CandidateEvaluations > 0)
                _entryEvaluationPassesWithCandidates++;
            _entryAcceptedOrders += pass.AcceptedNewOrders;
            if (pass.LimitReached)
                _entryLimitReachedPasses++;
        }

        private void RecordMevNoEntry(
            double hourlyMev,
            double dailyMev,
            HourlyMevSignal hourlyState,
            DailyMevContext dailyContext,
            string reason,
            DateTime nowUtc)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;

            StartVerboseSummaryWindowIfNeeded();
            string key = string.Concat(hourlyState, "|", dailyContext, "|", reason);
            MevNoEntrySummary summary;
            if (!_mevNoEntries.TryGetValue(key, out summary))
            {
                summary = new MevNoEntrySummary
                {
                    HourlyState = hourlyState,
                    DailyContext = dailyContext,
                    Reason = reason,
                    FirstUtc = nowUtc,
                    MinHourlyMev = hourlyMev,
                    MaxHourlyMev = hourlyMev,
                    MinDailyMev = dailyMev,
                    MaxDailyMev = dailyMev
                };
                _mevNoEntries.Add(key, summary);
            }

            summary.Count++;
            summary.LastUtc = nowUtc;
            summary.MinHourlyMev = Math.Min(summary.MinHourlyMev, hourlyMev);
            summary.MaxHourlyMev = Math.Max(summary.MaxHourlyMev, hourlyMev);
            summary.MinDailyMev = Math.Min(summary.MinDailyMev, dailyMev);
            summary.MaxDailyMev = Math.Max(summary.MaxDailyMev, dailyMev);
            summary.LastHourlyMev = hourlyMev;
            summary.LastDailyMev = dailyMev;
        }

        private void RecordDaughterContextCandidate(
            DailyMevContext committedContext,
            DailyMevContext candidateContext,
            double dailyMev,
            DateTime nowUtc)
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;

            StartVerboseSummaryWindowIfNeeded();
            string key = string.Concat(committedContext, "|", candidateContext);
            DaughterContextCandidateSummary summary;
            if (!_daughterContextCandidates.TryGetValue(key, out summary))
            {
                summary = new DaughterContextCandidateSummary
                {
                    CommittedContext = committedContext,
                    CandidateContext = candidateContext,
                    FirstUtc = nowUtc,
                    MinDailyMev = dailyMev,
                    MaxDailyMev = dailyMev
                };
                _daughterContextCandidates.Add(key, summary);
            }

            summary.Count++;
            summary.LastUtc = nowUtc;
            summary.MinDailyMev = Math.Min(summary.MinDailyMev, dailyMev);
            summary.MaxDailyMev = Math.Max(summary.MaxDailyMev, dailyMev);
            summary.LastDailyMev = dailyMev;
        }

        private void FlushVerboseSummariesIfDue()
        {
            if (_verboseSummaryWindowStartUtc.HasValue && Server.TimeInUtc >= _nextVerboseSummaryUtc)
                FlushVerboseSummaries("periodic");
        }

        private void FlushVerboseSummaries(string flushReason)
        {
            DateTime now = Server.TimeInUtc;
            DateTime start = _verboseSummaryWindowStartUtc ?? now;
            if (_entryEvaluationPasses > 0)
            {
                LogVerbose("ENTRY", "ENTRY_EVALUATION_SUMMARY",
                    "schemaVersion=1 windowStartUtc={0:O} windowEndUtc={1:O} flushReason={2} mode={3} passes={4} passesWithCandidates={5} candidateEvaluations={6} acceptedNewOrders={7} limitReachedPasses={8}",
                    start,
                    now,
                    flushReason,
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    _entryEvaluationPasses,
                    _entryEvaluationPassesWithCandidates,
                    _entryCandidateEvaluations,
                    _entryAcceptedOrders,
                    _entryLimitReachedPasses);
                _entryEvaluationPasses = 0;
                _entryEvaluationPassesWithCandidates = 0;
                _entryCandidateEvaluations = 0;
                _entryAcceptedOrders = 0;
                _entryLimitReachedPasses = 0;
            }

            if (_rangeUpdateCount > 0)
            {
                LogVerbose("REF", "REF_RANGE_SUMMARY",
                    "schemaVersion=1 windowStartUtc={0:O} windowEndUtc={1:O} flushReason={2} updates={3} refHiMin={4} refHiMax={5} refLoMin={6} refLoMax={7} lastRefHi={8} lastRefLo={9}",
                    start, now, flushReason, _rangeUpdateCount,
                    FormatPrice(_rangeMinHigh), FormatPrice(_rangeMaxHigh),
                    FormatPrice(_rangeMinLow), FormatPrice(_rangeMaxLow),
                    FormatPrice(_rangeLastHigh), FormatPrice(_rangeLastLow));
                _rangeUpdateCount = 0;
            }

            foreach (KeyValuePair<string, EntrySkipSummary> pair in _entrySkipsByReason.OrderBy(item => item.Key))
            {
                LogVerbose("ENTRY", "ENTRY_SKIP_SUMMARY",
                    "schemaVersion=2 windowStartUtc={0:O} windowEndUtc={1:O} flushReason={2} skips={3} reason={4} firstSkipUtc={5:O} lastSkipUtc={6:O}",
                    start, now, flushReason, pair.Value.Count.ToString(CultureInfo.InvariantCulture),
                    pair.Key, pair.Value.FirstUtc, pair.Value.LastUtc);
            }
            _entrySkipsByReason.Clear();

            foreach (KeyValuePair<string, MevNoEntrySummary> pair in
                     _mevNoEntries.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                MevNoEntrySummary summary = pair.Value;
                LogVerbose("POLICY", "MEV_NO_ENTRY_SUMMARY",
                    "schemaVersion=1 windowStartUtc={0:O} windowEndUtc={1:O} flushReason={2} count={3} hourlyState={4} dailyContext={5} reason={6} firstUtc={7:O} lastUtc={8:O} hourlyMevMin={9:F4} hourlyMevMax={10:F4} hourlyMevLast={11:F4} dailyMevMin={12:F4} dailyMevMax={13:F4} dailyMevLast={14:F4}",
                    start,
                    now,
                    flushReason,
                    summary.Count,
                    summary.HourlyState,
                    summary.DailyContext,
                    summary.Reason,
                    summary.FirstUtc,
                    summary.LastUtc,
                    summary.MinHourlyMev,
                    summary.MaxHourlyMev,
                    summary.LastHourlyMev,
                    summary.MinDailyMev,
                    summary.MaxDailyMev,
                    summary.LastDailyMev);
            }
            _mevNoEntries.Clear();

            foreach (KeyValuePair<string, DaughterContextCandidateSummary> pair in
                     _daughterContextCandidates.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                DaughterContextCandidateSummary summary = pair.Value;
                LogVerbose("DAUGHTER", "CONTEXT_CANDIDATE_SUMMARY",
                    "schemaVersion=1 windowStartUtc={0:O} windowEndUtc={1:O} flushReason={2} count={3} committedContext={4} candidateContext={5} firstUtc={6:O} lastUtc={7:O} dailyMevMin={8:F4} dailyMevMax={9:F4} dailyMevLast={10:F4}",
                    start,
                    now,
                    flushReason,
                    summary.Count,
                    summary.CommittedContext,
                    summary.CandidateContext,
                    summary.FirstUtc,
                    summary.LastUtc,
                    summary.MinDailyMev,
                    summary.MaxDailyMev,
                    summary.LastDailyMev);
            }
            _daughterContextCandidates.Clear();

            _verboseSummaryWindowStartUtc = flushReason == "shutdown" ? (DateTime?)null : now;
            _nextVerboseSummaryUtc = now + VerboseSummaryInterval;
        }
    }
}
