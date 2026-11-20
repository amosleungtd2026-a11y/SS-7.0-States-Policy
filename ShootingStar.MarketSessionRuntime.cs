using System;
using System.Collections.Generic;
using System.Globalization;

namespace cAlgo.Robots
{
    // Market-session calendar calculations and state queries.
    public partial class ShootingStar
    {
        #region Market Session Runtime
        private DateTime ConvertNewYorkLocalToUtc(DateTime local)
        {
            if (_newYorkTimeZone == null)
                throw new InvalidOperationException("The New York time zone has not been initialized.");

            DateTime unspecifiedLocal = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (_newYorkTimeZone.IsInvalidTime(unspecifiedLocal))
            {
                throw new InvalidOperationException(
                    "A market-session boundary falls in a skipped New York daylight-saving interval.");
            }

            if (_newYorkTimeZone.IsAmbiguousTime(unspecifiedLocal))
            {
                throw new InvalidOperationException(
                    "A market-session boundary falls in a repeated New York daylight-saving interval.");
            }

            return TimeZoneInfo.ConvertTimeToUtc(unspecifiedLocal, _newYorkTimeZone);
        }

        private bool TryBuildTradingSession(DateTime tradingDateNewYork, out TradingSession session)
        {
            DateTime tradingDate = DateTime.SpecifyKind(tradingDateNewYork.Date, DateTimeKind.Unspecified);
            if (tradingDate.DayOfWeek == DayOfWeek.Saturday ||
                tradingDate.DayOfWeek == DayOfWeek.Sunday)
            {
                session = null;
                return false;
            }

            if (_sessionOverrides.TryGetValue(tradingDate, out SessionOverride sessionOverride) &&
                sessionOverride.IsClosed)
            {
                session = null;
                return false;
            }

            TimeSpan openTime = sessionOverride != null && sessionOverride.OpenTimeNewYork.HasValue
                ? sessionOverride.OpenTimeNewYork.Value
                : MarketSessionActualOpenLocal;
            TimeSpan flattenTime = sessionOverride != null && sessionOverride.FlattenTimeNewYork.HasValue
                ? sessionOverride.FlattenTimeNewYork.Value
                : MarketSessionFlattenStartLocal;
            TimeSpan closeTime = sessionOverride != null && sessionOverride.CloseTimeNewYork.HasValue
                ? sessionOverride.CloseTimeNewYork.Value
                : MarketSessionCloseLocal;

            DateTime actualOpenLocal = tradingDate.AddDays(-1).Add(openTime);
            DateTime flattenStartLocal = tradingDate.Add(flattenTime);
            DateTime closeLocal = tradingDate.Add(closeTime);

            DateTime actualOpenUtc = ConvertNewYorkLocalToUtc(actualOpenLocal);
            DateTime executionOpenUtc = actualOpenUtc.AddMinutes(5);
            DateTime executionOpenLocal =
                TimeZoneInfo.ConvertTimeFromUtc(executionOpenUtc, _newYorkTimeZone);
            DateTime flattenStartUtc = ConvertNewYorkLocalToUtc(flattenStartLocal);
            DateTime closeUtc = ConvertNewYorkLocalToUtc(closeLocal);
            if (!(actualOpenUtc < executionOpenUtc &&
                  executionOpenUtc < flattenStartUtc &&
                  flattenStartUtc < closeUtc))
            {
                throw new InvalidOperationException(
                    "Calculated New York market-session boundaries are not strictly ordered.");
            }

            session = new TradingSession
            {
                TradingDateNewYork = tradingDate,
                ActualOpenLocal = actualOpenLocal,
                ExecutionOpenLocal = executionOpenLocal,
                FlattenStartLocal = flattenStartLocal,
                CloseLocal = closeLocal,
                ActualOpenUtc = actualOpenUtc,
                ExecutionOpenUtc = executionOpenUtc,
                FlattenStartUtc = flattenStartUtc,
                CloseUtc = closeUtc
            };
            return true;
        }

        private MarketSessionState GetMarketSessionState(DateTime utc)
        {
            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime newYork = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone);
            if (TryGetTradingSessionContaining(utcValue, out TradingSession session))
            {
                if (utcValue < session.ExecutionOpenUtc)
                    return MarketSessionState.PostOpenStabilization;
                if (utcValue >= session.FlattenStartUtc)
                    return MarketSessionState.PreCloseFlattening;
                return MarketSessionState.Open;
            }

            if (newYork.DayOfWeek == DayOfWeek.Saturday ||
                newYork.DayOfWeek == DayOfWeek.Sunday)
            {
                return MarketSessionState.Closed;
            }

            if (_sessionOverrides.TryGetValue(
                    DateTime.SpecifyKind(newYork.Date, DateTimeKind.Unspecified),
                    out SessionOverride dayOverride) &&
                dayOverride.IsClosed)
            {
                return MarketSessionState.Closed;
            }

            DateTime nextTradingDate = newYork.Date.AddDays(1);
            while (nextTradingDate.DayOfWeek == DayOfWeek.Saturday ||
                   nextTradingDate.DayOfWeek == DayOfWeek.Sunday)
            {
                nextTradingDate = nextTradingDate.AddDays(1);
            }

            if (TryBuildTradingSession(nextTradingDate, out TradingSession nextSession) &&
                nextSession.ActualOpenLocal.Date == newYork.Date &&
                utcValue < nextSession.ActualOpenUtc)
            {
                return MarketSessionState.MaintenanceBreak;
            }

            return MarketSessionState.Closed;
        }

        private bool IsTradingSessionOpen(DateTime utc)
        {
            return GetMarketSessionState(utc) == MarketSessionState.Open;
        }

        private void RecordExecutableMarketTick(DateTime utc, double bid, double ask)
        {
            if (IsExecutableSessionState(GetMarketSessionState(utc)) &&
                IsFinitePositive(bid) &&
                IsFinitePositive(ask) &&
                ask >= bid)
            {
                _lastExecutableMarketTickUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }
        }

        private bool HasExecutableMarketQuote(DateTime utc)
        {
            if (!IsExecutableSessionState(GetMarketSessionState(utc)) ||
                !IsFinitePositive(Symbol.Bid) ||
                !IsFinitePositive(Symbol.Ask) ||
                Symbol.Ask < Symbol.Bid ||
                _lastExecutableMarketTickUtc == DateTime.MinValue)
            {
                return false;
            }

            return TryGetSessionContaining(
                       utc,
                       out DateTime currentOpenUtc,
                       out _,
                       out _) &&
                   TryGetSessionContaining(
                       _lastExecutableMarketTickUtc,
                       out DateTime lastTickSessionOpenUtc,
                       out _,
                       out _) &&
                   currentOpenUtc == lastTickSessionOpenUtc;
        }

        private static bool IsExecutableSessionState(MarketSessionState state)
        {
            return state == MarketSessionState.Open ||
                   state == MarketSessionState.PreCloseFlattening ||
                   state == MarketSessionState.PostOpenStabilization;
        }

        private bool TryGetSessionContaining(
            DateTime utc,
            out DateTime openUtc,
            out DateTime flattenStartUtc,
            out DateTime closeUtc)
        {
            if (TryGetTradingSessionContaining(utc, out TradingSession session))
            {
                openUtc = session.ActualOpenUtc;
                flattenStartUtc = session.FlattenStartUtc;
                closeUtc = session.CloseUtc;
                return true;
            }

            openUtc = DateTime.MinValue;
            flattenStartUtc = DateTime.MinValue;
            closeUtc = DateTime.MinValue;
            return false;
        }

        private bool TryGetTradingSessionContaining(DateTime utc, out TradingSession containingSession)
        {
            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime localDate = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone).Date;

            for (int dayOffset = -1; dayOffset <= 1; dayOffset++)
            {
                if (!TryBuildTradingSession(localDate.AddDays(dayOffset), out TradingSession session))
                    continue;

                if (utcValue >= session.ActualOpenUtc && utcValue < session.CloseUtc)
                {
                    containingSession = session;
                    return true;
                }
            }

            containingSession = null;
            return false;
        }

        private bool TryGetNextSession(DateTime utc, out TradingSession nextSession)
        {
            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime localDate = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone).Date;
            TradingSession selected = null;

            for (int dayOffset = -1; dayOffset <= 370; dayOffset++)
            {
                if (!TryBuildTradingSession(localDate.AddDays(dayOffset), out TradingSession candidate) ||
                    candidate.ActualOpenUtc <= utcValue)
                {
                    continue;
                }

                if (selected == null || candidate.ActualOpenUtc < selected.ActualOpenUtc)
                    selected = candidate;
            }

            nextSession = selected;
            return selected != null;
        }

        private bool TryFindPreviousActiveSession(
            DateTime tradingDateNewYork,
            out TradingSession previousSession)
        {
            for (int dayOffset = 1; dayOffset <= 370; dayOffset++)
            {
                DateTime candidateDate;
                try
                {
                    candidateDate = tradingDateNewYork.Date.AddDays(-dayOffset);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                if (TryBuildTradingSession(candidateDate, out previousSession))
                    return true;
            }

            previousSession = null;
            return false;
        }

        private bool TryFindNextActiveSession(
            DateTime tradingDateNewYork,
            out TradingSession nextSession)
        {
            for (int dayOffset = 1; dayOffset <= 370; dayOffset++)
            {
                DateTime candidateDate;
                try
                {
                    candidateDate = tradingDateNewYork.Date.AddDays(dayOffset);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                if (TryBuildTradingSession(candidateDate, out nextSession))
                    return true;
            }

            nextSession = null;
            return false;
        }

        private bool TryGetRolloverWindow(
            DateTime utc,
            out DateTime rolloverStartUtc,
            out DateTime rolloverEndUtc)
        {
            DateTime utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime localDate = TimeZoneInfo.ConvertTimeFromUtc(utcValue, _newYorkTimeZone).Date;
            var sessions = new List<TradingSession>();

            for (int dayOffset = -370; dayOffset <= 370; dayOffset++)
            {
                if (TryBuildTradingSession(localDate.AddDays(dayOffset), out TradingSession session))
                    sessions.Add(session);
            }

            sessions.Sort((left, right) => left.ActualOpenUtc.CompareTo(right.ActualOpenUtc));

            for (int index = 0; index < sessions.Count - 1; index++)
            {
                TradingSession session = sessions[index];
                TradingSession nextSession = sessions[index + 1];
                if (session.CloseUtc >= nextSession.ActualOpenUtc)
                {
                    throw new InvalidOperationException(
                        "Calculated New York market sessions overlap or are out of order.");
                }

                DateTime startUtc = session.FlattenStartUtc;
                DateTime endUtc = nextSession.ExecutionOpenUtc;
                if (utcValue >= startUtc && utcValue < endUtc)
                {
                    rolloverStartUtc = startUtc;
                    rolloverEndUtc = endUtc;
                    return true;
                }

                if (startUtc > utcValue)
                {
                    rolloverStartUtc = startUtc;
                    rolloverEndUtc = endUtc;
                    return true;
                }
            }

            rolloverStartUtc = DateTime.MinValue;
            rolloverEndUtc = DateTime.MinValue;
            return false;
        }

        #endregion

    }
}
