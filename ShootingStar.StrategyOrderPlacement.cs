using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Strategy order placement and placement-result reconciliation.
    public partial class ShootingStar
    {
        #region Strategy Order Placement
        private bool HasRequiredPriceMove(
            TradeType tradeType,
            StrategyKind strategy,
            TickSnapshot tick)
        {
            if (strategy == StrategyKind.MeanReversion)
            {
                if (tradeType == TradeType.Buy)
                {
                    return MovementGatePasses(
                        tick.ReferenceHigh - tick.Bid,
                        tick.ReferenceHigh * _actualMrDownMovePercent / 100.0);
                }

                if (tradeType == TradeType.Sell)
                {
                    return MovementGatePasses(
                        tick.Bid - tick.ReferenceLow,
                        tick.ReferenceLow * _actualMrUpMovePercent / 100.0);
                }
            }
            else if (strategy == StrategyKind.TrendFollowing)
            {
                if (tradeType == TradeType.Buy)
                {
                    return MovementGatePasses(
                        tick.Bid - tick.ReferenceLow,
                        tick.ReferenceLow * _actualTfLongMovePercent / 100.0);
                }

                if (tradeType == TradeType.Sell)
                {
                    return MovementGatePasses(
                        tick.ReferenceHigh - tick.Bid,
                        tick.ReferenceHigh * _actualTfShortMovePercent / 100.0);
                }
            }

            throw new ArgumentOutOfRangeException(
                tradeType == TradeType.Buy || tradeType == TradeType.Sell
                    ? nameof(strategy)
                    : nameof(tradeType));
        }

        private PolicyPlacementResult TryPlacePolicySelectedOrder(
            PendingPolicyDecision decision,
            TickSnapshot tick)
        {
            PolicyPlacementResult barrier = GetPolicyDecisionBarrier(tick.UtcNow);
            if (barrier != null)
                return barrier;

            if (decision.SubmissionCorrelationId.HasValue)
            {
                foreach (PendingOrder existingOrder in PendingOrders)
                {
                    if (GetSubmissionCorrelationId(existingOrder.Id) ==
                        decision.SubmissionCorrelationId)
                    {
                        return PolicyPlacementResult.Success(
                            existingOrder.Id,
                            "existingCorrelatedOrder",
                            requestIssued: true);
                    }
                }

                foreach (Position existingPosition in Positions)
                {
                    if (GetPositionTradeIdeaId(existingPosition.Id) ==
                        decision.SubmissionCorrelationId)
                    {
                        return PolicyPlacementResult.Failure(
                            PlacementFailureClass.Permanent,
                            "submissionAlreadyFilled");
                    }
                }
            }

            if (!IsSideAllocated(decision.Direction))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "directionalAllocationRemoved");
            }

            if (!IsTradableVolume(decision.VolumeInUnits))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "directionalVolumeUnavailable");
            }

            return PlacePolicyStrategyLimitWithVolume(
                decision.Strategy,
                decision.Direction,
                decision.TargetPrice,
                decision.MovePercent,
                decision.StopLossPercent,
                decision.TakeProfitPercent,
                decision.VolatilityFactor,
                decision.Label,
                decision.MovementReferencePrice,
                decision.Description,
                decision.VolumeInUnits,
                tick,
                decision.OrderKind);
        }

        private bool TryPlaceMrSellOrderAfterUpMove(TickSnapshot tick)
        {
            if (!MovementGatePasses(
                    tick.Bid - tick.ReferenceLow,
                    tick.ReferenceLow * _actualMrUpMovePercent / 100.0))
                return false;

            if (!IsSideAllocated(TradeType.Sell))
                return false;

            double vf = tick.VolatilityFactor;
            double target = NormalizePrice(tick.Bid * (1.0 + _actualMrSellLimitDistancePercent * vf / 100.0));
            return PlaceStrategyLimit(StrategyKind.MeanReversion, TradeType.Sell, target, _actualMrUpMovePercent,
                _actualMrSellStopLossPercent, _actualMrSellTakeProfitPercent, vf, _mrShortLabel, tick.ReferenceLow, "MR sell", tick);
        }

        private bool TryPlaceMrBuyOrderAfterDownMove(TickSnapshot tick)
        {
            if (!MovementGatePasses(
                    tick.ReferenceHigh - tick.Bid,
                    tick.ReferenceHigh * _actualMrDownMovePercent / 100.0))
                return false;

            if (!IsSideAllocated(TradeType.Buy))
                return false;

            double vf = tick.VolatilityFactor;
            double target = NormalizePrice(tick.Ask * (1.0 - _actualMrBuyLimitDistancePercent * vf / 100.0));
            return PlaceStrategyLimit(StrategyKind.MeanReversion, TradeType.Buy, target, _actualMrDownMovePercent,
                _actualMrBuyStopLossPercent, _actualMrBuyTakeProfitPercent, vf, _mrLongLabel, tick.ReferenceHigh, "MR buy", tick);
        }

        private bool TryPlaceTfLongOrderAfterUpMove(TickSnapshot tick)
        {
            if (!MovementGatePasses(
                    tick.Bid - tick.ReferenceLow,
                    tick.ReferenceLow * _actualTfLongMovePercent / 100.0))
                return false;

            if (!IsSideAllocated(TradeType.Buy))
                return false;

            double vf = tick.VolatilityFactor;
            double target = NormalizePrice(tick.Ask * (1.0 - _actualTfBuyLimitDistancePercent * vf / 100.0));
            return PlaceStrategyLimit(StrategyKind.TrendFollowing, TradeType.Buy, target, _actualTfLongMovePercent,
                _actualTfLongStopLossPercent, _actualTfLongTakeProfitPercent, vf, _tfLongLabel, tick.ReferenceLow, "TF long", tick);
        }

        private bool TryPlaceTfShortOrderAfterDownMove(TickSnapshot tick)
        {
            if (!MovementGatePasses(
                    tick.ReferenceHigh - tick.Bid,
                    tick.ReferenceHigh * _actualTfShortMovePercent / 100.0))
                return false;

            if (!IsSideAllocated(TradeType.Sell))
                return false;

            double vf = tick.VolatilityFactor;
            double target = NormalizePrice(tick.Bid * (1.0 + _actualTfSellLimitDistancePercent * vf / 100.0));
            return PlaceStrategyLimit(StrategyKind.TrendFollowing, TradeType.Sell, target, _actualTfShortMovePercent,
                _actualTfShortStopLossPercent, _actualTfShortTakeProfitPercent, vf, _tfShortLabel, tick.ReferenceHigh, "TF short", tick);
        }

        private bool TryPlaceManualStrategyLimit(
            TradeType tradeType,
            TickSnapshot tick,
            out string status,
            out StrategyKind selectedStrategy)
        {
            selectedStrategy = StrategyKind.MeanReversion;
            if (!CanPlaceNewEntry())
            {
                status = "Order not submitted: new entries are currently unavailable. See the log.";
                return false;
            }

            StrategyKind strategy;
            if (_effectiveTradingMode == TradingMode.RuleMappedAuto ||
                _effectiveTradingMode == TradingMode.UserTriggered)
            {
                if (!_hasValidHourlyMev)
                {
                    status = "Order not submitted: a valid hourly MEV is required to select the Manual MEV band.";
                    return false;
                }
                ManualSideStrategy sideStrategy = GetManualSideStrategy(tradeType);
                if (sideStrategy == ManualSideStrategy.None)
                {
                    status = "Order not submitted: no strategy is selected for this direction and rank band.";
                    return false;
                }
                strategy = sideStrategy == ManualSideStrategy.MeanReversion
                    ? StrategyKind.MeanReversion
                    : StrategyKind.TrendFollowing;
            }
            else if (_hasValidHourlyMev)
            {
                strategy = GetActiveAutoStrategy(tradeType);
            }
            else
            {
                status = "Order not submitted: the active strategy is unavailable.";
                return false;
            }

            selectedStrategy = strategy;
            if (!IsStrategyAllowedByHourlyRegime(strategy, tradeType))
            {
                status = "Order not submitted: the selected strategy is not active for the current MEV band.";
                return false;
            }

            double vf = tick.VolatilityFactor;
            double movePercent;
            double limitDistancePercent;
            double stopLossPercent;
            double takeProfitPercent;
            string label;
            string description;

            if (strategy == StrategyKind.MeanReversion)
            {
                if (tradeType == TradeType.Buy)
                {
                    movePercent = _actualMrDownMovePercent;
                    limitDistancePercent = _actualMrBuyLimitDistancePercent;
                    stopLossPercent = _actualMrBuyStopLossPercent;
                    takeProfitPercent = _actualMrBuyTakeProfitPercent;
                    label = _mrLongLabel;
                    description = "User-triggered MR long";
                }
                else
                {
                    movePercent = _actualMrUpMovePercent;
                    limitDistancePercent = _actualMrSellLimitDistancePercent;
                    stopLossPercent = _actualMrSellStopLossPercent;
                    takeProfitPercent = _actualMrSellTakeProfitPercent;
                    label = _mrShortLabel;
                    description = "User-triggered MR short";
                }
            }
            else if (tradeType == TradeType.Buy)
            {
                movePercent = _actualTfLongMovePercent;
                limitDistancePercent = _actualTfBuyLimitDistancePercent;
                stopLossPercent = _actualTfLongStopLossPercent;
                takeProfitPercent = _actualTfLongTakeProfitPercent;
                label = _tfLongLabel;
                description = "User-triggered TF long";
            }
            else
            {
                movePercent = _actualTfShortMovePercent;
                limitDistancePercent = _actualTfSellLimitDistancePercent;
                stopLossPercent = _actualTfShortStopLossPercent;
                takeProfitPercent = _actualTfShortTakeProfitPercent;
                label = _tfShortLabel;
                description = "User-triggered TF short";
            }

            double target = tradeType == TradeType.Buy
                ? NormalizePrice(tick.Ask * (1.0 - limitDistancePercent * vf / 100.0))
                : NormalizePrice(tick.Bid * (1.0 + limitDistancePercent * vf / 100.0));
            double minimumVolume = Symbol.NormalizeVolumeInUnits(
                Symbol.VolumeInUnitsMin,
                RoundingMode.Up);
            if (!IsTradableVolume(minimumVolume))
            {
                status = "Order not submitted: the broker minimum volume is not tradable.";
                LogError("ORDER", "MANUAL_ENTRY_INVALID_MINIMUM_VOLUME",
                    "strategy={0} side={1} brokerMinimum={2}",
                    strategy, tradeType, Symbol.VolumeInUnitsMin);
                return false;
            }

            bool submitted = PlaceStrategyLimitWithVolume(
                strategy,
                tradeType,
                target,
                movePercent,
                stopLossPercent,
                takeProfitPercent,
                vf,
                label,
                GetMovementReferencePrice(strategy, tradeType),
                description,
                minimumVolume,
                tick,
                false,
                EntryOriginMode.UserTriggered);
            status = submitted
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} limit order submitted at {2} (minimum volume {3}).",
                    strategy,
                    tradeType == TradeType.Buy ? "Long" : "Short",
                    FormatPrice(target),
                    minimumVolume)
                : "Order not submitted: an entry or broker safety check rejected it. See the log.";
            return submitted;
        }

        private bool PlaceStrategyLimit(StrategyKind strategy, TradeType tradeType, double target, double movePercent,
            double stopLossPercent, double takeProfitPercent, double vf, string label, double movementReferencePrice,
            string description, TickSnapshot tick, bool policySelectedStrategy = false,
            EntryOriginMode entryOrigin = EntryOriginMode.RuleMappedAuto)
        {
            if (!CanPlaceNewEntry())
                return false;
            if (!policySelectedStrategy &&
                !IsStrategyAllowedByHourlyRegime(strategy, tradeType))
                return false;

            double baseVolume = GetAllocationAdjustedVolumeInUnits(tradeType);
            if (baseVolume <= 0.0)
                return false;

            double volume = ApplyHourlyVolatilityToVolume(baseVolume, vf);
            return PlaceStrategyLimitWithVolume(
                strategy,
                tradeType,
                target,
                movePercent,
                stopLossPercent,
                takeProfitPercent,
                vf,
                label,
                movementReferencePrice,
                description,
                volume,
                tick,
                policySelectedStrategy,
                entryOrigin);
        }

        private bool PlaceStrategyLimitWithVolume(
            StrategyKind strategy,
            TradeType tradeType,
            double target,
            double movePercent,
            double stopLossPercent,
            double takeProfitPercent,
            double vf,
            string label,
            double movementReferencePrice,
            string description,
            double volume,
            TickSnapshot tick,
            bool policySelectedStrategy = false,
            EntryOriginMode entryOrigin = EntryOriginMode.RuleMappedAuto)
        {
            return PlaceStrategyLimitWithVolumeCore(
                strategy,
                tradeType,
                target,
                movePercent,
                stopLossPercent,
                takeProfitPercent,
                vf,
                label,
                movementReferencePrice,
                description,
                volume,
                tick,
                policySelectedStrategy,
                entryOrigin).Submitted;
        }

        private PolicyPlacementResult PlacePolicyStrategyLimitWithVolume(
            StrategyKind strategy,
            TradeType tradeType,
            double target,
            double movePercent,
            double stopLossPercent,
            double takeProfitPercent,
            double vf,
            string label,
            double movementReferencePrice,
            string description,
            double volume,
            TickSnapshot tick,
            V2OrderKind orderKind)
        {
            return PlaceStrategyLimitWithVolumeCore(
                strategy,
                tradeType,
                target,
                movePercent,
                stopLossPercent,
                takeProfitPercent,
                vf,
                label,
                movementReferencePrice,
                description,
                volume,
                tick,
                true,
                EntryOriginMode.ProbabilisticAuto,
                orderKind);
        }

        private PolicyPlacementResult PlaceStrategyLimitWithVolumeCore(
            StrategyKind strategy,
            TradeType tradeType,
            double target,
            double movePercent,
            double stopLossPercent,
            double takeProfitPercent,
            double vf,
            string label,
            double movementReferencePrice,
            string description,
            double volume,
            TickSnapshot tick,
            bool policySelectedStrategy,
            EntryOriginMode entryOrigin,
            V2OrderKind orderKind = V2OrderKind.Limit)
        {
            if (!CanPlaceNewEntry())
            {
                return policySelectedStrategy
                    ? GetPolicyDecisionBarrier(tick.UtcNow) ??
                        PolicyPlacementResult.Failure(
                            PlacementFailureClass.Transient,
                            "entryBarrierUnavailable")
                    : PolicyPlacementResult.Failure(
                        PlacementFailureClass.Permanent,
                        "entryBarrierUnavailable");
            }

            if (!policySelectedStrategy &&
                !IsStrategyAllowedByHourlyRegime(strategy, tradeType))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "strategyNotAllowedByCurrentRegime");
            }

            if (IsCircuitBreakerPaused(label, Server.TimeInUtc, out DateTime circuitBreakerExpiryUtc))
            {
                RecordEntrySkip("circuit-breaker:" + label);
                LogActiveWarning(
                    "circuit-breaker-" + label,
                    "CIRCUIT_BREAKER",
                    "ENTRY_PAUSED",
                    "label={0} expiresUtc={1:O}",
                    label,
                    circuitBreakerExpiryUtc);
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "circuitBreakerActive",
                    circuitBreakerExpiryUtc.ToString("O", CultureInfo.InvariantCulture));
            }

            double market = tradeType == TradeType.Buy ? tick.Ask : tick.Bid;
            bool targetIsOnOrBeyondMarket = orderKind == V2OrderKind.Stop
                ? (tradeType == TradeType.Buy && target <= market) ||
                    (tradeType == TradeType.Sell && target >= market)
                : (tradeType == TradeType.Buy && target >= market) ||
                    (tradeType == TradeType.Sell && target <= market);
            if (targetIsOnOrBeyondMarket)
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "targetCrossedByCurrentQuote");

            if (!IsPendingTargetDistanceValid(
                    tradeType,
                    orderKind,
                    target,
                    tick,
                    description + " pending-order snapshot validation"))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "targetDistanceInvalidAtSnapshot");
            }

            if (!IsTradableVolume(volume))
            {
                LogVerbose("RISK_CAP", "VOLUME_OUTSIDE_BROKER_RANGE", "{0} skipped: calculated volume {1} is outside broker range {2}-{3}.",
                    description, volume, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax);
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "volumeOutsideBrokerRange");
            }

            double slPips = NormalizeProtectionPipsUp(PercentDistanceToPips(target, stopLossPercent * vf));
            double tpPips = NormalizeProtectionPipsUp(PercentDistanceToPips(
                target,
                takeProfitPercent * GetScaleUpMultiplier(label) * vf));
            if (!ValidateRelativeProtectionDistances(
                    target,
                    slPips,
                    tpPips,
                    description + " pending-order placement"))
            {
                return PolicyPlacementResult.Failure(
                    IsFinitePositive(target) &&
                    IsFinitePositive(slPips) &&
                    IsFinitePositive(tpPips)
                        ? PlacementFailureClass.Transient
                        : PlacementFailureClass.Permanent,
                    "protectionDistanceInvalid");
            }

            if (!PassesOrderFootprintSafeDistanceCheck(
                    tradeType,
                    target,
                    slPips,
                    tpPips,
                    SafeDistanceMultiplier * movePercent))
            {
                return PolicyPlacementResult.Failure(
                    _provisionalSafeDistancePendingOrders.Count > 0
                        ? PlacementFailureClass.Transient
                        : PlacementFailureClass.Permanent,
                    _provisionalSafeDistancePendingOrders.Count > 0
                        ? "provisionalSafeDistanceUnresolved"
                        : "safeSeparationConflict");
            }

            if (!IsPendingTargetDistanceValid(
                    tradeType,
                    orderKind,
                    target,
                    tick,
                    description + " pending-order final live validation"))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "targetDistanceInvalidAtFinalCheck");
            }

            // Last placement safeguard: cap this individual order's estimated
            // stop-loss risk at 2% of the current account equity.
            PendingOrderRiskCapDecision riskCapDecision;
            if (!TryCapPendingOrderVolumeByEquityRisk(
                    volume,
                    slPips,
                    description + " pending-order placement",
                    out volume,
                    out riskCapDecision))
            {
                return PolicyPlacementResult.Failure(
                    _lastRiskCapFailureWasConversionTimeout
                        ? PlacementFailureClass.Transient
                        : PlacementFailureClass.Permanent,
                    _lastRiskCapFailureWasConversionTimeout
                        ? "riskConversionTimeout"
                        : "riskCapRejected");
            }

            LogRiskCapDecision(description + " pending-order placement", riskCapDecision);
            var capsule = new PersistentTradeCapsule
            {
                Strategy = strategy,
                TradeType = tradeType,
                SymbolFingerprint = ComputeSymbolFingerprint(Symbol.Name),
                FractalFactor = FractalFactor,
                OriginalTargetPrice = target,
                MovementReferencePrice = movementReferencePrice,
                OriginalStopLossPips = slPips,
                OriginalTakeProfitPips = tpPips,
                OriginalVolumeInUnits = volume,
                MovePercent = movePercent,
                LimitDistancePercent = Math.Abs(
                    (target - (tradeType == TradeType.Buy ? tick.Ask : tick.Bid)) /
                    (tradeType == TradeType.Buy ? tick.Ask : tick.Bid)) *
                    100.0 / (orderKind == V2OrderKind.Stop ? 1.0 : vf),
                StopLossPercent = stopLossPercent,
                TakeProfitPercent = takeProfitPercent
            };
            Guid? preferredSubmissionCorrelationId =
                policySelectedStrategy &&
                _pendingPolicyDecision != null
                    ? _pendingPolicyDecision.SubmissionCorrelationId
                    : null;
            Guid? submissionCorrelationId = BeginOrderSubmissionTracking(
                strategy,
                tradeType,
                target,
                volume,
                preferredSubmissionCorrelationId);
            if (policySelectedStrategy &&
                _pendingPolicyDecision != null &&
                submissionCorrelationId.HasValue)
            {
                _pendingPolicyDecision.SubmissionCorrelationId =
                    submissionCorrelationId;
            }
            if (!submissionCorrelationId.HasValue)
            {
                LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_SKIPPED_UNCORRELATED",
                    "strategy={0} side={1} outcome=skipped reason=persistentCapsuleIdentityUnavailable",
                    strategy, tradeType);
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "submissionCorrelationUnavailable");
            }

            TradeResult result;
            string comment = GetPersistentTradeComment(submissionCorrelationId, capsule);
            if (comment == null)
            {
                RunTelemetry("AbortOrderSubmissionTracking",
                    () => AbortOrderSubmissionTracking(
                        submissionCorrelationId,
                        new InvalidOperationException("Submission lineage comment could not be created.")));
                LogError("TRADE_LIFECYCLE", "ORDER_SUBMISSION_SKIPPED_UNCORRELATED",
                    "strategy={0} side={1} outcome=skipped reason=persistentCapsuleCommentUnavailable",
                    strategy, tradeType);
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Permanent,
                    "submissionCorrelationCommentUnavailable");
            }

            try
            {
                _activeOrderSubmissionOrigin = entryOrigin;
                result = orderKind == V2OrderKind.Stop
                    ? PlaceStopOrder(
                        tradeType,
                        Symbol.Name,
                        volume,
                        target,
                        label,
                        slPips,
                        tpPips,
                        ProtectionType.Relative,
                        null,
                        comment,
                        false)
                    : PlaceLimitOrder(
                        tradeType,
                        Symbol.Name,
                        volume,
                        target,
                        label,
                        slPips,
                        tpPips,
                        ProtectionType.Relative,
                        null,
                        comment);
            }
            catch (Exception exception)
            {
                _activeOrderSubmissionOrigin = EntryOriginMode.Unattributed;
                RunTelemetry("AbortOrderSubmissionTracking",
                    () => AbortOrderSubmissionTracking(
                        submissionCorrelationId,
                        exception));
                if (!policySelectedStrategy)
                    throw;

                return ReconcileAmbiguousPlacement(
                    submissionCorrelationId,
                    strategy,
                    tradeType,
                    target,
                    movementReferencePrice,
                    description,
                    tick,
                    exception.GetType().Name + ": " + exception.Message,
                    entryOrigin,
                    capsule);
            }

            RunTelemetry("FinishOrderSubmissionTracking",
                () => FinishOrderSubmissionTracking(
                    submissionCorrelationId,
                    result));
            _activeOrderSubmissionOrigin = EntryOriginMode.Unattributed;
            if (!policySelectedStrategy)
            {
                if (IsAmbiguousPolicyPlacementResult(result))
                {
                    return ReconcileAmbiguousPlacement(
                        submissionCorrelationId,
                        strategy,
                        tradeType,
                        target,
                        movementReferencePrice,
                        description,
                        tick,
                        result == null
                            ? "nullResult"
                            : result.Error.HasValue
                                ? result.Error.Value.ToString()
                                : "errorCodeUnavailable",
                        entryOrigin,
                        capsule);
                }

                bool accepted = HandlePlacementResult(
                    result,
                    description,
                    target,
                    movementReferencePrice,
                    strategy,
                    tick,
                    submissionCorrelationId,
                    entryOrigin,
                    capsule);
                return accepted
                    ? PolicyPlacementResult.Success(
                        result.PendingOrder.Id,
                        "orderSubmitted",
                        true)
                    : PolicyPlacementResult.Failure(
                        PlacementFailureClass.Permanent,
                        "placementRejected",
                        requestIssued: true);
            }

            if (result != null &&
                result.IsSuccessful &&
                result.PendingOrder != null)
            {
                bool accepted = HandlePlacementResult(
                    result,
                    description,
                    target,
                    movementReferencePrice,
                    strategy,
                    tick,
                    submissionCorrelationId,
                    entryOrigin,
                    capsule);
                return accepted
                    ? PolicyPlacementResult.Success(
                        result.PendingOrder.Id,
                        "orderSubmitted",
                        true)
                    : PolicyPlacementResult.Failure(
                        PlacementFailureClass.RuntimeFault,
                        "acceptedOrderHandlingFailed",
                        requestIssued: true);
            }

            if (IsAmbiguousPolicyPlacementResult(result))
            {
                return ReconcileAmbiguousPlacement(
                    submissionCorrelationId,
                    strategy,
                    tradeType,
                    target,
                    movementReferencePrice,
                    description,
                    tick,
                    result == null
                        ? "nullResult"
                        : result.Error.HasValue
                            ? result.Error.Value.ToString()
                            : "errorCodeUnavailable",
                    entryOrigin,
                    capsule);
            }

            HandlePlacementResult(
                result,
                description,
                target,
                movementReferencePrice,
                strategy,
                tick,
                submissionCorrelationId,
                entryOrigin,
                capsule);
            return ClassifyPolicyTradeFailure(result);
        }

        private bool IsAmbiguousPolicyPlacementResult(TradeResult result)
        {
            if (result == null || result.IsSuccessful)
                return true;

            if (!result.Error.HasValue)
                return true;

            ErrorCode errorCode = result.Error.Value;
            return errorCode == ErrorCode.Timeout ||
                   errorCode == ErrorCode.Disconnected ||
                   errorCode == ErrorCode.TechnicalError;
        }

        private PolicyPlacementResult ClassifyPolicyTradeFailure(TradeResult result)
        {
            ErrorCode? errorCode = result == null ? (ErrorCode?)null : result.Error;
            if (errorCode == ErrorCode.InvalidStopLossTakeProfit)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    errorCode.Value.ToString(),
                    requestIssued: true);
            }

            if (errorCode == ErrorCode.MarketClosed)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Expired,
                    errorCode.Value.ToString(),
                    requestIssued: true);
            }

            return PolicyPlacementResult.Failure(
                PlacementFailureClass.Permanent,
                errorCode.HasValue ? errorCode.Value.ToString() : "brokerRejected",
                requestIssued: true);
        }

        private PolicyPlacementResult ReconcileAmbiguousPlacement(
            Guid? submissionCorrelationId,
            StrategyKind strategy,
            TradeType tradeType,
            double target,
            double movementReferencePrice,
            string description,
            TickSnapshot tick,
            string detail,
            EntryOriginMode entryOrigin,
            PersistentTradeCapsule capsule)
        {
            if (submissionCorrelationId.HasValue)
            {
                List<PendingOrder> matchingOrders = PendingOrders
                    .Where(order => IsBotOrder(order) &&
                                    HasMatchingPersistentCapsule(
                                        order.Comment,
                                        submissionCorrelationId.Value,
                                        capsule))
                    .ToList();
                List<Position> matchingPositions = Positions
                    .Where(position => IsBotPosition(position) &&
                                       HasMatchingPersistentCapsule(
                                           position.Comment,
                                           submissionCorrelationId.Value,
                                           capsule))
                    .ToList();

                if (matchingOrders.Count == 1 && matchingPositions.Count == 0)
                {
                    PendingOrder order = matchingOrders[0];
                    if (_submissionCorrelationIdsByOrderId.TryGetValue(
                        order.Id,
                        out Guid existingId) &&
                        existingId != submissionCorrelationId.Value)
                    {
                        _runtimeFaulted = true;
                        LogError("TRADE_LIFECYCLE", "SUBMISSION_CORRELATION_RECOVERY_CONFLICT",
                            "schemaVersion=4 kind=order objectId={0} existingSubmissionCorrelationId={1} commentSubmissionCorrelationId={2}",
                            order.Id,
                            existingId,
                            submissionCorrelationId.Value);
                        return PolicyPlacementResult.Failure(
                            PlacementFailureClass.RuntimeFault,
                            "submissionCorrelationConflict",
                            requestIssued: true);
                    }

                    _submissionCorrelationIdsByOrderId[order.Id] =
                        submissionCorrelationId.Value;
                    HandleAcceptedPendingOrder(
                        order,
                        description,
                        target,
                        movementReferencePrice,
                        strategy,
                        tick,
                        submissionCorrelationId,
                        "brokerReconciliation",
                        entryOrigin,
                        capsule);
                    LogNormal("ENTRY", "PLACEMENT_OUTCOME_RECONCILED",
                        "entryOrigin={0} outcome=correlatedPendingOrder orderId={1} submissionCorrelationId={2}",
                        entryOrigin,
                        order.Id,
                        submissionCorrelationId.Value);
                    return PolicyPlacementResult.Success(
                        order.Id,
                        "correlatedPendingOrderFound",
                        true);
                }

                if (matchingOrders.Count == 0 && matchingPositions.Count == 1)
                {
                    Position position = matchingPositions[0];
                    VerifyPersistentCapsuleComment(
                        submissionCorrelationId,
                        capsule,
                        position.Comment,
                        "position",
                        position.Id);
                    if (Account.AccountType == AccountType.Hedged)
                    {
                        Guid tradeIdeaId = submissionCorrelationId.Value;
                        SetTradeIdeaEntryOrigin(tradeIdeaId, entryOrigin);
                        SetPositionEntryOrigin(position.Id, entryOrigin);
                        if (!_tradeIdeaSummaries.ContainsKey(tradeIdeaId))
                        {
                            StartTradeIdeaSummary(tradeIdeaId);
                            LogVerbose("TRADE_LIFECYCLE",
                                "SUBMISSION_PROMOTED_TO_TRADE_IDEA",
                                "schemaVersion=1 submissionCorrelationId={0} tradeIdeaId={1} orderId=unknown positionId={2} source=ambiguousPlacementReconciliation",
                                submissionCorrelationId.Value,
                                tradeIdeaId,
                                position.Id);
                            LogVerbose("TRADE_LIFECYCLE", "TRADE_IDEA_STARTED",
                                "schemaVersion=4 tradeIdeaId={0} originatingSubmissionCorrelationId={1} orderId=unknown positionId={2} source=ambiguousPlacementReconciliation",
                                tradeIdeaId,
                                submissionCorrelationId.Value,
                                position.Id);
                        }
                        string association = AssociateTradeIdeaWithPosition(
                            position.Id,
                            tradeIdeaId);
                        if (association == "exclusive")
                            AddIdeaPosition(
                                submissionCorrelationId,
                                position.Id,
                                false);
                        else
                            LogError("TRADE_LIFECYCLE", "IDEA_RECOVERY_CONFLICT",
                                "kind=position objectId={0} tradeIdeaId={1} association={2}",
                                position.Id,
                                tradeIdeaId,
                                association);
                    }

                    else
                    {
                        SetPositionEntryOrigin(position.Id, entryOrigin);
                    }

                    LogNormal("ENTRY", "PLACEMENT_OUTCOME_RECONCILED",
                        "entryOrigin={0} outcome=correlatedPosition positionId={1} submissionCorrelationId={2} tradeIdeaId={3} accountType={4}",
                        entryOrigin,
                        position.Id,
                        submissionCorrelationId.Value,
                        submissionCorrelationId.Value,
                        Account.AccountType);
                    return PolicyPlacementResult.Success(
                        null,
                        "correlatedPositionFound",
                        true);
                }

                if (matchingOrders.Count > 1 || matchingPositions.Count > 1 ||
                    (matchingOrders.Count > 0 && matchingPositions.Count > 0))
                {
                    detail = "multipleCorrelatedObjects:" + detail;
                }
            }

            _runtimeFaulted = true;
            LogError("ENTRY", "PLACEMENT_OUTCOME_UNRESOLVED",
                "entryOrigin={0} strategy={1} direction={2} submissionCorrelationId={3} outcome=entriesDisabledForRun reason=brokerOutcomeUnknown detail={4}",
                entryOrigin,
                strategy,
                tradeType,
                FormatSubmissionCorrelationId(submissionCorrelationId),
                detail);
            return PolicyPlacementResult.Failure(
                PlacementFailureClass.RuntimeFault,
                "brokerOutcomeUnknown",
                detail,
                true);
        }
        #endregion


    }
}
