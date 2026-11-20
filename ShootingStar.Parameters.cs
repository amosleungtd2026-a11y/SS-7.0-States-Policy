using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    // Public bot parameters and fixed trading configuration.
    public partial class ShootingStar
    {
        #region Parameters

        // Fixed implementation configuration. These values are intentionally not exposed
        // through the cTrader parameter interface.
        private const int CheckEveryNTicks = 50;
        private const int PositionModifyCooldownMilliseconds = 2000;
        private const int TransientPlacementRetryCount = 3;
        private const int TransientPlacementRetrySeconds = 30;
        private const int PolicyDecisionLifetimeMinutes = 10;
        private const int MaxNewOrdersPerMaintenancePass = 1;
        private const string BotLabel = "ShootingStar";
        private const YesNo PositionTrailing = YesNo.Y;
        private const string UtcHourlyVolatilityValues =
            "0.22, 0.17, 0.14, 0.12, 0.16, 0.18, 0.26, 0.26, 0.23, 0.21, 0.22, 0.28, " +
            "0.44, 0.53, 0.48, 0.4, 0.38, 0.4, 0.46, 0.31, 0.22, 0.24, 0.19, 0.22";
        private const int AtrFastPeriod = 60;
        private const int AtrSlowPeriod = 480;
        private const double MinNormalizedAtrRatio = 0.5;
        private const double MaxNormalizedAtrRatio = 2.0;
        private const int SafetyTimerSeconds = 1;

        [Parameter("Global Volatility (GV)", DefaultValue = 1.0, MinValue = 0.4, MaxValue = 8.0, Step = 0.1, Group = "General")]
        public double GlobalVolatility { get; set; }

        [Parameter("Minimum Trail Step (%)", DefaultValue = 0.02, MinValue = 0.01, MaxValue = 1.0, Step = 0.01, Group = "General")]
        public double MinimumPositionTrailStepPercent { get; set; }

        [Parameter("Fractal Factor (FF)", DefaultValue = 1, MinValue = 1, MaxValue = 5, Step = 1, Group = "Position Management")]
        public int FractalFactor { get; set; }

        [Parameter("Total Trading Volume (Lots)", DefaultValue = 1.0, MinValue = 0.01, Group = "Directional Allocation")]
        public double TotalTradingVolumeInLots { get; set; }

        [Parameter("Long Allocation (%)", DefaultValue = 50.0, MinValue = 0.0, MaxValue = 100.0, Step = 1.0, Group = "Directional Allocation")]
        public double DirectionalAllocation { get; set; }

        [Parameter("Log Level", DefaultValue = BotLogLevel.ErrorsOnly, Group = "Logging")]
        public BotLogLevel LogLevel { get; set; }

        [Parameter("Session Overrides", DefaultValue = "", Group = "Market Session")]
        public string MarketSessionOverrides { get; set; }

        [Parameter("Recover Idea IDs From Comments", DefaultValue = true, Group = "Logging")]
        public bool RecoverIdeaIdsFromComments { get; set; }

        [Parameter("Policy Random Seed", DefaultValue = 1, MinValue = 0, MaxValue = 2147483647, Group = "Stochastic Testing")]
        public int PolicyRandomSeed { get; set; }

        [Parameter("Extreme Countertrend Stop Probability", DefaultValue = 0.50, MinValue = 0.00, MaxValue = 1.00, Step = 0.05, Group = "Extreme Countertrend Entry")]
        public double ExtremeCountertrendStopProbability { get; set; }

        [Parameter("Stop Order Multiplier", DefaultValue = 1.50, MinValue = 1.10, MaxValue = 3.00, Step = 0.10, Group = "Extreme Countertrend Entry")]
        public double StopOrderMultiplier { get; set; }

        private const double SonBreakoutFloor = 0.15;
        private const double SonBreakoutCeiling = 0.85;
        private const double SonBreakoutExponent = 1.40;
        private const double SonBreakdownFloor = 0.20;
        private const double SonBreakdownCeiling = 0.85;
        private const double SonBreakdownExponent = 1.50;
        private const double SonPanicFloor = 0.10;
        private const double SonPanicCeiling = 0.80;
        private const double SonPanicExponent = 1.30;
        private const double SonExtremeFloor = 0.10;
        private const double SonExtremeCeiling = 0.75;
        private const double SonExtremeExponent = 1.25;
        private const int DaughterContextPersistenceMinutes = 3;
        private const double DaughterDailyHysteresis = 0.075;
        private const double BossMaximumHoldProbability = 0.15;
        private const double BossMaximumContrarySellProbability = 0.07;
        private const double BossMaximumContraryBuyProbability = 0.12;
        private const double WifeBreakoutAlignedTfProbability = 0.80;
        private const double WifeBreakoutContraryTfProbability = 0.15;
        private const double WifeBreakdownAlignedTfProbability = 0.70;
        private const double WifeBreakdownContraryTfProbability = 0.15;
        private const double WifePanicReversalAlignedTfProbability = 0.25;
        private const double WifePanicReversalContraryTfProbability = 0.70;
        private const double WifeExtremeReversalAlignedTfProbability = 0.15;
        private const double WifeExtremeReversalContraryTfProbability = 0.75;
        private const bool RejectContraryReversalSelections = true;

        [Parameter("Trading Mode", DefaultValue = StartupTradingMode.Auto, Group = "Strategy Selection")]
        public StartupTradingMode TradingMechanismMode { get; set; }

        [Parameter("Lower Band - Long: Use TF", DefaultValue = true, Group = "Strategy Selection")]
        public bool LowerBandLongUseTrendFollowing { get; set; }

        [Parameter("Lower Band - Short: Use TF", DefaultValue = false, Group = "Strategy Selection")]
        public bool LowerBandShortUseTrendFollowing { get; set; }

        [Parameter("Middle Band - Long: Use TF", DefaultValue = true, Group = "Strategy Selection")]
        public bool MiddleBandLongUseTrendFollowing { get; set; }

        [Parameter("Middle Band - Short: Use TF", DefaultValue = true, Group = "Strategy Selection")]
        public bool MiddleBandShortUseTrendFollowing { get; set; }

        [Parameter("Upper Band - Long: Use TF", DefaultValue = true, Group = "Strategy Selection")]
        public bool UpperBandLongUseTrendFollowing { get; set; }

        [Parameter("Upper Band - Short: Use TF", DefaultValue = true, Group = "Strategy Selection")]
        public bool UpperBandShortUseTrendFollowing { get; set; }

        [Parameter("Momentum Efficiency Length", DefaultValue = 10, MinValue = 2, MaxValue = 200, Step = 1, Group = "Strategy Selection")]
        public int MomentumEfficiencyLength { get; set; }

        [Parameter("MEV Boundary Upper", DefaultValue = 0.35, MinValue = -100.0, MaxValue = 100.0, Step = 0.01, Group = "Strategy Selection")]
        public double MevUpperBoundary { get; set; }

        [Parameter("MEV Boundary Lower", DefaultValue = -0.35, MinValue = -100.0, MaxValue = 100.0, Step = 0.01, Group = "Strategy Selection")]
        public double MevLowerBoundary { get; set; }

        [Parameter("MEV Extreme Threshold", DefaultValue = 0.90, MinValue = 0.01, MaxValue = 100.0, Step = 0.01, Group = "Strategy Selection")]
        public double MevExtremeThreshold { get; set; }

        [Parameter("Percentage Unit (%)", DefaultValue = 0.2, MinValue = 0.01, Step = 0.1, Group = "Price Unit")]
        public double PercentageUnit { get; set; }

        [Parameter("Move M (Units)", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 20.0, Step = 0.25, Group = "Price Parameters")]
        public double MoveUnits { get; set; }

        [Parameter("Distance Ratio", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.5, Step = 0.1, Group = "Price Parameters")]
        public double DistanceRatio { get; set; }

        [Parameter("Stop Loss (Units)", DefaultValue = 2.5, MinValue = 0.5, Step = 0.25, Group = "Price Parameters")]
        public double StopLossUnits { get; set; }

        [Parameter("Reward", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 3.0, Step = 0.1, Group = "Price Parameters")]
        public double Reward { get; set; }

        private double MovePercent => UnitsToPercent(MoveUnits);
        private double LimitDistancePercent => MovePercent * DistanceRatio;
        private double StopLossPercent => UnitsToPercent(StopLossUnits);
        private double TakeProfitPercent => StopLossPercent * Reward;

        // Direction-specific calculation aliases use the same shared price settings.
        public double MrUpMoveUnits => MoveUnits;
        public double MrSellDistanceRatio => DistanceRatio;
        public double MrSellStopLossUnits => StopLossUnits;
        public double MrSellReward => Reward;
        public double MrDownMoveUnits => MoveUnits;
        public double MrBuyDistanceRatio => DistanceRatio;
        public double MrBuyStopLossUnits => StopLossUnits;
        public double MrBuyReward => Reward;
        public double TfLongMoveUnits => MoveUnits;
        public double TfBuyDistanceRatio => DistanceRatio;
        public double TfLongStopLossUnits => StopLossUnits;
        public double TfLongReward => Reward;
        public double TfShortMoveUnits => MoveUnits;
        public double TfSellDistanceRatio => DistanceRatio;
        public double TfShortStopLossUnits => StopLossUnits;
        public double TfShortReward => Reward;

        public double MrUpMovePercent => MovePercent;
        public double MrSellLimitDistancePercent => LimitDistancePercent;
        public double MrSellStopLossPercent => StopLossPercent;
        public double MrSellTakeProfitPercent => TakeProfitPercent;
        public double MrDownMovePercent => MovePercent;
        public double MrBuyLimitDistancePercent => LimitDistancePercent;
        public double MrBuyStopLossPercent => StopLossPercent;
        public double MrBuyTakeProfitPercent => TakeProfitPercent;
        public double TfLongMovePercent => MovePercent;
        public double TfBuyLimitDistancePercent => LimitDistancePercent;
        public double TfLongStopLossPercent => StopLossPercent;
        public double TfLongTakeProfitPercent => TakeProfitPercent;
        public double TfShortMovePercent => MovePercent;
        public double TfSellLimitDistancePercent => LimitDistancePercent;
        public double TfShortStopLossPercent => StopLossPercent;
        public double TfShortTakeProfitPercent => TakeProfitPercent;

        private double UnitsToPercent(double units)
        {
            return units * PercentageUnit;
        }
        #endregion

        #region Position Protection Constants

        private const double SafeDistanceMultiplier = 1.3;
        private const double MaximumPendingOrderRiskPercent = 2.0;

        #endregion

        #region Strategy and Volatility Constants

        private const double AtrNormalizationSensitivity = 2.0;
        private const double DefaultDirectionalAllocation = 50.0;
        private const double MinHourlyVolatilityFactor = 0.8;
        private const double MaxHourlyVolatilityFactor = 1.2;
        private const double MinAtrRatioEpsilon = 1e-6;
        private const double TfPendingOrderCancellationMoveMultiplier = 2.0;

        #endregion

        #region Rollover Constants

        private const int RolloverRestorationMaxAttempts = 5;
        private const int RolloverRestorationRetrySeconds = 5;
        private const int RolloverPositionRestorationMaxAttempts = 3;
        private const int RolloverPositionRestorationRetrySeconds = 300;
        private const int RolloverTransientRetrySeconds = 15;
        private const int RolloverTransientMaxWaitMinutes = 60;
        private const int AtrTimerCheckIntervalSeconds = 30;
        private const int EquityUiRefreshIntervalSeconds = 60;

        #endregion


    }
}
