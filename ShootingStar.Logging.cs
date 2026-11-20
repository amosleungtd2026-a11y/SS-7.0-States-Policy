using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Centralized logging, callback diagnostics, and startup summary.
    public partial class ShootingStar
    {
        private const string SourceReleaseId = "ShootingStar-2026.11.20-mev-state-v2";


        #region Centralised Logging
        private bool ShouldLog(BotLogLevel requiredLevel)
        {
            return (int)LogLevel >= (int)requiredLevel;
        }

        private const int AssetConversionTimeoutMaxConsecutiveFailures = 3;
        private int _assetConversionTimeoutConsecutiveFailures;
        private bool _assetConversionTimeoutRetryPending;
        private bool _assetConversionTimeoutEscalated;
        private bool _runtimeCallbackExceptionLogged;

        private bool IsAutomateWaitTimeoutException(Exception exception)
        {
            Exception current = exception;
            while (current != null)
            {
                if (string.Equals(
                        current.GetType().Name,
                        "AutomateWaitTimeoutException",
                        StringComparison.Ordinal))
                {
                    return true;
                }
                current = current.InnerException;
            }
            return false;
        }

        private void HandleAssetConversionTimeout(Exception exception)
        {
            _assetConversionTimeoutConsecutiveFailures++;
            _assetConversionTimeoutRetryPending = true;

            string exceptionMessage = exception == null
                ? "unspecified"
                : exception.Message;

            LogActiveWarning(
                "asset-conversion-timeout",
                "RUNTIME",
                "ASSET_CONVERSION_TIMEOUT",
                "consecutiveFailures={0} maxConsecutiveFailures={1} retryPolicy=NextScheduledMaintenancePass message={2}",
                _assetConversionTimeoutConsecutiveFailures,
                AssetConversionTimeoutMaxConsecutiveFailures,
                exceptionMessage);

            if (_assetConversionTimeoutConsecutiveFailures <
                AssetConversionTimeoutMaxConsecutiveFailures)
            {
                return;
            }

            _assetConversionTimeoutEscalated = true;
            _runtimeFaulted = true;
            LogError(
                "RUNTIME",
                "ASSET_CONVERSION_TIMEOUT_ESCALATED",
                "consecutiveFailures={0} outcome=EntriesDisabledForRun message={1}",
                _assetConversionTimeoutConsecutiveFailures,
                exceptionMessage);
        }

        private void CompleteAssetConversionTimeoutRecovery()
        {
            if (!_assetConversionTimeoutRetryPending ||
                _assetConversionTimeoutEscalated)
            {
                return;
            }

            int recoveredFailures = _assetConversionTimeoutConsecutiveFailures;
            _assetConversionTimeoutRetryPending = false;
            _assetConversionTimeoutConsecutiveFailures = 0;
            ClearWarning(
                "asset-conversion-timeout",
                "RUNTIME",
                "ASSET_CONVERSION_RECOVERED",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "recoveredConsecutiveFailures={0} completionReason=ScheduledMaintenanceSucceeded",
                    recoveredFailures));
        }

        private void HandleUnexpectedCallbackException(
            string callbackName,
            Exception exception,
            CallbackFailurePolicy failurePolicy)
        {
            _runtimeFaulted = true;
            string exceptionType = exception == null ? "unknown" : exception.GetType().Name;
            string exceptionMessage = exception == null ? "unspecified" : exception.Message;

            if (!_runtimeCallbackExceptionLogged)
            {
                _runtimeCallbackExceptionLogged = true;
                LogError("RUNTIME", "CALLBACK_EXCEPTION",
                    "unexpected exception in {0}; new entries are disabled for this run. Type={1}, Message={2}",
                    callbackName, exceptionType, exceptionMessage);
            }
            else
            {
                LogActiveWarning("runtime-callback-fault", "RUNTIME", "CALLBACK_EXCEPTION_REPEATED",
                    "unexpected exception in {0}; new entries remain disabled. Type={1}, Message={2}",
                    callbackName, exceptionType, exceptionMessage);
            }

            if (failurePolicy == CallbackFailurePolicy.StopBot)
            {
                LogError("RUNTIME", "STARTUP_ABORTED",
                    "startup was aborted after an unexpected exception in {0}", callbackName);
                Stop();
            }
        }

        private readonly HashSet<string> _telemetryFailureSteps = new HashSet<string>();
        private int _telemetryFailureCount;

        // Telemetry is observational only: a failure here is reported but must never
        // fault the runtime or disable entries.
        private void RunTelemetry(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                ReportTelemetryFailure(step, exception);
            }
        }

        private T RunTelemetry<T>(string step, Func<T> function, T fallback)
        {
            try
            {
                return function();
            }
            catch (Exception exception)
            {
                ReportTelemetryFailure(step, exception);
                return fallback;
            }
        }

        private void ReportTelemetryFailure(string step, Exception exception)
        {
            _telemetryFailureCount++;
            if (!_telemetryFailureSteps.Add(step))
                return;

            try
            {
                LogError("TELEMETRY", "TELEMETRY_ERROR",
                    "step={0} type={1} message={2} impact=telemetryOnly totalFailures={3}",
                    step,
                    exception == null ? "unknown" : exception.GetType().Name,
                    exception == null ? "unspecified" : exception.Message,
                    _telemetryFailureCount);
            }
            catch (Exception)
            {
            }
        }

        private void LogError(string category, string eventName, string format, params object[] args)
        {
            WriteLog("ERROR", category, eventName, format, args);
        }

        private void LogNormal(string category, string eventName, string format, params object[] args)
        {
            if (ShouldLog(BotLogLevel.Normal))
                WriteLog("INFO", category, eventName, format, args);
        }

        private void LogVerbose(string category, string eventName, string format, params object[] args)
        {
            if (ShouldLog(BotLogLevel.Verbose))
                WriteLog("VERBOSE", category, eventName, format, args);
        }

        private void WriteLog(string severity, string category, string eventName, string format, params object[] args)
        {
            string details;
            try
            {
                details = args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                details = format + " [log-format-error]";
            }
            if (_activeMaintenancePassId > 0L)
            {
                Print("[{0}][{1}][{2}][pass={3}] {4}",
                    severity, category, eventName, _activeMaintenancePassId, details);
                return;
            }

            Print("[{0}][{1}][{2}] {3}", severity, category, eventName, details);
        }

        private void LogActiveWarning(string key, string category, string eventName, string format, params object[] args)
        {
            LogActiveWarningCore(key, category, eventName, false, format, args);
        }

        private void LogActiveWarningWithoutUpdates(
            string key,
            string category,
            string eventName,
            string format,
            params object[] args)
        {
            LogActiveWarningCore(key, category, eventName, true, format, args);
        }

        private void LogActiveWarningCore(
            string key,
            string category,
            string eventName,
            bool suppressUpdates,
            string format,
            object[] args)
        {
            string signature;
            try
            {
                signature = args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                signature = format + " [log-format-error]";
            }

            DateTime now = Server.TimeInUtc;
            ActiveWarningState state;
            if (_activeWarningStates.TryGetValue(key, out state))
            {
                state.LastOccurrenceUtc = now;
                state.Occurrences++;
                if (state.Signature == signature)
                {
                    state.SuppressedOccurrences++;
                    _warningSuppressedOccurrences++;
                    return;
                }

                state.Signature = signature;
                if (suppressUpdates)
                {
                    state.SuppressedOccurrences++;
                    _warningSuppressedOccurrences++;
                    return;
                }
                if (state.OpeningRecordVisible && ShouldLog(BotLogLevel.Normal))
                    WriteLog("WARN", category, eventName, "{0}", signature);
                return;
            }

            bool openingRecordVisible = ShouldLog(BotLogLevel.Normal);
            state = new ActiveWarningState
            {
                Category = category,
                EventName = eventName,
                Signature = signature,
                ActivatedUtc = now,
                LastOccurrenceUtc = now,
                Occurrences = 1L,
                SuppressedOccurrences = 0L,
                OpeningRecordVisible = openingRecordVisible
            };
            _activeWarningStates[key] = state;
            _warningActivations++;
            if (openingRecordVisible)
                WriteLog("WARN", category, eventName, "{0}", signature);
        }

        private void ClearWarning(
            string key,
            string category,
            string eventName,
            string recoveryDetails,
            bool forceVisible = false)
        {
            ActiveWarningState state;
            if (!_activeWarningStates.TryGetValue(key, out state))
            {
                if (forceVisible)
                    WriteLog("RECOVERY", category, eventName, "{0}", recoveryDetails);
                return;
            }

            _activeWarningStates.Remove(key);
            _warningRecoveries++;
            if (!forceVisible &&
                (!state.OpeningRecordVisible || !ShouldLog(BotLogLevel.Normal)))
            {
                return;
            }

            DateTime resolvedUtc = Server.TimeInUtc;
            double durationSeconds = Math.Max(0.0, (resolvedUtc - state.ActivatedUtc).TotalSeconds);
            WriteLog("RECOVERY", category, eventName,
                "{0} activeSinceUtc={1:O} resolvedUtc={2:O} durationSeconds={3:F3} occurrences={4} suppressedOccurrences={5} lastDetails=\"{6}\"",
                recoveryDetails,
                state.ActivatedUtc,
                resolvedUtc,
                durationSeconds,
                state.Occurrences,
                state.SuppressedOccurrences,
                state.Signature);
        }

        private void LogActiveWarningSummary()
        {
            if (!ShouldLog(BotLogLevel.Normal))
                return;

            DateTime now = Server.TimeInUtc;
            long activeVisible = 0L;
            long activeSuppressedOccurrences = 0L;
            double oldestActiveSeconds = 0.0;
            foreach (ActiveWarningState state in _activeWarningStates.Values)
            {
                if (state.OpeningRecordVisible)
                    activeVisible++;
                activeSuppressedOccurrences += state.SuppressedOccurrences;
                oldestActiveSeconds = Math.Max(oldestActiveSeconds,
                    Math.Max(0.0, (now - state.ActivatedUtc).TotalSeconds));
            }

            foreach (KeyValuePair<string, ActiveWarningState> entry in _activeWarningStates)
            {
                ActiveWarningState state = entry.Value;
                LogNormal("RUNTIME", "ACTIVE_WARNING_DETAIL",
                    "key={0} category={1} event={2} activeSinceUtc={3:O} lastOccurrenceUtc={4:O} occurrences={5} suppressedOccurrences={6} lastDetails=\"{7}\"",
                    entry.Key,
                    state.Category,
                    state.EventName,
                    state.ActivatedUtc,
                    state.LastOccurrenceUtc,
                    state.Occurrences,
                    state.SuppressedOccurrences,
                    state.Signature);
            }

            LogNormal("RUNTIME", "ACTIVE_WARNING_SUMMARY",
                "activations={0} recoveries={1} active={2} activeVisible={3} suppressedOccurrences={4} activeSuppressedOccurrences={5} oldestActiveSeconds={6:F3}",
                _warningActivations,
                _warningRecoveries,
                _activeWarningStates.Count,
                activeVisible,
                _warningSuppressedOccurrences,
                activeSuppressedOccurrences,
                oldestActiveSeconds);
        }

        private string FormatPrice(double price)
        {
            return price.ToString("F" + _symbolDigits, CultureInfo.InvariantCulture);
        }

        #endregion

        private void LogStartupSummary(bool sufficientAtrHistoryLoaded)
        {
            LogNormal("STARTUP", "RELEASE", "sourceReleaseId={0}", SourceReleaseId);
            LogNormal("STARTUP", "MODE_CONFIGURATION",
                "configuredMode={0} effectiveMode={1} maximumNewOrdersPerPass={2}",
                TradingMechanismMode,
                GetTradingModeDisplayName(_effectiveTradingMode),
                MaxNewOrdersPerMaintenancePass);
            LogNormal("STARTUP", "CONFIG",
                "volumeLots={0:F2} gv={1:F2} gvAdjustedVolumeLots={2:F4} fixedAllocation={3:F2}/{4:F2} logLevel={5}",
                TotalTradingVolumeInLots,
                GlobalVolatility,
                TotalTradingVolumeInLots / GlobalVolatility,
                _effectiveDirectionalAllocation,
                100.0 - _effectiveDirectionalAllocation,
                LogLevel);
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
            {
                LogNormal("AUTO_POLICY", "RETRY_CONFIGURATION",
                    "retryCount={0} retryCountMeaning=additionalAttempts retrySeconds={1} decisionLifetimeMinutes={2}",
                    TransientPlacementRetryCount,
                    TransientPlacementRetrySeconds,
                    PolicyDecisionLifetimeMinutes);
                long configuredRetryWindowSeconds =
                    (long)TransientPlacementRetryCount *
                    TransientPlacementRetrySeconds;
                long decisionLifetimeSeconds =
                    (long)PolicyDecisionLifetimeMinutes * 60L;
                if (configuredRetryWindowSeconds >= decisionLifetimeSeconds)
                {
                    LogNormal("AUTO_POLICY", "RETRY_CONFIGURATION_LIMITED_BY_EXPIRY",
                        "configuredRetryWindowSeconds={0} decisionLifetimeSeconds={1} outcome=decisionExpiryMayPreventSomeRetries",
                        configuredRetryWindowSeconds,
                        decisionLifetimeSeconds);
                }
            }
            LogNormal("STARTUP", "MODEL",
                "atr={0}/{1} history={2} vfHours=24 vfRange={3:F3}-{4:F3}",
                AtrFastPeriod, AtrSlowPeriod,
                sufficientAtrHistoryLoaded ? "ready" : "neutral-fallback",
                _utcHourlyVolatilityFactors.Min(), _utcHourlyVolatilityFactors.Max());
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
            {
                LogNormal("STARTUP", "REGIME",
                    "mode={0} mevLength={1} hourlySigmaReturnLength={2} mevBoundaries={3:F2}/{4:F2} extreme={5:F2} hourlyState={6} dailyContext={7} liveBarsIncluded=true status={8}",
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    MomentumEfficiencyLength,
                    HourlyStandardDeviationLength,
                    MevLowerBoundary,
                    MevUpperBoundary,
                    MevExtremeThreshold,
                    _hasValidHourlyMev ? _currentHourlyMevSignal.ToString() : "unavailable",
                    _hasValidDailyMev ? _currentDailyMevContext.ToString() : "unavailable",
                    _hasValidHourlyMev && _hasValidDailyMev ? "available" : "unavailable");
            }
            else
            {
                LogNormal("STARTUP", "REGIME",
                    "mode={0} manualLongStrategies={1} manualShortStrategies={2} hourlyMevBandSelection=enabled",
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    string.Join(",", _effectiveManualLongStrategies),
                    string.Join(",", _effectiveManualShortStrategies));
            }
            LogNormal("STARTUP", "READY",
                "symbol={0} marketSessionTimeZone={1} openLocal={2:hh\\:mm} executionOpenLocal={3:hh\\:mm} closeLocal={4:hh\\:mm} flattenLocal={5:hh\\:mm} reference=rolling-hi-lo windowEligibleMinutes={6} status={7}",
                Symbol.Name, _newYorkTimeZone.Id,
                MarketSessionActualOpenLocal, MarketSessionExecutionOpenLocal,
                MarketSessionCloseLocal, MarketSessionFlattenStartLocal,
                ReferenceWindowEligibleMinutes,
                _referenceRangeReady ? "ready" : "warming");
            LogNormal("STARTUP", "PRICE_UNIT",
                "percentageUnit={0:F4}% shared(moveUnits/distanceRatio/slUnits/reward)={1:F4}/{2:F4}/{3:F4}/{4:F4}",
                PercentageUnit,
                MoveUnits, DistanceRatio, StopLossUnits, Reward);
            LogNormal("STARTUP", "RUNTIME",
                "checkEveryNTicks={0} liveMevRefreshSeconds={1:F0} policyEvaluationSeconds={2:F0} safetyTimerSeconds={3} atrTimerCheckSeconds={4} equityUiRefreshSeconds={5} positionCooldownMs={6} minTrailStepPercent={7:F4} labels={8}|{9}|{10}|{11}",
                CheckEveryNTicks,
                RealtimeEvaluationInterval.TotalSeconds,
                RealtimeEvaluationInterval.TotalSeconds,
                SafetyTimerSeconds,
                AtrTimerCheckIntervalSeconds,
                EquityUiRefreshIntervalSeconds,
                PositionModifyCooldownMilliseconds,
                MinimumPositionTrailStepPercent,
                _mrLongLabel, _mrShortLabel, _tfLongLabel, _tfShortLabel);
        }
    }
}
