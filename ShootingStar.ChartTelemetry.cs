using System;
using System.Globalization;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Equity, MEV, and reference-range chart telemetry.
    public partial class ShootingStar
    {
        #region Chart Telemetry State

        // Equity statistics measured from the moment this cBot starts.
        private double _peakEquity = double.NaN;
        private double _troughEquity = double.NaN;
        private bool _equityStatisticsInitialized;
        private Grid _equityStatisticsGrid;
        private TextBlock _peakEquityValueText;
        private TextBlock _troughEquityValueText;
        private TextBlock _equityRangeRatioValueText;
        private TextBlock _hourlyMevValueText;
        private TextBlock _dailyMevValueText;
        private TextBlock _bossValueText;
        private TextBlock _wifeValueText;
        private TextBlock _sonScoreValueText;
        private TextBlock _daughterScoreValueText;
        private TextBlock _referenceHighValueText;
        private TextBlock _referenceLowValueText;
        private string _lastRenderedPeakEquity;
        private string _lastRenderedTroughEquity;
        private string _lastRenderedEquityRangeRatio;
        private string _lastRenderedHourlyMev;
        private string _lastRenderedDailyMev;
        private string _lastRenderedBoss;
        private string _lastRenderedWife;
        private string _lastRenderedSonScore;
        private string _lastRenderedDaughterScore;
        private string _lastRenderedReferenceHigh;
        private string _lastRenderedReferenceLow;
        private bool _hasDailyPolicySnapshot;
        private string _dailyPolicyBoss;
        private string _dailyPolicyWife;
        private double _dailyPolicySonScore = double.NaN;
        private double _dailyPolicyDaughterScore = double.NaN;
        private double _lastRenderedPeakEquityValue = double.NaN;
        private double _lastRenderedTroughEquityValue = double.NaN;
        private double _lastRenderedEquityRangeRatioValue = double.NaN;
        private string _equityAssetName = string.Empty;

        #endregion

        #region Chart Telemetry and Equity Statistics

        private void InitializeEquityStatistics()
        {
            _equityStatisticsInitialized = false;
            _peakEquity = double.NaN;
            _troughEquity = double.NaN;
            TryAcceptEquityStatisticsSample(Account.Equity);
        }

        private void UpdateEquityStatisticsValues()
        {
            // Sample peak/trough values on every tick so brief equity extrema are retained.
            // Chart redraws remain timer-driven and are not performed here.
            TryAcceptEquityStatisticsSample(Account.Equity);
        }

        private bool TryAcceptEquityStatisticsSample(double currentEquity)
        {
            if (!IsValidEquitySample(currentEquity))
                return false;

            if (!_equityStatisticsInitialized)
            {
                _peakEquity = currentEquity;
                _troughEquity = currentEquity;
                _equityStatisticsInitialized = true;
                return true;
            }

            if (currentEquity > _peakEquity)
                _peakEquity = currentEquity;

            if (currentEquity < _troughEquity)
                _troughEquity = currentEquity;

            return true;
        }

        private void CreateEquityStatisticsTable()
        {
            RemoveEquityStatisticsTable();

            _equityStatisticsGrid = new Grid(12, 2)
            {
                BackgroundColor = Color.Black,
                Opacity = 0.80,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = 10
            };

            var headerStyle = new Style();
            headerStyle.Set(ControlProperty.Padding, 5);
            headerStyle.Set(ControlProperty.FontWeight, FontWeight.Bold);
            headerStyle.Set(ControlProperty.ForegroundColor, Color.White);

            var labelStyle = new Style();
            labelStyle.Set(ControlProperty.Padding, 5);
            labelStyle.Set(ControlProperty.ForegroundColor, Color.LightGray);

            var valueStyle = new Style();
            valueStyle.Set(ControlProperty.Padding, 5);
            valueStyle.Set(ControlProperty.FontWeight, FontWeight.Bold);
            valueStyle.Set(ControlProperty.ForegroundColor, Color.White);

            _equityStatisticsGrid.AddChild(new TextBlock
            {
                Text = "Equity Statistics",
                Style = headerStyle,
                HorizontalAlignment = HorizontalAlignment.Center
            }, 0, 0, 1, 2);

            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Peak equity",
                    Style = labelStyle
                },
                1,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Equity trough",
                    Style = labelStyle
                },
                2,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Peak-to-trough range / trough",
                    Style = labelStyle
                },
                3,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Hourly MEV",
                    Style = labelStyle
                },
                4,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Daily MEV",
                    Style = labelStyle
                },
                5,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Boss",
                    Style = labelStyle
                },
                6,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Wife",
                    Style = labelStyle
                },
                7,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Son score",
                    Style = labelStyle
                },
                8,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Daughter score",
                    Style = labelStyle
                },
                9,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Ref_hi",
                    Style = labelStyle
                },
                10,
                0);
            _equityStatisticsGrid.AddChild(
                new TextBlock
                {
                    Text = "Ref_lo",
                    Style = labelStyle
                },
                11,
                0);

            _peakEquityValueText = new TextBlock { Style = valueStyle };
            _troughEquityValueText = new TextBlock { Style = valueStyle };
            _equityRangeRatioValueText = new TextBlock { Style = valueStyle };
            _hourlyMevValueText = new TextBlock { Style = valueStyle };
            _dailyMevValueText = new TextBlock { Style = valueStyle };
            _bossValueText = new TextBlock { Style = valueStyle };
            _wifeValueText = new TextBlock { Style = valueStyle };
            _sonScoreValueText = new TextBlock { Style = valueStyle };
            _daughterScoreValueText = new TextBlock { Style = valueStyle };
            _referenceHighValueText = new TextBlock { Style = valueStyle };
            _referenceLowValueText = new TextBlock { Style = valueStyle };

            _equityStatisticsGrid.AddChild(_peakEquityValueText, 1, 1);
            _equityStatisticsGrid.AddChild(_troughEquityValueText, 2, 1);
            _equityStatisticsGrid.AddChild(_equityRangeRatioValueText, 3, 1);
            _equityStatisticsGrid.AddChild(_hourlyMevValueText, 4, 1);
            _equityStatisticsGrid.AddChild(_dailyMevValueText, 5, 1);
            _equityStatisticsGrid.AddChild(_bossValueText, 6, 1);
            _equityStatisticsGrid.AddChild(_wifeValueText, 7, 1);
            _equityStatisticsGrid.AddChild(_sonScoreValueText, 8, 1);
            _equityStatisticsGrid.AddChild(_daughterScoreValueText, 9, 1);
            _equityStatisticsGrid.AddChild(_referenceHighValueText, 10, 1);
            _equityStatisticsGrid.AddChild(_referenceLowValueText, 11, 1);

            Chart.AddControl(_equityStatisticsGrid);
            RefreshEquityStatisticsTable();
            RefreshHourlyMevDisplay();
            RefreshDailyPolicySnapshotDisplay();
            RefreshReferenceRangeDisplay();
        }

        private static void UpdateTextIfChanged(TextBlock textBlock,
            ref string lastRenderedValue, string newValue)
        {
            if (textBlock == null || lastRenderedValue == newValue)
                return;

            textBlock.Text = newValue;
            lastRenderedValue = newValue;
        }

        private void RemoveEquityStatisticsTable()
        {
            if (_equityStatisticsGrid != null)
            {
                Chart.RemoveControl(_equityStatisticsGrid);
                _equityStatisticsGrid = null;
            }

            _peakEquityValueText = null;
            _troughEquityValueText = null;
            _equityRangeRatioValueText = null;
            _hourlyMevValueText = null;
            _dailyMevValueText = null;
            _bossValueText = null;
            _wifeValueText = null;
            _sonScoreValueText = null;
            _daughterScoreValueText = null;
            _referenceHighValueText = null;
            _referenceLowValueText = null;
            _lastRenderedPeakEquity = null;
            _lastRenderedTroughEquity = null;
            _lastRenderedEquityRangeRatio = null;
            _lastRenderedHourlyMev = null;
            _lastRenderedDailyMev = null;
            _lastRenderedBoss = null;
            _lastRenderedWife = null;
            _lastRenderedSonScore = null;
            _lastRenderedDaughterScore = null;
            _lastRenderedReferenceHigh = null;
            _lastRenderedReferenceLow = null;
            _lastRenderedPeakEquityValue = double.NaN;
            _lastRenderedTroughEquityValue = double.NaN;
            _lastRenderedEquityRangeRatioValue = double.NaN;
            _equityAssetName = Account.Asset == null ? string.Empty : Account.Asset.Name;
        }

        private void RefreshEquityStatisticsTable()
        {
            if (_peakEquityValueText == null ||
                _troughEquityValueText == null ||
                _equityRangeRatioValueText == null)
            {
                return;
            }

            double displayPeakEquity = _equityStatisticsInitialized
                ? _peakEquity
                : double.NaN;
            double displayTroughEquity = _equityStatisticsInitialized
                ? _troughEquity
                : double.NaN;
            double ratioPercent = _equityStatisticsInitialized && _troughEquity > 0.0
                ? (_peakEquity - _troughEquity) / _troughEquity * 100.0
                : double.NaN;

            if (!AreEquivalentRenderedValues(_lastRenderedPeakEquityValue, displayPeakEquity))
            {
                _lastRenderedPeakEquityValue = displayPeakEquity;
                string peakText = double.IsNaN(displayPeakEquity)
                    ? "N/A"
                    : string.Format("{0:N2} {1}", displayPeakEquity, _equityAssetName);
                UpdateTextIfChanged(_peakEquityValueText, ref _lastRenderedPeakEquity, peakText);
            }

            if (!AreEquivalentRenderedValues(_lastRenderedTroughEquityValue, displayTroughEquity))
            {
                _lastRenderedTroughEquityValue = displayTroughEquity;
                string troughText = double.IsNaN(displayTroughEquity)
                    ? "N/A"
                    : string.Format("{0:N2} {1}", displayTroughEquity, _equityAssetName);
                UpdateTextIfChanged(_troughEquityValueText, ref _lastRenderedTroughEquity, troughText);
            }

            if (!AreEquivalentRenderedValues(_lastRenderedEquityRangeRatioValue, ratioPercent))
            {
                _lastRenderedEquityRangeRatioValue = ratioPercent;
                string ratioText = double.IsNaN(ratioPercent)
                    ? "N/A"
                    : string.Format("{0:N2}%", ratioPercent);
                UpdateTextIfChanged(_equityRangeRatioValueText, ref _lastRenderedEquityRangeRatio, ratioText);
            }
        }

        private void RefreshHourlyMevDisplay()
        {
            if (_hourlyMevValueText == null)
                return;

            string mevText = _hasValidHourlyMev && IsFinite(_currentHourlyMev)
                ? string.Format("{0:F4}", _currentHourlyMev)
                : "N/A";
            UpdateTextIfChanged(
                _hourlyMevValueText,
                ref _lastRenderedHourlyMev,
                mevText);
        }

        private void RecordDailyPolicySnapshot(
            TradeType boss,
            StrategyKind wife,
            double sonScore,
            double daughterScore)
        {
            _hasDailyPolicySnapshot = true;
            _dailyPolicyBoss = boss.ToString();
            _dailyPolicyWife = wife.ToString();
            _dailyPolicySonScore = sonScore;
            _dailyPolicyDaughterScore = daughterScore;
        }

        private void RefreshDailyPolicySnapshotDisplay()
        {
            if (_dailyMevValueText == null)
                return;

            UpdateTextIfChanged(
                _dailyMevValueText,
                ref _lastRenderedDailyMev,
                _hasValidDailyMev && IsFinite(_currentDailyMev)
                    ? string.Format("{0:F4}", _currentDailyMev)
                    : "N/A");
            UpdateTextIfChanged(
                _bossValueText,
                ref _lastRenderedBoss,
                _hasDailyPolicySnapshot ? _dailyPolicyBoss : "N/A");
            UpdateTextIfChanged(
                _wifeValueText,
                ref _lastRenderedWife,
                _hasDailyPolicySnapshot ? _dailyPolicyWife : "N/A");
            UpdateTextIfChanged(
                _sonScoreValueText,
                ref _lastRenderedSonScore,
                _hasDailyPolicySnapshot && IsFinite(_dailyPolicySonScore)
                    ? string.Format("{0:F4}", _dailyPolicySonScore)
                    : "N/A");
            UpdateTextIfChanged(
                _daughterScoreValueText,
                ref _lastRenderedDaughterScore,
                _hasDailyPolicySnapshot && IsFinite(_dailyPolicyDaughterScore)
                    ? string.Format("{0:F4}", _dailyPolicyDaughterScore)
                    : "N/A");
        }

        private void RefreshReferenceRangeDisplay()
        {
            if (_referenceHighValueText == null || _referenceLowValueText == null)
                return;

            string highText = _referenceRangeReady && IsFinitePositive(_referenceHigh)
                ? FormatPrice(_referenceHigh)
                : "N/A";
            string lowText = _referenceRangeReady && IsFinitePositive(_referenceLow)
                ? FormatPrice(_referenceLow)
                : "N/A";
            UpdateTextIfChanged(
                _referenceHighValueText,
                ref _lastRenderedReferenceHigh,
                highText);
            UpdateTextIfChanged(
                _referenceLowValueText,
                ref _lastRenderedReferenceLow,
                lowText);
        }

        private static bool AreEquivalentRenderedValues(double previousValue, double currentValue)
        {
            return previousValue.Equals(currentValue) ||
                (double.IsNaN(previousValue) && double.IsNaN(currentValue));
        }

        #endregion

    }
}
