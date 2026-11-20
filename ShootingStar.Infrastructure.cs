using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Price, pip, and volume normalization helpers.
    public partial class ShootingStar
    {
        #region Price, Pip, and Volume Normalisation

        private static bool IsFinite(double value)
        {
            return double.IsFinite(value);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }

        private double PercentDistanceToPips(double entryPrice, double percent)
        {
            return entryPrice * percent / 100.0 / _symbolPipSize;
        }

        private double NormalizeVolume(double volumeInUnits)
        {
            if (!IsFinitePositive(volumeInUnits))
                return 0.0;
            double normalized = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            return IsTradableVolume(normalized) ? normalized : 0.0;
        }
        private bool IsTradableVolume(double volumeInUnits)
        {
            return IsFinitePositive(volumeInUnits) &&
                   volumeInUnits >= Symbol.VolumeInUnitsMin &&
                   volumeInUnits <= Symbol.VolumeInUnitsMax;
        }
        private double GetProtectionPipStep()
        {
            if (_symbolPipSize <= 0.0 || _symbolTickSize <= 0.0)
                return 1.0;

            return _symbolTickSize / _symbolPipSize;
        }

        private double NormalizeProtectionPips(double pips)
        {
            double step = GetProtectionPipStep();
            if (!IsFinitePositive(pips) || !IsFinitePositive(step))
                return 0.0;

            double steps = Math.Round(pips / step, MidpointRounding.AwayFromZero);
            return steps * step;
        }

        private double NormalizeProtectionPipsUp(double pips)
        {
            double step = GetProtectionPipStep();
            if (!IsFinitePositive(pips) || !IsFinitePositive(step))
                return 0.0;

            // Safety-sensitive protection distances must never be rounded inward.
            double steps = Math.Ceiling((pips / step) - 1e-12);
            return steps * step;
        }

        private double GetEffectiveMinimumPositionTrailStepPips(TickSnapshot tick)
        {
            double hourlyVolatilityFactor = tick.VolatilityFactor;
            double referencePrice = (tick.Bid + tick.Ask) / 2.0;
            double basePips = PercentDistanceToPips(
                referencePrice,
                MinimumPositionTrailStepPercent);
            double effectivePips = basePips *
                GlobalVolatility *
                _normalizedAtrRatio *
                hourlyVolatilityFactor;

            return Math.Max(
                GetProtectionPipStep(),
                NormalizeProtectionPips(effectivePips));
        }

        private double NormalizePrice(double price)
        {
            double tickSize = _symbolTickSize;
            if (tickSize <= 0.0)
                return Math.Round(price, _symbolDigits, MidpointRounding.AwayFromZero);

            double ticks = Math.Round(price / tickSize, MidpointRounding.AwayFromZero);
            return Math.Round(ticks * tickSize, _symbolDigits, MidpointRounding.AwayFromZero);
        }

        #endregion


    }
}
