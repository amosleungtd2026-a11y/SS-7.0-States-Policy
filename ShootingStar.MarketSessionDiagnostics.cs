using System;
using System.Collections.Generic;
using System.Globalization;

namespace cAlgo.Robots
{
    // Market-session logging and startup self-checks.
    public partial class ShootingStar
    {
        #region Market Session Diagnostics and Self-Checks
        private void LogMarketSessionState(DateTime utc)
        {
            MarketSessionState state = GetMarketSessionState(utc);
            if (_hasLoggedMarketSessionState && state == _lastLoggedMarketSessionState)
                return;

            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone);
            string previous = _hasLoggedMarketSessionState
                ? _lastLoggedMarketSessionState.ToString()
                : "unknown";
            WriteLog("INFO", "SESSION", "STATE_CHANGED",
                "previous={0} current={1} utc={2:O} newYork={3:O}",
                previous,
                state,
                utcValue,
                local);

            _lastLoggedMarketSessionState = state;
            _hasLoggedMarketSessionState = true;
        }

        private void LogMarketSessionConfiguration(DateTime utc)
        {
            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone);
            if (!TryGetNextSession(utcValue, out TradingSession nextSession))
            {
                throw new InvalidOperationException(
                    "The next New York market session could not be determined at startup.");
            }

            string currentSession;
            if (TryGetTradingSessionContaining(utcValue, out TradingSession activeSession))
            {
                currentSession = string.Format(
                    CultureInfo.InvariantCulture,
                    "currentTradingDateNewYork={0:yyyy-MM-dd} currentActualOpenLocal={1:HH:mm} currentExecutionOpenLocal={2:HH:mm} " +
                    "currentFlattenLocal={3:HH:mm} currentCloseLocal={4:HH:mm} currentActualOpenUtc={5:O} " +
                    "currentExecutionOpenUtc={6:O} currentFlattenUtc={7:O} currentCloseUtc={8:O}",
                    activeSession.TradingDateNewYork,
                    activeSession.ActualOpenLocal,
                    activeSession.ExecutionOpenLocal,
                    activeSession.FlattenStartLocal,
                    activeSession.CloseLocal,
                    activeSession.ActualOpenUtc,
                    activeSession.ExecutionOpenUtc,
                    activeSession.FlattenStartUtc,
                    activeSession.CloseUtc);
            }
            else
            {
                currentSession = "currentSession=none";
            }

            WriteLog("INFO", "STARTUP", "MARKET_SESSION",
                "timeZoneId={0} baseUtcOffset={1} supportsDaylightSavingTime={2} currentOffset={3} isDst={4} state={5} " +
                "newYorkNow={6:O} {7} nextTradingDateNewYork={8:yyyy-MM-dd} " +
                "actualOpenLocal={9:HH:mm} executionOpenLocal={10:HH:mm} flattenLocal={11:HH:mm} closeLocal={12:HH:mm} " +
                "actualOpenUtc={13:O} executionOpenUtc={14:O} flattenUtc={15:O} closeUtc={16:O}",
                _newYorkTimeZone.Id,
                _newYorkTimeZone.BaseUtcOffset,
                _newYorkTimeZone.SupportsDaylightSavingTime,
                _newYorkTimeZone.GetUtcOffset(utcValue),
                _newYorkTimeZone.IsDaylightSavingTime(local),
                GetMarketSessionState(utcValue),
                local,
                currentSession,
                nextSession.TradingDateNewYork,
                nextSession.ActualOpenLocal,
                nextSession.ExecutionOpenLocal,
                nextSession.FlattenStartLocal,
                nextSession.CloseLocal,
                nextSession.ActualOpenUtc,
                nextSession.ExecutionOpenUtc,
                nextSession.FlattenStartUtc,
                nextSession.CloseUtc);
        }

        private bool RunMarketSessionSelfTests()
        {
            Dictionary<DateTime, SessionOverride> configuredOverrides = _sessionOverrides;
            bool passed;
            try
            {
                _sessionOverrides = new Dictionary<DateTime, SessionOverride>();
                passed = RunNormalMarketSessionAssertions() &&
                         RunExceptionalMarketSessionAssertions();
            }
            finally
            {
                _sessionOverrides = configuredOverrides;
            }

            if (!passed)
            {
                LogError("STARTUP", "MARKET_SESSION_SELF_TEST_FAILED",
                    "New York session boundary, DST, weekend, or exceptional-session assertions failed");
                return false;
            }

            LogNormal("STARTUP", "MARKET_SESSION_SELF_TEST",
                "standard-time, daylight-time, DST-transition, weekend, and exceptional-session assertions passed");
            return true;
        }

        private bool RunNormalMarketSessionAssertions()
        {
            return
                AssertNewYorkBoundary(new DateTime(2026, 1, 15), new TimeSpan(16, 40, 0), new DateTime(2026, 1, 15, 21, 40, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 1, 15), new TimeSpan(17, 0, 0), new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 7, 15), new TimeSpan(16, 40, 0), new DateTime(2026, 7, 15, 20, 40, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 7, 15), new TimeSpan(17, 0, 0), new DateTime(2026, 7, 15, 21, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 7, 15), new TimeSpan(18, 5, 0), new DateTime(2026, 7, 15, 22, 5, 0, DateTimeKind.Utc)) &&
                AssertMarketSessionState(new DateTime(2026, 1, 16, 16, 39, 59, DateTimeKind.Unspecified), MarketSessionState.Open) &&
                AssertMarketSessionState(new DateTime(2026, 1, 16, 16, 40, 0, DateTimeKind.Unspecified), MarketSessionState.PreCloseFlattening) &&
                AssertMarketSessionState(new DateTime(2026, 1, 16, 17, 0, 0, DateTimeKind.Unspecified), MarketSessionState.Closed) &&
                AssertMarketSessionState(new DateTime(2026, 1, 17, 12, 0, 0, DateTimeKind.Unspecified), MarketSessionState.Closed) &&
                AssertMarketSessionState(new DateTime(2026, 1, 18, 17, 59, 59, DateTimeKind.Unspecified), MarketSessionState.Closed) &&
                AssertMarketSessionState(new DateTime(2026, 1, 18, 18, 0, 0, DateTimeKind.Unspecified), MarketSessionState.PostOpenStabilization) &&
                AssertMarketSessionState(new DateTime(2026, 1, 18, 18, 5, 0, DateTimeKind.Unspecified), MarketSessionState.Open) &&
                AssertNewYorkBoundary(new DateTime(2026, 3, 6), MarketSessionCloseLocal, new DateTime(2026, 3, 6, 22, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 3, 9), MarketSessionCloseLocal, new DateTime(2026, 3, 9, 21, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 10, 30), MarketSessionCloseLocal, new DateTime(2026, 10, 30, 21, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 11, 2), MarketSessionCloseLocal, new DateTime(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc)) &&
                AssertNewYorkBoundary(new DateTime(2026, 11, 1), MarketSessionActualOpenLocal, new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc)) &&
                AssertRolloverWindow(
                    new DateTime(2026, 1, 16, 21, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 1, 16, 21, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 1, 18, 23, 5, 0, DateTimeKind.Utc)) &&
                AssertRolloverWindow(
                    new DateTime(2026, 3, 6, 21, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 3, 6, 21, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 3, 8, 22, 5, 0, DateTimeKind.Utc)) &&
                AssertRolloverWindow(
                    new DateTime(2026, 10, 30, 20, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 10, 30, 20, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 11, 1, 23, 5, 0, DateTimeKind.Utc));
        }

        private bool RunExceptionalMarketSessionAssertions()
        {
            bool invalidDuplicate =
                AssertInvalidMarketSessionOverrides(
                    "2026-12-25=CLOSED;2026-12-25=CLOSED");
            bool invalidTime =
                AssertInvalidMarketSessionOverrides("2026-12-24=OPEN,25:00");
            bool invalidBoundaryOrder =
                AssertInvalidMarketSessionOverrideGeometry(
                    "2026-12-24=FLATTEN,17:00,CLOSE,16:30");

            _sessionOverrides = ParseMarketSessionOverrides(
                "2026-12-24=OPEN,18:00,FLATTEN,12:40,CLOSE,13:00;" +
                "2026-12-25=CLOSED;" +
                "2026-12-28=OPEN,19:00");
            ValidateMarketSessionOverrides(_sessionOverrides);

            bool earlyClose =
                AssertMarketSessionState(
                    new DateTime(2026, 12, 24, 12, 39, 59, DateTimeKind.Unspecified),
                    MarketSessionState.Open) &&
                AssertMarketSessionState(
                    new DateTime(2026, 12, 24, 12, 40, 0, DateTimeKind.Unspecified),
                    MarketSessionState.PreCloseFlattening) &&
                AssertNewYorkBoundary(
                    new DateTime(2026, 12, 24),
                    new TimeSpan(13, 0, 0),
                    new DateTime(2026, 12, 24, 18, 0, 0, DateTimeKind.Utc)) &&
                AssertMarketSessionState(
                    new DateTime(2026, 12, 24, 13, 0, 0, DateTimeKind.Unspecified),
                    MarketSessionState.Closed);

            bool fullClosure =
                !TryBuildTradingSession(
                    new DateTime(2026, 12, 25),
                    out TradingSession closedSession) &&
                AssertMarketSessionState(
                    new DateTime(2026, 12, 25, 12, 0, 0, DateTimeKind.Unspecified),
                    MarketSessionState.Closed);

            bool delayedOpen =
                AssertMarketSessionState(
                    new DateTime(2026, 12, 27, 18, 59, 59, DateTimeKind.Unspecified),
                    MarketSessionState.Closed) &&
                AssertMarketSessionState(
                    new DateTime(2026, 12, 27, 19, 0, 0, DateTimeKind.Unspecified),
                    MarketSessionState.PostOpenStabilization) &&
                AssertMarketSessionState(
                    new DateTime(2026, 12, 27, 19, 5, 0, DateTimeKind.Unspecified),
                    MarketSessionState.Open);

            bool holidayRollover =
                AssertRolloverWindow(
                    new DateTime(2026, 12, 24, 17, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 12, 24, 17, 40, 0, DateTimeKind.Utc),
                    new DateTime(2026, 12, 28, 0, 5, 0, DateTimeKind.Utc));

            return invalidDuplicate &&
                   invalidTime &&
                   invalidBoundaryOrder &&
                   earlyClose &&
                   fullClosure &&
                   delayedOpen &&
                   holidayRollover;
        }

        private bool AssertInvalidMarketSessionOverrides(string configuration)
        {
            try
            {
                ParseMarketSessionOverrides(configuration);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private bool AssertInvalidMarketSessionOverrideGeometry(string configuration)
        {
            Dictionary<DateTime, SessionOverride> configuredOverrides = _sessionOverrides;
            try
            {
                _sessionOverrides = ParseMarketSessionOverrides(configuration);
                try
                {
                    ValidateMarketSessionOverrides(_sessionOverrides);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }
            finally
            {
                _sessionOverrides = configuredOverrides;
            }
        }

        private bool AssertNewYorkBoundary(DateTime date, TimeSpan time, DateTime expectedUtc)
        {
            DateTime local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
            DateTime actualUtc = ConvertNewYorkLocalToUtc(local);
            return actualUtc == expectedUtc;
        }

        private bool AssertMarketSessionState(DateTime local, MarketSessionState expected)
        {
            DateTime utc = ConvertNewYorkLocalToUtc(local);
            return GetMarketSessionState(utc) == expected;
        }

        private bool AssertRolloverWindow(
            DateTime utc,
            DateTime expectedStartUtc,
            DateTime expectedEndUtc)
        {
            return TryGetRolloverWindow(utc, out DateTime actualStartUtc, out DateTime actualEndUtc) &&
                   actualStartUtc == expectedStartUtc &&
                   actualEndUtc == expectedEndUtc;
        }

        #endregion

    }
}
