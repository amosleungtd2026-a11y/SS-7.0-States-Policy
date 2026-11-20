using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Reference prices and pending-order trailing.
    public partial class ShootingStar
    {
        #region Reference Prices and Pending Order Trailing

        private const string ReferencePriceLineName = "ShootingStar.ReferencePrice";

        private void DrawReferencePriceLine()
        {
            if (!_referenceRangeReady ||
                !IsFinitePositive(_referenceHigh) ||
                !IsFinitePositive(_referenceLow))
            {
                return;
            }

            Chart.DrawHorizontalLine(
                ReferencePriceLineName + ".High",
                _referenceHigh,
                Color.Blue,
                2,
                LineStyle.Solid);
            Chart.DrawHorizontalLine(
                ReferencePriceLineName + ".Low",
                _referenceLow,
                Color.Orange,
                2,
                LineStyle.Solid);
        }

        private bool CanPlaceNewEntry()
        {
            if (_provenanceEntriesBlocked || !_configurationProvenanceInitialized)
            {
                LogActiveWarning("provenance-entry-barrier", "PROVENANCE", "ENTRY_DISABLED",
                    "new entries are disabled because configuration provenance is unavailable");
                return false;
            }
            if (_riskConversionEntriesBlocked)
            {
                LogActiveWarning("risk-conversion-entry-barrier", "RISK_CONVERSION", "ENTRY_DISABLED",
                    "new entries are disabled while broker risk conversion state={0}; nextRetryUtc={1}",
                    _riskConversionRecoveryState,
                    _nextRiskConversionRetryUtc.HasValue
                        ? _nextRiskConversionRetryUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                        : "none");
                return false;
            }
            if (!IsTradingSessionOpen(Server.TimeInUtc))
            {
                LogActiveWarning("market-session-entry-barrier", "SESSION", "ENTRY_DISABLED",
                    "new entries are disabled while market session state={0}",
                    GetMarketSessionState(Server.TimeInUtc));
                return false;
            }
            if (!HasExecutableMarketQuote(Server.TimeInUtc))
            {
                LogActiveWarning("market-session-quote-barrier", "SESSION", "ENTRY_WAITING_FOR_EXECUTABLE_QUOTE",
                    "new entries remain disabled until a valid quote is observed in the active market session");
                return false;
            }
            if (!CanPlaceNewStrategyEntries)
            {
                LogActiveWarning("startup-entry-barrier", "STARTUP", "ENTRY_DISABLED",
                    "new entries are disabled until startup strategy and the rolling Ref-hi/Ref-lo range are ready");
                return false;
            }
            ClearWarning("startup-entry-barrier", "STARTUP", "ENTRY_BARRIER_CLEARED",
                "startup strategy and the rolling reference range are ready");
            if (_rolloverContext.Phase != RolloverPhase.Normal)
            {
                LogActiveWarning("rollover-entry-barrier", "ROLLOVER", "ENTRY_DISABLED",
                    "new entries are disabled while rollover phase={0}", _rolloverContext.Phase);
                return false;
            }
            if (_runtimeFaulted)
            {
                LogActiveWarning("runtime-entry-barrier", "RUNTIME", "ENTRY_DISABLED",
                    "new entries remain disabled after an unexpected callback exception");
                return false;
            }
            if (!_hasValidHourlyMev)
            {
                LogActiveWarning("hourly-mev", "REGIME", "ENTRY_DISABLED",
                    "new entries are disabled until a valid hourly MEV is available");
                return false;
            }

            if (_effectiveTradingMode != TradingMode.ProbabilisticAuto ||
                _hasValidDailyMev)
            {
                RecordStrategyOperationalStartForFitness(Server.TimeInUtc);
            }

            return true;
        }

        private string GetEntryBarrierReason()
        {
            if (!IsStartupOperational || !_atrConfigurationReady || _startupStopRequested)
                return "startupNotOperational";
            if (!IsTradingSessionOpen(Server.TimeInUtc))
                return "marketSessionNotOpen";
            if (!HasExecutableMarketQuote(Server.TimeInUtc))
                return "executableQuoteUnavailable";
            if (!_referenceRangeReady)
                return "referenceRangeNotReady";
            if (_rolloverContext.Phase != RolloverPhase.Normal)
                return "rolloverActive";
            if (_runtimeFaulted)
                return "runtimeEntryFault";
            if (_strategyInitializationInProgress)
                return "strategyInitializationInProgress";
            if (!_hasValidHourlyMev)
                return "hourlyMevUnavailable";
            return "unknownEntryBarrier";
        }

        private void TrailAllBotPendingOrders(TickSnapshot tick)
        {
            // OnTick invokes this once per scheduled maintenance pass.
            RemoveStalePendingOrderTrailingStates();
            int currentUtcHourKey = tick.UtcHourKey;
            double currentVolatilityFactor = tick.VolatilityFactor;
            // Stable reusable snapshot required: cancellation and modification mutate PendingOrders.
            // This snapshot keeps order iteration stable while cancellation or modification
            // mutates the live collection.
            CaptureBotPendingOrders(_pendingOrderTrailingSnapshot);
            foreach (var order in _pendingOrderTrailingSnapshot)
            {
                TrailSinglePendingOrder(order, tick);
            }
        }
        private void TrailSinglePendingOrder(
            PendingOrder order,
            TickSnapshot tick)
        {
            bool sideIsAllocated = IsSideAllocated(order.TradeType);
            if (!sideIsAllocated)
            {
                CancelPendingOrderWithRetry(order, "unallocated-side");
                return;
            }
            bool trailingDistanceChangedThisCycle = UpdatePendingOrderTrailingState(
                order,
                tick.UtcHourKey,
                tick.VolatilityFactor);
            PendingOrderTrailingState trailingState = _pendingOrderTrailingStates[order.Id];
            if (trailingState.CapsuleRecoveryFailed)
            {
                PendingOrderCancellationAttempt capsuleCancelAttempt =
                    CancelPendingOrderWithRetry(order, "persistent-capsule-invalid");
                if (capsuleCancelAttempt.Outcome == PendingOrderCancellationOutcome.Successful)
                    _pendingOrderTrailingStates.Remove(order.Id);
                return;
            }
            bool isBuy = order.TradeType == TradeType.Buy;
            if (order.OrderType == PendingOrderType.Stop)
                return;
            if (trailingState.Strategy == StrategyKind.TrendFollowing)
            {
                TrailTrendFollowingPendingOrder(order, trailingState, trailingDistanceChangedThisCycle, tick);
                return;
            }
            // Use the market side corresponding to the pending-order side:
            // Ask for a buy limit and Bid for a sell limit.
            double currentMovementPrice = isBuy ? tick.Ask : tick.Bid;
            double movementReferencePrice = trailingState.MovementReferencePrice;
            bool crossedReference = isBuy
                ? currentMovementPrice > movementReferencePrice
                : currentMovementPrice < movementReferencePrice;
            if (crossedReference)
            {
                PendingOrderCancellationAttempt cancelAttempt = CancelPendingOrderWithRetry(order, "mr-reference-crossed");
                if (cancelAttempt.Outcome == PendingOrderCancellationOutcome.Successful)
                {
                    _pendingOrderTrailingStates.Remove(order.Id);
                    LogNormal("ORDER", "REFERENCE_CROSSED",
                        "orderId={0} side={1} current={2} reference={3}; pending order cancelled",
                        order.Id,
                        order.TradeType,
                        FormatPrice(currentMovementPrice),
                        FormatPrice(movementReferencePrice));
                }
                return;
            }
            double trailDistance = Math.Abs(
                movementReferencePrice - trailingState.OriginalTargetPrice);
            double currentDistanceFromReference = Math.Abs(
                currentMovementPrice - movementReferencePrice);
            bool referenceGateAllowsTrailing = IsFinitePositive(trailDistance) &&
                currentDistanceFromReference > 0.5 * trailDistance;
            double desiredTarget = order.TargetPrice;
            double hourlyDistancePercent = tick.VolatilityFactor * (trailingState.Capsule != null
                ? trailingState.Capsule.LimitDistancePercent
                : (isBuy
                    ? _actualMrBuyLimitDistancePercent
                    : _actualMrSellLimitDistancePercent));
            double candidate = NormalizePrice(isBuy
                ? tick.Ask * (1.0 - hourlyDistancePercent / 100.0)
                : tick.Bid * (1.0 + hourlyDistancePercent / 100.0));
            // On an hourly factor change, preserve the entry target for this cycle.
            if (referenceGateAllowsTrailing &&
                !trailingDistanceChangedThisCycle &&
                ((isBuy && candidate > order.TargetPrice && candidate < tick.Ask) ||
                 (!isBuy && candidate < order.TargetPrice && candidate > tick.Bid)))
            {
                desiredTarget = candidate;
            }
            double desiredSlPercent = tick.VolatilityFactor * (trailingState.Capsule != null
                ? trailingState.Capsule.StopLossPercent
                : (isBuy
                    ? _actualMrBuyStopLossPercent
                    : _actualMrSellStopLossPercent));
            double desiredTpPercent = tick.VolatilityFactor * (trailingState.Capsule != null
                ? trailingState.Capsule.TakeProfitPercent
                : (isBuy
                    ? _actualMrBuyTakeProfitPercent
                    : _actualMrSellTakeProfitPercent));
            double allocationAdjustedVolumeInUnits = GetAllocationAdjustedVolumeInUnits(
                order.TradeType);
            double desiredVolumeInUnits = ApplyHourlyVolatilityToVolume(
                allocationAdjustedVolumeInUnits,
                tick.VolatilityFactor);
            double desiredStopLossPips = PercentDistanceToPips(desiredTarget, desiredSlPercent);
            double desiredTakeProfitPips = PercentDistanceToPips(desiredTarget, desiredTpPercent);
            desiredTarget = NormalizePrice(desiredTarget);
            desiredVolumeInUnits = NormalizeVolume(desiredVolumeInUnits);
            desiredStopLossPips = NormalizeProtectionPipsUp(desiredStopLossPips);
            desiredTakeProfitPips = NormalizeProtectionPipsUp(desiredTakeProfitPips);
            double adjustedMinimumTrailStepPips =
                GetEffectiveMinimumPositionTrailStepPips(tick);
            double minimumTrailStepPrice = Math.Max(
                adjustedMinimumTrailStepPips * Symbol.PipSize,
                Symbol.TickSize * 0.5);
            double minimumTrailStepPips = Math.Max(
                adjustedMinimumTrailStepPips,
                GetProtectionPipStep() * 0.5);
            bool targetChanged = Math.Abs(desiredTarget - order.TargetPrice) >= minimumTrailStepPrice;
            bool volumeChanged = Math.Abs(desiredVolumeInUnits - order.VolumeInUnits) >=
                Symbol.VolumeInUnitsStep * 0.5;
            bool stopLossChanged = !order.StopLossPips.HasValue ||
                Math.Abs(desiredStopLossPips - NormalizeProtectionPips(order.StopLossPips.Value)) >=
                minimumTrailStepPips;
            bool takeProfitChanged = !order.TakeProfitPips.HasValue ||
                Math.Abs(desiredTakeProfitPips - NormalizeProtectionPips(order.TakeProfitPips.Value)) >=
                minimumTrailStepPips;
            double targetToSend = targetChanged ? desiredTarget : order.TargetPrice;
            double stopLossPipsToSend = stopLossChanged
                ? desiredStopLossPips
                : order.StopLossPips.Value;
            double takeProfitPipsToSend = takeProfitChanged
                ? desiredTakeProfitPips
                : order.TakeProfitPips.Value;
            if (targetChanged || volumeChanged || stopLossChanged || takeProfitChanged)
            {
                ModifyTrailingOrder(
                    order,
                    targetToSend,
                    stopLossPipsToSend,
                    takeProfitPipsToSend,
                    desiredVolumeInUnits,
                    tick);
            }
        }
        private void TrailTrendFollowingPendingOrder(PendingOrder order, PendingOrderTrailingState state,
            bool trailingDistanceChangedThisCycle, TickSnapshot tick)
        {
            bool isBuy = order.TradeType == TradeType.Buy;
            double currentMovementPrice = isBuy ? tick.Ask : tick.Bid;
            double movementReferencePrice = state.MovementReferencePrice;
            double movePercent = isBuy
                ? (state.Capsule == null ? _actualTfLongMovePercent : state.Capsule.MovePercent)
                : (state.Capsule == null ? _actualTfShortMovePercent : state.Capsule.MovePercent);
            double movementDistance = Math.Abs(currentMovementPrice - movementReferencePrice);
            double movePriceDistance = movementReferencePrice * movePercent / 100.0;
            double cancellationDistance =
                TfPendingOrderCancellationMoveMultiplier * movePriceDistance;

            if (IsFinitePositive(movePriceDistance) &&
                movementDistance >= cancellationDistance)
            {
                PendingOrderCancellationAttempt cancelAttempt = CancelPendingOrderWithRetry(order, "tf-movement-threshold");
                if (cancelAttempt.Outcome == PendingOrderCancellationOutcome.Successful)
                {
                    _pendingOrderTrailingStates.Remove(order.Id);
                    LogNormal("ORDER", "TF_MOVE_CANCELLED",
                        "orderId={0} side={1} current={2} reference={3} d_m={4} p_m={5} threshold={6}; pending order cancelled",
                        order.Id,
                        order.TradeType,
                        FormatPrice(currentMovementPrice),
                        FormatPrice(movementReferencePrice),
                        FormatPrice(movementDistance),
                        FormatPrice(movePriceDistance),
                        FormatPrice(cancellationDistance));
                }
                return;
            }

            double distancePercent = tick.VolatilityFactor * (state.Capsule != null
                ? state.Capsule.LimitDistancePercent
                : (isBuy ? _actualTfBuyLimitDistancePercent : _actualTfSellLimitDistancePercent));
            double desiredTarget = order.TargetPrice;
            double candidate = NormalizePrice(isBuy
                ? tick.Ask * (1.0 - distancePercent / 100.0)
                : tick.Bid * (1.0 + distancePercent / 100.0));
            if (!trailingDistanceChangedThisCycle &&
                ((isBuy && candidate > order.TargetPrice && candidate < tick.Ask) ||
                 (!isBuy && candidate < order.TargetPrice && candidate > tick.Bid)))
                desiredTarget = candidate;
            double slPercent = tick.VolatilityFactor * (state.Capsule != null
                ? state.Capsule.StopLossPercent
                : (isBuy ? _actualTfLongStopLossPercent : _actualTfShortStopLossPercent));
            double tpPercent = tick.VolatilityFactor * (state.Capsule != null
                ? state.Capsule.TakeProfitPercent
                : (isBuy ? _actualTfLongTakeProfitPercent : _actualTfShortTakeProfitPercent));
            AmendPendingOrderIfNeeded(order, desiredTarget, slPercent, tpPercent, tick);
        }
        private void AmendPendingOrderIfNeeded(PendingOrder order, double desiredTarget, double slPercent, double tpPercent, TickSnapshot tick)
        {
            double baseVolume = GetAllocationAdjustedVolumeInUnits(order.TradeType);
            double desiredVolume = ApplyHourlyVolatilityToVolume(baseVolume, tick.VolatilityFactor);
            if (!IsTradableVolume(desiredVolume))
            {
                LogVerbose("ORDER", "MAINTENANCE_SKIPPED_INVALID_VOLUME", "Pending order {0} maintenance skipped: calculated volume {1} is outside broker range {2}-{3}.",
                    order.Id, desiredVolume, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax);
                return;
            }
            desiredTarget = NormalizePrice(desiredTarget);
            double desiredSl = NormalizeProtectionPipsUp(PercentDistanceToPips(desiredTarget, slPercent));
            double desiredTp = NormalizeProtectionPipsUp(PercentDistanceToPips(desiredTarget, tpPercent));
            double minPips = Math.Max(GetEffectiveMinimumPositionTrailStepPips(tick), GetProtectionPipStep() * 0.5);
            double minPrice = Math.Max(GetEffectiveMinimumPositionTrailStepPips(tick) * Symbol.PipSize, Symbol.TickSize * 0.5);
            bool targetChanged = Math.Abs(desiredTarget - order.TargetPrice) >= minPrice;
            bool volumeChanged = Math.Abs(desiredVolume - order.VolumeInUnits) >= Symbol.VolumeInUnitsStep * 0.5;
            bool slChanged = !order.StopLossPips.HasValue || Math.Abs(desiredSl - NormalizeProtectionPips(order.StopLossPips.Value)) >= minPips;
            bool tpChanged = !order.TakeProfitPips.HasValue || Math.Abs(desiredTp - NormalizeProtectionPips(order.TakeProfitPips.Value)) >= minPips;
            if (targetChanged || volumeChanged || slChanged || tpChanged)
                ModifyTrailingOrder(order, targetChanged ? desiredTarget : order.TargetPrice,
                    slChanged ? desiredSl : order.StopLossPips.Value,
                    tpChanged ? desiredTp : order.TakeProfitPips.Value,
                    desiredVolume,
                    tick);
        }
        private bool UpdatePendingOrderTrailingState(
            PendingOrder order,
            int currentUtcHourKey,
            double currentVolatilityFactor)
        {
            PendingOrderTrailingState trailingStateCandidate;
            if (!_pendingOrderTrailingStates.TryGetValue(order.Id, out trailingStateCandidate))
            {
                TryGetCapsuleFromOrder(order, out PersistentTradeCapsule capsule);
                bool capsuleRecoveryFailed = capsule == null &&
                    HasPersistentCapsuleMarker(order.Comment);
                StrategyKind strategy = capsule == null
                    ? GetStrategy(order.Label)
                    : capsule.Strategy;
                _pendingOrderTrailingStates[order.Id] = new PendingOrderTrailingState
                {
                    Strategy = strategy,
                    Capsule = capsule,
                    CapsuleRecoveryFailed = capsuleRecoveryFailed,
                    EntryOrigin = EntryOriginMode.Unattributed,
                    Evidence = GetPendingOrderEvidence(order.Id) ??
                        CreateUnknownTradeEvidence(),
                    LastUtcHourKey = currentUtcHourKey,
                    LastVolatilityFactor = currentVolatilityFactor,
                    OriginalVolatilityFactor = currentVolatilityFactor,
                    OriginalStopLossPips = capsule != null
                        ? capsule.OriginalStopLossPips
                        : order.StopLossPips.HasValue
                            ? NormalizeProtectionPips(order.StopLossPips.Value)
                            : double.NaN,
                    OriginalTakeProfitPips = capsule != null
                        ? capsule.OriginalTakeProfitPips
                        : order.TakeProfitPips.HasValue
                            ? NormalizeProtectionPips(order.TakeProfitPips.Value)
                            : double.NaN,
                    TradeType = order.TradeType,
                    VolumeInUnits = order.VolumeInUnits,
                    Label = order.Label,
                    // Fallback for an order that existed before this cBot instance started.
                    // Orders placed by this instance are registered at placement with their
                    // true original target and movement reference.
                    OriginalTargetPrice = capsule == null
                        ? order.TargetPrice
                        : capsule.OriginalTargetPrice,
                    MovementReferencePrice = capsule == null
                        ? GetMovementReferencePrice(strategy, order.TradeType)
                        : capsule.MovementReferencePrice
                };
                return false;
            }

            if (!IsFinitePositive(trailingStateCandidate.MovementReferencePrice) &&
                _referenceRangeReady)
            {
                trailingStateCandidate.MovementReferencePrice =
                    GetMovementReferencePrice(trailingStateCandidate.Strategy, order.TradeType);
            }

            if (trailingStateCandidate.LastUtcHourKey == currentUtcHourKey)
                return false;
            bool distanceChanged = Math.Abs(
                currentVolatilityFactor - trailingStateCandidate.LastVolatilityFactor) > 1e-12;
            trailingStateCandidate.LastUtcHourKey = currentUtcHourKey;
            trailingStateCandidate.LastVolatilityFactor = currentVolatilityFactor;
            if (distanceChanged)
            {
                LogNormal("ORDER", "HOURLY",
                    "orderId={0} vf={1:F3} targetHeld={2} retraceFromNextCycle=true",
                    order.Id, currentVolatilityFactor, FormatPrice(order.TargetPrice));
            }
            return distanceChanged;
        }
        #endregion


    }
}
