using System;
using System.Globalization;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Runtime strategy settings controls and chart panel coordination.
    public partial class ShootingStar
    {

        #region Strategy Settings State

        private Grid _strategySettingsPanel;
        private Grid _manualStrategySelectionGrid;
        private ComboBox _tradingModeComboBox;
        private readonly CheckBox[,] _manualRankBandStrategyCheckBoxes = new CheckBox[3, 4];
        private TextBlock _strategySettingsStatusText;
        private TextBlock _activeStrategyText;
        private Button _applyStrategySettingsButton;
        private ComboBox _manualEntryDirectionComboBox;
        private Button _executeManualEntryButton;
        private TextBlock _manualEntryStatusText;
        private Button _panelVisibilityButton;
        private bool _telemetryPanelsVisible = true;
        private bool _strategySettingsCommitInProgress;
        private bool _manualRankBandSelectionUpdateInProgress;
        private bool _userExecutionInProgress;
        private DateTime _lastUserExecutionUtc = DateTime.MinValue;
        private bool _runtimeFaulted;

        #endregion

        private void RefreshRuntimePanelValues()
        {
            try
            {
                RefreshEquityStatisticsTable();
                RefreshHourlyMevDisplay();
                RefreshDailyPolicySnapshotDisplay();
                RefreshReferenceRangeDisplay();

                if (_activeStrategyText != null)
                {
                    _activeStrategyText.Text = _hasValidHourlyMev
                        ? GetStrategySelectionDisplay()
                        : "Unavailable";
                }
            }
            catch (Exception exception)
            {
                HandlePresentationException("RefreshRuntimePanelValues", exception);
            }
        }

        private void HandlePresentationException(string callback, Exception exception)
        {
            LogError("UI", "PRESENTATION_CALLBACK_FAILED",
                "callback={0} exceptionType={1} message={2}",
                callback, exception.GetType().Name, exception.Message);
        }

        #region Strategy Settings Panel

        private void CreateStrategySettingsPanel()
        {
            RemoveStrategySettingsPanel();
            _strategySettingsPanel = new Grid(13, 4)
            {
                BackgroundColor = Color.Black,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 280,
                Height = 560,
                Margin = 12
            };

            _strategySettingsPanel.AddChild(new TextBlock
            {
                Text = "Strategy Settings",
                ForegroundColor = Color.White,
                FontWeight = FontWeight.Bold,
                Margin = 5
            }, 0, 0, 1, 4);

            AddStrategyPanelLabel("Trading mode", 1);
            _tradingModeComboBox = new ComboBox
            {
                Width = 190,
                Height = 24,
                Margin = 1
            };
            _tradingModeComboBox.AddItem(GetTradingModeDisplayName(TradingMode.ProbabilisticAuto));
            _tradingModeComboBox.AddItem(GetTradingModeDisplayName(TradingMode.RuleMappedAuto));
            _tradingModeComboBox.AddItem(GetTradingModeDisplayName(TradingMode.UserTriggered));
            _tradingModeComboBox.SelectedItem = GetTradingModeDisplayName(_effectiveTradingMode);
            _strategySettingsPanel.AddChild(_tradingModeComboBox, 1, 1, 1, 3);

            AddStrategyPanelLabel("Active strategy", 2);
            _activeStrategyText = new TextBlock { ForegroundColor = Color.White, Margin = 5 };
            _strategySettingsPanel.AddChild(_activeStrategyText, 2, 1, 1, 3);

            CreateManualRankBandSelectionGrid();
            _strategySettingsPanel.AddChild(_manualStrategySelectionGrid, 3, 0, 5, 4);

            _applyStrategySettingsButton = new Button
            {
                Text = "Apply",
                Width = 90,
                Height = 24,
                Margin = 1
            };
            _applyStrategySettingsButton.Click += OnApplyStrategySettingsClicked;
            _strategySettingsPanel.AddChild(_applyStrategySettingsButton, 8, 0, 1, 4);

            _strategySettingsStatusText = new TextBlock
            {
                ForegroundColor = Color.LightGray,
                TextWrapping = TextWrapping.Wrap,
                Width = 240,
                Margin = 5
            };
            _strategySettingsPanel.AddChild(_strategySettingsStatusText, 9, 0, 1, 4);

            _manualEntryDirectionComboBox = new ComboBox
            {
                Width = 110,
                Height = 25,
                Margin = 1
            };
            _manualEntryDirectionComboBox.AddItem("Long");
            _manualEntryDirectionComboBox.AddItem("Short");
            _manualEntryDirectionComboBox.SelectedItem = "Long";
            _strategySettingsPanel.AddChild(_manualEntryDirectionComboBox, 10, 0, 1, 2);

            _executeManualEntryButton = new Button
            {
                Text = "Execute",
                Width = 90,
                Height = 24,
                Margin = 1
            };
            _executeManualEntryButton.Click += OnExecuteManualEntryClicked;
            _strategySettingsPanel.AddChild(_executeManualEntryButton, 10, 2, 1, 2);

            _manualEntryStatusText = new TextBlock
            {
                ForegroundColor = Color.LightGray,
                TextWrapping = TextWrapping.Wrap,
                Width = 240,
                Margin = 5
            };
            _strategySettingsPanel.AddChild(_manualEntryStatusText, 11, 0, 1, 4);
            Chart.AddControl(_strategySettingsPanel);
            RefreshStrategySettingsPanel("Startup parameters are active.", Color.LightGray);
            UpdateStrategyDependentControls();
            SetManualEntryStatus("Select Long or Short, then execute.", Color.LightGray);
        }

        private void CreatePanelVisibilityButton()
        {
            RemovePanelVisibilityButton();
            _panelVisibilityButton = new Button
            {
                Text = "Hide panels",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = 10
            };
            _panelVisibilityButton.Click += OnPanelVisibilityButtonClicked;
            Chart.AddControl(_panelVisibilityButton);
            SetTelemetryPanelsVisible(_telemetryPanelsVisible);
        }

        private void OnPanelVisibilityButtonClicked(ButtonClickEventArgs args)
        {
            try
            {
                SetTelemetryPanelsVisible(!_telemetryPanelsVisible);
            }
            catch (Exception exception)
            {
                HandlePresentationException("OnPanelVisibilityButtonClicked", exception);
            }
        }

        private void SetTelemetryPanelsVisible(bool isVisible)
        {
            _telemetryPanelsVisible = isVisible;
            if (_equityStatisticsGrid != null)
                _equityStatisticsGrid.IsVisible = isVisible;
            if (_strategySettingsPanel != null)
                _strategySettingsPanel.IsVisible = isVisible;
            if (_panelVisibilityButton != null)
                _panelVisibilityButton.Text = isVisible ? "Hide panels" : "Show panels";
        }

        private void RemovePanelVisibilityButton()
        {
            if (_panelVisibilityButton == null)
                return;

            _panelVisibilityButton.Click -= OnPanelVisibilityButtonClicked;
            Chart.RemoveControl(_panelVisibilityButton);
            _panelVisibilityButton = null;
        }

        private void CreateManualRankBandSelectionGrid()
        {
            _manualStrategySelectionGrid = new Grid(5, 6)
            {
                Width = 230,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _manualStrategySelectionGrid.AddChild(new TextBlock
            {
                Text = "MEV range",
                ForegroundColor = Color.LightGray,
                Margin = 3
            }, 0, 0, 2, 2);
            _manualStrategySelectionGrid.AddChild(new TextBlock
            {
                Text = "Long strategy",
                ForegroundColor = Color.LightGray,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = 3
            }, 0, 2, 1, 2);
            _manualStrategySelectionGrid.AddChild(new TextBlock
            {
                Text = "Short strategy",
                ForegroundColor = Color.LightGray,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = 3
            }, 0, 4, 1, 2);

            AddManualRankBandHeader("MR", 2);
            AddManualRankBandHeader("TF", 3);
            AddManualRankBandHeader("MR", 4);
            AddManualRankBandHeader("TF", 5);

            for (int band = 0; band < 3; band++)
            {
                int row = band + 2;
                _manualStrategySelectionGrid.AddChild(new TextBlock
                {
                    Text = GetManualRankBandLabel((ManualRankBand)band),
                    ForegroundColor = Color.LightGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = 3
                }, row, 0, 1, 2);

                for (int option = 0; option < 4; option++)
                {
                    var checkBox = new CheckBox
                    {
                        Margin = 3,
                        IsChecked = option < 2
                            ? _effectiveManualLongStrategies[band] ==
                                (option == 0 ? ManualSideStrategy.MeanReversion : ManualSideStrategy.TrendFollowing)
                            : _effectiveManualShortStrategies[band] ==
                                (option == 2 ? ManualSideStrategy.MeanReversion : ManualSideStrategy.TrendFollowing)
                    };
                    checkBox.Click += OnManualRankBandSelectionChanged;
                    _manualRankBandStrategyCheckBoxes[band, option] = checkBox;
                    _manualStrategySelectionGrid.AddChild(checkBox, row, option + 2);
                }
            }

            UpdateManualRankBandControlsEnabled();
        }

        private void AddManualRankBandHeader(string text, int column)
        {
            _manualStrategySelectionGrid.AddChild(new TextBlock
            {
                Text = text,
                ForegroundColor = Color.LightGray,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = 3
            }, 1, column);
        }

        private string GetManualRankBandLabel(ManualRankBand band)
        {
            switch (band)
            {
                case ManualRankBand.Lower:
                    return string.Format(CultureInfo.InvariantCulture, "MEV <= {0:F2}", MevLowerBoundary);
                case ManualRankBand.Middle:
                    return string.Format(CultureInfo.InvariantCulture, "{0:F2} < MEV <= {1:F2}",
                        MevLowerBoundary, MevUpperBoundary);
                default:
                    return string.Format(CultureInfo.InvariantCulture, "MEV > {0:F2}", MevUpperBoundary);
            }
        }

        private void AddStrategyPanelLabel(string text, int row)
        {
            _strategySettingsPanel.AddChild(new TextBlock
            {
                Text = text,
                ForegroundColor = Color.LightGray,
                Margin = 5
            }, row, 0);
        }

        private void UpdateStrategyDependentControls()
        {
            TradingMode selectedMode = _effectiveTradingMode;
            if (_tradingModeComboBox != null)
                TryParseTradingModeDisplayName(
                    _tradingModeComboBox.SelectedItem,
                    out selectedMode);
            bool mappingModeSelected =
                selectedMode != TradingMode.ProbabilisticAuto;
            if (_manualStrategySelectionGrid != null)
                _manualStrategySelectionGrid.IsVisible = mappingModeSelected;
            UpdateManualRankBandControlsEnabled();
        }

        private void OnManualRankBandSelectionChanged(CheckBoxEventArgs args)
        {
            if (_manualRankBandSelectionUpdateInProgress)
                return;
            try
            {
                _manualRankBandSelectionUpdateInProgress = true;
                for (int band = 0; band < 3; band++)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        CheckBox mr = _manualRankBandStrategyCheckBoxes[band, side * 2];
                        CheckBox tf = _manualRankBandStrategyCheckBoxes[band, side * 2 + 1];
                        if (mr == null || tf == null)
                            continue;
                        if (ReferenceEquals(args.CheckBox, mr))
                        {
                            mr.IsChecked = true;
                            tf.IsChecked = false;
                        }
                        else if (ReferenceEquals(args.CheckBox, tf))
                        {
                            tf.IsChecked = true;
                            mr.IsChecked = false;
                        }
                    }
                }
                UpdateManualRankBandControlsEnabled();
            }
            catch (Exception exception)
            {
                HandlePresentationException("OnManualRankBandSelectionChanged", exception);
            }
            finally
            {
                _manualRankBandSelectionUpdateInProgress = false;
            }
        }

        private void UpdateManualRankBandControlsEnabled()
        {
            TradingMode selectedMode = _effectiveTradingMode;
            if (_tradingModeComboBox != null &&
                !TryParseTradingModeDisplayName(
                    _tradingModeComboBox.SelectedItem,
                    out selectedMode))
                selectedMode = TradingMode.ProbabilisticAuto;
            bool mappingModeSelected =
                selectedMode != TradingMode.ProbabilisticAuto;
            for (int band = 0; band < 3; band++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int mrIndex = side * 2;
                    int tfIndex = mrIndex + 1;
                    CheckBox mr = _manualRankBandStrategyCheckBoxes[band, mrIndex];
                    CheckBox tf = _manualRankBandStrategyCheckBoxes[band, tfIndex];
                    if (mr == null || tf == null)
                        continue;

                    mr.IsEnabled = mappingModeSelected;
                    tf.IsEnabled = mappingModeSelected;
                }
            }
        }

        private void OnApplyStrategySettingsClicked(ButtonClickEventArgs args)
        {
            try
            {
                CommitStrategySettingsAndBeginTransition();
            }
            catch (Exception exception)
            {
                SetStrategySettingsStatus(
                    "The settings were not committed. See the log for details.",
                    Color.Red);
                HandleUnexpectedCallbackException(
                    "OnApplyStrategySettingsClicked",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
        }

        private void OnExecuteManualEntryClicked(ButtonClickEventArgs args)
        {
            if (_userExecutionInProgress)
            {
                SetManualEntryStatus("An execution request is already in progress.", Color.Orange);
                return;
            }

            DateTime requestUtc = Server.TimeInUtc;
            if (_lastUserExecutionUtc != DateTime.MinValue &&
                requestUtc - _lastUserExecutionUtc < TimeSpan.FromSeconds(2))
            {
                SetManualEntryStatus("Please wait before submitting another execution request.", Color.Orange);
                return;
            }

            _userExecutionInProgress = true;
            _lastUserExecutionUtc = requestUtc;
            if (_executeManualEntryButton != null)
                _executeManualEntryButton.IsEnabled = false;
            try
            {
                if (_manualEntryDirectionComboBox == null)
                {
                    SetManualEntryStatus("Order not submitted: the direction control is unavailable.", Color.Red);
                    return;
                }

                TradeType tradeType;
                if (string.Equals(_manualEntryDirectionComboBox.SelectedItem, "Long", StringComparison.Ordinal))
                    tradeType = TradeType.Buy;
                else if (string.Equals(_manualEntryDirectionComboBox.SelectedItem, "Short", StringComparison.Ordinal))
                    tradeType = TradeType.Sell;
                else
                {
                    SetManualEntryStatus("Order not submitted: select Long or Short.", Color.Red);
                    return;
                }

                string status;
                StrategyKind strategy;
                bool submitted = TryPlaceManualStrategyLimit(
                    tradeType,
                    CaptureTickSnapshot(),
                    out status,
                    out strategy);
                SetManualEntryStatus(
                    status,
                    submitted ? Color.LightGreen : Color.Red);
                LogNormal("ENTRY", "USER_TRIGGERED_EXECUTION",
                    "direction={0} strategy={1} accepted={2} failureCode={3} activeMode={4}",
                    tradeType,
                    strategy,
                    submitted,
                    submitted ? "none" : "executionRejected",
                    GetTradingModeDisplayName(_effectiveTradingMode));
            }
            catch (Exception exception)
            {
                SetManualEntryStatus("Order not submitted due to an unexpected error. See the log.", Color.Red);
                LogError("ENTRY", "USER_TRIGGERED_EXECUTION",
                    "accepted=false failureCode=unexpectedException exception={0}",
                    exception.GetType().Name);
                HandleUnexpectedCallbackException(
                    "OnExecuteManualEntryClicked",
                    exception,
                    CallbackFailurePolicy.Continue);
            }
            finally
            {
                _userExecutionInProgress = false;
                if (_executeManualEntryButton != null)
                    _executeManualEntryButton.IsEnabled = true;
            }
        }

        private void SetManualEntryStatus(string text, Color color)
        {
            if (_manualEntryStatusText == null)
                return;
            _manualEntryStatusText.Text = text;
            _manualEntryStatusText.ForegroundColor = color;
        }

        private void CommitStrategySettingsAndBeginTransition()
        {
            if (_strategySettingsCommitInProgress)
            {
                SetStrategySettingsStatus(
                    "A strategy-settings commit is already in progress.",
                    Color.Orange);
                return;
            }

            if (_runtimeFaulted)
            {
                SetStrategySettingsStatus(
                    "Runtime trading is faulted. Strategy settings cannot be changed during this run.",
                    Color.Red);
                return;
            }

            if (_rolloverContext.Phase != RolloverPhase.Normal)
            {
                SetStrategySettingsStatus(
                    "Strategy settings cannot be changed while rollover recovery is active.",
                    Color.Orange);
                return;
            }

            TradingMode mode;
            ManualSideStrategy[] longStrategies;
            ManualSideStrategy[] shortStrategies;
            string error;
            if (!TryReadStrategySettings(
                    out mode,
                    out longStrategies,
                    out shortStrategies,
                    out error))
            {
                SetStrategySettingsStatus(error, Color.Red);
                return;
            }

            bool settingsChanged = mode != _effectiveTradingMode ||
                !StrategySelectionsEqual(longStrategies, _effectiveManualLongStrategies) ||
                !StrategySelectionsEqual(shortStrategies, _effectiveManualShortStrategies);

            if (!settingsChanged)
            {
                RefreshStrategySettingsPanel(
                    "No runtime strategy changes were required.",
                    Color.LightGreen);
                return;
            }

            _strategySettingsCommitInProgress = true;
            try
            {
                if (mode != TradingMode.ProbabilisticAuto &&
                    _pendingPolicyDecision != null)
                {
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.Expired,
                        "tradingModeChanged");
                }

                TradingMode previousMode = _effectiveTradingMode;
                _effectiveTradingMode = mode;
                Array.Copy(longStrategies, _effectiveManualLongStrategies, 3);
                Array.Copy(shortStrategies, _effectiveManualShortStrategies, 3);
                CommitConfigurationProvenanceChange(
                    "tradingMode,lowerMiddleUpperLongStrategy,lowerMiddleUpperShortStrategy");
                LogNormal("REGIME", "TRADING_MODE_CHANGED",
                    "oldMode={0} newMode={1} commitUtc={2:O} existingPendingOrders={3} existingPositions={4} existingObjectsUnchanged=true",
                    GetTradingModeDisplayName(previousMode),
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    Server.TimeInUtc,
                    PendingOrders.Where(IsBotOrder).Count(),
                    Positions.Where(IsBotPosition).Count());
                LogNormal("REGIME", "PANEL_SETTINGS_COMMITTED",
                    "mode={0} manualLongStrategies={1} manualShortStrategies={2}; appliesToNewPendingOrdersOnly=true",
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    string.Join(",", _effectiveManualLongStrategies),
                    string.Join(",", _effectiveManualShortStrategies));

                try
                {
                    if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                        UpdateDailyMevSignal(true);
                    UpdateHourlyMevSignal(true);
                    _hourlyMevReady =
                        _hasValidHourlyMev &&
                        (_effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                         _hasValidDailyMev);
                }
                catch (Exception exception)
                {
                    _hourlyMevReady = false;
                    _hasValidHourlyMev = false;
                    _currentHourlyMev = double.NaN;
                    LogActiveWarning("hourly-mev", "REGIME", "MEV_REFRESH_FAILED",
                        "runtime settings are committed, but new entries remain blocked until MEV is recalculated. Error: {0}",
                        exception.Message);
                    LogError("REGIME", "MEV_REFRESH_EXCEPTION",
                        "runtime settings remain committed; existing orders and positions were not changed. {0}: {1}",
                        exception.GetType().Name,
                        exception.Message);
                    RefreshStrategySettingsPanel(
                        "Settings committed. New entries are blocked until MEV data is available; existing orders and positions are unchanged.",
                        Color.Red);
                    return;
                }

                if (!_hasValidHourlyMev)
                {
                    RefreshStrategySettingsPanel(
                        "Settings committed. New entries are blocked until MEV data is available; existing orders and positions are unchanged.",
                        Color.Red);
                    return;
                }

                RefreshStrategySettingsPanel(
                    "Settings committed for new pending orders. Existing pending orders and open positions are unchanged.",
                    Color.LightGreen);

                LogNormal("REGIME", "PANEL_SETTINGS_APPLIED",
                    "mode={0} hourlyMevAvailable={1} existingPendingOrdersAndPositionsUnchanged=true",
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    _hasValidHourlyMev);
            }
            finally
            {
                _strategySettingsCommitInProgress = false;
            }
        }

        private bool TryReadStrategySettings(
            out TradingMode mode,
            out ManualSideStrategy[] longStrategies,
            out ManualSideStrategy[] shortStrategies,
            out string error)
        {
            mode = _effectiveTradingMode;
            longStrategies = new ManualSideStrategy[3];
            shortStrategies = new ManualSideStrategy[3];
            error = null;

            if (_tradingModeComboBox == null ||
                !TryParseTradingModeDisplayName(
                    _tradingModeComboBox.SelectedItem,
                    out mode))
            {
                error = "Select a valid trading mode.";
                return false;
            }

            for (int band = 0; band < 3; band++)
            {
                if (!TryReadManualBandStrategy(band, 0, out longStrategies[band]) ||
                    !TryReadManualBandStrategy(band, 2, out shortStrategies[band]))
                {
                    error = "Select exactly one strategy for Long and Short in every MEV band.";
                    return false;
                }
            }

            try
            {
                ValidateMevParameters();
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }

            return true;
        }

        private bool TryReadManualBandStrategy(
            int band,
            int firstOptionIndex,
            out ManualSideStrategy strategy)
        {
            strategy = ManualSideStrategy.None;
            CheckBox mrButton = _manualRankBandStrategyCheckBoxes[band, firstOptionIndex];
            CheckBox tfButton = _manualRankBandStrategyCheckBoxes[band, firstOptionIndex + 1];
            if (mrButton == null || tfButton == null)
                return false;

            int selectedCount = (mrButton.IsChecked == true ? 1 : 0) +
                                (tfButton.IsChecked == true ? 1 : 0);
            if (selectedCount != 1)
                return false;

            if (mrButton.IsChecked == true)
                strategy = ManualSideStrategy.MeanReversion;
            else if (tfButton.IsChecked == true)
                strategy = ManualSideStrategy.TrendFollowing;
            return true;
        }

        private static bool StrategySelectionsEqual(
            ManualSideStrategy[] first,
            ManualSideStrategy[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index])
                    return false;
            }
            return true;
        }

        private void RefreshStrategySettingsPanel(string status, Color statusColor)
        {
            if (_strategySettingsPanel == null)
                return;
            UpdateStrategyDependentControls();
            if (_effectiveTradingMode == TradingMode.RuleMappedAuto)
            {
                _activeStrategyText.Text = GetManualStrategyDisplay();
                _activeStrategyText.Text += Environment.NewLine +
                    "Max accepted orders/pass: " +
                    MaxNewOrdersPerMaintenancePass.ToString(
                        CultureInfo.InvariantCulture);
            }
            else if (_effectiveTradingMode == TradingMode.UserTriggered)
            {
                _activeStrategyText.Text = "New orders only on Execute." +
                    Environment.NewLine + GetManualStrategyDisplay();
            }
            else
            {
                _activeStrategyText.Text = _hasValidHourlyMev
                    ? GetStrategySelectionDisplay()
                    : "Unavailable";
            }
            SetStrategySettingsStatus(status, statusColor);
        }

        private string GetManualStrategyDisplay()
        {
            return GetStrategySelectionDisplay();
        }

        private void SetStrategySettingsStatus(string text, Color color)
        {
            if (_strategySettingsStatusText == null)
                return;
            _strategySettingsStatusText.Text = text;
            _strategySettingsStatusText.ForegroundColor = color;
        }

        private void RemoveStrategySettingsPanel()
        {
            if (_applyStrategySettingsButton != null)
                _applyStrategySettingsButton.Click -= OnApplyStrategySettingsClicked;
            if (_executeManualEntryButton != null)
                _executeManualEntryButton.Click -= OnExecuteManualEntryClicked;
            for (int band = 0; band < 3; band++)
            {
                for (int option = 0; option < 4; option++)
                {
                    CheckBox checkBox = _manualRankBandStrategyCheckBoxes[band, option];
                    if (checkBox != null)
                    {
                        checkBox.Click -= OnManualRankBandSelectionChanged;
                        _manualRankBandStrategyCheckBoxes[band, option] = null;
                    }
                }
            }
            if (_strategySettingsPanel != null)
                Chart.RemoveControl(_strategySettingsPanel);
            _strategySettingsPanel = null;
            _manualStrategySelectionGrid = null;
            _tradingModeComboBox = null;
            _strategySettingsStatusText = null;
            _activeStrategyText = null;
            _applyStrategySettingsButton = null;
            _manualEntryDirectionComboBox = null;
            _executeManualEntryButton = null;
            _manualEntryStatusText = null;
        }

        #endregion
    }
}
