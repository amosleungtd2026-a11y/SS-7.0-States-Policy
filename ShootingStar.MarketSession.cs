using System;
using System.Collections.Generic;
using System.Globalization;

namespace cAlgo.Robots
{
    // Market-session overrides, configuration, and validation.
    public partial class ShootingStar
    {
        private enum MarketSessionState
        {
            Closed,
            PreCloseFlattening,
            MaintenanceBreak,
            PostOpenStabilization,
            Open
        }

        private sealed class TradingSession
        {
            public DateTime TradingDateNewYork;
            public DateTime ActualOpenLocal;
            public DateTime ExecutionOpenLocal;
            public DateTime FlattenStartLocal;
            public DateTime CloseLocal;
            public DateTime ActualOpenUtc;
            public DateTime ExecutionOpenUtc;
            public DateTime FlattenStartUtc;
            public DateTime CloseUtc;
        }

        private sealed class SessionOverride
        {
            public bool IsClosed;
            public TimeSpan? OpenTimeNewYork;
            public TimeSpan? FlattenTimeNewYork;
            public TimeSpan? CloseTimeNewYork;
        }

        private static readonly TimeSpan MarketSessionActualOpenLocal = new TimeSpan(18, 0, 0);
        private static readonly TimeSpan MarketSessionExecutionOpenLocal = new TimeSpan(18, 5, 0);
        private static readonly TimeSpan MarketSessionFlattenStartLocal = new TimeSpan(16, 40, 0);
        private static readonly TimeSpan MarketSessionCloseLocal = new TimeSpan(17, 0, 0);

        private TimeZoneInfo _newYorkTimeZone;
        private Dictionary<DateTime, SessionOverride> _sessionOverrides =
            new Dictionary<DateTime, SessionOverride>();
        private bool _hasLoggedMarketSessionState;
        private MarketSessionState _lastLoggedMarketSessionState;
        private DateTime _lastExecutableMarketTickUtc = DateTime.MinValue;

        private void InitializeMarketTimeZone()
        {
            string[] timeZoneIds =
            {
                "America/New_York",
                "Eastern Standard Time"
            };

            foreach (string timeZoneId in timeZoneIds)
            {
                try
                {
                    _newYorkTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                    return;
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            throw new InvalidOperationException(
                "The New York time zone is unavailable; market-session behavior cannot be initialized.");
        }

        private void InitializeMarketSessionOverrides()
        {
            Dictionary<DateTime, SessionOverride> parsed =
                ParseMarketSessionOverrides(MarketSessionOverrides);
            _sessionOverrides = parsed;
            ValidateMarketSessionOverrides(parsed);

            foreach (KeyValuePair<DateTime, SessionOverride> entry in parsed)
            {
                string details = entry.Value.IsClosed
                    ? "closed=true"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "closed=false open={0} flatten={1} close={2}",
                        FormatOverrideTime(entry.Value.OpenTimeNewYork),
                        FormatOverrideTime(entry.Value.FlattenTimeNewYork),
                        FormatOverrideTime(entry.Value.CloseTimeNewYork));
                WriteLog("INFO", "STARTUP", "MARKET_SESSION_OVERRIDE",
                    "tradingDateNewYork={0:yyyy-MM-dd} {1}",
                    entry.Key,
                    details);
            }
        }

        private static Dictionary<DateTime, SessionOverride> ParseMarketSessionOverrides(
            string configuration)
        {
            var overrides = new Dictionary<DateTime, SessionOverride>();
            string text = (configuration ?? string.Empty).Trim();
            if (text.Length == 0)
                return overrides;

            if (text.EndsWith(";", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 1);

            string[] entries = text.Split(';');
            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim();
                int separator = entry.IndexOf('=');
                if (entry.Length == 0 ||
                    separator <= 0 ||
                    separator != entry.LastIndexOf('='))
                {
                    throw new ArgumentException(
                        "Market session overrides must use date=override entries separated by semicolons.");
                }

                string dateText = entry.Substring(0, separator).Trim();
                if (!DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime tradingDate))
                {
                    throw new ArgumentException(
                        "Invalid market-session override date '" + dateText + "'; expected yyyy-MM-dd.");
                }

                tradingDate = DateTime.SpecifyKind(tradingDate.Date, DateTimeKind.Unspecified);
                if (tradingDate.Year == 1 ||
                    tradingDate.Year == 9999 ||
                    tradingDate.DayOfWeek == DayOfWeek.Saturday ||
                    tradingDate.DayOfWeek == DayOfWeek.Sunday)
                {
                    throw new ArgumentException(
                        "Market-session override dates must be weekday trading dates with a valid preceding calendar day.");
                }

                if (overrides.ContainsKey(tradingDate))
                {
                    throw new ArgumentException(
                        "Duplicate market-session override date '" +
                        tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'.");
                }

                string[] tokens = entry.Substring(separator + 1).Split(',');
                for (int index = 0; index < tokens.Length; index++)
                    tokens[index] = tokens[index].Trim();

                var sessionOverride = new SessionOverride();
                if (tokens.Length == 1 &&
                    string.Equals(tokens[0], "CLOSED", StringComparison.OrdinalIgnoreCase))
                {
                    sessionOverride.IsClosed = true;
                }
                else
                {
                    if (tokens.Length == 0 || tokens.Length % 2 != 0)
                    {
                        throw new ArgumentException(
                            "Market-session override times must use OPEN,hh:mm, FLATTEN,hh:mm, or CLOSE,hh:mm pairs.");
                    }

                    for (int index = 0; index < tokens.Length; index += 2)
                    {
                        TimeSpan time;
                        if (!TryParseMarketSessionTime(tokens[index + 1], out time))
                        {
                            throw new ArgumentException(
                                "Invalid market-session override time '" + tokens[index + 1] + "'; expected 24-hour HH:mm.");
                        }

                        if (string.Equals(tokens[index], "OPEN", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sessionOverride.OpenTimeNewYork.HasValue)
                                throw DuplicateOverrideBoundary(tradingDate, "OPEN");
                            sessionOverride.OpenTimeNewYork = time;
                        }
                        else if (string.Equals(tokens[index], "FLATTEN", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sessionOverride.FlattenTimeNewYork.HasValue)
                                throw DuplicateOverrideBoundary(tradingDate, "FLATTEN");
                            sessionOverride.FlattenTimeNewYork = time;
                        }
                        else if (string.Equals(tokens[index], "CLOSE", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sessionOverride.CloseTimeNewYork.HasValue)
                                throw DuplicateOverrideBoundary(tradingDate, "CLOSE");
                            sessionOverride.CloseTimeNewYork = time;
                        }
                        else
                        {
                            throw new ArgumentException(
                                "Unknown market-session override boundary '" + tokens[index] +
                                "'; expected OPEN, FLATTEN, CLOSE, or CLOSED.");
                        }
                    }
                }

                overrides.Add(tradingDate, sessionOverride);
            }

            return overrides;
        }

        private static ArgumentException DuplicateOverrideBoundary(DateTime date, string boundary)
        {
            return new ArgumentException(
                "Duplicate " + boundary + " boundary in market-session override for " +
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
        }

        private static bool TryParseMarketSessionTime(string text, out TimeSpan time)
        {
            return TimeSpan.TryParseExact(
                       text,
                       @"hh\:mm",
                       CultureInfo.InvariantCulture,
                       out time) &&
                   time >= TimeSpan.Zero &&
                   time < TimeSpan.FromDays(1);
        }

        private static string FormatOverrideTime(TimeSpan? time)
        {
            return time.HasValue
                ? time.Value.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                : "default";
        }

        private void ValidateMarketSessionOverrides(
            Dictionary<DateTime, SessionOverride> overrides)
        {
            foreach (KeyValuePair<DateTime, SessionOverride> entry in overrides)
            {
                try
                {
                    TradingSession currentSession = null;
                    if (!entry.Value.IsClosed &&
                        !TryBuildTradingSession(entry.Key, out currentSession))
                    {
                        throw new ArgumentException(
                            "Market-session override for " +
                            entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                            " does not produce an active trading session.");
                    }

                    bool hasPrevious = TryFindPreviousActiveSession(
                        entry.Key,
                        out TradingSession previousSession);
                    bool hasNext = TryFindNextActiveSession(
                        entry.Key,
                        out TradingSession nextSession);

                    if (entry.Value.IsClosed)
                    {
                        if (hasPrevious && hasNext &&
                            previousSession.CloseUtc >= nextSession.ActualOpenUtc)
                        {
                            throw new ArgumentException(
                                "Closed-date override for " +
                                entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                                " creates overlapping market sessions.");
                        }
                        continue;
                    }

                    if ((hasPrevious && previousSession.CloseUtc >= currentSession.ActualOpenUtc) ||
                        (hasNext && currentSession.CloseUtc >= nextSession.ActualOpenUtc))
                    {
                        throw new ArgumentException(
                            "Market-session override for " +
                            entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                            " creates overlapping or unordered session boundaries.");
                    }
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException(
                        "Invalid market-session boundary in override for " +
                        entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                        ": " + exception.Message,
                        exception);
                }
            }
        }


    }
}
