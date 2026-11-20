using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    /// <summary>
    /// Merged mean-reversion and trend-following trailing-limit cBot.
    /// The implementation is organized across the partial-class source files.
    /// </summary>
    public partial class ShootingStar : Robot
    {

        #region Diagnostic State

        private long _maintenancePassSequence;
        private long _activeMaintenancePassId;
        private long _maintenanceTimingPassCount;
        private double _maintenancePositionsTotalMs;
        private double _maintenancePendingOrdersTotalMs;
        private double _maintenanceEntriesTotalMs;
        private double _maintenanceTotalMs;
        private double _maintenanceMaximumTotalMs;
        private int _ticksUntilMaintenance;
        private DateTime _nextAtrTimerCheckUtc = DateTime.MinValue;
        private DateTime _nextEquityUiRefreshUtc = DateTime.MinValue;
        private bool _tradingEventsSubscribed;
        private bool _runtimeServicesStarted;

        #endregion

        #region Robot Lifecycle

        protected override void OnStart()
        {
            try
            {
                OnStartCore();
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException(
                    "OnStart",
                    exception,
                    CallbackFailurePolicy.StopBot);
            }
        }

        private void OnStartCore()
        {
            InitializeImmutableSymbolMetadata();
            InitializeMarketTimeZone();
            if (!TryInitializeConfiguration())
                return;
            TryInitializeConfigurationProvenance();

            LogNormal("TRADE_LIFECYCLE", "TRADE_LINEAGE_CONFIGURATION",
                "commentPersistenceEnabled={0} inMemoryTrackingEnabled={1} logLevel={2} accountType={3}",
                ShouldPersistTradeIdeaLineage, ShouldTrackTradeIdeaLineage, LogLevel,
                Account.AccountType);
            InitializeLabels();
            InitializeFitnessTracking();
            LoadCircuitBreakerState();
            LoadScaleUpState();
            if (!TryInitializeRollover())
                return;

            InitializeRuntimeUserInterface();
            InitializeDailyEquityTracking();
            SubscribeToTradingEvents();
            InitializeStartupRecoveryEvidence();
            RecoverLineageFromComments();
            BeginDeferredStartup();
        }

        private bool TryInitializeConfiguration()
        {
            string startupValidationError;
            if (TryValidateStartupConfiguration(out startupValidationError))
                return true;

            LogError("STARTUP", "CONFIGURATION_INVALID", "{0}", startupValidationError);
            Stop();
            return false;
        }

        private void InitializeLabels()
        {
            _mrLongLabel = BotLabel + "_MR_LONG";
            _mrShortLabel = BotLabel + "_MR_SHORT";
            _tfLongLabel = BotLabel + "_TF_LONG";
            _tfShortLabel = BotLabel + "_TF_SHORT";
        }

        private bool TryInitializeRollover()
        {
            if (!RunV2ModelSelfTests())
            {
                LogError("STARTUP", "V2_MODEL_SELF_TEST_FAILED",
                    "MEV state, regime, strength, confidence, or probability assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunV2PersistenceSerializationSelfTest())
            {
                LogError("STARTUP", "V2_PERSISTENCE_SELF_TEST_FAILED",
                    "reservation/journal serialization or integrity assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!ValidateV2RuntimeEnvironment())
            {
                Stop();
                return false;
            }

            if (!RunTradingModeSelfTests())
            {
                LogError("STARTUP", "TRADING_MODE_SELF_TEST_FAILED",
                    "legacy parameter values or effective mode mappings are invalid; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunPolicySamplingSelfTests())
            {
                LogError("STARTUP", "POLICY_SAMPLING_SELF_TEST_FAILED",
                    "Boss/Wife probability boundary assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunSonCurveSelfTests())
            {
                LogError("STARTUP", "SON_CURVE_SELF_TEST_FAILED",
                    "Son curve and reversal confirmation assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunTradeEvidenceSelfTests())
            {
                LogError("STARTUP", "TRADE_EVIDENCE_SELF_TEST_FAILED",
                    "trade evidence combination or capability assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunPersistentCapsuleSelfTests())
            {
                LogError("STARTUP", "PERSISTENT_CAPSULE_SELF_TEST_FAILED",
                    "broker-comment capsule round-trip or corruption assertions failed; the cBot will stop");
                Stop();
                return false;
            }

            if (!RunRolloverStateMachineSelfTests())
            {
                LogError("STARTUP", "ROLLOVER_SELF_TEST_FAILED",
                    "rollover state-machine invariant tests failed; the cBot will stop");
                Stop();
                return false;
            }

            RefreshRolloverWindowCache(Server.TimeInUtc);
            LogMarketSessionConfiguration(Server.TimeInUtc);
            LogMarketSessionState(Server.TimeInUtc);
            return true;
        }

        private void InitializeRuntimeUserInterface()
        {
            InitializeEquityStatistics();
            CreateEquityStatisticsTable();
            CreateStrategySettingsPanel();
            CreatePanelVisibilityButton();
        }

        private void SubscribeToTradingEvents()
        {
            if (_tradingEventsSubscribed)
            {
                LogVerbose("STARTUP", "EVENT_SUBSCRIPTIONS_REUSED",
                    "trading event subscriptions were already installed; duplicate subscription was skipped");
                return;
            }

            try
            {
                Positions.Opened += OnPositionOpened;
                Positions.Closed += OnPositionClosed;
                Positions.Modified += OnPositionModified;
                PendingOrders.Created += OnPendingOrderCreated;
                PendingOrders.Modified += OnPendingOrderModified;
                PendingOrders.Cancelled += OnPendingOrderCancelled;
                PendingOrders.Filled += OnPendingOrderFilled;
                _tradingEventsSubscribed = true;
            }
            catch
            {
                // Roll back every handler, including handlers installed before a partial
                // subscription failure, so a later startup attempt begins from a clean state.
                TryUnsubscribeFromTradingEvents();
                throw;
            }
        }

        private void TryUnsubscribeFromTradingEvents()
        {
            try { Positions.Opened -= OnPositionOpened; } catch { }
            try { Positions.Closed -= OnPositionClosed; } catch { }
            try { Positions.Modified -= OnPositionModified; } catch { }
            try { PendingOrders.Created -= OnPendingOrderCreated; } catch { }
            try { PendingOrders.Modified -= OnPendingOrderModified; } catch { }
            try { PendingOrders.Cancelled -= OnPendingOrderCancelled; } catch { }
            try { PendingOrders.Filled -= OnPendingOrderFilled; } catch { }
            _tradingEventsSubscribed = false;
        }

        private void UnsubscribeFromTradingEvents()
        {
            if (!_tradingEventsSubscribed)
                return;

            TryUnsubscribeFromTradingEvents();
        }

        private void RestoreRuntimePositionState()
        {
            CaptureBotPositions(_botPositionSnapshot);
            foreach (var position in _botPositionSnapshot)
                InitializePositionState(position);
        }

        private void StopRuntimeServices()
        {
            if (!_runtimeServicesStarted)
                return;

            Timer.Stop();
            _runtimeServicesStarted = false;
        }

        protected override void OnTick()
        {
            try
            {
                OnTickCore();
            }
            catch (Exception exception)
            {
                if (IsAutomateWaitTimeoutException(exception))
                {
                    HandleAssetConversionTimeout(exception);
                    return;
                }

                HandleUnexpectedCallbackException(
                    "OnTick",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
        }

        protected override void OnBar()
        {
            try
            {
                DrawReferencePriceLine();
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException(
                    "OnBar",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
        }

        private void OnTickCore()
        {
            DateTime utcNow = Server.TimeInUtc;
            RecordExecutableMarketTick(utcNow, Symbol.Bid, Symbol.Ask);
            RecordReferenceTick(utcNow, Symbol.Bid, Symbol.Ask);
            UpdateEquityStatisticsValues();
            SampleDailyEquity();

            if (!IsStartupOperational)
            {
                ProcessStartupExposureSafety();
                ProcessPreReadyRolloverTick();
                return;
            }

            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                UpdateDailyMevSignal(false);
            UpdateHourlyMevSignal(false);
            ExpirePendingPolicyDecisionIfRequired(utcNow, true);

            if (TryProcessRolloverTick())
                return;

            ProcessDeferredRolloverQuarterStops();

            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                utcNow >= _nextPolicyEntryEvaluationUtc)
            {
                _nextPolicyEntryEvaluationUtc = utcNow.Add(RealtimeEvaluationInterval);
                RunScheduledPolicyEntryEvaluation(CaptureTickSnapshot());
            }

            _ticksUntilMaintenance++;
            if (_ticksUntilMaintenance < CheckEveryNTicks)
                return;

            _ticksUntilMaintenance = 0;
            RunScheduledTradingMaintenance(CaptureTickSnapshot());
            CompleteAssetConversionTimeoutRecovery();
        }

        private bool TryProcessRolloverTick()
        {
            if (ProcessRolloverRecovery())
                return true;

            if (_rolloverContext.Phase == RolloverPhase.Restoring)
                ProcessDeferredRolloverQuarterStops();

            if (!ValidateRolloverRuntimeInvariants("OnTick"))
                return true;

            if (IsInsideRolloverWindow())
            {
                ProcessRolloverGuard();
                return true;
            }

            // Restoration remains tick-driven so it uses a fresh executable quote.
            // Never restore while any pre-rollover bot exposure is still live.
            if (_rolloverContext.Phase == RolloverPhase.Flattening)
            {
                if (HasLiveBotExposure())
                {
                    ProcessRolloverGuard();
                    LogActiveWarning("rollover-flattening-incomplete", "ROLLOVER", "FLATTENING_INCOMPLETE",
                        "live bot exposure remains after the rollover window; flattening will be retried before restoration");
                    return true;
                }

                ClearWarning("rollover-flattening-incomplete", "ROLLOVER", "FLATTENING_COMPLETE",
                    "all pre-rollover bot exposure has been removed; waiting for market-open restoration tick");
                EnterWaitingForMarketOpenPhase();
            }

            if (_rolloverContext.Phase == RolloverPhase.WaitingForMarketOpen ||
                _rolloverContext.Phase == RolloverPhase.Restoring)
            {
                if (!CanPerformRolloverRestoration)
                    return true;
                ReopenFromSnapshot();
                return true;
            }

            return _rolloverContext.Phase == RolloverPhase.RecoveryCleanup ||
                   _rolloverContext.Phase == RolloverPhase.RecoveryCooldown ||
                   _rolloverContext.Phase == RolloverPhase.FatalStop;
        }

        private TickSnapshot CaptureTickSnapshot()
        {
            // Capture one immutable market/time snapshot for a scheduled trading pass.
            DateTime utcNow = Server.TimeInUtc;
            return new TickSnapshot(
                Symbol.Bid,
                Symbol.Ask,
                utcNow,
                _utcHourlyVolatilityFactors[utcNow.Hour],
                _referenceHigh,
                _referenceLow);
        }

        private void RunScheduledTradingMaintenance(TickSnapshot tick)
        {
            long passId = ++_maintenancePassSequence;
            _activeMaintenancePassId = passId;
            bool timingEnabled = ShouldLog(BotLogLevel.Verbose);
            long startedAt = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

            try
            {
                // Price-sensitive calculations within this pass use the same quote,
                // UTC hour and hourly volatility factor.
                ManageOpenPositions(tick);
                long positionsCompletedAt = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

                TrailAllBotPendingOrders(tick);
                long pendingOrdersCompletedAt = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

                if (_effectiveTradingMode != TradingMode.ProbabilisticAuto)
                    EvaluateEntrySignals(tick);
                long entriesCompletedAt = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

                if (timingEnabled)
                {
                    double positionsMs = ElapsedMilliseconds(startedAt, positionsCompletedAt);
                    double pendingOrdersMs = ElapsedMilliseconds(positionsCompletedAt, pendingOrdersCompletedAt);
                    double entriesMs = ElapsedMilliseconds(pendingOrdersCompletedAt, entriesCompletedAt);
                    double totalMs = ElapsedMilliseconds(startedAt, entriesCompletedAt);
                    _maintenanceTimingPassCount++;
                    _maintenancePositionsTotalMs += positionsMs;
                    _maintenancePendingOrdersTotalMs += pendingOrdersMs;
                    _maintenanceEntriesTotalMs += entriesMs;
                    _maintenanceTotalMs += totalMs;
                    _maintenanceMaximumTotalMs = Math.Max(_maintenanceMaximumTotalMs, totalMs);
                }
            }
            finally
            {
                _activeMaintenancePassId = 0L;
            }
        }

        private void RunScheduledPolicyEntryEvaluation(TickSnapshot tick)
        {
            _activeMaintenancePassId = ++_maintenancePassSequence;
            try
            {
                EvaluateEntrySignals(tick);
            }
            finally
            {
                _activeMaintenancePassId = 0L;
            }
        }

        private static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        protected override void OnTimer()
        {
            try
            {
                OnTimerCore();
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException(
                    "OnTimer",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
        }

        private void OnTimerCore()
        {
            DateTime utcNow = Server.TimeInUtc;
            ExpirePendingPolicyDecisionIfRequired(utcNow, false);
            RunTelemetry("FlushPeriodicModificationTelemetry", FlushPeriodicModificationTelemetry);
            RunTelemetry("FlushVerboseSummariesIfDue", FlushVerboseSummariesIfDue);
            RunTelemetry("ProcessClosedPositionDealObservations", ProcessClosedPositionDealObservations);
            // Daily equity boundary detection must run before rollover/recovery early returns.
            ProcessDailyEquityTimerObservation();

            if (utcNow >= _nextEquityUiRefreshUtc)
            {
                _nextEquityUiRefreshUtc = utcNow.AddSeconds(EquityUiRefreshIntervalSeconds);
                RefreshRuntimePanelValues();
            }

            if (!IsStartupOperational)
            {
                ProcessStartupExposureSafety();
                ProcessPreReadyRolloverTick();
                ProcessDeferredStartup();
                if (!IsStartupOperational)
                    return;
            }

            // Recovery and rollover safety remain on every timer callback. They must
            // not depend on market ticks, especially while the market is closed.
            if (ProcessRolloverRecovery())
                return;

            if (_rolloverContext.Phase == RolloverPhase.Restoring)
                ProcessDeferredRolloverQuarterStops();

            if (!ValidateRolloverRuntimeInvariants("OnTimer"))
                return;

            if (IsInsideRolloverWindow())
                ProcessRolloverGuard();

            if (_rolloverContext.Phase == RolloverPhase.Normal)
            {
                ProcessPositionProtectionRecoveries();
                ProcessDeferredRolloverQuarterStops();
            }

            ProcessRuntimeHistoryRecovery(utcNow);

            if (utcNow >= _nextAtrTimerCheckUtc)
            {
                _nextAtrTimerCheckUtc = utcNow.AddSeconds(AtrTimerCheckIntervalSeconds);
                ProcessScheduledAtrUpdate();
            }

        }

        private void ProcessScheduledAtrUpdate()
        {
            // ATR retains its completed-hour guard. This cadence only avoids invoking
            // that guard on every rollover-safety timer callback.
            AtrUpdateOutcome atrOutcome = UpdateAtrAdjustedValues();
            if (atrOutcome == AtrUpdateOutcome.RetainedPreviousValues)
            {
                LogActiveWarning("atr-update-retained", "ATR", "RETAINED_PREVIOUS_VALUES",
                    "latest completed-hour ATR update was rejected; the previous complete ATR-adjusted configuration remains active");
            }
            else if (atrOutcome == AtrUpdateOutcome.Updated)
            {
                ClearWarning("atr-update-retained", "ATR", "UPDATE_RECOVERED",
                    "a later completed-hour ATR update was accepted");
            }
        }

        #endregion

        #region Shutdown

        protected override void OnStop()
        {
            ExecuteShutdownStep("ExpirePendingPolicyDecisionOnStop", () =>
            {
                if (_pendingPolicyDecision != null)
                {
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.Expired,
                        "botShutdown");
                }
            });
            ExecuteShutdownStep("EndClosingDealObservationsOnStop", EndClosingDealObservationsOnStop);
            ExecuteShutdownStep("LogIncompleteTradeIdeasOnStop", LogIncompleteTradeIdeasOnStop);
            ExecuteShutdownStep("FlushAllModificationTelemetry", FlushAllModificationTelemetry);
            ExecuteShutdownStep("FlushVerboseSummaries", () => FlushVerboseSummaries("shutdown"));
            ExecuteShutdownStep("LogDailyEquityPartialSummaryOnStop", LogDailyEquityPartialSummaryOnStop);
            ExecuteShutdownStep("LogMaintenanceSummary", LogMaintenanceSummary);
            ExecuteShutdownStep("LogTradeStatisticsSummary", LogTradeStatisticsSummary);
            ExecuteShutdownStep("LogPendingOrderModificationSummary", LogPendingOrderModificationSummary);
            ExecuteShutdownStep("LogSafeSeparationSuppressionSummary", LogSafeSeparationSuppressionSummary);
            ExecuteShutdownStep("LogProvisionalSafeDistanceSummary", LogProvisionalSafeDistanceSummary);
            ExecuteShutdownStep("LogActiveWarningSummary", LogActiveWarningSummary);
            ExecuteShutdownStep("StopRuntimeServices", StopRuntimeServices);
            ExecuteShutdownStep("UnsubscribeFromTradingEvents", UnsubscribeFromTradingEvents);
            ExecuteShutdownStep("RemovePanelVisibilityButton", RemovePanelVisibilityButton);
            ExecuteShutdownStep("RemoveStrategySettingsPanel", RemoveStrategySettingsPanel);
            ExecuteShutdownStep("RemoveEquityStatisticsTable", RemoveEquityStatisticsTable);
        }

        private void LogMaintenanceSummary()
        {
            if (!ShouldLog(BotLogLevel.Verbose))
                return;
            double divisor = _maintenanceTimingPassCount > 0
                ? _maintenanceTimingPassCount
                : 1.0;
            LogVerbose("PERFORMANCE", "MAINTENANCE_SUMMARY",
                "passes={0} avgPositionsMs={1:F3} avgPendingOrdersMs={2:F3} avgEntriesMs={3:F3} avgTotalMs={4:F3} maxTotalMs={5:F3}",
                _maintenanceTimingPassCount,
                _maintenancePositionsTotalMs / divisor,
                _maintenancePendingOrdersTotalMs / divisor,
                _maintenanceEntriesTotalMs / divisor,
                _maintenanceTotalMs / divisor,
                _maintenanceMaximumTotalMs);
        }
        private void ExecuteShutdownStep(string stepName, Action shutdownStep)
        {
            try
            {
                shutdownStep();
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException(
                    "OnStop/" + stepName,
                    exception,
                    CallbackFailurePolicy.Continue);
            }
        }

        #endregion
    }
}
