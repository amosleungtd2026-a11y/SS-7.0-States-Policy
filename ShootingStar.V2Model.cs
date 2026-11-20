using System;
using System.Security.Cryptography;
using System.Text;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const int V2ConfigurationSchemaVersion = 2;
        private const string V2DecisionModelId = "MEV_STATE_BOSS_WIFE_V2";

        private const double V2HourlyHysteresis = 0.025;
        private const double V2DailyHysteresis = 0.075;
        private const double V2HourlyStrengthWeight = 0.60;
        private const double V2DailyStrengthWeight = 0.40;
        private const int V2HourlyPersistenceSeconds = 30;
        private const int V2DailyPersistenceSeconds = 180;
        private const int V2MinimumQualifyingRefreshes = 2;
        private const double V2MevEvidenceEpsilon = 1e-9;
        private const double V2ExtremeHourlyClearHysteresis = 0.025;
        private const double V2ExtremeDailyClearHysteresis = 0.075;
        private const int V2ReversalWatchActivationSeconds = 30;
        private const int V2ReversalWatchMinimumRefreshes = 2;
        private const int V2ReversalWatchInvalidGraceSeconds = 30;
        private const double V2ReversalWatchActivationThreshold = 0.50;
        private const double V2ReversalWatchClearThreshold = 0.25;
        private const double V2BossMaximumHoldProbability = 0.20;
        private const double V2BossMaximumContrarySellProbability = 0.07;
        private const double V2BossMaximumContraryBuyProbability = 0.12;
        private const double MinimumStopOrderMultiplier = 1.10;
        private const double MaximumStopOrderMultiplier = 3.00;

        private enum TrendDirectionState
        {
            Down,
            Neutral,
            Up
        }

        private enum CombinedTrendRegime
        {
            Range,
            EmergingUptrend,
            ConfirmedUptrend,
            UptrendPause,
            BullishPullback,
            EmergingDowntrend,
            ConfirmedDowntrend,
            DowntrendPause,
            BearishRally
        }

        private enum V2BossOutcome
        {
            Hold,
            Long,
            Short
        }

        private enum V2OrderKind
        {
            Limit,
            Stop
        }

        private enum ExtremeCountertrendStyle
        {
            MeanReversionLimit,
            StopConfirmation
        }

        private sealed class TrendTransitionCandidate
        {
            public TrendDirectionState TargetState;
            public DateTime FirstSeenUtc;
            public DateTime LastCountedMarketDataUtc;
            public long LastCountedRevision;
            public double LastCountedMev;
            public int QualifyingRefreshCount;
        }

        private sealed class V2ReversalWatch
        {
            public bool IsActive;
            public bool IsPaused;
            public DateTime BarOpenUtc = DateTime.MinValue;
            public DateTime CandidateFirstSeenUtc = DateTime.MinValue;
            public DateTime InvalidSinceUtc = DateTime.MinValue;
            public long LastCountedEvidenceRevision;
            public int QualifyingRefreshCount;
            public double LastNormalizedEvidence = double.NaN;
        }

        private sealed class TrendStateTracker
        {
            public TrendDirectionState State;
            public bool IsInitialized;
            public DateTime BarOpenUtc = DateTime.MinValue;
            public TrendTransitionCandidate Candidate;

            public bool Update(
                double mev,
                DateTime nowUtc,
                DateTime barOpenUtc,
                DateTime marketDataUtc,
                long marketRevision,
                double lowerBoundary,
                double upperBoundary,
                double hysteresis,
                TimeSpan minimumPersistence,
                int minimumRefreshes)
            {
                if (!IsFinite(mev))
                {
                    Candidate = null;
                    return false;
                }

                if (!IsInitialized)
                {
                    State = ClassifyTrendState(mev, lowerBoundary, upperBoundary);
                    IsInitialized = true;
                    BarOpenUtc = barOpenUtc;
                    Candidate = null;
                    return true;
                }

                if (BarOpenUtc != barOpenUtc)
                {
                    BarOpenUtc = barOpenUtc;
                    Candidate = null;
                }

                TrendDirectionState? target = GetCandidateTarget(
                    State,
                    Candidate == null ? (TrendDirectionState?)null : Candidate.TargetState,
                    mev,
                    lowerBoundary,
                    upperBoundary,
                    hysteresis);

                if (!target.HasValue)
                {
                    Candidate = null;
                    return false;
                }

                if (Candidate == null || Candidate.TargetState != target.Value)
                {
                    Candidate = new TrendTransitionCandidate
                    {
                        TargetState = target.Value,
                        FirstSeenUtc = nowUtc,
                        LastCountedMarketDataUtc = marketDataUtc,
                        LastCountedRevision = marketRevision,
                        LastCountedMev = mev,
                        QualifyingRefreshCount = 1
                    };
                }
                else if (marketRevision > Candidate.LastCountedRevision &&
                         Math.Abs(mev - Candidate.LastCountedMev) >=
                             V2MevEvidenceEpsilon)
                {
                    Candidate.LastCountedRevision = marketRevision;
                    Candidate.LastCountedMarketDataUtc = marketDataUtc;
                    Candidate.LastCountedMev = mev;
                    Candidate.QualifyingRefreshCount++;
                }

                if (nowUtc - Candidate.FirstSeenUtc < minimumPersistence ||
                    Candidate.QualifyingRefreshCount < minimumRefreshes)
                {
                    return false;
                }

                State = Candidate.TargetState;
                Candidate = null;
                return true;
            }

            private static TrendDirectionState? GetCandidateTarget(
                TrendDirectionState committedState,
                TrendDirectionState? currentTarget,
                double mev,
                double lowerBoundary,
                double upperBoundary,
                double hysteresis)
            {
                switch (committedState)
                {
                    case TrendDirectionState.Neutral:
                        if (currentTarget == TrendDirectionState.Up)
                        {
                            if (mev > upperBoundary - hysteresis)
                                return TrendDirectionState.Up;
                            return mev < lowerBoundary
                                ? TrendDirectionState.Down
                                : null;
                        }
                        if (currentTarget == TrendDirectionState.Down)
                        {
                            if (mev < lowerBoundary + hysteresis)
                                return TrendDirectionState.Down;
                            return mev > upperBoundary
                                ? TrendDirectionState.Up
                                : null;
                        }
                        if (mev > upperBoundary)
                            return TrendDirectionState.Up;
                        if (mev < lowerBoundary)
                            return TrendDirectionState.Down;
                        return null;

                    case TrendDirectionState.Up:
                        if (currentTarget == TrendDirectionState.Down)
                        {
                            if (mev < lowerBoundary + hysteresis)
                                return TrendDirectionState.Down;
                            return mev <= upperBoundary
                                ? TrendDirectionState.Neutral
                                : null;
                        }
                        if (currentTarget == TrendDirectionState.Neutral)
                        {
                            if (mev < lowerBoundary)
                                return TrendDirectionState.Down;
                            return mev <= upperBoundary
                                ? TrendDirectionState.Neutral
                                : null;
                        }
                        if (mev < lowerBoundary)
                            return TrendDirectionState.Down;
                        return mev <= upperBoundary - hysteresis
                            ? TrendDirectionState.Neutral
                            : null;

                    case TrendDirectionState.Down:
                        if (currentTarget == TrendDirectionState.Up)
                        {
                            if (mev > upperBoundary - hysteresis)
                                return TrendDirectionState.Up;
                            return mev >= lowerBoundary
                                ? TrendDirectionState.Neutral
                                : null;
                        }
                        if (currentTarget == TrendDirectionState.Neutral)
                        {
                            if (mev > upperBoundary)
                                return TrendDirectionState.Up;
                            return mev >= lowerBoundary
                                ? TrendDirectionState.Neutral
                                : null;
                        }
                        if (mev > upperBoundary)
                            return TrendDirectionState.Up;
                        return mev >= lowerBoundary + hysteresis
                            ? TrendDirectionState.Neutral
                            : null;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(committedState),
                            committedState,
                            "Unknown committed trend state.");
                }
            }
        }

        private readonly TrendStateTracker _v2HourlyTrend = new TrendStateTracker();
        private readonly TrendStateTracker _v2DailyTrend = new TrendStateTracker();
        private readonly V2ReversalWatch _v2ReversalWatch = new V2ReversalWatch();
        private long _v2MarketDataRevision;
        private long _v2LiveBarEvidenceRevision;
        private DateTime _v2LiveBarEvidenceOpenUtc = DateTime.MinValue;
        private double _v2LastLiveBarHigh = double.NaN;
        private double _v2LastLiveBarLow = double.NaN;
        private CombinedTrendRegime _v2CombinedRegime = CombinedTrendRegime.Range;
        private double _v2HourlyStrength;
        private double _v2DailyStrength;
        private double _v2RegimeConfidence;
        private bool _v2HourlyExtremeUp;
        private bool _v2HourlyExtremeDown;
        private bool _v2DailyExtremeUp;
        private bool _v2DailyExtremeDown;
        private bool _v2ExtremeExtendedRally;
        private bool _v2ExtremeExtendedCrash;

        private static TrendDirectionState ClassifyTrendState(
            double mev,
            double lowerBoundary,
            double upperBoundary)
        {
            if (mev < lowerBoundary)
                return TrendDirectionState.Down;
            if (mev > upperBoundary)
                return TrendDirectionState.Up;
            return TrendDirectionState.Neutral;
        }

        private void UpdateV2HourlyTrend(double mev, DateTime nowUtc)
        {
            if (_hourlyBars == null || _hourlyBars.Count == 0)
                return;

            _v2MarketDataRevision++;
            DateTime barOpenUtc =
                _hourlyBars.OpenTimes[_hourlyBars.Count - 1];
            bool committed = _v2HourlyTrend.Update(
                mev,
                nowUtc,
                barOpenUtc,
                nowUtc,
                _v2MarketDataRevision,
                MevLowerBoundary,
                MevUpperBoundary,
                V2HourlyHysteresis,
                TimeSpan.FromSeconds(V2HourlyPersistenceSeconds),
                V2MinimumQualifyingRefreshes);

            UpdateV2ExtremeFlags(mev, _currentDailyMev);
            RecalculateV2Regime();
            if (committed)
            {
                LogNormal("REGIME", "HOURLY_STATE_COMMITTED",
                    "barOpenUtc={0:O} mev={1:F6} state={2}",
                    barOpenUtc,
                    mev,
                    _v2HourlyTrend.State);
            }
        }

        private void UpdateV2LiveBarEvidenceRevision(
            Bars bars,
            int currentIndex,
            DateTime barOpenUtc)
        {
            double high = bars.HighPrices[currentIndex];
            double low = bars.LowPrices[currentIndex];
            if (!IsFinitePositive(high) || !IsFinitePositive(low))
                return;

            if (_v2LiveBarEvidenceOpenUtc != barOpenUtc)
            {
                _v2LiveBarEvidenceOpenUtc = barOpenUtc;
                _v2LastLiveBarHigh = high;
                _v2LastLiveBarLow = low;
                _v2LiveBarEvidenceRevision++;
                return;
            }

            bool newHigh = !IsFinitePositive(_v2LastLiveBarHigh) ||
                           high > _v2LastLiveBarHigh;
            bool newLow = !IsFinitePositive(_v2LastLiveBarLow) ||
                          low < _v2LastLiveBarLow;
            if (!newHigh && !newLow)
                return;

            if (newHigh)
                _v2LastLiveBarHigh = high;
            if (newLow)
                _v2LastLiveBarLow = low;
            _v2LiveBarEvidenceRevision++;
        }

        private void UpdateV2ReversalWatch(
            DateTime nowUtc,
            DateTime barOpenUtc,
            double sigma21)
        {
            if (_v2ReversalWatch.BarOpenUtc != barOpenUtc)
            {
                bool wasActive = _v2ReversalWatch.IsActive;
                _v2ReversalWatch.BarOpenUtc = barOpenUtc;
                _v2ReversalWatch.IsActive = false;
                _v2ReversalWatch.IsPaused = false;
                _v2ReversalWatch.CandidateFirstSeenUtc = DateTime.MinValue;
                _v2ReversalWatch.InvalidSinceUtc = DateTime.MinValue;
                _v2ReversalWatch.LastCountedEvidenceRevision =
                    _v2LiveBarEvidenceRevision;
                _v2ReversalWatch.QualifyingRefreshCount = 0;
                _v2ReversalWatch.LastNormalizedEvidence = double.NaN;
                if (wasActive)
                {
                    LogNormal("REGIME", "V2_REVERSAL_WATCH_CANCELLED",
                        "barOpenUtc={0:O} reason=newHourlyBar diagnosticOnly=true",
                        barOpenUtc);
                }
            }

            double normalizedEvidence;
            if (!TryCalculateV2ReversalEvidence(
                    sigma21,
                    out normalizedEvidence))
            {
                if (!_v2ReversalWatch.IsPaused)
                {
                    _v2ReversalWatch.IsPaused = true;
                    _v2ReversalWatch.InvalidSinceUtc = nowUtc;
                    LogVerbose("REGIME", "V2_REVERSAL_WATCH_PAUSED",
                        "barOpenUtc={0:O} reason=Sigma21OrReturnEvidenceInvalid graceSeconds={1} diagnosticOnly=true",
                        barOpenUtc,
                        V2ReversalWatchInvalidGraceSeconds);
                }

                if (nowUtc - _v2ReversalWatch.InvalidSinceUtc >=
                    TimeSpan.FromSeconds(V2ReversalWatchInvalidGraceSeconds))
                {
                    bool hadState = _v2ReversalWatch.IsActive ||
                                    _v2ReversalWatch.CandidateFirstSeenUtc !=
                                        DateTime.MinValue;
                    _v2ReversalWatch.IsActive = false;
                    _v2ReversalWatch.CandidateFirstSeenUtc = DateTime.MinValue;
                    _v2ReversalWatch.QualifyingRefreshCount = 0;
                    if (hadState)
                    {
                        LogNormal("REGIME", "V2_REVERSAL_WATCH_CANCELLED",
                            "barOpenUtc={0:O} reason=invalidEvidenceGraceExceeded diagnosticOnly=true",
                            barOpenUtc);
                    }
                }
                return;
            }

            if (_v2ReversalWatch.IsPaused)
            {
                _v2ReversalWatch.IsPaused = false;
                _v2ReversalWatch.InvalidSinceUtc = DateTime.MinValue;
                LogVerbose("REGIME", "V2_REVERSAL_WATCH_RESUMED",
                    "barOpenUtc={0:O} normalizedEvidence={1:F4} diagnosticOnly=true",
                    barOpenUtc,
                    normalizedEvidence);
            }

            _v2ReversalWatch.LastNormalizedEvidence = normalizedEvidence;
            if (_v2ReversalWatch.IsActive)
            {
                if (normalizedEvidence <= V2ReversalWatchClearThreshold)
                {
                    _v2ReversalWatch.IsActive = false;
                    _v2ReversalWatch.CandidateFirstSeenUtc = DateTime.MinValue;
                    _v2ReversalWatch.QualifyingRefreshCount = 0;
                    LogNormal("REGIME", "V2_REVERSAL_WATCH_CLEARED",
                        "barOpenUtc={0:O} normalizedEvidence={1:F4} clearThreshold={2:F2} diagnosticOnly=true",
                        barOpenUtc,
                        normalizedEvidence,
                        V2ReversalWatchClearThreshold);
                }
                return;
            }

            if (normalizedEvidence < V2ReversalWatchActivationThreshold)
            {
                _v2ReversalWatch.CandidateFirstSeenUtc = DateTime.MinValue;
                _v2ReversalWatch.QualifyingRefreshCount = 0;
                return;
            }

            if (_v2ReversalWatch.CandidateFirstSeenUtc == DateTime.MinValue)
            {
                _v2ReversalWatch.CandidateFirstSeenUtc = nowUtc;
                _v2ReversalWatch.QualifyingRefreshCount = 1;
                _v2ReversalWatch.LastCountedEvidenceRevision =
                    _v2LiveBarEvidenceRevision;
            }
            else if (_v2LiveBarEvidenceRevision >
                     _v2ReversalWatch.LastCountedEvidenceRevision)
            {
                _v2ReversalWatch.LastCountedEvidenceRevision =
                    _v2LiveBarEvidenceRevision;
                _v2ReversalWatch.QualifyingRefreshCount++;
            }

            if (_v2ReversalWatch.QualifyingRefreshCount >=
                    V2ReversalWatchMinimumRefreshes &&
                nowUtc - _v2ReversalWatch.CandidateFirstSeenUtc >=
                    TimeSpan.FromSeconds(V2ReversalWatchActivationSeconds))
            {
                _v2ReversalWatch.IsActive = true;
                LogNormal("REGIME", "V2_REVERSAL_WATCH_ACTIVATED",
                    "barOpenUtc={0:O} normalizedEvidence={1:F4} refreshes={2} elapsedSeconds={3:F1} diagnosticOnly=true",
                    barOpenUtc,
                    normalizedEvidence,
                    _v2ReversalWatch.QualifyingRefreshCount,
                    (nowUtc - _v2ReversalWatch.CandidateFirstSeenUtc)
                        .TotalSeconds);
            }
        }

        private bool TryCalculateV2ReversalEvidence(
            double sigma21,
            out double normalizedEvidence)
        {
            normalizedEvidence = double.NaN;
            if (_hourlyBars == null ||
                _hourlyBars.Count < 3 ||
                !IsFinite(sigma21) ||
                sigma21 <= 0.0 ||
                (_v2HourlyTrend.State != TrendDirectionState.Up &&
                 _v2HourlyTrend.State != TrendDirectionState.Down))
            {
                return false;
            }

            int currentIndex = _hourlyBars.Count - 1;
            double currentClose = _hourlyBars.ClosePrices[currentIndex];
            double previousClose = _hourlyBars.ClosePrices[currentIndex - 1];
            double priorClose = _hourlyBars.ClosePrices[currentIndex - 2];
            if (!IsFinitePositive(currentClose) ||
                !IsFinitePositive(previousClose) ||
                !IsFinitePositive(priorClose))
            {
                return false;
            }

            double currentReturn =
                (currentClose - previousClose) / previousClose * 100.0;
            double previousReturn =
                (previousClose - priorClose) / priorClose * 100.0;
            double deceleration = _v2HourlyTrend.State == TrendDirectionState.Up
                ? (previousReturn - currentReturn) / sigma21
                : (currentReturn - previousReturn) / sigma21;
            if (!IsFinite(deceleration))
                return false;

            normalizedEvidence = Clamp01(0.5 + deceleration / 2.0);
            return IsFinite(normalizedEvidence);
        }

        private void UpdateV2DailyTrend(double mev, DateTime nowUtc)
        {
            if (_dailyBars == null || _dailyBars.Count == 0)
                return;

            DateTime barOpenUtc =
                _dailyBars.OpenTimes[_dailyBars.Count - 1];
            bool committed = _v2DailyTrend.Update(
                mev,
                nowUtc,
                barOpenUtc,
                nowUtc,
                _v2MarketDataRevision,
                MevLowerBoundary,
                MevUpperBoundary,
                V2DailyHysteresis,
                TimeSpan.FromSeconds(V2DailyPersistenceSeconds),
                V2MinimumQualifyingRefreshes);

            UpdateV2ExtremeFlags(_currentHourlyMev, mev);
            RecalculateV2Regime();
            if (committed)
            {
                LogNormal("REGIME", "DAILY_STATE_COMMITTED",
                    "barOpenUtc={0:O} mev={1:F6} state={2}",
                    barOpenUtc,
                    mev,
                    _v2DailyTrend.State);
            }
        }

        private void UpdateV2ExtremeFlags(double hourlyMev, double dailyMev)
        {
            UpdateExtremeFlag(
                hourlyMev,
                MevExtremeThreshold,
                V2ExtremeHourlyClearHysteresis,
                ref _v2HourlyExtremeUp,
                ref _v2HourlyExtremeDown);
            UpdateExtremeFlag(
                dailyMev,
                MevExtremeThreshold,
                V2ExtremeDailyClearHysteresis,
                ref _v2DailyExtremeUp,
                ref _v2DailyExtremeDown);
        }

        private static void UpdateExtremeFlag(
            double mev,
            double extremeThreshold,
            double clearHysteresis,
            ref bool extremeUp,
            ref bool extremeDown)
        {
            if (!IsFinite(mev))
                return;

            if (!extremeUp && mev > extremeThreshold)
                extremeUp = true;
            else if (extremeUp && mev <= extremeThreshold - clearHysteresis)
                extremeUp = false;

            if (!extremeDown && mev < -extremeThreshold)
                extremeDown = true;
            else if (extremeDown && mev >= -extremeThreshold + clearHysteresis)
                extremeDown = false;
        }

        private void RecalculateV2Regime()
        {
            if (!_hasValidHourlyMev ||
                !_hasValidDailyMev ||
                !_v2HourlyTrend.IsInitialized ||
                !_v2DailyTrend.IsInitialized)
            {
                _v2CombinedRegime = CombinedTrendRegime.Range;
                _v2RegimeConfidence = 0.0;
                _currentPolicySignalEligible = false;
                _currentPolicySignalReason = "V2 hourly or daily state is unavailable";
                return;
            }

            _v2CombinedRegime = MapCombinedTrendRegime(
                _v2DailyTrend.State,
                _v2HourlyTrend.State);
            _v2HourlyStrength = CalculateCommittedStrength(
                _v2HourlyTrend.State,
                _currentHourlyMev,
                MevLowerBoundary,
                MevUpperBoundary,
                MevExtremeThreshold);
            _v2DailyStrength = CalculateCommittedStrength(
                _v2DailyTrend.State,
                _currentDailyMev,
                MevLowerBoundary,
                MevUpperBoundary,
                MevExtremeThreshold);
            _v2RegimeConfidence = CalculateRegimeConfidence(
                _v2CombinedRegime,
                _v2HourlyStrength,
                _v2DailyStrength);
            _v2ExtremeExtendedRally =
                _v2CombinedRegime == CombinedTrendRegime.ConfirmedUptrend &&
                (_v2HourlyExtremeUp || _v2DailyExtremeUp);
            _v2ExtremeExtendedCrash =
                _v2CombinedRegime == CombinedTrendRegime.ConfirmedDowntrend &&
                (_v2HourlyExtremeDown || _v2DailyExtremeDown);
            _currentPolicySignalEligible =
                _v2CombinedRegime != CombinedTrendRegime.Range;
            _currentPolicySignalReason = _currentPolicySignalEligible
                ? "V2 committed regime is actionable"
                : "V2 combined regime is Range";
            _currentHourlyMevSignal = HourlyMevSignal.None;
            _currentSonScore = double.NaN;
            _currentDaughterScore = double.NaN;
            _currentWeightedScore = double.NaN;
            ResetCurrentSonCurveDiagnostics();
            _currentDailyMevContext = _v2DailyTrend.State == TrendDirectionState.Up
                ? DailyMevContext.MidPositive
                : _v2DailyTrend.State == TrendDirectionState.Down
                    ? DailyMevContext.MidNegative
                    : DailyMevContext.Neutral;
        }

        private static CombinedTrendRegime MapCombinedTrendRegime(
            TrendDirectionState daily,
            TrendDirectionState hourly)
        {
            if (daily == TrendDirectionState.Up)
            {
                if (hourly == TrendDirectionState.Up)
                    return CombinedTrendRegime.ConfirmedUptrend;
                if (hourly == TrendDirectionState.Neutral)
                    return CombinedTrendRegime.UptrendPause;
                return CombinedTrendRegime.BullishPullback;
            }

            if (daily == TrendDirectionState.Down)
            {
                if (hourly == TrendDirectionState.Down)
                    return CombinedTrendRegime.ConfirmedDowntrend;
                if (hourly == TrendDirectionState.Neutral)
                    return CombinedTrendRegime.DowntrendPause;
                return CombinedTrendRegime.BearishRally;
            }

            if (hourly == TrendDirectionState.Up)
                return CombinedTrendRegime.EmergingUptrend;
            if (hourly == TrendDirectionState.Down)
                return CombinedTrendRegime.EmergingDowntrend;
            return CombinedTrendRegime.Range;
        }

        private static double CalculateCommittedStrength(
            TrendDirectionState state,
            double mev,
            double lowerBoundary,
            double upperBoundary,
            double extremeThreshold)
        {
            if (!IsFinite(mev) || state == TrendDirectionState.Neutral)
                return 0.0;

            if (state == TrendDirectionState.Up)
            {
                double span = Math.Max(0.10, extremeThreshold - upperBoundary);
                return Clamp01((mev - upperBoundary) / span);
            }
            double negativeSpan = Math.Max(
                0.10,
                extremeThreshold - Math.Abs(lowerBoundary));
            return Clamp01(
                (Math.Abs(mev) - Math.Abs(lowerBoundary)) / negativeSpan);
        }

        private static double CalculateRegimeConfidence(
            CombinedTrendRegime regime,
            double hourlyStrength,
            double dailyStrength)
        {
            double confidence;
            switch (regime)
            {
                case CombinedTrendRegime.ConfirmedUptrend:
                case CombinedTrendRegime.ConfirmedDowntrend:
                    confidence = V2HourlyStrengthWeight * hourlyStrength +
                                 V2DailyStrengthWeight * dailyStrength;
                    break;
                case CombinedTrendRegime.EmergingUptrend:
                case CombinedTrendRegime.EmergingDowntrend:
                    confidence = V2HourlyStrengthWeight * hourlyStrength;
                    break;
                case CombinedTrendRegime.UptrendPause:
                case CombinedTrendRegime.DowntrendPause:
                    confidence = V2DailyStrengthWeight * dailyStrength;
                    break;
                case CombinedTrendRegime.BullishPullback:
                case CombinedTrendRegime.BearishRally:
                    confidence = V2DailyStrengthWeight * dailyStrength -
                                 0.30 * hourlyStrength;
                    break;
                case CombinedTrendRegime.Range:
                    confidence = 0.0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(regime));
            }

            return Clamp01(confidence);
        }

        private void GetV2BossProbabilities(
            out double longProbability,
            out double shortProbability,
            out double holdProbability)
        {
            if (!IsFinite(_v2RegimeConfidence) ||
                _v2RegimeConfidence < 0.0 ||
                _v2RegimeConfidence > 1.0)
            {
                throw new InvalidOperationException(
                    "V2 regime confidence must be finite and normalized.");
            }

            holdProbability =
                V2BossMaximumHoldProbability * (1.0 - _v2RegimeConfidence);
            if (IsUptrendFamily(_v2CombinedRegime))
            {
                shortProbability =
                    V2BossMaximumContrarySellProbability *
                    (1.0 - _v2RegimeConfidence);
                longProbability = 1.0 - holdProbability - shortProbability;
            }
            else if (IsDowntrendFamily(_v2CombinedRegime))
            {
                longProbability =
                    V2BossMaximumContraryBuyProbability *
                    (1.0 - _v2RegimeConfidence);
                shortProbability = 1.0 - holdProbability - longProbability;
            }
            else if (_v2CombinedRegime == CombinedTrendRegime.Range)
            {
                longProbability = 0.0;
                shortProbability = 0.0;
                holdProbability = 1.0;
            }
            else
            {
                throw new InvalidOperationException(
                    "Boss probabilities are not defined for the Range regime.");
            }
        }

        private static bool IsUptrendFamily(CombinedTrendRegime regime)
        {
            return regime == CombinedTrendRegime.ConfirmedUptrend ||
                   regime == CombinedTrendRegime.EmergingUptrend ||
                   regime == CombinedTrendRegime.UptrendPause ||
                   regime == CombinedTrendRegime.BullishPullback;
        }

        private static bool IsDowntrendFamily(CombinedTrendRegime regime)
        {
            return regime == CombinedTrendRegime.ConfirmedDowntrend ||
                   regime == CombinedTrendRegime.EmergingDowntrend ||
                   regime == CombinedTrendRegime.DowntrendPause ||
                   regime == CombinedTrendRegime.BearishRally;
        }

        private static V2BossOutcome SelectV2BossOutcome(
            double draw,
            double longProbability,
            double shortProbability,
            double holdProbability)
        {
            if (!IsFinite(draw) || draw < 0.0 || draw >= 1.0 ||
                !IsProbability(longProbability) ||
                !IsProbability(shortProbability) ||
                !IsProbability(holdProbability) ||
                Math.Abs(longProbability + shortProbability + holdProbability - 1.0) >
                    1e-12)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(draw),
                    "V2 Boss probabilities and draw must be valid.");
            }

            if (draw < holdProbability)
                return V2BossOutcome.Hold;
            return draw < holdProbability + longProbability
                ? V2BossOutcome.Long
                : V2BossOutcome.Short;
        }

        private bool EvaluateV2PolicyEntrySignal(TickSnapshot tick)
        {
            if (!_hasValidHourlyMev || !_hasValidDailyMev ||
                !_v2HourlyTrend.IsInitialized || !_v2DailyTrend.IsInitialized)
            {
                RecordEntrySkip("v2MevStateUnavailable");
                return false;
            }

            if (_currentHourlyMevBarOpenTime == _lastPolicyDecisionBarOpenTime ||
                _currentHourlyMevBarOpenTime == _lastConsumedPolicyBarOpenTime ||
                tick.UtcNow < _policySamplingCooldownUntilUtc)
            {
                return false;
            }

            double longProbability;
            double shortProbability;
            double holdProbability;
            GetV2BossProbabilities(
                out longProbability,
                out shortProbability,
                out holdProbability);

            byte[] drawKey = DeriveV2DrawKey(PolicyRandomSeed);
            string decisionKey;
            if (!TryReserveV2Decision(
                    _currentHourlyMevBarOpenTime,
                    drawKey,
                    longProbability,
                    shortProbability,
                    holdProbability,
                    out decisionKey))
            {
                ReconcileExistingV2Decision(
                    decisionKey,
                    _currentHourlyMevBarOpenTime);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.RuntimeFault,
                    "v2DecisionReservationFailed");
                return false;
            }

            var reservationJournal = new V2DecisionJournalEntry
            {
                DecisionKey = decisionKey,
                Phase = "Reserved",
                BossOutcome = "notDrawn",
                Status = "Reserved",
                Reason = "drawKeyAndConfigurationDurablyReserved"
            };
            if (!PersistV2DecisionJournal(reservationJournal))
            {
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.RuntimeFault,
                    "v2InitialJournalFailed");
                return false;
            }

            _lastPolicyDecisionBarOpenTime = _currentHourlyMevBarOpenTime;
            double bossDraw = DeriveV2UnitDraw(
                drawKey,
                "BOSS",
                V2DecisionModelId,
                Symbol.Name,
                _currentHourlyMevBarOpenTime);
            V2BossOutcome bossOutcome = SelectV2BossOutcome(
                bossDraw,
                longProbability,
                shortProbability,
                holdProbability);

            var bossJournal = new V2DecisionJournalEntry
            {
                DecisionKey = decisionKey,
                Phase = "BossCompleted",
                BossDraw = bossDraw,
                BossOutcome = bossOutcome.ToString(),
                Status = bossOutcome.ToString(),
                Reason = "bossOutcomePersisted"
            };
            if (!PersistV2DecisionJournal(bossJournal))
            {
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.RuntimeFault,
                    "v2BossJournalFailed");
                return false;
            }

            if (bossOutcome == V2BossOutcome.Hold)
            {
                bossJournal.Phase = "Terminal";
                bossJournal.Status = PolicyDecisionStatus.Hold.ToString();
                bossJournal.Reason = "v2BossHold";
                if (!PersistV2DecisionJournal(bossJournal))
                {
                    ConsumePolicyBar(
                        _currentHourlyMevBarOpenTime,
                        PolicyDecisionStatus.RuntimeFault,
                        "v2HoldJournalFailed");
                    return false;
                }

                LogNormal("POLICY", "V2_BOSS_HOLD",
                    "model={0} barOpenUtc={1:O} regime={2} confidence={3:F4} hourlyState={4} dailyState={5} longProbability={6:F4} shortProbability={7:F4} holdProbability={8:F4} bossDraw={9:F12} outcome=Hold",
                    V2DecisionModelId,
                    _currentHourlyMevBarOpenTime,
                    _v2CombinedRegime,
                    _v2RegimeConfidence,
                    _v2HourlyTrend.State,
                    _v2DailyTrend.State,
                    longProbability,
                    shortProbability,
                    holdProbability,
                    bossDraw);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.Hold,
                    "v2BossHold");
                return false;
            }

            TradeType tradeType = bossOutcome == V2BossOutcome.Long
                ? TradeType.Buy
                : TradeType.Sell;
            bool uptrendFamily = IsUptrendFamily(_v2CombinedRegime);
            bool aligned = uptrendFamily
                ? tradeType == TradeType.Buy
                : tradeType == TradeType.Sell;
            StrategyKind strategy = aligned
                ? StrategyKind.TrendFollowing
                : StrategyKind.MeanReversion;

            bool wifeConsulted =
                (_v2ExtremeExtendedRally && tradeType == TradeType.Sell) ||
                (_v2ExtremeExtendedCrash && tradeType == TradeType.Buy);
            double wifeDraw = double.NaN;
            double wifeStopProbability = double.NaN;
            V2OrderKind orderKind = V2OrderKind.Limit;
            if (wifeConsulted)
            {
                wifeStopProbability = ExtremeCountertrendStopProbability;
                wifeDraw = DeriveV2UnitDraw(
                    drawKey,
                    "WIFE_EXTREME_COUNTERTREND",
                    V2DecisionModelId,
                    Symbol.Name,
                    _currentHourlyMevBarOpenTime);
                if (wifeDraw < wifeStopProbability)
                    orderKind = V2OrderKind.Stop;
            }

            double limitDistancePercent = GetV2LimitDistancePercent(
                strategy,
                tradeType);
            double entryDistancePercent = limitDistancePercent *
                tick.VolatilityFactor *
                (orderKind == V2OrderKind.Stop ? StopOrderMultiplier : 1.0);
            double referencePrice = GetMovementReferencePrice(strategy, tradeType);
            double targetPrice = NormalizePrice(CalculateV2PendingTarget(
                tradeType,
                orderKind,
                tick.Bid,
                tick.Ask,
                entryDistancePercent));

            double movePercent;
            double stopLossPercent;
            double takeProfitPercent;
            string label;
            string description;
            GetV2PolicyStrategyParameters(
                strategy,
                tradeType,
                out movePercent,
                out stopLossPercent,
                out takeProfitPercent,
                out label,
                out description);
            double effectiveStopLossPercent;
            double effectiveTakeProfitPercent;
            AdjustV2ProtectionPercentages(
                orderKind,
                StopOrderMultiplier,
                stopLossPercent,
                takeProfitPercent,
                out effectiveStopLossPercent,
                out effectiveTakeProfitPercent);
            double volume = ApplyHourlyVolatilityToVolume(
                GetAllocationAdjustedVolumeInUnits(tradeType),
                tick.VolatilityFactor);

            DateTime nowUtc = tick.UtcNow;
            var decision = new PendingPolicyDecision
            {
                DecisionKey = decisionKey,
                BarOpenTimeUtc = _currentHourlyMevBarOpenTime,
                Direction = tradeType,
                Strategy = strategy,
                OrderKind = orderKind,
                FrozenStopOrderMultiplier = StopOrderMultiplier,
                EffectiveLimitDistancePercent =
                    limitDistancePercent * tick.VolatilityFactor,
                EffectiveEntryDistancePercent = entryDistancePercent,
                VolatilityFactor = tick.VolatilityFactor,
                TargetPrice = targetPrice,
                MovementReferencePrice = referencePrice,
                MovePercent = movePercent,
                StopLossPercent = effectiveStopLossPercent,
                TakeProfitPercent = effectiveTakeProfitPercent,
                VolumeInUnits = volume,
                Label = label,
                Description = description,
                CreatedUtc = nowUtc,
                NextAttemptUtc = nowUtc,
                ExpiresUtc = nowUtc.AddMinutes(PolicyDecisionLifetimeMinutes),
                SetupName = _v2CombinedRegime.ToString(),
                BossOutcome = bossOutcome.ToString(),
                BossRandomDraw = bossDraw,
                WifeStopProbability = wifeStopProbability,
                SubmissionCorrelationId = Guid.ParseExact(decisionKey, "N"),
                DirectionProbability = tradeType == TradeType.Buy
                    ? longProbability
                    : shortProbability,
                WifeTrendFollowingProbability = wifeConsulted
                    ? 1.0 - wifeStopProbability
                    : (aligned ? 1.0 : 0.0),
                WifeRandomDraw = wifeDraw,
                StrategyProbability = aligned
                    ? 1.0
                    : wifeConsulted
                        ? 1.0 - wifeStopProbability
                        : 0.0,
                SonScore = double.NaN,
                DaughterScore = double.NaN,
                Status = PolicyDecisionStatus.Ready
            };
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "OutcomeSelected",
                        "bossAndWifeOutcomesAndEntryGeometryPersisted")))
            {
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.RuntimeFault,
                    "v2OutcomeJournalFailed");
                return false;
            }

            bool allocationAvailable = IsSideAllocated(tradeType);
            bool movementGatePassed =
                HasRequiredPriceMove(tradeType, strategy, tick);
            if (!allocationAvailable || !movementGatePassed)
            {
                string failureCode = !allocationAvailable
                    ? "directionalAllocationUnavailable"
                    : "movementGateFailed";
                LogNormal("POLICY", "V2_DECISION_INELIGIBLE",
                    "barOpenUtc={0:O} regime={1} bossOutcome={2} wifeConsulted={3} wifeDraw={4:F12} wifeStopProbability={5:F4} direction={6} strategy={7} orderKind={8} failure={9}",
                    _currentHourlyMevBarOpenTime,
                    _v2CombinedRegime,
                    bossOutcome,
                    wifeConsulted,
                    wifeDraw,
                    wifeStopProbability,
                    tradeType,
                    strategy,
                    orderKind,
                    failureCode);
                _pendingPolicyDecision = decision;
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.NoTradeIneligible,
                    failureCode);
                return false;
            }

            _pendingPolicyDecision = decision;
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "ReadyToPlace",
                        "eligibilitySnapshotPassed")))
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.RuntimeFault,
                    "v2ReadyJournalFailed");
                return false;
            }

            LogNormal("POLICY", "V2_SIGNAL_SAMPLED",
                "model={0} barOpenUtc={1:O} regime={2} confidence={3:F4} hourlyStrength={4:F4} dailyStrength={5:F4} longProbability={6:F4} shortProbability={7:F4} holdProbability={8:F4} bossDraw={9:F12} bossOutcome={10} wifeConsulted={11} wifeStopProbability={12:F4} wifeDraw={13:F12} wifeOutcome={14} direction={15} strategy={16} orderKind={17} dLimitPercent={18:F6} dEntryPercent={19:F6} stopMultiplier={20:F2} effectiveSlPercent={21:F6} effectiveTpPercent={22:F6} target={23:R}",
                V2DecisionModelId,
                _currentHourlyMevBarOpenTime,
                _v2CombinedRegime,
                _v2RegimeConfidence,
                _v2HourlyStrength,
                _v2DailyStrength,
                longProbability,
                shortProbability,
                holdProbability,
                bossDraw,
                bossOutcome,
                wifeConsulted,
                wifeStopProbability,
                wifeDraw,
                orderKind == V2OrderKind.Stop
                    ? ExtremeCountertrendStyle.StopConfirmation
                    : ExtremeCountertrendStyle.MeanReversionLimit,
                tradeType,
                strategy,
                orderKind,
                _pendingPolicyDecision.EffectiveLimitDistancePercent,
                _pendingPolicyDecision.EffectiveEntryDistancePercent,
                _pendingPolicyDecision.FrozenStopOrderMultiplier,
                _pendingPolicyDecision.StopLossPercent,
                _pendingPolicyDecision.TakeProfitPercent,
                _pendingPolicyDecision.TargetPrice);

            return TryProcessPendingPolicyDecision(tick);
        }

        private void GetV2PolicyStrategyParameters(
            StrategyKind strategy,
            TradeType tradeType,
            out double movePercent,
            out double stopLossPercent,
            out double takeProfitPercent,
            out string label,
            out string description)
        {
            if (strategy == StrategyKind.MeanReversion && tradeType == TradeType.Buy)
            {
                movePercent = _actualMrDownMovePercent;
                stopLossPercent = _actualMrBuyStopLossPercent;
                takeProfitPercent = _actualMrBuyTakeProfitPercent;
                label = _mrLongLabel;
                description = "Policy MR long";
                return;
            }

            if (strategy == StrategyKind.MeanReversion)
            {
                movePercent = _actualMrUpMovePercent;
                stopLossPercent = _actualMrSellStopLossPercent;
                takeProfitPercent = _actualMrSellTakeProfitPercent;
                label = _mrShortLabel;
                description = "Policy MR short";
                return;
            }

            if (tradeType == TradeType.Buy)
            {
                movePercent = _actualTfLongMovePercent;
                stopLossPercent = _actualTfLongStopLossPercent;
                takeProfitPercent = _actualTfLongTakeProfitPercent;
                label = _tfLongLabel;
                description = "Policy TF long";
                return;
            }

            movePercent = _actualTfShortMovePercent;
            stopLossPercent = _actualTfShortStopLossPercent;
            takeProfitPercent = _actualTfShortTakeProfitPercent;
            label = _tfShortLabel;
            description = "Policy TF short";
        }

        private double GetV2LimitDistancePercent(
            StrategyKind strategy,
            TradeType tradeType)
        {
            if (strategy == StrategyKind.MeanReversion)
                return tradeType == TradeType.Buy
                    ? _actualMrBuyLimitDistancePercent
                    : _actualMrSellLimitDistancePercent;
            return tradeType == TradeType.Buy
                ? _actualTfBuyLimitDistancePercent
                : _actualTfSellLimitDistancePercent;
        }

        private static double CalculateV2PendingTarget(
            TradeType tradeType,
            V2OrderKind orderKind,
            double bid,
            double ask,
            double distancePercent)
        {
            if (!IsFinitePositive(bid) ||
                !IsFinitePositive(ask) ||
                !IsFinite(distancePercent) ||
                distancePercent <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(distancePercent),
                    "V2 pending-order prices and distance must be finite and positive.");
            }

            double reference = tradeType == TradeType.Buy ? ask : bid;
            bool addDistance =
                (tradeType == TradeType.Buy && orderKind == V2OrderKind.Stop) ||
                (tradeType == TradeType.Sell && orderKind == V2OrderKind.Limit);
            double target = addDistance
                ? reference * (1.0 + distancePercent / 100.0)
                : reference * (1.0 - distancePercent / 100.0);
            if (!IsFinitePositive(target))
                throw new InvalidOperationException("V2 pending-order target is invalid.");
            return target;
        }

        private static void AdjustV2ProtectionPercentages(
            V2OrderKind orderKind,
            double stopOrderMultiplier,
            double configuredStopLossPercent,
            double configuredTakeProfitPercent,
            out double effectiveStopLossPercent,
            out double effectiveTakeProfitPercent)
        {
            if (!IsFinitePositive(configuredStopLossPercent) ||
                !IsFinitePositive(configuredTakeProfitPercent))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(configuredStopLossPercent),
                    "Configured stop-loss and take-profit distances must be finite and positive.");
            }

            effectiveStopLossPercent = configuredStopLossPercent;
            effectiveTakeProfitPercent = configuredTakeProfitPercent;
            if (orderKind == V2OrderKind.Stop)
            {
                if (!IsFinitePositive(stopOrderMultiplier))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(stopOrderMultiplier),
                        "Stop order multiplier must be finite and positive.");
                }

                effectiveStopLossPercent /= stopOrderMultiplier;
                effectiveTakeProfitPercent *= stopOrderMultiplier;
            }

            if (!IsFinitePositive(effectiveStopLossPercent) ||
                !IsFinitePositive(effectiveTakeProfitPercent))
            {
                throw new InvalidOperationException(
                    "Adjusted stop-loss and take-profit distances are invalid.");
            }
        }

        private static bool RunV2ModelSelfTests()
        {
            if (MapCombinedTrendRegime(
                    TrendDirectionState.Up,
                    TrendDirectionState.Up) !=
                    CombinedTrendRegime.ConfirmedUptrend ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Up,
                    TrendDirectionState.Neutral) !=
                    CombinedTrendRegime.UptrendPause ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Up,
                    TrendDirectionState.Down) !=
                    CombinedTrendRegime.BullishPullback ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Neutral,
                    TrendDirectionState.Up) !=
                    CombinedTrendRegime.EmergingUptrend ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Neutral,
                    TrendDirectionState.Neutral) !=
                    CombinedTrendRegime.Range ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Neutral,
                    TrendDirectionState.Down) !=
                    CombinedTrendRegime.EmergingDowntrend ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Down,
                    TrendDirectionState.Up) !=
                    CombinedTrendRegime.BearishRally ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Down,
                    TrendDirectionState.Neutral) !=
                    CombinedTrendRegime.DowntrendPause ||
                MapCombinedTrendRegime(
                    TrendDirectionState.Down,
                    TrendDirectionState.Down) !=
                    CombinedTrendRegime.ConfirmedDowntrend)
            {
                return false;
            }

            double upHold = 0.20;
            double upShort = 0.07;
            double upLong = 1.0 - upHold - upShort;
            double downLong = 0.12;
            double downHold = 0.20;
            double downShort = 1.0 - downLong - downHold;
            if (SelectV2BossOutcome(0.20, upLong, upShort, upHold) !=
                    V2BossOutcome.Long ||
                SelectV2BossOutcome(0.93, upLong, upShort, upHold) !=
                    V2BossOutcome.Short ||
                SelectV2BossOutcome(0.12, downShort, downLong, downHold) !=
                    V2BossOutcome.Hold ||
                SelectV2BossOutcome(0.90, downLong, downShort, downHold) !=
                    V2BossOutcome.Short ||
                Math.Abs(CalculateCommittedStrength(
                    TrendDirectionState.Up, 0.625, -0.35, 0.35, 0.90) - 0.5) >
                    1e-12 ||
                Math.Abs(CalculateCommittedStrength(
                    TrendDirectionState.Down, -0.625, -0.35, 0.35, 0.90) - 0.5) >
                    1e-12 ||
                CalculateCommittedStrength(
                    TrendDirectionState.Neutral, 0.8, -0.35, 0.35, 0.90) != 0.0 ||
                Math.Abs(CalculateRegimeConfidence(
                    CombinedTrendRegime.ConfirmedUptrend, 0.5, 0.5) - 0.5) >
                    1e-12 ||
                CalculateRegimeConfidence(
                    CombinedTrendRegime.BullishPullback, 0.8, 0.2) != 0.0)
            {
                return false;
            }

            DateTime deterministicBar =
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            byte[] deterministicKey = DeriveV2DrawKey(17);
            double repeatedBossDraw = DeriveV2UnitDraw(
                deterministicKey,
                "BOSS",
                V2DecisionModelId,
                "TEST",
                deterministicBar);
            double stopLossPercent;
            double takeProfitPercent;
            AdjustV2ProtectionPercentages(
                V2OrderKind.Stop,
                1.5,
                2.0,
                1.1,
                out stopLossPercent,
                out takeProfitPercent);
            double limitStopLossPercent;
            double limitTakeProfitPercent;
            AdjustV2ProtectionPercentages(
                V2OrderKind.Limit,
                1.5,
                2.0,
                1.1,
                out limitStopLossPercent,
                out limitTakeProfitPercent);
            if (Math.Abs(CalculateV2PendingTarget(
                    TradeType.Buy, V2OrderKind.Limit, 100.0, 100.0, 1.0) - 99.0) > 1e-12 ||
                Math.Abs(CalculateV2PendingTarget(
                    TradeType.Sell, V2OrderKind.Limit, 100.0, 100.0, 1.0) - 101.0) > 1e-12 ||
                Math.Abs(CalculateV2PendingTarget(
                    TradeType.Buy, V2OrderKind.Stop, 100.0, 100.0, 1.0) - 101.0) > 1e-12 ||
                Math.Abs(CalculateV2PendingTarget(
                    TradeType.Sell, V2OrderKind.Stop, 100.0, 100.0, 1.0) - 99.0) > 1e-12 ||
                DeriveV2UnitDraw(
                    deterministicKey,
                    "BOSS",
                    V2DecisionModelId,
                    "TEST",
                    deterministicBar) != repeatedBossDraw ||
                DeriveV2UnitDraw(
                    deterministicKey,
                    "WIFE",
                    V2DecisionModelId,
                    "TEST",
                    deterministicBar) == repeatedBossDraw ||
                Math.Abs(stopLossPercent - (2.0 / 1.5)) > 1e-12 ||
                Math.Abs(takeProfitPercent - (1.1 * 1.5)) > 1e-12 ||
                limitStopLossPercent != 2.0 ||
                limitTakeProfitPercent != 1.1)
            {
                return false;
            }

            var tracker = new TrendStateTracker();
            DateTime barOpen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime start = barOpen.AddMinutes(1);
            tracker.Update(0.0, start.AddSeconds(-1), barOpen,
                start.AddSeconds(-1), 0, -0.35, 0.35,
                V2HourlyHysteresis, TimeSpan.FromSeconds(30), 2);
            tracker.Update(0.36, start, barOpen, start, 1, -0.35, 0.35,
                V2HourlyHysteresis, TimeSpan.FromSeconds(30), 2);
            bool candidateStarted = tracker.State == TrendDirectionState.Neutral &&
                                    tracker.Candidate != null;
            tracker.Update(0.37, start.AddSeconds(15), barOpen,
                start.AddSeconds(15), 2, -0.35, 0.35,
                V2HourlyHysteresis, TimeSpan.FromSeconds(30), 2);
            bool notCommittedBeforePersistence =
                tracker.State == TrendDirectionState.Neutral;
            tracker.Update(0.38, start.AddSeconds(31), barOpen,
                start.AddSeconds(31), 3, -0.35, 0.35,
                V2HourlyHysteresis, TimeSpan.FromSeconds(30), 2);

            return candidateStarted &&
                   notCommittedBeforePersistence &&
                   tracker.State == TrendDirectionState.Up &&
                   tracker.Candidate == null;
        }

        private bool ValidateV2RuntimeEnvironment()
        {
            if (_effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                RunningMode != RunningMode.RealTime)
            {
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                {
                    LogNormal("CONFIGURATION", "V2_NONLIVE_MODE",
                        "decisionModel={0} durableRestartGuarantee=false runningMode={1}",
                        V2DecisionModelId,
                        RunningMode);
                }
                return true;
            }

            if (Application.EnvironmentType == EnvironmentType.Cloud)
            {
                LogError("STARTUP", "V2_DURABLE_STORAGE_REQUIRED",
                    "MEV State V2 live trading is disabled in cTrader Cloud because LocalStorage is not durable across runs.");
                return false;
            }

            if (Application.EnvironmentType != EnvironmentType.Desktop &&
                Application.EnvironmentType != EnvironmentType.CLI)
            {
                LogError("STARTUP", "V2_DURABLE_STORAGE_REQUIRED",
                    "MEV State V2 live trading requires Desktop or CLI with reloadable LocalStorage; environment={0}",
                    Application.EnvironmentType);
                return false;
            }

            return RunV2StorageSelfTest();
        }

        private bool RunV2StorageSelfTest()
        {
            string id = Guid.NewGuid().ToString("N");
            string keyA = "SSV2-ST-" + id + "-A";
            string keyB = "SSV2-ST-" + id + "-B";
            string valueA = "V2ST|" + id + "|A";
            string valueB = "V2ST|" + id + "|B";

            try
            {
                LocalStorage.SetString(keyA, valueA, LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
                LocalStorage.Reload(LocalStorageScope.Instance);
                if (!string.Equals(
                        LocalStorage.GetString(keyA, LocalStorageScope.Instance),
                        valueA,
                        StringComparison.Ordinal))
                {
                    LogError("STARTUP", "V2_STORAGE_SELF_TEST_FAILED",
                        "step=readBackA environment={0}",
                        Application.EnvironmentType);
                    return false;
                }

                LocalStorage.SetString(keyB, valueB, LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
                LocalStorage.Reload(LocalStorageScope.Instance);
                if (!string.Equals(
                        LocalStorage.GetString(keyA, LocalStorageScope.Instance),
                        valueA,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        LocalStorage.GetString(keyB, LocalStorageScope.Instance),
                        valueB,
                        StringComparison.Ordinal))
                {
                    LogError("STARTUP", "V2_STORAGE_SELF_TEST_FAILED",
                        "step=readBackBoth environment={0}",
                        Application.EnvironmentType);
                    return false;
                }

                LocalStorage.Remove(keyA, LocalStorageScope.Instance);
                LocalStorage.Remove(keyB, LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
                LocalStorage.Reload(LocalStorageScope.Instance);
                if (LocalStorage.GetString(keyA, LocalStorageScope.Instance) != null ||
                    LocalStorage.GetString(keyB, LocalStorageScope.Instance) != null)
                {
                    LogError("STARTUP", "V2_STORAGE_SELF_TEST_FAILED",
                        "step=verifyCleanup environment={0}",
                        Application.EnvironmentType);
                    return false;
                }

                LogNormal("STARTUP", "V2_STORAGE_SELF_TEST_PASSED",
                    "environment={0} scope={1} flushReloadReadBack=true",
                    Application.EnvironmentType,
                    LocalStorageScope.Instance);
                return true;
            }
            catch (Exception exception)
            {
                LogError("STARTUP", "V2_STORAGE_SELF_TEST_FAILED",
                    "environment={0} exceptionType={1} message={2}",
                    Application.EnvironmentType,
                    exception.GetType().Name,
                    exception.Message);
                return false;
            }
            finally
            {
                try
                {
                    LocalStorage.Remove(keyA, LocalStorageScope.Instance);
                    LocalStorage.Remove(keyB, LocalStorageScope.Instance);
                    LocalStorage.Flush(LocalStorageScope.Instance);
                }
                catch (Exception exception)
                {
                    LogError("STARTUP", "V2_STORAGE_SELF_TEST_CLEANUP_FAILED",
                        "exceptionType={0} message={1}",
                        exception.GetType().Name,
                        exception.Message);
                }
            }
        }

        private static byte[] DeriveV2DrawKey(int seed)
        {
            byte[] seedBytes = new byte[4];
            seedBytes[0] = (byte)((seed >> 24) & 0xff);
            seedBytes[1] = (byte)((seed >> 16) & 0xff);
            seedBytes[2] = (byte)((seed >> 8) & 0xff);
            seedBytes[3] = (byte)(seed & 0xff);

            byte[] applicationKey = SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    "ShootingStar|MEV_STATE_BOSS_WIFE_V2|PolicyDrawKey|AKDV1"));
            byte[] context = new byte[10];
            context[0] = (byte)'S';
            context[1] = (byte)'S';
            context[2] = (byte)'V';
            context[3] = (byte)'2';
            context[4] = 1;
            context[5] = 1;
            Buffer.BlockCopy(seedBytes, 0, context, 6, seedBytes.Length);

            using (var hmac = new HMACSHA256(applicationKey))
                return hmac.ComputeHash(context);
        }

        private static double DeriveV2UnitDraw(
            byte[] drawKey,
            string role,
            string decisionModelId,
            string symbolName,
            DateTime hourlyBarOpenUtc)
        {
            if (drawKey == null || drawKey.Length != 32)
                throw new ArgumentException("V2 draw key must contain 32 bytes.", nameof(drawKey));
            if (hourlyBarOpenUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Hourly bar open time must be UTC.", nameof(hourlyBarOpenUtc));

            string message = string.Concat(
                role, "|", decisionModelId, "|", symbolName, "|",
                hourlyBarOpenUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            byte[] payload = Encoding.UTF8.GetBytes(message);
            using (var hmac = new HMACSHA256(drawKey))
            {
                byte[] digest = hmac.ComputeHash(payload);
                ulong value =
                    ((ulong)digest[0] << 56) |
                    ((ulong)digest[1] << 48) |
                    ((ulong)digest[2] << 40) |
                    ((ulong)digest[3] << 32) |
                    ((ulong)digest[4] << 24) |
                    ((ulong)digest[5] << 16) |
                    ((ulong)digest[6] << 8) |
                    digest[7];
                return (value >> 11) * (1.0 / 9007199254740992.0);
            }
        }
    }
}
