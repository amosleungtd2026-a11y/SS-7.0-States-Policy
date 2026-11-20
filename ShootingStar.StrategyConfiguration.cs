using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Strategy startup validation and ATR-adjusted trading values.
    public partial class ShootingStar
    {
        #region Hourly ATR Normalisation

        private bool TryValidateStartupConfiguration(out string error)
        {
            error = null;
            try
            {
                InitializeMarketSessionOverrides();
                ValidateGlobalVolatilityParameter();
                ValidateFractalFactorParameter();
                ValidatePriceUnitParameters();
                ValidateAtrParameters();
                InitializeEffectiveStrategySettings();
                ValidateMevParameters();
                if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                {
                    ValidateV2Configuration();
                }
                InitializePolicyRandom();
                ValidateOptimizationFitnessParameters();
                ParseAndNormalizeHourlyVolatilityValues();
                ApplyDirectionalAllocationParameter();
                if (!ValidateSupportedMinimumDistanceType())
                {
                    error = "The symbol minimum-distance type is unsupported.";
                    return false;
                }

                return true;
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void ValidateV2Configuration()
        {
            ValidateProbabilityParameter(
                ExtremeCountertrendStopProbability,
                nameof(ExtremeCountertrendStopProbability));
            ValidateStopOrderMultiplier(StopOrderMultiplier);

            if (V2BossMaximumHoldProbability +
                    V2BossMaximumContrarySellProbability >= 1.0 ||
                V2BossMaximumHoldProbability +
                    V2BossMaximumContraryBuyProbability >= 1.0)
            {
                throw new InvalidOperationException(
                    "V2 Boss Hold and contrary probabilities must sum to less than one.");
            }
        }

        private static void ValidateStopOrderMultiplier(double value)
        {
            if (!IsFinite(value) ||
                value < MinimumStopOrderMultiplier ||
                value > MaximumStopOrderMultiplier)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Stop Order Multiplier must be finite and between {0:F2} and {1:F2} inclusive.",
                        MinimumStopOrderMultiplier,
                        MaximumStopOrderMultiplier));
            }
        }


        private void ValidateGlobalVolatilityParameter()
        {
            if (!IsFinite(GlobalVolatility) || GlobalVolatility < 0.4 || GlobalVolatility > 8.0)
            {
                throw new ArgumentOutOfRangeException(nameof(GlobalVolatility),
                    "Global Volatility (GV) must be a finite value from 0.4 to 8.0.");
            }
        }

        private void ValidateFractalFactorParameter()
        {
            if (FractalFactor < 1 || FractalFactor > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(FractalFactor),
                    "Fractal Factor (FF) must be an integer from 1 to 5.");
            }
        }

        private void ValidatePriceUnitParameters()
        {
            ValidateFinitePositiveParameter(PercentageUnit, nameof(PercentageUnit), "Percentage Unit (%)");

            ValidateFinitePositiveParameter(MoveUnits, nameof(MoveUnits), "Move M (Units)");
            ValidateDistanceRatioParameter(DistanceRatio, nameof(DistanceRatio), "Distance Ratio");
            ValidateFinitePositiveParameter(StopLossUnits, nameof(StopLossUnits), "Stop Loss (Units)");
            ValidateRewardParameter(Reward, nameof(Reward), "Reward");
        }

        private void ValidateFinitePositiveParameter(double value, string parameterName, string displayName)
        {
            if (!IsFinite(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(parameterName,
                    displayName + " must be a finite value greater than zero.");
        }

        private void ValidateDistanceRatioParameter(double value, string parameterName, string displayName)
        {
            if (!IsFinite(value) || value < 0.1 || value > 1.5)
                throw new ArgumentOutOfRangeException(parameterName,
                    displayName + " must be a finite value from 0.1 to 1.5.");
        }
        private void ValidateRewardParameter(double value, string parameterName, string displayName)
        {
            if (!IsFinite(value) || value < 0.1)
                throw new ArgumentOutOfRangeException(parameterName,
                    displayName + " must be a finite value greater than or equal to 0.1.");
        }

        private void ValidateMevParameters()
        {
            if (MomentumEfficiencyLength < 2 || MomentumEfficiencyLength > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(MomentumEfficiencyLength),
                    "Momentum Efficiency Length must be an integer from 2 to 200.");
            }

            if (!IsFinite(MevLowerBoundary) ||
                MevLowerBoundary < -100.0 ||
                MevLowerBoundary > 100.0)
            {
                throw new ArgumentOutOfRangeException(nameof(MevLowerBoundary),
                    "MEV Boundary Lower must be a finite value from -100 to 100.");
            }

            if (!IsFinite(MevUpperBoundary) ||
                MevUpperBoundary < -100.0 ||
                MevUpperBoundary > 100.0)
            {
                throw new ArgumentOutOfRangeException(nameof(MevUpperBoundary),
                    "MEV Boundary Upper must be a finite value from -100 to 100.");
            }

            if (MevLowerBoundary >= MevUpperBoundary)
                throw new ArgumentException("MEV Boundary Lower must be less than MEV Boundary Upper.");

            if (!IsFinite(MevExtremeThreshold) ||
                MevExtremeThreshold <= MevUpperBoundary ||
                MevExtremeThreshold > 100.0)
            {
                throw new ArgumentOutOfRangeException(nameof(MevExtremeThreshold),
                    "MEV Extreme Threshold must be greater than the upper boundary and no greater than 100.");
            }

        }

        private static void ValidateProbabilityParameter(
            double value,
            string parameterName)
        {
            if (!IsFinite(value) || value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Probability parameters must be finite values from 0 to 1.");
            }
        }

        private void InitializeEffectiveStrategySettings()
        {
            _effectiveTradingMode = MapStartupTradingMode(TradingMechanismMode);
            LogNormal("STARTUP", "TRADING_MODE_RESOLVED",
                "configuredMode={0} effectiveMode={1}",
                TradingMechanismMode,
                GetTradingModeDisplayName(_effectiveTradingMode));
            if (TradingMechanismMode == StartupTradingMode.Manual)
            {
                LogNormal("STARTUP", "TRADING_MODE_MIGRATED",
                    "oldMode=Manual newMode=RuleMappedAuto reason=legacyManualModeWasAutomaticallyEvaluated");
            }

            _effectiveManualLongStrategies[(int)ManualRankBand.Lower] =
                LowerBandLongUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
            _effectiveManualShortStrategies[(int)ManualRankBand.Lower] =
                LowerBandShortUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
            _effectiveManualLongStrategies[(int)ManualRankBand.Middle] =
                MiddleBandLongUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
            _effectiveManualShortStrategies[(int)ManualRankBand.Middle] =
                MiddleBandShortUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
            _effectiveManualLongStrategies[(int)ManualRankBand.Upper] =
                UpperBandLongUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
            _effectiveManualShortStrategies[(int)ManualRankBand.Upper] =
                UpperBandShortUseTrendFollowing
                    ? ManualSideStrategy.TrendFollowing
                    : ManualSideStrategy.MeanReversion;
        }
        private void ValidateAtrParameters()
        {
            if (AtrFastPeriod <= 0)
                throw new ArgumentOutOfRangeException(nameof(AtrFastPeriod), "ATR Fast Period must be greater than zero.");

            if (AtrSlowPeriod <= 0)
                throw new ArgumentOutOfRangeException(nameof(AtrSlowPeriod), "ATR Slow Period must be greater than zero.");

            if (AtrFastPeriod >= AtrSlowPeriod)
                throw new ArgumentException("ATR Fast Period must be smaller than ATR Slow Period.");

            if (!IsFinitePositive(MinNormalizedAtrRatio))
                throw new ArgumentOutOfRangeException(nameof(MinNormalizedAtrRatio),
                    "Normalised Ratio Minimum must be a finite value greater than zero.");

            if (!IsFinitePositive(MaxNormalizedAtrRatio))
                throw new ArgumentOutOfRangeException(nameof(MaxNormalizedAtrRatio),
                    "Normalised Ratio Maximum must be a finite value greater than zero.");

            if (MinNormalizedAtrRatio > 1.0)
                throw new ArgumentException("Normalised Ratio Minimum must be less than or equal to 1.0.");

            if (MaxNormalizedAtrRatio < 1.0)
                throw new ArgumentException("Normalised Ratio Maximum must be greater than or equal to 1.0.");

            if (MinNormalizedAtrRatio >= MaxNormalizedAtrRatio)
                throw new ArgumentException("Normalised Ratio Minimum must be smaller than Normalised Ratio Maximum.");
        }

        private AtrUpdateOutcome UpdateAtrAdjustedValues(bool forceUpdate = false)
        {
            int requiredBars = Math.Max(AtrFastPeriod, AtrSlowPeriod) + 2;
            bool hasSufficientHistory = _hourlyBars != null && _hourlyBars.Count >= requiredBars;
            DateTime completedBarOpenTime = DateTime.MinValue;

            if (hasSufficientHistory)
            {
                int completedBarIndex = _hourlyBars.Count - 2;
                completedBarOpenTime = _hourlyBars.OpenTimes[completedBarIndex];

                // The current hourly bar is excluded. Update only when a bar has closed.
                if (!forceUpdate && completedBarOpenTime == _lastProcessedCompletedHourlyBarOpenTime)
                    return AtrUpdateOutcome.Unchanged;
            }
            else if (!forceUpdate && _lastProcessedCompletedHourlyBarOpenTime == DateTime.MinValue)
            {
                // The neutral fallback is already active and history remains insufficient.
                return AtrUpdateOutcome.Unchanged;
            }

            double candidateAtrRatio = 1.0;
            double candidateNormalizedAtrRatio = 1.0;

            if (hasSufficientHistory)
            {
                double fastAtr = _fastHourlyAtr == null ? double.NaN : _fastHourlyAtr.Result.Last(1);
                double slowAtr = _slowHourlyAtr == null ? double.NaN : _slowHourlyAtr.Result.Last(1);

                if (IsFinitePositive(fastAtr) && IsFinitePositive(slowAtr))
                {
                    candidateAtrRatio = fastAtr / slowAtr;
                    double safeAtrRatio = Math.Max(candidateAtrRatio, MinAtrRatioEpsilon);
                    double logDistanceFromOne = Math.Abs(Math.Log(safeAtrRatio));
                    double normalizedDistance = Math.Tanh(
                        AtrNormalizationSensitivity * logDistanceFromOne);

                    // Map each side of 1.0 independently, allowing asymmetric input limits.
                    candidateNormalizedAtrRatio = safeAtrRatio >= 1.0
                        ? Math.Exp(Math.Log(MaxNormalizedAtrRatio) * normalizedDistance)
                        : Math.Exp(-Math.Log(1.0 / MinNormalizedAtrRatio) * normalizedDistance);
                }
            }

            candidateNormalizedAtrRatio = Math.Max(
                MinNormalizedAtrRatio,
                Math.Min(MaxNormalizedAtrRatio, candidateNormalizedAtrRatio));

            // Validate the fixed long/short allocation used by placement and pending-order maintenance.
            double longAllocationFraction = _effectiveDirectionalAllocation / 100.0;
            double shortAllocationFraction = 1.0 - longAllocationFraction;
            double globalVolatilityAdjustedTotalLots = TotalTradingVolumeInLots / GlobalVolatility;
            double adjustedLongLots = globalVolatilityAdjustedTotalLots * longAllocationFraction /
                candidateNormalizedAtrRatio;
            double adjustedShortLots = globalVolatilityAdjustedTotalLots * shortAllocationFraction /
                candidateNormalizedAtrRatio;

            double candidateLongVolumeInUnits = Symbol.NormalizeVolumeInUnits(
                Symbol.QuantityToVolumeInUnits(adjustedLongLots),
                RoundingMode.Down);
            double candidateShortVolumeInUnits = Symbol.NormalizeVolumeInUnits(
                Symbol.QuantityToVolumeInUnits(adjustedShortLots),
                RoundingMode.Down);

            bool longVolumeIsValid = ValidateAdjustedVolume(
                candidateLongVolumeInUnits,
                adjustedLongLots,
                "Long",
                _effectiveDirectionalAllocation > 0.0);
            bool shortVolumeIsValid = ValidateAdjustedVolume(
                candidateShortVolumeInUnits,
                adjustedShortLots,
                "Short",
                _effectiveDirectionalAllocation < 100.0);

            if (!longVolumeIsValid || !shortVolumeIsValid)
            {
                // Mark this completed bar as processed to prevent repeated warnings on every tick.
                // The previous complete, internally consistent ATR-adjusted values remain active.
                _lastProcessedCompletedHourlyBarOpenTime = completedBarOpenTime;
                return forceUpdate
                    ? AtrUpdateOutcome.Fatal
                    : AtrUpdateOutcome.RetainedPreviousValues;
            }

            _lastProcessedCompletedHourlyBarOpenTime = hasSufficientHistory
                ? completedBarOpenTime
                : DateTime.MinValue;
            _atrRatio = candidateAtrRatio;
            _normalizedAtrRatio = candidateNormalizedAtrRatio;

            _actualMrUpMovePercent = MrUpMovePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrSellLimitDistancePercent = MrSellLimitDistancePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrSellTakeProfitPercent = MrSellTakeProfitPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrSellStopLossPercent = MrSellStopLossPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrDownMovePercent = MrDownMovePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrBuyLimitDistancePercent = MrBuyLimitDistancePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrBuyTakeProfitPercent = MrBuyTakeProfitPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualMrBuyStopLossPercent = MrBuyStopLossPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfLongMovePercent = TfLongMovePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfBuyLimitDistancePercent = TfBuyLimitDistancePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfLongTakeProfitPercent = TfLongTakeProfitPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfLongStopLossPercent = TfLongStopLossPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfShortMovePercent = TfShortMovePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfSellLimitDistancePercent = TfSellLimitDistancePercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfShortTakeProfitPercent = TfShortTakeProfitPercent * GlobalVolatility * _normalizedAtrRatio;
            _actualTfShortStopLossPercent = TfShortStopLossPercent * GlobalVolatility * _normalizedAtrRatio;

            // Candidate volumes are validated here; operational sizing is calculated
            // through the single allocation-adjusted volume path when an action is due.
            return AtrUpdateOutcome.Updated;
        }

        private bool ValidateAdjustedVolume(
            double volumeInUnits,
            double adjustedLots,
            string side,
            bool sideEnabled)
        {
            if (!sideEnabled)
                return true;

            if (!IsFinitePositive(volumeInUnits))
            {
                LogNormal("RISK_CAP", "ADJUSTED_VOLUME_INVALID",
                    "{0} ATR-adjusted volume is invalid. Adjusted lots={1}, Normalized units={2}.",
                    side,
                    adjustedLots,
                    volumeInUnits);
                return false;
            }

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                LogNormal("RISK_CAP", "ADJUSTED_VOLUME_BELOW_MINIMUM",
                    "{0} ATR-adjusted volume is below the broker minimum. Adjusted lots={1}, " +
                    "Normalized units={2}, Minimum units={3}.",
                    side,
                    adjustedLots,
                    volumeInUnits,
                    Symbol.VolumeInUnitsMin);
                return false;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
            {
                LogNormal("RISK_CAP", "ADJUSTED_VOLUME_ABOVE_MAXIMUM",
                    "{0} ATR-adjusted volume exceeds the broker maximum. Adjusted lots={1}, " +
                    "Normalized units={2}, Maximum units={3}.",
                    side,
                    adjustedLots,
                    volumeInUnits,
                    Symbol.VolumeInUnitsMax);
                return false;
            }

            return true;
        }

        #endregion
    }
}
