using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Bot order and position identification.
    public partial class ShootingStar
    {
        #region Bot Trade Identification

        private bool IsKnownLabel(string label)
        {
            return label == _mrLongLabel || label == _mrShortLabel ||
                   label == _tfLongLabel || label == _tfShortLabel;
        }

        private bool TryGetStrategy(string label, out StrategyKind strategy)
        {
            if (label == _tfLongLabel || label == _tfShortLabel)
            {
                strategy = StrategyKind.TrendFollowing;
                return true;
            }
            if (label == _mrLongLabel || label == _mrShortLabel)
            {
                strategy = StrategyKind.MeanReversion;
                return true;
            }

            strategy = default(StrategyKind);
            return false;
        }

        private StrategyKind GetStrategy(string label)
        {
            StrategyKind strategy;
            if (TryGetStrategy(label, out strategy))
                return strategy;
            throw new InvalidOperationException(
                "Unknown bot strategy label: " + (label ?? "<null>"));
        }

        private bool IsBotPosition(Position position)
        {
            return position.SymbolName == Symbol.Name && IsKnownLabel(position.Label);
        }

        private bool IsBotOrder(PendingOrder order)
        {
            return order.SymbolName == Symbol.Name && IsKnownLabel(order.Label);
        }

        #endregion
    }
}
