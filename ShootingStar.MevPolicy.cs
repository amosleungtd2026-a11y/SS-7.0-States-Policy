using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // MEV signals, policy scoring, and hourly strategy selection.
    public partial class ShootingStar
    {
        #region MEV Policy and Hourly Strategy Selection


        private int RequiredHourlyMevHistoryBars
        {
            get
            {
                return Math.Max(MomentumEfficiencyLength, HourlyStandardDeviationLength) + 1;
            }
        }

        private int RequiredDailyMevHistoryBars
        {
            get { return MomentumEfficiencyLength + 1; }
        }

        private DateTime _lastProcessedHourlyMevBarOpenTime = DateTime.MinValue;
        private DateTime _lastProcessedDailyMevBarOpenTime = DateTime.MinValue;

        private void UpdateHourlyMevSignal(bool force)
        {
            if (_hourlyBars == null || _hourlyBars.Count < 2)
            {
                InvalidateHourlyMevSignal("hourly bar data is unavailable");
                return;
            }

            int currentIndex = _hourlyBars.Count - 1;
            DateTime currentOpenTime = _hourlyBars.OpenTimes[currentIndex];
            DateTime nowUtc = Server.TimeInUtc;
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
            {
                UpdateV2LiveBarEvidenceRevision(
                    _hourlyBars,
                    currentIndex,
                    currentOpenTime);
            }
            if (!force &&
                currentOpenTime == _lastProcessedHourlyMevBarOpenTime &&
                nowUtc - _lastHourlyMevRefreshUtc < RealtimeEvaluationInterval)
            {
                return;
            }
            _lastProcessedHourlyMevBarOpenTime = currentOpenTime;
            _lastHourlyMevRefreshUtc = nowUtc;
            DateTime previousMevBarOpenTime = _currentHourlyMevBarOpenTime;

            double hourlyMev;
            double sigma21;
            if (_hourlyBars.Count < RequiredHourlyMevHistoryBars + 1 ||
                !TryCalculateMomentumEfficiencyValueAt(
                    _hourlyBars,
                    currentIndex,
                    MomentumEfficiencyLength,
                    out hourlyMev) ||
                !TryCalculateReturnStandardDeviationForBars(
                    _hourlyBars,
                    currentIndex,
                    HourlyStandardDeviationLength,
                    out sigma21))
            {
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                {
                    UpdateV2ReversalWatch(
                        nowUtc,
                        currentOpenTime,
                        double.NaN);
                }
                InvalidateHourlyMevSignal("insufficient or invalid live hourly MEV history");
                return;
            }

            double previousHourlyMev;
            if (currentIndex - 1 >= MomentumEfficiencyLength &&
                TryCalculateMomentumEfficiencyValueAt(
                    _hourlyBars,
                    currentIndex - 1,
                    MomentumEfficiencyLength,
                    out previousHourlyMev))
            {
                _previousHourlyMev = previousHourlyMev;
            }
            else
            {
                _previousHourlyMev = double.NaN;
            }

            _currentHourlyMev = hourlyMev;
            _currentHourlyMevSigma21 = sigma21;
            _currentHourlyMevBarOpenTime = currentOpenTime;
            _hasValidHourlyMev = true;

            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
            {
                UpdateV2HourlyTrend(hourlyMev, nowUtc);
                UpdateV2ReversalWatch(
                    nowUtc,
                    currentOpenTime,
                    sigma21);
            }
            else
            {
                _currentSonScore = double.NaN;
                ResetCurrentSonCurveDiagnostics();
                _currentPolicySignalEligible = false;
                _currentPolicySignalReason = "non-probabilistic mode";
            }

            _hourlyMevReady =
                _effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                (_hasValidHourlyMev && _hasValidDailyMev);
            ClearWarning("hourly-mev", "REGIME", "AVAILABLE",
                "live hourly MEV and Sigma21 are available");
            if (previousMevBarOpenTime != currentOpenTime)
            {
                LogNormal("REGIME", "HOURLY_MEV_SIGNAL",
                    "schemaVersion=2 liveBar={0:yyyy-MM-dd HH:mm} mev={1:F4} sigma21={2:F6} state={3} dailyContext={4} eligible={5} sonScore={6:F4} sonCurve={7} sonCurveU={8:F4} sonCurveFloor={9:F4} sonCurveCeiling={10:F4} sonCurveExponent={11:F3} rawSonMagnitude={12:F4} reversalEvidence={13:F4} normalizedReversalEvidence={14:F4} reversalConfirmation={15:F4} reason={16}",
                    currentOpenTime,
                    _currentHourlyMev,
                    _currentHourlyMevSigma21,
                    _currentHourlyMevSignal,
                    _currentDailyMevContext,
                    _currentPolicySignalEligible,
                    _currentSonScore,
                    _currentSonCurveName,
                    _currentSonCurveStrength,
                    _currentSonCurveFloor,
                    _currentSonCurveCeiling,
                    _currentSonCurveExponent,
                    _currentSonRawMagnitude,
                    _currentReversalEvidence,
                    _currentNormalizedReversalEvidence,
                    _currentReversalConfirmation,
                    _currentPolicySignalReason);
            }
        }

        private void UpdateDailyMevSignal(bool force)
        {
            if (_dailyBars == null || _dailyBars.Count < 2)
            {
                _hasValidDailyMev = false;
                _currentDailyMev = double.NaN;
                _currentDailyMevContext = DailyMevContext.Unavailable;
                _v2DailyTrend.Candidate = null;
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                    RecalculateV2Regime();
                else
                    UpdatePolicySignalScores();
                return;
            }

            int currentIndex = _dailyBars.Count - 1;
            DateTime currentOpenTime = _dailyBars.OpenTimes[currentIndex];
            DateTime nowUtc = Server.TimeInUtc;
            if (!force &&
                currentOpenTime == _lastProcessedDailyMevBarOpenTime &&
                nowUtc - _lastDailyMevRefreshUtc < RealtimeEvaluationInterval)
            {
                return;
            }
            _lastProcessedDailyMevBarOpenTime = currentOpenTime;
            _lastDailyMevRefreshUtc = nowUtc;
            DailyMevContext previousContext = _currentDailyMevContext;
            bool previousValid = _hasValidDailyMev;

            double dailyMev;
            if (_dailyBars.Count < RequiredDailyMevHistoryBars + 1 ||
                !TryCalculateMomentumEfficiencyValueAt(
                    _dailyBars,
                    currentIndex,
                    MomentumEfficiencyLength,
                    out dailyMev))
            {
                _hasValidDailyMev = false;
                _currentDailyMev = double.NaN;
                _currentDailyMevContext = DailyMevContext.Unavailable;
                _v2DailyTrend.Candidate = null;
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                    RecalculateV2Regime();
                else
                    UpdatePolicySignalScores();
                return;
            }

            _currentDailyMev = dailyMev;
            _hasValidDailyMev = true;
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                UpdateV2DailyTrend(dailyMev, nowUtc);
            else
                UpdateCommittedDailyContext(dailyMev, nowUtc);
            if (!previousValid || previousContext != _currentDailyMevContext)
            {
                LogNormal("REGIME", "DAILY_MEV_CONTEXT",
                    "liveBar={0:yyyy-MM-dd} mev={1:F4} context={2}",
                    currentOpenTime,
                    _currentDailyMev,
                    _currentDailyMevContext);
            }
        }

        private bool TryCalculateReturnStandardDeviationForBars(
            Bars bars,
            int barIndex,
            int returnLength,
            out double standardDeviation)
        {
            standardDeviation = double.NaN;
            int firstReturnIndex = barIndex - returnLength + 1;
            if (bars == null ||
                firstReturnIndex < 1 ||
                barIndex >= bars.Count)
            {
                return false;
            }

            var returns = new double[returnLength];
            double mean = 0.0;
            for (int offset = 0; offset < returnLength; offset++)
            {
                int index = firstReturnIndex + offset;
                double previousClose = bars.ClosePrices[index - 1];
                double close = bars.ClosePrices[index];
                if (!IsFinitePositive(previousClose) || !IsFinitePositive(close))
                    return false;

                double barReturn = (close - previousClose) / previousClose * 100.0;
                if (!IsFinite(barReturn))
                    return false;
                returns[offset] = barReturn;
                mean += barReturn;
            }

            mean /= returnLength;
            double squaredDeviationSum = 0.0;
            for (int offset = 0; offset < returns.Length; offset++)
            {
                double deviation = returns[offset] - mean;
                squaredDeviationSum += deviation * deviation;
            }

            standardDeviation = Math.Sqrt(squaredDeviationSum / returnLength);
            return IsFinite(standardDeviation);
        }

        private const double DailyNegativeContextThreshold = -0.25;
        private const double MinimumHourlyReturnSigmaEpsilonPercent = 1e-6;

        private DailyMevContext ClassifyDailyMev(double dailyMev)
        {
            if (dailyMev < -MevExtremeThreshold)
                return DailyMevContext.DeepNegative;
            if (dailyMev < DailyNegativeContextThreshold)
                return DailyMevContext.MidNegative;
            if (dailyMev <= MevUpperBoundary)
                return DailyMevContext.Neutral;
            if (dailyMev <= MevExtremeThreshold)
                return DailyMevContext.MidPositive;
            return DailyMevContext.HighPositive;
        }

        private static double Clamp01(double value)
        {
            if (value <= 0.0) return 0.0;
            if (value >= 1.0) return 1.0;
            return value;
        }

        private void ResetCurrentSonCurveDiagnostics()
        {
            _currentSonCurveName = "none";
            _currentSonCurveStrength = double.NaN;
            _currentSonCurveFloor = double.NaN;
            _currentSonCurveCeiling = double.NaN;
            _currentSonCurveExponent = double.NaN;
            _currentSonRawMagnitude = double.NaN;
            _currentReversalEvidence = double.NaN;
            _currentNormalizedReversalEvidence = double.NaN;
            _currentReversalConfirmation = double.NaN;
        }

        private static double NormalizeSonCurveStrength(
            double value,
            double threshold,
            double span)
        {
            if (!IsFinite(value) ||
                !IsFinite(threshold) ||
                !IsFinite(span) ||
                span <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(span),
                    "Son curve inputs and span must be finite, and span must be positive.");
            }

            return Clamp01((value - threshold) / span);
        }

        private static double CalculateSonCurveMagnitude(
            double normalizedStrength,
            double floor,
            double ceiling,
            double exponent)
        {
            if (!IsFinite(normalizedStrength) ||
                normalizedStrength < 0.0 ||
                normalizedStrength > 1.0 ||
                !IsFinite(floor) ||
                floor < 0.0 ||
                floor > 1.0 ||
                !IsFinite(ceiling) ||
                ceiling < floor ||
                ceiling > 1.0 ||
                !IsFinite(exponent) ||
                exponent <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(normalizedStrength),
                    "Son curve values must be finite, normalized, and ordered.");
            }

            return floor +
                (ceiling - floor) * Math.Pow(normalizedStrength, exponent);
        }

        private double CalculateReversalConfirmation(
            bool bullishReversal)
        {
            if (_hourlyBars == null ||
                _hourlyBars.Count < 3 ||
                !IsFinite(_currentHourlyMevSigma21) ||
                _currentHourlyMevSigma21 < 0.0)
            {
                throw new InvalidOperationException(
                    "Valid hourly bars and Sigma21 are required for reversal scoring.");
            }

            int currentIndex = _hourlyBars.Count - 1;
            double currentClose = _hourlyBars.ClosePrices[currentIndex];
            double previousClose = _hourlyBars.ClosePrices[currentIndex - 1];
            double priorClose = _hourlyBars.ClosePrices[currentIndex - 2];
            if (!IsFinitePositive(currentClose) ||
                !IsFinitePositive(previousClose) ||
                !IsFinitePositive(priorClose))
            {
                throw new InvalidOperationException(
                    "Positive finite hourly closes are required for reversal scoring.");
            }

            double currentReturnPercent =
                (currentClose - previousClose) / previousClose * 100.0;
            double previousReturnPercent =
                (previousClose - priorClose) / priorClose * 100.0;
            double sigmaPercent = Math.Max(
                MinimumHourlyReturnSigmaEpsilonPercent,
                _currentHourlyMevSigma21);
            if (!IsFinite(currentReturnPercent) ||
                !IsFinite(previousReturnPercent) ||
                !IsFinite(sigmaPercent) ||
                sigmaPercent <= 0.0)
            {
                throw new InvalidOperationException(
                    "Finite percentage returns and positive Sigma21 are required for reversal scoring.");
            }

            _currentReversalEvidence = bullishReversal
                ? (currentReturnPercent - previousReturnPercent) /
                  sigmaPercent
                : (previousReturnPercent - currentReturnPercent) /
                  sigmaPercent;
            _currentReversalConfirmation = CalculateReversalConfirmation(
                _currentReversalEvidence,
                out _currentNormalizedReversalEvidence);
            return _currentReversalConfirmation;
        }

        private static double CalculateReversalConfirmation(
            double evidence,
            out double normalizedEvidence)
        {
            if (!IsFinite(evidence))
                throw new ArgumentOutOfRangeException(nameof(evidence));

            normalizedEvidence = Clamp01(0.50 + evidence / 2.00);
            return 0.35 + 0.65 * normalizedEvidence;
        }

        private static bool RunSonCurveSelfTests()
        {
            double normalizedEvidence;
            double negativeConfirmation = CalculateReversalConfirmation(
                -1.0,
                out normalizedEvidence);
            bool negativeEvidenceClamped =
                normalizedEvidence == 0.0 &&
                negativeConfirmation == 0.35;

            double neutralConfirmation = CalculateReversalConfirmation(
                0.0,
                out normalizedEvidence);
            bool neutralEvidenceCentered =
                normalizedEvidence == 0.5 &&
                Math.Abs(neutralConfirmation - 0.675) <= 1e-12;

            double positiveConfirmation = CalculateReversalConfirmation(
                1.0,
                out normalizedEvidence);
            bool positiveEvidenceClamped =
                normalizedEvidence == 1.0 &&
                positiveConfirmation == 1.0;

            return NormalizeSonCurveStrength(0.35, 0.35, 0.55) == 0.0 &&
                NormalizeSonCurveStrength(0.90, 0.35, 0.55) == 1.0 &&
                Math.Abs(
                    NormalizeSonCurveStrength(0.625, 0.35, 0.55) - 0.5) <=
                    1e-12 &&
                CalculateSonCurveMagnitude(0.0, 0.15, 0.85, 1.40) == 0.15 &&
                Math.Abs(
                    CalculateSonCurveMagnitude(1.0, 0.15, 0.85, 1.40) -
                    0.85) <= 1e-12 &&
                Math.Abs(
                    CalculateSonCurveMagnitude(0.5, 0.15, 0.85, 1.40) -
                    (0.15 + 0.70 * Math.Pow(0.5, 1.40))) <= 1e-12 &&
                CalculateSonCurveMagnitude(0.0, 0.20, 0.85, 1.50) == 0.20 &&
                Math.Abs(
                    CalculateSonCurveMagnitude(1.0, 0.20, 0.85, 1.50) -
                    0.85) <= 1e-12 &&
                CalculateSonCurveMagnitude(0.0, 0.10, 0.80, 1.30) == 0.10 &&
                Math.Abs(
                    CalculateSonCurveMagnitude(1.0, 0.10, 0.80, 1.30) -
                    0.80) <= 1e-12 &&
                CalculateSonCurveMagnitude(0.0, 0.10, 0.75, 1.25) == 0.10 &&
                Math.Abs(
                    CalculateSonCurveMagnitude(1.0, 0.10, 0.75, 1.25) -
                    0.75) <= 1e-12 &&
                negativeEvidenceClamped &&
                neutralEvidenceCentered &&
                positiveEvidenceClamped;
        }

        private void CalculateContinuousPolicyScores(
            HourlyMevSignal signal,
            out double sonScore,
            out double daughterScore)
        {
            ResetCurrentSonCurveDiagnostics();
            sonScore = 0.0;
            daughterScore = 0.0;

            if (signal == HourlyMevSignal.PanicReversalLong)
            {
                double span = Math.Max(
                    0.10,
                    MevExtremeThreshold - Math.Abs(MevLowerBoundary));
                _currentSonCurveName = "panicReversal";
                _currentSonCurveStrength = NormalizeSonCurveStrength(
                    Math.Abs(_currentHourlyMev),
                    Math.Abs(MevLowerBoundary),
                    span);
                _currentSonCurveFloor = SonPanicFloor;
                _currentSonCurveCeiling = SonPanicCeiling;
                _currentSonCurveExponent = SonPanicExponent;
                _currentSonRawMagnitude = CalculateSonCurveMagnitude(
                    _currentSonCurveStrength,
                    _currentSonCurveFloor,
                    _currentSonCurveCeiling,
                    _currentSonCurveExponent);
                sonScore = _currentSonRawMagnitude *
                    CalculateReversalConfirmation(true);

                if (_currentDailyMevContext == DailyMevContext.DeepNegative)
                {
                    double dailyDepth = Math.Abs(_currentDailyMev) - Math.Abs(MevLowerBoundary);
                    double bell = dailyDepth <= 1.25
                        ? 0.25 + 0.20 * (dailyDepth / 1.25)
                        : Math.Max(0.15, 0.45 - 0.15 * ((dailyDepth - 1.25) / 1.50));
                    daughterScore = bell;
                }
                else
                {
                    daughterScore = 0.10;
                }
            }
            else if (signal == HourlyMevSignal.ConfirmedBreakoutLong)
            {
                double span = Math.Max(0.10, MevExtremeThreshold - MevUpperBoundary);
                _currentSonCurveName = "breakout";
                _currentSonCurveStrength = NormalizeSonCurveStrength(
                    _currentHourlyMev,
                    MevUpperBoundary,
                    span);
                _currentSonCurveFloor = SonBreakoutFloor;
                _currentSonCurveCeiling = SonBreakoutCeiling;
                _currentSonCurveExponent = SonBreakoutExponent;
                _currentSonRawMagnitude = CalculateSonCurveMagnitude(
                    _currentSonCurveStrength,
                    _currentSonCurveFloor,
                    _currentSonCurveCeiling,
                    _currentSonCurveExponent);
                sonScore = _currentSonRawMagnitude;

                double dailyU = Clamp01((_currentDailyMev - MevLowerBoundary) / 2.00);
                daughterScore = 0.10 + 0.40 * dailyU;
            }
            else if (signal == HourlyMevSignal.ConfirmedBreakdownShort)
            {
                double span = Math.Max(
                    0.10,
                    MevExtremeThreshold - Math.Abs(MevLowerBoundary));
                _currentSonCurveName = "breakdown";
                _currentSonCurveStrength = NormalizeSonCurveStrength(
                    Math.Abs(_currentHourlyMev),
                    Math.Abs(MevLowerBoundary),
                    span);
                _currentSonCurveFloor = SonBreakdownFloor;
                _currentSonCurveCeiling = SonBreakdownCeiling;
                _currentSonCurveExponent = SonBreakdownExponent;
                _currentSonRawMagnitude = CalculateSonCurveMagnitude(
                    _currentSonCurveStrength,
                    _currentSonCurveFloor,
                    _currentSonCurveCeiling,
                    _currentSonCurveExponent);
                sonScore = -_currentSonRawMagnitude;

                double dailySpan = Math.Max(
                    0.10,
                    MevExtremeThreshold -
                    Math.Abs(DailyNegativeContextThreshold));
                double dailyU = Clamp01(
                    (Math.Abs(_currentDailyMev) -
                     Math.Abs(DailyNegativeContextThreshold)) /
                    dailySpan);
                daughterScore = -(0.15 + 0.30 * dailyU);
            }
            else if (signal == HourlyMevSignal.ExtremePositiveReversalShort)
            {
                double span = Math.Max(0.10, MevExtremeThreshold);
                _currentSonCurveName = "extremeReversal";
                _currentSonCurveStrength = NormalizeSonCurveStrength(
                    _currentHourlyMev,
                    MevExtremeThreshold,
                    span);
                _currentSonCurveFloor = SonExtremeFloor;
                _currentSonCurveCeiling = SonExtremeCeiling;
                _currentSonCurveExponent = SonExtremeExponent;
                _currentSonRawMagnitude = CalculateSonCurveMagnitude(
                    _currentSonCurveStrength,
                    _currentSonCurveFloor,
                    _currentSonCurveCeiling,
                    _currentSonCurveExponent);
                sonScore = -_currentSonRawMagnitude *
                    CalculateReversalConfirmation(false);

                double dailyDepth = Math.Abs(_currentDailyMev) - Math.Abs(MevLowerBoundary);
                double dailyU = Clamp01(dailyDepth / 2.00);
                daughterScore = -(0.15 + 0.35 * dailyU);
            }
        }

        private bool HasHourlyReversalExhaustion(bool bullishReversal, out bool confirmedPriceFlip)
        {
            confirmedPriceFlip = false;
            if (_hourlyBars == null || _hourlyBars.Count < 3 || !IsFinite(_currentHourlyMev))
                return false;

            int currentIndex = _hourlyBars.Count - 1;
            double close = _hourlyBars.ClosePrices[currentIndex];
            double prevClose = _hourlyBars.ClosePrices[currentIndex - 1];
            double high = _hourlyBars.HighPrices[currentIndex];
            double low = _hourlyBars.LowPrices[currentIndex];
            if (!IsFinitePositive(close) || !IsFinitePositive(prevClose) ||
                !IsFinitePositive(high) || !IsFinitePositive(low))
            {
                return false;
            }

            double lastReturn = (close - prevClose) / prevClose;
            double range = high - low;
            double clv = range > 0.0 ? (close - low) / range : 0.5;

            if (bullishReversal)
            {
                bool mevHookedUp = IsFinite(_previousHourlyMev) && _currentHourlyMev > _previousHourlyMev;
                bool bullishBar = lastReturn > 0.0 && clv >= 0.50;
                confirmedPriceFlip = mevHookedUp && lastReturn > 0.0;
                return mevHookedUp || bullishBar;
            }

            bool mevHookedDown = IsFinite(_previousHourlyMev) && _currentHourlyMev < _previousHourlyMev;
            bool bearishBar = lastReturn < 0.0;
            confirmedPriceFlip = mevHookedDown && bearishBar;
            return mevHookedDown || bearishBar;
        }

        private bool IsDailyCandidateValid(
            DailyMevContext committed,
            DailyMevContext candidate,
            double mev)
        {
            double h = DaughterDailyHysteresis;
            switch (candidate)
            {
                case DailyMevContext.DeepNegative:
                    return mev < -MevExtremeThreshold;
                case DailyMevContext.MidNegative:
                    if (committed == DailyMevContext.DeepNegative)
                        return mev >= -MevExtremeThreshold + h;
                    return mev < DailyNegativeContextThreshold &&
                           mev >= -MevExtremeThreshold;
                case DailyMevContext.Neutral:
                    if (committed == DailyMevContext.MidNegative)
                        return mev >= DailyNegativeContextThreshold + h;
                    if (committed == DailyMevContext.MidPositive)
                        return mev <= MevUpperBoundary - h;
                    return mev >= DailyNegativeContextThreshold &&
                           mev <= MevUpperBoundary;
                case DailyMevContext.MidPositive:
                    if (committed == DailyMevContext.Neutral)
                        return mev > MevUpperBoundary;
                    if (committed == DailyMevContext.HighPositive)
                        return mev <= MevExtremeThreshold - h;
                    return mev > MevUpperBoundary &&
                           mev <= MevExtremeThreshold;
                case DailyMevContext.HighPositive:
                    return mev > MevExtremeThreshold;
                default:
                    return false;
            }
        }

        private void UpdateCommittedDailyContext(double dailyMev, DateTime nowUtc)
        {
            if (!IsFinite(dailyMev))
            {
                _currentDailyMevContext = DailyMevContext.Unavailable;
                _daughterCandidateContext = DailyMevContext.Unavailable;
                _daughterCandidateFirstSeenUtc = DateTime.MinValue;
                return;
            }

            DailyMevContext measured = ClassifyDailyMev(dailyMev);
            if (measured == _currentDailyMevContext)
            {
                _daughterCandidateContext = DailyMevContext.Unavailable;
                _daughterCandidateFirstSeenUtc = DateTime.MinValue;
                return;
            }

            if (_daughterCandidateContext != measured)
            {
                _daughterCandidateContext = measured;
                _daughterCandidateFirstSeenUtc = nowUtc;
                RecordDaughterContextCandidate(
                    _currentDailyMevContext,
                    measured,
                    dailyMev,
                    nowUtc);
            }

            if (!IsDailyCandidateValid(
                    _currentDailyMevContext,
                    measured,
                    dailyMev))
            {
                _daughterCandidateContext = DailyMevContext.Unavailable;
                _daughterCandidateFirstSeenUtc = DateTime.MinValue;
                return;
            }

            TimeSpan age = nowUtc - _daughterCandidateFirstSeenUtc;
            if (age >= TimeSpan.FromMinutes(DaughterContextPersistenceMinutes))
            {
                DailyMevContext previous = _currentDailyMevContext;
                _currentDailyMevContext = measured;
                _daughterCandidateContext = DailyMevContext.Unavailable;
                _daughterCandidateFirstSeenUtc = DateTime.MinValue;
                LogNormal("DAUGHTER", "CONTEXT_COMMITTED",
                    "previousContext={0} committedContext={1} dailyMev={2:F4} ageSeconds={3:F1}",
                    previous,
                    measured,
                    dailyMev,
                    age.TotalSeconds);
            }
        }

        private void UpdatePolicySignalScores()
        {
            _currentSonScore = double.NaN;
            _currentDaughterScore = double.NaN;
            _currentWeightedScore = double.NaN;
            _currentHourlyMevSignal = HourlyMevSignal.None;
            ResetCurrentSonCurveDiagnostics();
            _currentPolicySignalEligible = false;
            _currentPolicySignalReason = "hourly or daily MEV unavailable";
            _activeLongEntryStrategy = StrategyKind.MeanReversion;
            _activeShortEntryStrategy = StrategyKind.MeanReversion;

            if (!_hasValidHourlyMev || !_hasValidDailyMev)
                return;

            _currentSonScore = 0.0;
            _currentDaughterScore = 0.0;
            _currentWeightedScore = 0.0;
            _currentPolicySignalReason = "hourly MEV is inside the no-entry band";

            if (_currentHourlyMev <= MevLowerBoundary)
            {
                if (_currentDailyMevContext == DailyMevContext.MidNegative)
                {
                    _currentHourlyMevSignal = HourlyMevSignal.ConfirmedBreakdownShort;
                    _activeLongEntryStrategy = StrategyKind.TrendFollowing;
                    _activeShortEntryStrategy = StrategyKind.TrendFollowing;
                    _currentPolicySignalReason = "negative hourly MEV confirmed as trend-following breakdown short";
                    CalculateContinuousPolicyScores(
                        _currentHourlyMevSignal,
                        out _currentSonScore,
                        out _currentDaughterScore);
                }
                else if (_currentDailyMevContext == DailyMevContext.DeepNegative ||
                         _currentDailyMevContext == DailyMevContext.Neutral)
                {
                    if (_currentDailyMevContext != DailyMevContext.DeepNegative &&
                        _currentDailyMevContext != DailyMevContext.Neutral)
                    {
                        _currentPolicySignalReason = "daily MEV context conflicts with panic-reversal long";
                        return;
                    }

                    bool confirmedFlip;
                    if (!HasHourlyReversalExhaustion(true, out confirmedFlip))
                    {
                        _currentPolicySignalReason = "panic hourly MEV is still accelerating downward; awaiting exhaustion";
                        return;
                    }

                    _currentHourlyMevSignal = HourlyMevSignal.PanicReversalLong;
                    _currentPolicySignalReason = confirmedFlip
                        ? "exhausted panic hourly MEV confirmed by upward hook and bullish bar"
                        : "panic hourly MEV deceleration detected; scaled reversal long";
                    CalculateContinuousPolicyScores(_currentHourlyMevSignal, out _currentSonScore, out _currentDaughterScore);
                }
                else
                {
                    _currentPolicySignalReason = "positive daily MEV context conflicts with negative hourly MEV entry";
                    return;
                }
            }
            else if (_currentHourlyMev > MevUpperBoundary &&
                     _currentHourlyMev <= MevExtremeThreshold)
            {
                if (_currentDailyMevContext == DailyMevContext.DeepNegative ||
                    _currentDailyMevContext == DailyMevContext.MidNegative)
                {
                    _currentPolicySignalReason = "negative daily MEV conflicts with the hourly breakout";
                    return;
                }

                _currentHourlyMevSignal = HourlyMevSignal.ConfirmedBreakoutLong;
                _activeLongEntryStrategy = StrategyKind.TrendFollowing;
                _activeShortEntryStrategy = StrategyKind.TrendFollowing;
                _currentPolicySignalReason = "positive hourly MEV confirmed by Sigma21 and daily context";
                CalculateContinuousPolicyScores(_currentHourlyMevSignal, out _currentSonScore, out _currentDaughterScore);
            }
            else if (_currentHourlyMev > MevExtremeThreshold)
            {
                if (_currentDailyMevContext != DailyMevContext.MidNegative &&
                    _currentDailyMevContext != DailyMevContext.DeepNegative)
                {
                    _currentPolicySignalReason = "extreme positive hourly MEV is not confirmed by negative daily MEV";
                    return;
                }

                bool confirmedFlip;
                if (!HasHourlyReversalExhaustion(false, out confirmedFlip))
                {
                    _currentPolicySignalReason = "extreme positive hourly MEV is still accelerating upward; awaiting exhaustion";
                    return;
                }

                _currentHourlyMevSignal = HourlyMevSignal.ExtremePositiveReversalShort;
                _currentPolicySignalReason = confirmedFlip
                    ? "exhausted extreme positive hourly MEV confirmed by downward hook and red bar"
                    : "extreme positive hourly MEV deceleration detected; scaled reversal short";
                CalculateContinuousPolicyScores(_currentHourlyMevSignal, out _currentSonScore, out _currentDaughterScore);
            }

            _currentWeightedScore = Math.Max(
                -1.0,
                Math.Min(
                    1.0,
                    0.70 * _currentSonScore + 0.30 * _currentDaughterScore));
            _currentPolicySignalEligible =
                _currentHourlyMevSignal != HourlyMevSignal.None;
        }

        private void InvalidateHourlyMevSignal(string reason)
        {
            _hasValidHourlyMev = false;
            _currentHourlyMev = double.NaN;
            _previousHourlyMev = double.NaN;
            _currentHourlyMevSigma21 = double.NaN;
            _currentHourlyMevBarOpenTime = DateTime.MinValue;
            _currentHourlyMevSignal = HourlyMevSignal.None;
            ResetCurrentSonCurveDiagnostics();
            _currentPolicySignalEligible = false;
            _currentPolicySignalReason = reason;
            _currentSonScore = double.NaN;
            _currentDaughterScore = double.NaN;
            _currentWeightedScore = double.NaN;
            _hourlyMevReady = false;
            _v2HourlyTrend.Candidate = null;
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                RecalculateV2Regime();
            LogActiveWarning("hourly-mev", "REGIME", "UNAVAILABLE",
                "{0}; new entries are disabled until hourly MEV is available", reason);
        }


        private bool TryCalculateMomentumEfficiencyValueAt(
            Bars bars,
            int barIndex,
            int momentumEfficiencyLength,
            out double mev)
        {
            mev = double.NaN;
            if (barIndex < momentumEfficiencyLength || barIndex >= bars.Count)
                return false;

            int firstPriceIndex = barIndex - momentumEfficiencyLength;
            double change =
                bars.ClosePrices[barIndex] -
                bars.ClosePrices[firstPriceIndex];
            double sumChanges = 0.0;
            for (int index = firstPriceIndex + 1; index <= barIndex; index++)
            {
                sumChanges += Math.Abs(
                    bars.ClosePrices[index] -
                    bars.ClosePrices[index - 1]);
            }

            double previousClose = bars.ClosePrices[firstPriceIndex];
            if (previousClose == 0.0)
                return false;

            double efficiencyRatio = sumChanges == 0.0
                ? 0.0
                : change / sumChanges;
            mev = Math.Abs(efficiencyRatio) *
                  change /
                  previousClose *
                  100.0;
            return IsFinite(mev);
        }


        private bool IsStrategyAllowedByHourlyRegime(StrategyKind strategy, TradeType tradeType)
        {
            if (_effectiveTradingMode != TradingMode.ProbabilisticAuto)
            {
                if (!_hasValidHourlyMev)
                    return false;
                ManualSideStrategy selectedStrategy = GetManualSideStrategy(tradeType);
                return strategy == StrategyKind.MeanReversion
                    ? selectedStrategy == ManualSideStrategy.MeanReversion
                    : selectedStrategy == ManualSideStrategy.TrendFollowing;
            }

            return _hasValidHourlyMev;
        }

        private StrategyKind GetActiveAutoStrategy(TradeType tradeType)
        {
            return tradeType == TradeType.Buy
                ? _activeLongEntryStrategy
                : _activeShortEntryStrategy;
        }

        private string GetStrategySelectionDisplay()
        {
            if (_effectiveTradingMode != TradingMode.ProbabilisticAuto)
            {
                return _hasValidHourlyMev
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Hourly MEV {0:F2} ({1}): Long={2} Short={3}",
                        _currentHourlyMev,
                        GetCurrentManualRankBand(),
                        GetManualSideStrategy(TradeType.Buy),
                        GetManualSideStrategy(TradeType.Sell))
                    : "Unavailable";
            }
            if (!_hasValidHourlyMev)
                return "Unavailable";

            return string.Format(
                CultureInfo.InvariantCulture,
                "H={0:F2} D={1:F2} {2}",
                _currentHourlyMev,
                _hasValidDailyMev ? _currentDailyMev : double.NaN,
                _currentHourlyMevSignal);
        }

        private ManualSideStrategy GetManualSideStrategy(TradeType tradeType)
        {
            return GetManualSideStrategy(tradeType, GetCurrentManualRankBand());
        }

        private ManualSideStrategy GetManualSideStrategy(
            TradeType tradeType,
            ManualRankBand rankBand)
        {
            int bandIndex = (int)rankBand;
            return tradeType == TradeType.Buy
                ? _effectiveManualLongStrategies[bandIndex]
                : _effectiveManualShortStrategies[bandIndex];
        }

        private ManualRankBand GetCurrentManualRankBand()
        {
            if (_currentHourlyMev <= MevLowerBoundary)
                return ManualRankBand.Lower;
            if (_currentHourlyMev <= MevUpperBoundary)
                return ManualRankBand.Middle;
            return ManualRankBand.Upper;
        }
        #endregion


    }
}
