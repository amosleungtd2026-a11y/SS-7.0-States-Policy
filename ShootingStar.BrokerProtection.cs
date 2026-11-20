using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Broker distance validation and position protection geometry.
    public partial class ShootingStar
    {
        #region Broker Distance and Protection Validation

        private enum BrokerMinimumDistanceMode
        {
            Unsupported,
            Percentage,
            Pips,
            Points
        }

        private double _symbolPipSize;
        private double _symbolTickSize;
        private int _symbolDigits;
        private BrokerMinimumDistanceMode _minimumDistanceMode;

        private void InitializeImmutableSymbolMetadata()
        {
            _symbolPipSize = Symbol.PipSize;
            _symbolTickSize = Symbol.TickSize;
            _symbolDigits = Symbol.Digits;
            string distanceType = Symbol.MinDistanceType.ToString();
            if (string.Equals(distanceType, "Percentage", StringComparison.OrdinalIgnoreCase))
                _minimumDistanceMode = BrokerMinimumDistanceMode.Percentage;
            else if (string.Equals(distanceType, "Pips", StringComparison.OrdinalIgnoreCase))
                _minimumDistanceMode = BrokerMinimumDistanceMode.Pips;
            else if (string.Equals(distanceType, "Points", StringComparison.OrdinalIgnoreCase))
                _minimumDistanceMode = BrokerMinimumDistanceMode.Points;
            else
                _minimumDistanceMode = BrokerMinimumDistanceMode.Unsupported;
        }

        private bool ValidateSupportedMinimumDistanceType()
        {
            string distanceType = Symbol.MinDistanceType.ToString();
            if (_minimumDistanceMode != BrokerMinimumDistanceMode.Unsupported)
                return true;
            LogError("STARTUP", "UNSUPPORTED_MIN_DISTANCE_TYPE",
                "symbol {0} reports unsupported minimum-distance type '{1}'; startup is stopped before trading events and timers are enabled",
                SymbolName, distanceType);
            return false;
        }

        private bool ValidatePendingProtectionGeometry(
            TradeType tradeType,
            double targetPrice,
            double? stopLossPrice,
            double? takeProfitPrice,
            string operation,
            out string error)
        {
            error = null;
            if (!IsFinitePositive(targetPrice) ||
                (stopLossPrice.HasValue && !IsFinitePositive(stopLossPrice.Value)) ||
                (takeProfitPrice.HasValue && !IsFinitePositive(takeProfitPrice.Value)))
            {
                error = operation + " contains a non-finite or non-positive target/protection price";
                LogError("PROTECTION", "INVALID_PROTECTION_GEOMETRY", "{0}", error);
                return false;
            }
            bool stopLossValid = !stopLossPrice.HasValue ||
                (tradeType == TradeType.Buy
                    ? stopLossPrice.Value < targetPrice
                    : stopLossPrice.Value > targetPrice);
            bool takeProfitValid = !takeProfitPrice.HasValue ||
                (tradeType == TradeType.Buy
                    ? takeProfitPrice.Value > targetPrice
                    : takeProfitPrice.Value < targetPrice);
            if (stopLossValid && takeProfitValid)
                return true;
            error = string.Format(CultureInfo.InvariantCulture,
                "{0} has invalid {1} protection geometry at target {2}: SL={3}, TP={4}",
                operation, tradeType, targetPrice,
                stopLossPrice.HasValue ? stopLossPrice.Value.ToString("R", CultureInfo.InvariantCulture) : "none",
                takeProfitPrice.HasValue ? takeProfitPrice.Value.ToString("R", CultureInfo.InvariantCulture) : "none");
            LogError("PROTECTION", "INVALID_PROTECTION_GEOMETRY", "{0}", error);
            return false;
        }

        private double GetMinimumProtectionPriceDistance(
            double referencePrice,
            double configuredMinimum)
        {
            if (configuredMinimum <= 0.0 || referencePrice <= 0.0)
                return 0.0;

            // Compare by name to remain source-compatible with cTrader API versions
            // while explicitly accepting only the distance types this cBot supports.
            if (_minimumDistanceMode == BrokerMinimumDistanceMode.Percentage)
                return referencePrice * configuredMinimum / 100.0;
            if (_minimumDistanceMode == BrokerMinimumDistanceMode.Pips)
                return configuredMinimum * _symbolPipSize;
            if (_minimumDistanceMode == BrokerMinimumDistanceMode.Points)
                return configuredMinimum * _symbolTickSize;

            throw new InvalidOperationException(
                string.Format(
                    "Unsupported symbol minimum-distance type '{0}' for {1}. " +
                    "Protection distance calculation has been stopped to avoid treating an unknown unit as pips.",
                    Symbol.MinDistanceType,
                    SymbolName));
        }

        private double GetBufferedMinimumProtectionPips(
            double referencePrice,
            double configuredMinimum)
        {
            if (!IsFinitePositive(referencePrice) ||
                !IsFinitePositive(_symbolPipSize))
            {
                return double.PositiveInfinity;
            }

            double priceDistance = GetMinimumProtectionPriceDistance(
                referencePrice,
                configuredMinimum) + GetBrokerDistanceSafetyBuffer();

            return priceDistance / _symbolPipSize;
        }

        private double GetMinimumStopLossPips(double referencePrice)
        {
            return GetBufferedMinimumProtectionPips(
                referencePrice,
                Symbol.MinStopLossDistance);
        }

        private double GetMinimumTakeProfitPips(double referencePrice)
        {
            return GetBufferedMinimumProtectionPips(
                referencePrice,
                Symbol.MinTakeProfitDistance);
        }

        private bool ValidateRelativeProtectionDistances(
            double referencePrice,
            double stopLossPips,
            double takeProfitPips,
            string operation)
        {
            if (!IsFinitePositive(referencePrice) ||
                !IsFinitePositive(stopLossPips) ||
                !IsFinitePositive(takeProfitPips))
            {
                LogError("PROTECTION", "INVALID_RELATIVE_DISTANCE",
                    "{0} rejected: reference={1}, SL={2} pips, TP={3} pips",
                    operation, referencePrice, stopLossPips, takeProfitPips);
                return false;
            }

            double minimumStopLossPips = GetMinimumStopLossPips(referencePrice);
            double minimumTakeProfitPips = GetMinimumTakeProfitPips(referencePrice);
            double tolerance = 0.0000001;
            bool valid = true;

            if (stopLossPips + tolerance < minimumStopLossPips)
            {
                LogVerbose("PROTECTION", "STOP_DISTANCE_BELOW_MINIMUM",
                    "{0} skipped: SL distance {1} pips is below broker minimum {2} pips ({3}).",
                    operation,
                    stopLossPips,
                    minimumStopLossPips,
                    Symbol.MinDistanceType);
                valid = false;
            }

            if (takeProfitPips + tolerance < minimumTakeProfitPips)
            {
                LogVerbose("PROTECTION", "TARGET_DISTANCE_BELOW_MINIMUM",
                    "{0} skipped: TP distance {1} pips is below broker minimum {2} pips ({3}).",
                    operation,
                    takeProfitPips,
                    minimumTakeProfitPips,
                    Symbol.MinDistanceType);
                valid = false;
            }

            return valid;
        }

        private bool IsPositionProtectionValid(
            TradeType tradeType,
            double marketPrice,
            double protectionPrice,
            bool isStopLoss,
            long positionId,
            string action)
        {
            bool directionIsValid = tradeType == TradeType.Buy
                ? (isStopLoss ? protectionPrice < marketPrice : protectionPrice > marketPrice)
                : (isStopLoss ? protectionPrice > marketPrice : protectionPrice < marketPrice);

            if (!directionIsValid)
            {
                LogVerbose("PROTECTION", "INVALID_MARKET_SIDE",
                    "Position {0} {1} {2} skipped: protection price {3} is on the invalid side of market price {4}.",
                    positionId,
                    action,
                    isStopLoss ? "SL" : "TP",
                    protectionPrice,
                    marketPrice);
                return false;
            }

            return IsPositionAbsoluteProtectionDistanceValid(
                marketPrice,
                protectionPrice,
                isStopLoss,
                positionId,
                action);
        }

        private bool IsPendingTargetDistanceValid(
            TradeType tradeType,
            double targetPrice,
            double marketPrice,
            string quoteSource,
            string operation)
        {
            if (!IsFinitePositive(targetPrice) || !IsFinitePositive(marketPrice))
            {
                LogError("ORDER", "INVALID_TARGET_DISTANCE_INPUT",
                    "{0} rejected using {1}: target={2}, market={3}",
                    operation, quoteSource, targetPrice, marketPrice);
                return false;
            }

            bool correctSide = tradeType == TradeType.Buy
                ? targetPrice < marketPrice
                : targetPrice > marketPrice;
            double minimumDistance = GetConservativePendingTargetPriceDistance(marketPrice);
            double actualDistance = Math.Abs(marketPrice - targetPrice);

            if (correctSide && actualDistance + 0.000000000001 >= minimumDistance)
                return true;

            LogVerbose("ORDER", "INVALID_TARGET_DISTANCE",
                "{0} skipped using {1}: limit target {2} is invalid versus market {3}; distance {4}, required {5}.",
                operation, quoteSource, targetPrice, marketPrice, actualDistance, minimumDistance);
            return false;
        }

        private bool IsPendingTargetDistanceValid(
            TradeType tradeType,
            V2OrderKind orderKind,
            double targetPrice,
            TickSnapshot tick,
            string operation)
        {
            double marketPrice = tradeType == TradeType.Buy ? tick.Ask : tick.Bid;
            if (!IsFinitePositive(targetPrice) || !IsFinitePositive(marketPrice))
            {
                LogError("ORDER", "INVALID_TARGET_DISTANCE_INPUT",
                    "{0} rejected: target={1}, market={2}, orderKind={3}",
                    operation,
                    targetPrice,
                    marketPrice,
                    orderKind);
                return false;
            }

            bool correctSide = orderKind == V2OrderKind.Stop
                ? (tradeType == TradeType.Buy
                    ? targetPrice > marketPrice
                    : targetPrice < marketPrice)
                : (tradeType == TradeType.Buy
                    ? targetPrice < marketPrice
                    : targetPrice > marketPrice);
            double minimumDistance =
                GetConservativePendingTargetPriceDistance(marketPrice);
            double actualDistance = Math.Abs(marketPrice - targetPrice);
            if (correctSide &&
                actualDistance + 0.000000000001 >= minimumDistance)
            {
                return true;
            }

            LogVerbose("ORDER", "INVALID_TARGET_DISTANCE",
                "{0} skipped: {1} {2} target {3} is invalid versus market {4}; distance {5}, required {6}.",
                operation,
                orderKind,
                tradeType,
                targetPrice,
                marketPrice,
                actualDistance,
                minimumDistance);
            return false;
        }

        private bool IsPendingTargetDistanceValid(
            TradeType tradeType,
            V2OrderKind orderKind,
            double targetPrice,
            string operation)
        {
            double marketPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            if (!IsFinitePositive(targetPrice) || !IsFinitePositive(marketPrice))
            {
                LogError("ORDER", "INVALID_TARGET_DISTANCE_INPUT",
                    "{0} rejected: target={1}, market={2}, orderKind={3}",
                    operation,
                    targetPrice,
                    marketPrice,
                    orderKind);
                return false;
            }

            bool correctSide = orderKind == V2OrderKind.Stop
                ? (tradeType == TradeType.Buy
                    ? targetPrice > marketPrice
                    : targetPrice < marketPrice)
                : (tradeType == TradeType.Buy
                    ? targetPrice < marketPrice
                    : targetPrice > marketPrice);
            double minimumDistance =
                GetConservativePendingTargetPriceDistance(marketPrice);
            double actualDistance = Math.Abs(marketPrice - targetPrice);
            if (correctSide &&
                actualDistance + 0.000000000001 >= minimumDistance)
            {
                return true;
            }

            LogVerbose("ORDER", "INVALID_TARGET_DISTANCE",
                "{0} skipped: {1} {2} target {3} is invalid versus market {4}; distance {5}, required {6}.",
                operation,
                orderKind,
                tradeType,
                targetPrice,
                marketPrice,
                actualDistance,
                minimumDistance);
            return false;
        }

        private bool IsPendingTargetDistanceValid(
            TradeType tradeType,
            double targetPrice,
            TickSnapshot tick,
            string operation)
        {
            double marketPrice = tradeType == TradeType.Buy ? tick.Ask : tick.Bid;
            return IsPendingTargetDistanceValid(
                tradeType,
                targetPrice,
                marketPrice,
                "maintenance-snapshot",
                operation);
        }

        private bool IsPendingTargetDistanceValid(
            TradeType tradeType,
            double targetPrice,
            string operation)
        {
            double marketPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            return IsPendingTargetDistanceValid(
                tradeType,
                targetPrice,
                marketPrice,
                "live-quote",
                operation);
        }

        private double GetBrokerDistanceSafetyBuffer()
        {
            return Math.Max(2.0 * _symbolTickSize, Math.Max(0.0, Symbol.Ask - Symbol.Bid));
        }

        // cTrader exposes protection-distance minimums but no distinct pending-entry
        // distance in this API surface. Use the larger exposed protection minimum as a
        // conservative pending-target separation floor, then add the safety buffer.
        private double GetConservativePendingTargetPriceDistance(double referencePrice)
        {
            double stopMinimum = GetMinimumProtectionPriceDistance(referencePrice, Symbol.MinStopLossDistance);
            double takeProfitMinimum = GetMinimumProtectionPriceDistance(referencePrice, Symbol.MinTakeProfitDistance);
            return Math.Max(stopMinimum, takeProfitMinimum) + GetBrokerDistanceSafetyBuffer();
        }

        private bool IsPositionAbsoluteProtectionDistanceValid(
            double marketPrice,
            double protectionPrice,
            bool isStopLoss,
            long positionId,
            string action)
        {
            double actualDistance = Math.Abs(marketPrice - protectionPrice);
            double configuredMinimum = isStopLoss
                ? Symbol.MinStopLossDistance
                : Symbol.MinTakeProfitDistance;
            double minimumDistance = GetMinimumProtectionPriceDistance(
                marketPrice,
                configuredMinimum) + GetBrokerDistanceSafetyBuffer();

            if (actualDistance + 0.000000000001 >= minimumDistance)
                return true;

            LogVerbose("PROTECTION", "POSITION_DISTANCE_BELOW_MINIMUM",
                "Position {0} {1} {2} skipped: distance {3} is below broker minimum price distance {4} ({5}).",
                positionId,
                action,
                isStopLoss ? "SL" : "TP",
                actualDistance,
                minimumDistance,
                Symbol.MinDistanceType);
            return false;
        }

        #endregion


    }
}
