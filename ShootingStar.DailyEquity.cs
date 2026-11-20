using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Daily equity tracking and reporting.
    public partial class ShootingStar
    {
        #region Daily Equity Tracking
        private const int DailyEquityBoundaryHourUtc = 22;
        private readonly DailyEquityTrackingState _dailyEquity = new DailyEquityTrackingState();

        private static DateTime GetDailyEquityPeriodStartUtc(DateTime utcNow)
        {
            DateTime normalizedUtc = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            DateTime boundary = new DateTime(
                normalizedUtc.Year,
                normalizedUtc.Month,
                normalizedUtc.Day,
                DailyEquityBoundaryHourUtc,
                0,
                0,
                DateTimeKind.Utc);
            return normalizedUtc >= boundary ? boundary : boundary.AddDays(-1.0);
        }

        private void InitializeDailyEquityTracking()
        {
            DateTime utcNow = Server.TimeInUtc;
            double equity = Account.Equity;
            if (!IsValidEquitySample(equity))
            {
                LogError("EQUITY", "DAILY_TRACKING_INITIALIZATION_FAILED",
                    "utc={0:O} equity={1:R} reason=InvalidEquitySample",
                    utcNow,
                    equity);
                return;
            }
            DateTime periodStartUtc = GetDailyEquityPeriodStartUtc(utcNow);
            _dailyEquity.IsInitialized = true;
            _dailyEquity.PeriodStartUtc = periodStartUtc;
            _dailyEquity.PeriodEndUtc = periodStartUtc.AddDays(1.0);
            _dailyEquity.FirstSampleUtc = utcNow;
            _dailyEquity.LastSampleUtc = utcNow;
            _dailyEquity.PeakUtc = utcNow;
            _dailyEquity.TroughUtc = utcNow;
            _dailyEquity.DrawdownPeakUtc = utcNow;
            _dailyEquity.DrawdownTroughUtc = utcNow;
            _dailyEquity.OpeningEquity = equity;
            _dailyEquity.LatestEquity = equity;
            _dailyEquity.PeakEquity = equity;
            _dailyEquity.TroughEquity = equity;
            _dailyEquity.DrawdownPeakEquity = equity;
            _dailyEquity.DrawdownTroughEquity = equity;
            _dailyEquity.MaximumDrawdown = 0.0;
            _dailyEquity.SampleCount = 1L;
            _dailyEquity.OpeningBasis = utcNow == periodStartUtc
                ? "BoundaryObservation"
                : "BotStartup";
            LogNormal("EQUITY", "DAILY_TRACKING_STARTED",
                "periodStartUtc={0:O} periodEndUtc={1:O} firstSampleUtc={2:O} openingEquity={3:R} openingBasis={4} periodComplete=false boundaryHourUtc={5}",
                _dailyEquity.PeriodStartUtc,
                _dailyEquity.PeriodEndUtc,
                _dailyEquity.FirstSampleUtc,
                _dailyEquity.OpeningEquity,
                _dailyEquity.OpeningBasis,
                DailyEquityBoundaryHourUtc);
        }

        private void SampleDailyEquity()
        {
            ProcessDailyEquityObservation(Server.TimeInUtc, Account.Equity, "Tick");
        }

        private void ProcessDailyEquityTimerObservation()
        {
            ProcessDailyEquityObservation(Server.TimeInUtc, Account.Equity, "Timer");
        }

        private void ProcessDailyEquityObservation(DateTime utcNow, double equity, string observationSource)
        {
            if (!_dailyEquity.IsInitialized)
            {
                InitializeDailyEquityTracking();
                return;
            }
            if (!IsValidEquitySample(equity))
            {
                LogActiveWarning("daily-equity-invalid-sample", "EQUITY", "DAILY_SAMPLE_REJECTED",
                    "utc={0:O} equity={1:R} source={2} reason=InvalidEquitySample",
                    utcNow, equity, observationSource);
                return;
            }
            ClearWarning("daily-equity-invalid-sample", "EQUITY", "DAILY_SAMPLE_RECOVERED",
                "a later valid account-equity sample was accepted");

            if (utcNow >= _dailyEquity.PeriodEndUtc)
                AdvanceDailyEquityPeriod(utcNow, equity, observationSource);
            else
                ApplyDailyEquitySample(utcNow, equity);
        }

        private void ApplyDailyEquitySample(DateTime utcNow, double equity)
        {
            _dailyEquity.LatestEquity = equity;
            _dailyEquity.LastSampleUtc = utcNow;
            _dailyEquity.SampleCount++;
            if (equity > _dailyEquity.PeakEquity)
            {
                _dailyEquity.PeakEquity = equity;
                _dailyEquity.PeakUtc = utcNow;
            }
            if (equity < _dailyEquity.TroughEquity)
            {
                _dailyEquity.TroughEquity = equity;
                _dailyEquity.TroughUtc = utcNow;
            }
            double currentDrawdown = _dailyEquity.PeakEquity - equity;
            if (currentDrawdown > _dailyEquity.MaximumDrawdown)
            {
                _dailyEquity.MaximumDrawdown = currentDrawdown;
                _dailyEquity.DrawdownPeakEquity = _dailyEquity.PeakEquity;
                _dailyEquity.DrawdownPeakUtc = _dailyEquity.PeakUtc;
                _dailyEquity.DrawdownTroughEquity = equity;
                _dailyEquity.DrawdownTroughUtc = utcNow;
            }
        }

        private void AdvanceDailyEquityPeriod(DateTime utcNow, double equity, string observationSource)
        {
            DateTime nextPeriodStartUtc = GetDailyEquityPeriodStartUtc(utcNow);
            LogDailyEquitySummary(true, "BoundaryCrossed", utcNow);
            int skippedPeriods = (int)Math.Max(0.0,
                Math.Floor((nextPeriodStartUtc - _dailyEquity.PeriodEndUtc).TotalDays));
            if (skippedPeriods > 0)
            {
                LogNormal("EQUITY", "DAILY_OBSERVATION_GAP",
                    "gapStartUtc={0:O} gapEndUtc={1:O} unobservedCompletePeriods={2} lastSampleUtc={3:O} nextObservationUtc={4:O} source={5}",
                    _dailyEquity.PeriodEndUtc, nextPeriodStartUtc, skippedPeriods,
                    _dailyEquity.LastSampleUtc, utcNow, observationSource);
            }
            OpenDailyEquityPeriod(nextPeriodStartUtc, utcNow, equity,
                utcNow == nextPeriodStartUtc ? "BoundaryObservation" : "FirstObservedAfterBoundary",
                observationSource);
        }

        private void OpenDailyEquityPeriod(DateTime periodStartUtc, DateTime sampleUtc, double equity,
            string openingBasis, string observationSource)
        {
            _dailyEquity.IsInitialized = true;
            _dailyEquity.PeriodStartUtc = periodStartUtc;
            _dailyEquity.PeriodEndUtc = periodStartUtc.AddDays(1.0);
            _dailyEquity.FirstSampleUtc = sampleUtc;
            _dailyEquity.LastSampleUtc = sampleUtc;
            _dailyEquity.PeakUtc = sampleUtc;
            _dailyEquity.TroughUtc = sampleUtc;
            _dailyEquity.DrawdownPeakUtc = sampleUtc;
            _dailyEquity.DrawdownTroughUtc = sampleUtc;
            _dailyEquity.OpeningEquity = equity;
            _dailyEquity.LatestEquity = equity;
            _dailyEquity.PeakEquity = equity;
            _dailyEquity.TroughEquity = equity;
            _dailyEquity.DrawdownPeakEquity = equity;
            _dailyEquity.DrawdownTroughEquity = equity;
            _dailyEquity.MaximumDrawdown = 0.0;
            _dailyEquity.SampleCount = 1L;
            _dailyEquity.OpeningBasis = openingBasis;
            LogNormal("EQUITY", "DAILY_PERIOD_OPENED",
                "periodStartUtc={0:O} periodEndUtc={1:O} openingEquity={2:R} firstSampleUtc={3:O} openingBasis={4} source={5}",
                _dailyEquity.PeriodStartUtc, _dailyEquity.PeriodEndUtc,
                _dailyEquity.OpeningEquity, _dailyEquity.FirstSampleUtc,
                _dailyEquity.OpeningBasis, observationSource);
        }

        private void LogDailyEquitySummary(bool periodComplete, string reason, DateTime observedUtc)
        {
            if (!_dailyEquity.IsInitialized || _dailyEquity.SampleCount <= 0L)
                return;
            double netChange = _dailyEquity.LatestEquity - _dailyEquity.OpeningEquity;
            double netChangePercent = SafeEquityPercent(netChange, _dailyEquity.OpeningEquity);
            double openingToTrough = Math.Max(0.0, _dailyEquity.OpeningEquity - _dailyEquity.TroughEquity);
            double openingToTroughPercent = SafeEquityPercent(openingToTrough, _dailyEquity.OpeningEquity);
            double maximumDrawdownPercent = SafeEquityPercent(
                _dailyEquity.MaximumDrawdown, _dailyEquity.DrawdownPeakEquity);
            bool observationComplete = periodComplete &&
                _dailyEquity.OpeningBasis == "BoundaryObservation";
            string closingBasis = periodComplete
                ? "LastObservedBeforeBoundary"
                : "ShutdownObservation";
            LogNormal("EQUITY", periodComplete ? "DAILY_SUMMARY" : "DAILY_PARTIAL_SUMMARY",
                "reportingDate={0:yyyy-MM-dd} periodStartUtc={1:O} periodEndUtc={2:O} firstSampleUtc={3:O} lastSampleUtc={4:O} observedUtc={5:O} openingEquity={6:R} closingEquity={7:R} peakEquity={8:R} peakUtc={9:O} troughEquity={10:R} troughUtc={11:O} openingToTrough={12:R} openingToTroughPercent={13:R} maxDrawdown={14:R} maxDrawdownPercent={15:R} drawdownPeakEquity={16:R} drawdownPeakUtc={17:O} drawdownTroughEquity={18:R} drawdownTroughUtc={19:O} netChange={20:R} netChangePercent={21:R} sampleCount={22} openingBasis={23} closingBasis={24} periodComplete={25} observationComplete={26} reason={27}",
                _dailyEquity.PeriodEndUtc.Date, _dailyEquity.PeriodStartUtc, _dailyEquity.PeriodEndUtc,
                _dailyEquity.FirstSampleUtc, _dailyEquity.LastSampleUtc, observedUtc,
                _dailyEquity.OpeningEquity, _dailyEquity.LatestEquity,
                _dailyEquity.PeakEquity, _dailyEquity.PeakUtc,
                _dailyEquity.TroughEquity, _dailyEquity.TroughUtc,
                openingToTrough, openingToTroughPercent,
                _dailyEquity.MaximumDrawdown, maximumDrawdownPercent,
                _dailyEquity.DrawdownPeakEquity, _dailyEquity.DrawdownPeakUtc,
                _dailyEquity.DrawdownTroughEquity, _dailyEquity.DrawdownTroughUtc,
                netChange, netChangePercent, _dailyEquity.SampleCount,
                _dailyEquity.OpeningBasis, closingBasis,
                periodComplete.ToString().ToLowerInvariant(),
                observationComplete.ToString().ToLowerInvariant(), reason);
        }

        private void LogDailyEquityPartialSummaryOnStop()
        {
            DateTime utcNow = Server.TimeInUtc;
            double equity = Account.Equity;
            if (!_dailyEquity.IsInitialized)
                return;
            if (IsValidEquitySample(equity))
                ProcessDailyEquityObservation(utcNow, equity, "Shutdown");
            LogDailyEquitySummary(false, "BotStopped", utcNow);
        }

        private static double SafeEquityPercent(double amount, double denominator)
        {
            if (!IsFinitePositive(denominator))
                return 0.0;
            double result = amount / denominator * 100.0;
            return IsFinite(result) ? result : 0.0;
        }

        private static bool IsValidEquitySample(double equity)
        {
            return IsFinite(equity) && equity >= 0.0;
        }
        #endregion


    }
}
