using System;
using System.Diagnostics;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private enum StartupPhase
        {
            NotStarted,
            AcquiringBarSeries,
            LoadingHourlyHistory,
            Settling,
            InitializingIndicators,
            InitializingStrategy,
            Operational,
            Failed
        }

        private const int StartupMinHistoryAttempts = 3;
        private static readonly TimeSpan StartupHistoryTimeout = TimeSpan.FromSeconds(60.0);
        private static readonly TimeSpan StartupHistoryRetry = TimeSpan.FromSeconds(30.0);

        private readonly Stopwatch _startupStopwatch = new Stopwatch();
        private StartupPhase _startupPhase = StartupPhase.NotStarted;
        private bool _atrConfigurationReady;
        private bool _hourlyMevReady;
        private bool _strategyInitializationInProgress;
        private bool _startupStopRequested;
        private bool _startupSummaryLogged;
        private bool _positionStateRestored;
        private int _hourlyLoadAttempts;
        private int _dailyLoadAttempts;
        private int _settleCallbacks;
        private DateTime _nextHourlyHistoryRetryUtc = DateTime.MinValue;
        private bool IsStartupOperational
        {
            get { return _startupPhase == StartupPhase.Operational; }
        }

        private bool CanPerformRolloverRestoration
        {
            get
            {
                return IsStartupOperational &&
                       _atrConfigurationReady &&
                       !_startupStopRequested &&
                       IsTradingSessionOpen(Server.TimeInUtc) &&
                       HasExecutableMarketQuote(Server.TimeInUtc);
            }
        }

        private bool CanPlaceNewStrategyEntries
        {
            get
            {
                return CanPerformRolloverRestoration &&
                       _referenceRangeReady &&
                       _rolloverContext.Phase == RolloverPhase.Normal &&
                       !_runtimeFaulted &&
                       !_strategyInitializationInProgress;
            }
        }
        private int RequiredHourlyBarsForAtr
        {
            get { return Math.Max(AtrFastPeriod, AtrSlowPeriod) + 2; }
        }

        private int RequiredHourlyBarsForStartup
        {
            get
            {
                return Math.Max(
                    RequiredHourlyBarsForAtr,
                    RequiredHourlyBarsForMevReadiness);
            }
        }

        private int RequiredHourlyBarsForMevReadiness
        {
            get
            {
                return RequiredHourlyMevHistoryBars + 1;
            }
        }

        private void BeginDeferredStartup()
        {
            if (_runtimeServicesStarted)
                return;

            _atrConfigurationReady = false;
            _hourlyMevReady = false;
            _startupStopRequested = false;
            _startupSummaryLogged = false;
            _positionStateRestored = false;
            _hourlyLoadAttempts = 0;
            _dailyLoadAttempts = 0;

            SetStartupPhase(StartupPhase.AcquiringBarSeries, "base services initialized");
            _nextEquityUiRefreshUtc = Server.TimeInUtc.AddSeconds(EquityUiRefreshIntervalSeconds);
            Timer.Start(TimeSpan.FromSeconds(SafetyTimerSeconds));
            _runtimeServicesStarted = true;
            LogNormal("STARTUP", "DEFERRED_INITIALIZATION_STARTED", "utc={0:O}", Server.TimeInUtc);
        }

        private void SetStartupPhase(StartupPhase next, string reason)
        {
            if (_startupStopRequested ||
                _startupPhase == StartupPhase.Failed ||
                _startupPhase == next)
            {
                return;
            }

            StartupPhase previous = _startupPhase;
            _startupPhase = next;
            _startupStopwatch.Restart();
            LogNormal("STARTUP", "PHASE_CHANGED",
                "previous={0} current={1} reason={2}", previous, next, reason);
        }

        private void ProcessDeferredStartup()
        {
            if (_startupStopRequested ||
                IsStartupOperational ||
                _startupPhase == StartupPhase.Failed)
            {
                return;
            }

            switch (_startupPhase)
            {
                case StartupPhase.AcquiringBarSeries:
                    AcquireStartupBars();
                    break;
                case StartupPhase.LoadingHourlyHistory:
                    LoadHourlyBatch();
                    break;
                case StartupPhase.Settling:
                    if (_settleCallbacks-- <= 0)
                    {
                        SetStartupPhase(
                            StartupPhase.InitializingIndicators,
                            "callback boundary completed");
                    }
                    break;
                case StartupPhase.InitializingIndicators:
                    InitializeStartupIndicators();
                    break;
                case StartupPhase.InitializingStrategy:
                    InitializeStartupStrategy();
                    break;
                default:
                    FailDeferredStartup("invalid phase " + _startupPhase, null);
                    break;
            }
        }

        private void AcquireStartupBars()
        {
            LogNormal("STARTUP", "BAR_SERIES_ACQUIRE_ENTER", "symbol={0}", Symbol.Name);
            _hourlyBars = MarketData.GetBars(TimeFrame.Hour, Symbol.Name);
            _dailyBars = MarketData.GetBars(TimeFrame.Daily, Symbol.Name);
            InitializeReferenceTickHistory();
            if (_hourlyBars == null ||
                (_effectiveTradingMode == TradingMode.ProbabilisticAuto && _dailyBars == null))
            {
                FailDeferredStartup("hourly or daily bar series unavailable", null);
                return;
            }

            LogNormal("STARTUP", "BAR_SERIES_ACQUIRE_EXIT",
                "hourly={0} daily={1}", _hourlyBars.Count,
                _dailyBars == null ? 0 : _dailyBars.Count);
            SetStartupPhase(
                StartupPhase.LoadingHourlyHistory,
                "hourly/daily MEV and ATR history required");
        }

        private void LoadHourlyBatch()
        {
            bool hourlyReady = _hourlyBars.Count >= RequiredHourlyBarsForStartup;
            bool dailyReady = _effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                (_dailyBars != null &&
                 _dailyBars.Count >= RequiredDailyMevHistoryBars + 1);
            if (hourlyReady && dailyReady)
            {
                BeginSettle("hourly and daily MEV history targets reached");
                return;
            }

            if (!hourlyReady)
            {
                _hourlyLoadAttempts++;
                int previousCount = _hourlyBars.Count;
                Stopwatch loadStopwatch = Stopwatch.StartNew();
                int loaded = _hourlyBars.LoadMoreHistory();
                loadStopwatch.Stop();
                LogNormal("STARTUP", "HOURLY_HISTORY_LOAD",
                    "attempt={0} loaded={1} count={2} required={3} durationMs={4:F1}",
                    _hourlyLoadAttempts, loaded, _hourlyBars.Count,
                    RequiredHourlyBarsForStartup, loadStopwatch.Elapsed.TotalMilliseconds);
                hourlyReady = _hourlyBars.Count >= RequiredHourlyBarsForStartup;
            }

            if (!dailyReady && _dailyBars != null)
            {
                _dailyLoadAttempts++;
                Stopwatch loadStopwatch = Stopwatch.StartNew();
                int loaded = _dailyBars.LoadMoreHistory();
                loadStopwatch.Stop();
                LogNormal("STARTUP", "DAILY_HISTORY_LOAD",
                    "attempt={0} loaded={1} count={2} required={3} durationMs={4:F1}",
                    _dailyLoadAttempts, loaded, _dailyBars.Count,
                    RequiredDailyMevHistoryBars + 1,
                    loadStopwatch.Elapsed.TotalMilliseconds);
                dailyReady =
                    _dailyBars.Count >= RequiredDailyMevHistoryBars + 1;
            }

            if (hourlyReady && dailyReady)
            {
                BeginSettle("hourly and daily MEV history targets reached");
                return;
            }

            if (_hourlyLoadAttempts + _dailyLoadAttempts >= StartupMinHistoryAttempts &&
                _startupStopwatch.Elapsed >= StartupHistoryTimeout)
            {
                _nextHourlyHistoryRetryUtc = Server.TimeInUtc.Add(StartupHistoryRetry);
                BeginSettle("MEV history degraded; hourly and daily readiness evaluated separately");
            }
        }

        private void BeginSettle(string reason)
        {
            _settleCallbacks = 1;
            SetStartupPhase(StartupPhase.Settling, reason);
        }

        private void InitializeStartupIndicators()
        {
            if ((_fastHourlyAtr == null) != (_slowHourlyAtr == null))
            {
                FailDeferredStartup("partial ATR indicator state", null);
                return;
            }

            if (_fastHourlyAtr == null)
            {
                _fastHourlyAtr = Indicators.AverageTrueRange(
                    _hourlyBars, AtrFastPeriod, MovingAverageType.Exponential);
                _slowHourlyAtr = Indicators.AverageTrueRange(
                    _hourlyBars, AtrSlowPeriod, MovingAverageType.Exponential);
            }

            if (_fastHourlyAtr == null || _slowHourlyAtr == null)
            {
                FailDeferredStartup("ATR indicator creation failed", null);
                return;
            }

            SetStartupPhase(StartupPhase.InitializingStrategy, "indicators ready");
        }

        private void InitializeStartupStrategy()
        {
            if (_strategyInitializationInProgress)
                return;

            _strategyInitializationInProgress = true;
            try
            {
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                    UpdateDailyMevSignal(true);
                UpdateHourlyMevSignal(true);
                _hourlyMevReady =
                    _hasValidHourlyMev &&
                    (_effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                     _hasValidDailyMev);

                AtrUpdateOutcome atrOutcome = UpdateAtrAdjustedValues(true);
                if (atrOutcome == AtrUpdateOutcome.Fatal ||
                    atrOutcome == AtrUpdateOutcome.RetainedPreviousValues)
                {
                    FailDeferredStartup("ATR-adjusted configuration invalid", null);
                    return;
                }

                _atrConfigurationReady = true;
                if (!_positionStateRestored)
                {
                    RestoreRuntimePositionState();
                    _positionStateRestored = true;
                }

                SetStartupPhase(StartupPhase.Operational, "strategy ready");
                _nextAtrTimerCheckUtc = Server.TimeInUtc.AddSeconds(AtrTimerCheckIntervalSeconds);
                if (!_startupSummaryLogged)
                {
                    _startupSummaryLogged = true;
                    LogStartupSummary(
                        _hourlyBars.Count >= RequiredHourlyBarsForAtr);
                }

                LogNormal("STARTUP", "DEFERRED_INITIALIZATION_COMPLETED",
                    "hourlyAttempts={0} atrReady={1} mevReady={2}",
                    _hourlyLoadAttempts,
                    _atrConfigurationReady, _hourlyMevReady);
            }
            finally
            {
                _strategyInitializationInProgress = false;
            }
        }

        private void ProcessRuntimeHistoryRecovery(DateTime now)
        {
            ProcessReferenceHistoryRecovery(now);

            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                _dailyBars == null)
            {
                _dailyBars = MarketData.GetBars(TimeFrame.Daily, Symbol.Name);
            }

            bool hourlyHistoryNeeded =
                _hourlyBars.Count < RequiredHourlyBarsForStartup ||
                !_hourlyMevReady;
            bool dailyHistoryNeeded = _effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                (_dailyBars == null ||
                 _dailyBars.Count < RequiredDailyMevHistoryBars + 1 ||
                 !_hasValidDailyMev);

            if ((hourlyHistoryNeeded || dailyHistoryNeeded) &&
                now >= _nextHourlyHistoryRetryUtc)
            {
                _nextHourlyHistoryRetryUtc = now.Add(StartupHistoryRetry);
                bool hadAtrHistory = _hourlyBars.Count >= RequiredHourlyBarsForAtr;
                if (_hourlyBars.Count < RequiredHourlyBarsForStartup)
                {
                    Stopwatch loadStopwatch = Stopwatch.StartNew();
                    int loaded = _hourlyBars.LoadMoreHistory();
                    loadStopwatch.Stop();
                    LogNormal("STARTUP", "RUNTIME_HISTORY_LOAD",
                        "loaded={0} count={1} required={2} durationMs={3:F1}",
                        loaded, _hourlyBars.Count, RequiredHourlyBarsForStartup,
                        loadStopwatch.Elapsed.TotalMilliseconds);
                }

                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                    _dailyBars != null &&
                    _dailyBars.Count < RequiredDailyMevHistoryBars + 1)
                {
                    Stopwatch loadStopwatch = Stopwatch.StartNew();
                    int loaded = _dailyBars.LoadMoreHistory();
                    loadStopwatch.Stop();
                    LogNormal("STARTUP", "RUNTIME_DAILY_HISTORY_LOAD",
                        "loaded={0} count={1} required={2} durationMs={3:F1}",
                        loaded, _dailyBars.Count,
                        RequiredDailyMevHistoryBars + 1,
                        loadStopwatch.Elapsed.TotalMilliseconds);
                }

                if (!hadAtrHistory && _hourlyBars.Count >= RequiredHourlyBarsForAtr)
                    UpdateAtrAdjustedValues(true);

                if (!_hourlyMevReady &&
                    _hourlyBars.Count >= RequiredHourlyBarsForMevReadiness)
                {
                    if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                        UpdateDailyMevSignal(true);
                    UpdateHourlyMevSignal(true);
                    _hourlyMevReady =
                        _hasValidHourlyMev &&
                        (_effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                         _hasValidDailyMev);
                }

                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                    _dailyBars != null &&
                    _dailyBars.Count >= RequiredDailyMevHistoryBars + 1)
                {
                    UpdateDailyMevSignal(true);
                    if (_hourlyBars.Count >= RequiredHourlyBarsForMevReadiness)
                        UpdateHourlyMevSignal(true);
                }
            }
        }

        private void ProcessPreReadyRolloverTick()
        {
            if (ProcessRolloverRecovery())
                return;

            if (!ValidateRolloverRuntimeInvariants("OnTick/Startup"))
                return;

            if (IsInsideRolloverWindow() ||
                _rolloverContext.Phase == RolloverPhase.Flattening)
            {
                ProcessRolloverGuard();
            }
        }
        private void ProcessStartupExposureSafety()
        {
            CaptureBotPositions(_botPositionSnapshot);
            foreach (Position position in _botPositionSnapshot)
            {
                if (!position.StopLoss.HasValue || !position.TakeProfit.HasValue)
                {
                    ClosePositionWithoutProtection(
                        position,
                        "startup exposure is missing live stop-loss or take-profit protection");
                }
            }
        }
        private void FailDeferredStartup(string reason, Exception ex)
        {
            if (_startupStopRequested)
                return;

            StartupPhase failedPhase = _startupPhase;
            _startupPhase = StartupPhase.Failed;
            _startupStopRequested = true;
            LogError("STARTUP", "DEFERRED_INITIALIZATION_FAILED",
                "phase={0} reason={1} type={2} message={3}",
                failedPhase,
                reason,
                ex == null ? "none" : ex.GetType().FullName,
                ex == null ? "none" : ex.Message);
            Stop();
        }
    }
}
