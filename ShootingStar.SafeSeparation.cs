using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Pending-order risk limits and safe-separation checks.
    public partial class ShootingStar
    {
        #region Safe Separation and Risk Controls

        private sealed class SafeSeparationLogState
        {
            public string ReasonCode;
            public DateTime FirstBlockedUtc;
            public DateTime LastBlockedUtc;
            public DateTime LastLoggedUtc;
            public long TotalBlockedCount;
            public long SuppressedSinceLastLog;
            public long TotalSuppressedCount;
            public long MaterialChangeCount;
            public long HeartbeatCount;
            public double LastObservedTargetPrice;
            public double LastLoggedTargetPrice;
            public double LastObservedMarketPrice;
            public double LastLoggedMarketPrice;
            public double LastObservedRequiredPercent;
            public double LastLoggedRequiredPercent;
        }
        private readonly struct PendingOrderModificationFingerprint
        {
            public PendingOrderModificationFingerprint(
                long targetTicks,
                long volumeSteps,
                long stopLossUnits,
                long takeProfitUnits)
            {
                TargetTicks = targetTicks;
                VolumeSteps = volumeSteps;
                StopLossUnits = stopLossUnits;
                TakeProfitUnits = takeProfitUnits;
            }

            public long TargetTicks { get; }
            public long VolumeSteps { get; }
            public long StopLossUnits { get; }
            public long TakeProfitUnits { get; }

            public bool Matches(PendingOrderModificationFingerprint other)
            {
                return TargetTicks == other.TargetTicks &&
                    VolumeSteps == other.VolumeSteps &&
                    StopLossUnits == other.StopLossUnits &&
                    TakeProfitUnits == other.TakeProfitUnits;
            }
        }

        private sealed class RejectedPendingOrderModification
        {
            public PendingOrderModificationFingerprint Fingerprint;
            public double Bid;
            public double Ask;
            public DateTime RejectedAt;
            public DateTime LastSkipLoggedAt;
            public string Error;
        }

        private PendingOrderModificationFingerprint CreatePendingOrderModificationFingerprint(
            double target,
            double volumeInUnits,
            double stopLossPips,
            double takeProfitPips)
        {
            double tickSize = Math.Max(Symbol.TickSize, 1e-12);
            double volumeStep = Math.Max(Symbol.VolumeInUnitsStep, 1e-12);
            double protectionStep = Math.Max(GetProtectionPipStep(), 1e-12);
            return new PendingOrderModificationFingerprint(
                Convert.ToInt64(Math.Round(target / tickSize, MidpointRounding.AwayFromZero)),
                Convert.ToInt64(Math.Round(volumeInUnits / volumeStep, MidpointRounding.AwayFromZero)),
                Convert.ToInt64(Math.Round(stopLossPips / protectionStep, MidpointRounding.AwayFromZero)),
                Convert.ToInt64(Math.Round(takeProfitPips / protectionStep, MidpointRounding.AwayFromZero)));
        }

        private bool ShouldSuppressRejectedPendingOrderModification(
            int pendingOrderId,
            PendingOrderModificationFingerprint fingerprint,
            out bool shouldLog,
            out string rejectedError)
        {
            shouldLog = false;
            rejectedError = string.Empty;
            RejectedPendingOrderModification rejected;
            if (!_rejectedPendingOrderModifications.TryGetValue(pendingOrderId, out rejected))
                return false;

            if (!rejected.Fingerprint.Matches(fingerprint))
            {
                _rejectedPendingOrderModifications.Remove(pendingOrderId);
                return false;
            }

            // The final live-quote geometry check runs before this guard on every pass.
            // A quote change alone must not release an identical broker request because an
            // active market can move every pass and recreate the original retry storm.
            // A changed API-ready proposal bypasses this guard immediately; an unchanged
            // proposal receives one bounded retry after the cooldown expires.
            bool retryExpired = (Server.TimeInUtc - rejected.RejectedAt).TotalSeconds >=
                RejectedPendingOrderRetrySeconds;
            if (retryExpired)
            {
                _rejectedPendingOrderModifications.Remove(pendingOrderId);
                return false;
            }

            _pendingOrderDuplicateRejectionSkipCount++;
            rejectedError = rejected.Error;
            if ((Server.TimeInUtc - rejected.LastSkipLoggedAt).TotalSeconds >=
                PendingOrderNoChangeLogIntervalSeconds)
            {
                rejected.LastSkipLoggedAt = Server.TimeInUtc;
                shouldLog = true;
            }
            return true;
        }

        private void RememberRejectedPendingOrderModification(
            int pendingOrderId,
            PendingOrderModificationFingerprint fingerprint,
            string error)
        {
            _rejectedPendingOrderModifications[pendingOrderId] =
                new RejectedPendingOrderModification
                {
                    Fingerprint = fingerprint,
                    Bid = Symbol.Bid,
                    Ask = Symbol.Ask,
                    RejectedAt = Server.TimeInUtc,
                    LastSkipLoggedAt = DateTime.MinValue,
                    Error = error
                };
        }

        private bool ShouldLogPendingOrderNoChange(int pendingOrderId)
        {
            DateTime now = Server.TimeInUtc;
            DateTime lastLogTime;
            if (_pendingOrderNoChangeLogTimes.TryGetValue(pendingOrderId, out lastLogTime) &&
                (now - lastLogTime).TotalSeconds < PendingOrderNoChangeLogIntervalSeconds)
            {
                return false;
            }

            _pendingOrderNoChangeLogTimes[pendingOrderId] = now;
            return true;
        }

        private bool TryGetCurrentRiskAmountPerVolumeUnitPerPip(
            double volumeInUnits, double stopLossPips, string context,
            out double riskAmountPerVolumeUnitPerPip)
        {
            riskAmountPerVolumeUnitPerPip = 0.0;
            double denominator = volumeInUnits * stopLossPips;
            if (!IsFinitePositive(denominator))
            {
                LogError("RISK_CAP", "INVALID_CONVERSION_INPUT",
                    "context={0} volume={1} stopLossPips={2} outcome=OrderBlocked",
                    context, volumeInUnits, stopLossPips);
                return false;
            }
            _riskConversionApiCallCount++;
            double riskAmount;
            try
            {
                riskAmount = Symbol.AmountRisked(volumeInUnits, stopLossPips);
            }
            catch (Exception exception)
            {
                _lastRiskCapFailureWasConversionTimeout = true;
                RegisterRiskConversionFailure("brokerRiskConversionException",
                    exception.GetType().Name + ": " + exception.Message);
                return false;
            }
            if (!IsFinitePositive(riskAmount))
            {
                _lastRiskCapFailureWasConversionTimeout = true;
                RegisterRiskConversionFailure("brokerRiskAmountInvalid",
                    riskAmount.ToString("R", CultureInfo.InvariantCulture));
                return false;
            }
            riskAmountPerVolumeUnitPerPip = riskAmount / denominator;
            if (!IsFinitePositive(riskAmountPerVolumeUnitPerPip))
            {
                _lastRiskCapFailureWasConversionTimeout = true;
                RegisterRiskConversionFailure("brokerRiskCoefficientInvalid",
                    riskAmountPerVolumeUnitPerPip.ToString("R", CultureInfo.InvariantCulture));
                return false;
            }
            _riskConversionCoefficientRefreshCount++;
            return true;
        }

        private readonly struct PendingOrderRiskCapDecision
        {
            public PendingOrderRiskCapDecision(
                double requestedVolume,
                double permittedVolume,
                double stopLossPips,
                double estimatedRisk,
                double maximumRisk,
                double capPercent)
            {
                RequestedVolume = requestedVolume;
                PermittedVolume = permittedVolume;
                StopLossPips = stopLossPips;
                EstimatedRisk = estimatedRisk;
                MaximumRisk = maximumRisk;
                CapPercent = capPercent;
            }

            public double RequestedVolume { get; }
            public double PermittedVolume { get; }
            public double StopLossPips { get; }
            public double EstimatedRisk { get; }
            public double MaximumRisk { get; }
            public double CapPercent { get; }
            public bool WasReduced { get { return PermittedVolume < RequestedVolume; } }
        }

        private bool TryCapPendingOrderVolumeByEquityRisk(
            double requestedVolumeInUnits,
            double stopLossPips,
            string context,
            out double permittedVolumeInUnits,
            out PendingOrderRiskCapDecision decision)
        {
            permittedVolumeInUnits = 0.0;
            decision = default(PendingOrderRiskCapDecision);
            _lastRiskCapFailureWasConversionTimeout = false;
            if (!IsFinitePositive(requestedVolumeInUnits) || !IsFinitePositive(stopLossPips))
            {
                LogError("RISK_CAP", "INVALID_INPUT",
                    "{0}: requestedVolume={1}, stopLossPips={2}",
                    context, requestedVolumeInUnits, stopLossPips);
                return false;
            }

            double equity = Account.Equity;
            double maximumRiskAmount = equity * MaximumPendingOrderRiskPercent / 100.0;
            if (!IsFinitePositive(equity) || !IsFinitePositive(maximumRiskAmount))
            {
                LogError("RISK_CAP", "INVALID_EQUITY",
                    "{0}: equity={1}, maximumRisk={2}",
                    context, equity, maximumRiskAmount);
                return false;
            }

            double normalizedRequestedVolume = Symbol.NormalizeVolumeInUnits(
                requestedVolumeInUnits,
                RoundingMode.Down);
            if (!IsTradableVolume(normalizedRequestedVolume))
            {
                LogError("RISK_CAP", "INVALID_VOLUME",
                    "{0}: normalized requested volume {1} is outside broker range {2}-{3}",
                    context, normalizedRequestedVolume,
                    Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax);
                return false;
            }

            double riskAmountPerVolumeUnitPerPip;
            if (!TryGetCurrentRiskAmountPerVolumeUnitPerPip(
                    normalizedRequestedVolume,
                    stopLossPips,
                    context,
                    out riskAmountPerVolumeUnitPerPip))
            {
                return false;
            }

            double requestedRiskAmount = normalizedRequestedVolume *
                stopLossPips * riskAmountPerVolumeUnitPerPip;
            if (!IsFinitePositive(requestedRiskAmount))
            {
                LogError("RISK_CAP", "CALCULATION_FAILED",
                    "{0}: risk calculation returned {1} for volume={2}, stopLossPips={3}",
                    context, requestedRiskAmount,
                    normalizedRequestedVolume, stopLossPips);
                return false;
            }

            double candidateVolume = normalizedRequestedVolume;
            if (requestedRiskAmount > maximumRiskAmount)
            {
                candidateVolume = Symbol.NormalizeVolumeInUnits(
                    normalizedRequestedVolume * maximumRiskAmount / requestedRiskAmount,
                    RoundingMode.Down);
            }

            if (!IsTradableVolume(candidateVolume))
            {
                LogNormal("RISK_CAP", "ORDER_BLOCKED",
                    "{0}: no tradable volume can keep estimated stop-loss risk at or below {1}% of current equity. requestedVolume={2}, stopLossPips={3}, equity={4}, maximumRisk={5}",
                    context, MaximumPendingOrderRiskPercent,
                    normalizedRequestedVolume, stopLossPips,
                    equity, maximumRiskAmount);
                return false;
            }

            double candidateRiskAmount = candidateVolume * stopLossPips *
                riskAmountPerVolumeUnitPerPip;
            if (!IsFinitePositive(candidateRiskAmount) ||
                candidateRiskAmount > maximumRiskAmount + 1e-8)
            {
                LogError("RISK_CAP", "CALCULATION_FAILED",
                    "{0}: normalized candidate risk {1} exceeds maximum {2}; volume={3}, stopLossPips={4}",
                    context, candidateRiskAmount, maximumRiskAmount,
                    candidateVolume, stopLossPips);
                return false;
            }

            permittedVolumeInUnits = candidateVolume;
            decision = new PendingOrderRiskCapDecision(
                normalizedRequestedVolume,
                candidateVolume,
                stopLossPips,
                candidateRiskAmount,
                maximumRiskAmount,
                MaximumPendingOrderRiskPercent);
            return true;
        }

        private void LogRiskCapDecision(
            string action,
            PendingOrderRiskCapDecision decision)
        {
            if (!decision.WasReduced)
                return;

            LogNormal("RISK_CAP", "VOLUME_REDUCED",
                "action={0} requested={1:F2} permitted={2:F2} slPips={3:F2} risk={4:F2}/{5:F2} capPercent={6:F2}",
                action,
                decision.RequestedVolume,
                decision.PermittedVolume,
                decision.StopLossPips,
                decision.EstimatedRisk,
                decision.MaximumRisk,
                decision.CapPercent);
        }

        private void MarkSafeDistanceSnapshotDirty()
        {
            _safeDistanceSnapshotDirty = true;
        }

        private void RegisterProvisionalSafeDistancePendingOrder(PendingOrder order)
        {
            if (order == null || order.SymbolName != Symbol.Name)
                return;

            _provisionalSafeDistanceRegistrationCount++;
            _provisionalSafeDistancePendingOrders[order.Id] =
                new ProvisionalSafeDistancePendingOrder(
                    new SafeDistanceLevelSnapshot(
                        order.Id,
                        order.TargetPrice,
                        GetPendingStopLossPrice(order),
                        GetPendingTakeProfitPrice(order)),
                    Server.TimeInUtc);

            MarkSafeDistanceSnapshotDirty();
        }

        private bool ResolveProvisionalSafeDistancePendingOrder(
            int pendingOrderId,
            ProvisionalSafeDistanceResolution resolution)
        {
            if (!_provisionalSafeDistancePendingOrders.Remove(pendingOrderId))
                return false;

            MarkSafeDistanceSnapshotDirty();
            switch (resolution)
            {
                case ProvisionalSafeDistanceResolution.LiveCollection:
                case ProvisionalSafeDistanceResolution.CreatedEvent:
                case ProvisionalSafeDistanceResolution.ModifiedEvent:
                    _provisionalSafeDistanceLiveSettlementCount++;
                    break;
                case ProvisionalSafeDistanceResolution.LiveCollectionNormalized:
                    _provisionalSafeDistanceLiveSettlementCount++;
                    _provisionalSafeDistanceNormalizedSettlementCount++;
                    LogVerbose("ORDER", "SAFE_DISTANCE_PROVISIONAL_SETTLED_NORMALIZED",
                        "pendingOrder={0} provisional snapshot differed from live normalized values",
                        pendingOrderId);
                    break;
                case ProvisionalSafeDistanceResolution.CancelledEvent:
                    _provisionalSafeDistanceCancellationCount++;
                    break;
                case ProvisionalSafeDistanceResolution.StaleCleanup:
                    _provisionalSafeDistanceStaleCleanupCount++;
                    break;
                case ProvisionalSafeDistanceResolution.Expired:
                    _provisionalSafeDistanceExpiryCount++;
                    break;
            }
            return true;
        }

        private void RegisterAndReconcileProvisionalSafeDistancePendingOrder(PendingOrder order)
        {
            RegisterProvisionalSafeDistancePendingOrder(order);
            if (order == null || order.SymbolName != Symbol.Name)
                return;

            PendingOrder liveOrder = PendingOrders.FirstOrDefault(candidate =>
                candidate.Id == order.Id && candidate.SymbolName == Symbol.Name);
            if (liveOrder == null)
                return;

            var liveLevels = new SafeDistanceLevelSnapshot(
                liveOrder.Id,
                liveOrder.TargetPrice,
                GetPendingStopLossPrice(liveOrder),
                GetPendingTakeProfitPrice(liveOrder));
            if (!_provisionalSafeDistancePendingOrders.TryGetValue(
                    order.Id,
                    out ProvisionalSafeDistancePendingOrder provisional))
            {
                return;
            }
            SafeDistanceLevelSnapshot provisionalLevels = provisional.Levels;
            ResolveProvisionalSafeDistancePendingOrder(
                order.Id,
                SafeDistanceLevelsMatch(provisionalLevels, liveLevels)
                    ? ProvisionalSafeDistanceResolution.LiveCollection
                    : ProvisionalSafeDistanceResolution.LiveCollectionNormalized);
        }

        private static bool NullableSafeDistancePricesMatch(
            double? first,
            double? second,
            double tolerance)
        {
            if (!first.HasValue || !second.HasValue)
                return first.HasValue == second.HasValue;

            return Math.Abs(first.Value - second.Value) <= tolerance;
        }

        private bool SafeDistanceLevelsMatch(
            SafeDistanceLevelSnapshot expected,
            SafeDistanceLevelSnapshot actual)
        {
            double tolerance = Math.Max(Symbol.TickSize * 0.5, 1e-12);
            return Math.Abs(expected.EntryOrTargetPrice - actual.EntryOrTargetPrice) <= tolerance &&
                   NullableSafeDistancePricesMatch(expected.StopLossPrice, actual.StopLossPrice, tolerance) &&
                   NullableSafeDistancePricesMatch(expected.TakeProfitPrice, actual.TakeProfitPrice, tolerance);
        }

        private void ReconcileProvisionalSafeDistancePendingOrders()
        {
            if (_provisionalSafeDistancePendingOrders.Count == 0)
                return;

            _settledProvisionalSafeDistancePendingOrderIds.Clear();
            DateTime utcNow = Server.TimeInUtc;

            foreach (var pair in _provisionalSafeDistancePendingOrders)
            {
                PendingOrder liveOrder = PendingOrders.FirstOrDefault(
                    candidate => candidate.Id == pair.Key &&
                                 candidate.SymbolName == Symbol.Name);

                if (liveOrder != null)
                {
                    var liveLevels = new SafeDistanceLevelSnapshot(
                        liveOrder.Id,
                        liveOrder.TargetPrice,
                        GetPendingStopLossPrice(liveOrder),
                        GetPendingTakeProfitPrice(liveOrder));
                    bool normalized = !SafeDistanceLevelsMatch(pair.Value.Levels, liveLevels);
                    _provisionalSafeDistanceLiveSettlementCount++;
                    if (normalized)
                    {
                        _provisionalSafeDistanceNormalizedSettlementCount++;
                        LogVerbose("ORDER", "SAFE_DISTANCE_PROVISIONAL_SETTLED_NORMALIZED",
                            "pendingOrder={0} provisional snapshot differed from live normalized values",
                            pair.Key);
                    }
                    _settledProvisionalSafeDistancePendingOrderIds.Add(pair.Key);
                    continue;
                }

                if ((utcNow - pair.Value.RegisteredUtc).TotalSeconds >=
                    ProvisionalSafeDistanceSettlementTimeoutSeconds)
                {
                    _settledProvisionalSafeDistancePendingOrderIds.Add(pair.Key);
                    _provisionalSafeDistanceExpiryCount++;
                    LogVerbose("ORDER", "SAFE_DISTANCE_PROVISIONAL_EXPIRED",
                        "pendingOrder={0} provisional state expired after {1}s because no same-symbol live order remained; the order may have filled, been cancelled, been replaced, or disappeared before confirmation",
                        pair.Key, ProvisionalSafeDistanceSettlementTimeoutSeconds);
                }
            }

            foreach (int pendingOrderId in _settledProvisionalSafeDistancePendingOrderIds)
                _provisionalSafeDistancePendingOrders.Remove(pendingOrderId);
        }

        private void EnsureSafeDistanceSnapshot()
        {
            ReconcileProvisionalSafeDistancePendingOrders();

            if (!_safeDistanceSnapshotDirty)
                return;

            _safeDistancePositionSnapshot.Clear();
            foreach (var position in Positions)
            {
                if (position.SymbolName != Symbol.Name)
                    continue;
                _safeDistancePositionSnapshot.Add(new SafeDistanceLevelSnapshot(
                    position.Id,
                    position.EntryPrice,
                    position.StopLoss,
                    position.TakeProfit));
            }

            _safeDistancePendingOrderSnapshot.Clear();
            foreach (var order in PendingOrders)
            {
                if (order.SymbolName != Symbol.Name)
                    continue;
                _safeDistancePendingOrderSnapshot.Add(new SafeDistanceLevelSnapshot(
                    order.Id,
                    order.TargetPrice,
                    GetPendingStopLossPrice(order),
                    GetPendingTakeProfitPrice(order)));
            }

            _safeDistanceSnapshotDirty = false;
        }


        private bool PassesSafeDistanceCheck(
            double proposedEntry,
            double requiredPercent,
            int? excludedPendingOrderId = null)
        {
            EnsureSafeDistanceSnapshot();

            foreach (var position in _safeDistancePositionSnapshot)
            {
                if (!IsSafelySeparated(proposedEntry, position.EntryOrTargetPrice, requiredPercent))
                    return false;

                if (position.TakeProfitPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, position.TakeProfitPrice.Value, requiredPercent))
                    return false;

                if (position.StopLossPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, position.StopLossPrice.Value, requiredPercent))
                    return false;
            }

            foreach (var order in _safeDistancePendingOrderSnapshot)
            {
                if (excludedPendingOrderId.HasValue && order.Id == excludedPendingOrderId.Value)
                    continue;

                if (!IsSafelySeparated(proposedEntry, order.EntryOrTargetPrice, requiredPercent))
                    return false;

                double? takeProfitPrice = order.TakeProfitPrice;
                if (takeProfitPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, takeProfitPrice.Value, requiredPercent))
                    return false;

                double? stopLossPrice = order.StopLossPrice;
                if (stopLossPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, stopLossPrice.Value, requiredPercent))
                    return false;
            }

            foreach (var pair in _provisionalSafeDistancePendingOrders)
            {
                SafeDistanceLevelSnapshot order = pair.Value.Levels;
                if (excludedPendingOrderId.HasValue && order.Id == excludedPendingOrderId.Value)
                    continue;

                if (!IsSafelySeparated(proposedEntry, order.EntryOrTargetPrice, requiredPercent))
                    return false;

                if (order.TakeProfitPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, order.TakeProfitPrice.Value, requiredPercent))
                    return false;

                if (order.StopLossPrice.HasValue &&
                    !IsSafelySeparated(proposedEntry, order.StopLossPrice.Value, requiredPercent))
                    return false;
            }

            return true;
        }

        private bool PassesOrderFootprintSafeDistanceCheck(
            TradeType tradeType,
            double proposedTarget,
            double stopLossPips,
            double takeProfitPips,
            double requiredPercent,
            int? excludedPendingOrderId = null)
        {
            if (!IsFinitePositive(proposedTarget) ||
                !IsFinitePositive(stopLossPips) ||
                !IsFinitePositive(takeProfitPips) ||
                !IsFinite(requiredPercent) ||
                requiredPercent < 0.0)
            {
                return false;
            }

            double stopLossDistance = stopLossPips * Symbol.PipSize;
            double takeProfitDistance = takeProfitPips * Symbol.PipSize;
            double proposedStopLoss = tradeType == TradeType.Buy
                ? proposedTarget - stopLossDistance
                : proposedTarget + stopLossDistance;
            double proposedTakeProfit = tradeType == TradeType.Buy
                ? proposedTarget + takeProfitDistance
                : proposedTarget - takeProfitDistance;

            return IsFinitePositive(proposedStopLoss) &&
                IsFinitePositive(proposedTakeProfit) &&
                PassesSafeDistanceCheck(
                    proposedTarget, requiredPercent, excludedPendingOrderId) &&
                PassesSafeDistanceCheck(
                    proposedStopLoss, requiredPercent, excludedPendingOrderId) &&
                PassesSafeDistanceCheck(
                    proposedTakeProfit, requiredPercent, excludedPendingOrderId);
        }

        private void RecordSafeSeparationBlock(
            PendingOrder order,
            string reasonCode,
            double targetPrice,
            double marketPrice,
            double requiredPercent,
            double effectiveMinimumTrailStepPrice)
        {
            DateTime now = Server.TimeInUtc;
            _safeSeparationBlockedEvaluationCount++;
            SafeSeparationLogState state;
            if (!_safeSeparationLogStates.TryGetValue(order.Id, out state))
            {
                state = new SafeSeparationLogState
                {
                    ReasonCode = reasonCode,
                    FirstBlockedUtc = now,
                    LastBlockedUtc = now,
                    LastLoggedUtc = now,
                    TotalBlockedCount = 1,
                    LastObservedTargetPrice = targetPrice,
                    LastLoggedTargetPrice = targetPrice,
                    LastObservedMarketPrice = marketPrice,
                    LastLoggedMarketPrice = marketPrice,
                    LastObservedRequiredPercent = requiredPercent,
                    LastLoggedRequiredPercent = requiredPercent
                };
                _safeSeparationLogStates[order.Id] = state;
                _safeSeparationFirstBlockCount++;
                LogNormal("ORDER", "SAFE_SEPARATION_BLOCKED",
                    "orderId={0} side={1} reason={2} target={3} market={4} requiredPercent={5:F6} blockedEvaluations=1",
                    order.Id, order.TradeType, state.ReasonCode,
                    FormatPrice(targetPrice), FormatPrice(marketPrice), requiredPercent);
                return;
            }

            state.TotalBlockedCount++;
            state.LastBlockedUtc = now;
            state.LastObservedTargetPrice = targetPrice;
            state.LastObservedMarketPrice = marketPrice;
            state.LastObservedRequiredPercent = requiredPercent;

            double tickThreshold = Math.Max(
                _symbolTickSize * SafeSeparationMaterialTargetChangeTicks,
                1e-12);
            double percentThreshold = Math.Max(
                Math.Abs(state.LastLoggedTargetPrice) *
                    SafeSeparationMaterialTargetChangePercent / 100.0,
                1e-12);
            double materialTargetThreshold = Math.Max(
                tickThreshold,
                Math.Max(percentThreshold, effectiveMinimumTrailStepPrice));
            double targetChange = Math.Abs(targetPrice - state.LastLoggedTargetPrice);
            bool reasonChanged = !string.Equals(
                state.ReasonCode,
                reasonCode,
                StringComparison.Ordinal);
            bool targetChangedMaterially = targetChange >= materialTargetThreshold;
            bool heartbeatDue = (now - state.LastLoggedUtc).TotalSeconds >=
                SafeSeparationHeartbeatIntervalSeconds;

            if (reasonChanged || targetChangedMaterially)
            {
                string previousReason = state.ReasonCode;
                double previousLoggedTargetPrice = state.LastLoggedTargetPrice;
                state.MaterialChangeCount++;
                _safeSeparationMaterialChangeCount++;
                LogNormal("ORDER", "SAFE_SEPARATION_CHANGED",
                    "orderId={0} side={1} changeType={2} previousReason={3} reason={4} previousLoggedTarget={5} target={6} targetChange={7} materialThreshold={8} market={9} requiredPercent={10:F6} suppressedSinceLastLog={11} totalBlockedEvaluations={12} totalSuppressed={13}",
                    order.Id, order.TradeType,
                    reasonChanged ? "Reason" : "MaterialTarget",
                    previousReason, reasonCode,
                    FormatPrice(previousLoggedTargetPrice), FormatPrice(targetPrice),
                    FormatPrice(targetChange), FormatPrice(materialTargetThreshold),
                    FormatPrice(marketPrice), requiredPercent,
                    state.SuppressedSinceLastLog, state.TotalBlockedCount,
                    state.TotalSuppressedCount);
                state.ReasonCode = reasonCode;
                state.LastLoggedUtc = now;
                state.LastLoggedTargetPrice = targetPrice;
                state.LastLoggedMarketPrice = marketPrice;
                state.LastLoggedRequiredPercent = requiredPercent;
                state.SuppressedSinceLastLog = 0;
                return;
            }

            state.SuppressedSinceLastLog++;
            state.TotalSuppressedCount++;
            _safeSeparationDuplicateSuppressedCount++;
            if (!heartbeatDue)
                return;

            state.HeartbeatCount++;
            _safeSeparationPeriodicSummaryCount++;
            LogNormal("ORDER", "SAFE_SEPARATION_STILL_BLOCKED",
                "orderId={0} side={1} reason={2} blockedSeconds={3:F1} target={4} lastLoggedTarget={5} market={6} requiredPercent={7:F6} suppressedSinceLastLog={8} totalBlockedEvaluations={9} totalSuppressed={10} heartbeatCount={11}",
                order.Id, order.TradeType, state.ReasonCode,
                Math.Max(0.0, (now - state.FirstBlockedUtc).TotalSeconds),
                FormatPrice(targetPrice), FormatPrice(state.LastLoggedTargetPrice),
                FormatPrice(marketPrice), requiredPercent,
                state.SuppressedSinceLastLog, state.TotalBlockedCount,
                state.TotalSuppressedCount, state.HeartbeatCount);
            state.LastLoggedUtc = now;
            state.LastLoggedTargetPrice = targetPrice;
            state.LastLoggedMarketPrice = marketPrice;
            state.LastLoggedRequiredPercent = requiredPercent;
            state.SuppressedSinceLastLog = 0;
        }
        private void ClearSafeSeparationBlock(int pendingOrderId, string outcome)
        {
            SafeSeparationLogState state;
            if (!_safeSeparationLogStates.TryGetValue(pendingOrderId, out state))
                return;
            _safeSeparationLogStates.Remove(pendingOrderId);
            _safeSeparationClearedCount++;
            LogNormal("ORDER", "SAFE_SEPARATION_CLEARED",
                "orderId={0} reason={1} outcome={2} blockedSeconds={3:F1} lastLoggedTarget={4} lastObservedTarget={5} suppressedSinceLastLog={6} totalBlockedEvaluations={7} totalSuppressed={8} materialChanges={9} heartbeats={10}",
                pendingOrderId, state.ReasonCode, outcome,
                Math.Max(0.0, (Server.TimeInUtc - state.FirstBlockedUtc).TotalSeconds),
                FormatPrice(state.LastLoggedTargetPrice),
                FormatPrice(state.LastObservedTargetPrice),
                state.SuppressedSinceLastLog, state.TotalBlockedCount,
                state.TotalSuppressedCount, state.MaterialChangeCount,
                state.HeartbeatCount);
        }
        private void EndSafeSeparationBlock(int pendingOrderId, string outcome, bool stalePrune)
        {
            SafeSeparationLogState state;
            if (!_safeSeparationLogStates.TryGetValue(pendingOrderId, out state))
                return;
            _safeSeparationLogStates.Remove(pendingOrderId);
            _safeSeparationLifecycleEndedCount++;
            if (stalePrune)
                _safeSeparationStatePrunedCount++;
            LogNormal("ORDER", "SAFE_SEPARATION_ENDED",
                "orderId={0} reason={1} outcome={2} blockedSeconds={3:F1} lastLoggedTarget={4} lastObservedTarget={5} suppressedSinceLastLog={6} totalBlockedEvaluations={7} totalSuppressed={8} materialChanges={9} heartbeats={10}",
                pendingOrderId, state.ReasonCode, outcome,
                Math.Max(0.0, (Server.TimeInUtc - state.FirstBlockedUtc).TotalSeconds),
                FormatPrice(state.LastLoggedTargetPrice),
                FormatPrice(state.LastObservedTargetPrice),
                state.SuppressedSinceLastLog, state.TotalBlockedCount,
                state.TotalSuppressedCount, state.MaterialChangeCount,
                state.HeartbeatCount);
        }
        private bool IsSafelySeparated(double proposedEntry, double level, double requiredPercent)
        {
            if (!IsFinitePositive(proposedEntry) ||
                !IsFinitePositive(level) ||
                !IsFinite(requiredPercent) ||
                requiredPercent < 0.0)
            {
                return false;
            }

            double referencePrice = Math.Max(proposedEntry, level);
            return Math.Abs(proposedEntry - level) / referencePrice * 100.0 >= requiredPercent;
        }

        private double? GetPendingTakeProfitPrice(PendingOrder order)
        {
            if (!order.TakeProfitPips.HasValue)
                return null;
            double distance = order.TakeProfitPips.Value * Symbol.PipSize;
            return order.TradeType == TradeType.Buy ? order.TargetPrice + distance : order.TargetPrice - distance;
        }

        private double? GetPendingStopLossPrice(PendingOrder order)
        {
            if (!order.StopLossPips.HasValue)
                return null;
            double distance = order.StopLossPips.Value * Symbol.PipSize;
            return order.TradeType == TradeType.Buy ? order.TargetPrice - distance : order.TargetPrice + distance;
        }

        private bool PendingOrderModificationMatchesLiveOrder(
            PendingOrder order,
            double target,
            double stopLossPips,
            double takeProfitPips,
            double volumeInUnits)
        {
            double priceTolerance = Math.Max(Symbol.TickSize * 0.5, 1e-12);
            double volumeTolerance = Math.Max(Symbol.VolumeInUnitsStep * 0.5, 1e-12);
            double protectionTolerance = Math.Max(GetProtectionPipStep() * 0.5, 1e-12);

            bool targetMatches = Math.Abs(target - order.TargetPrice) <= priceTolerance;
            bool volumeMatches = Math.Abs(volumeInUnits - order.VolumeInUnits) <= volumeTolerance;
            bool stopLossMatches = order.StopLossPips.HasValue &&
                Math.Abs(stopLossPips - NormalizeProtectionPips(order.StopLossPips.Value)) <= protectionTolerance;
            bool takeProfitMatches = order.TakeProfitPips.HasValue &&
                Math.Abs(takeProfitPips - NormalizeProtectionPips(order.TakeProfitPips.Value)) <= protectionTolerance;

            return targetMatches && volumeMatches && stopLossMatches && takeProfitMatches;
        }

        #endregion

    }
}
