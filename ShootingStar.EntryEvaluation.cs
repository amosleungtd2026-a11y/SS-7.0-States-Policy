using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Entry-signal evaluation and rule-mapped candidate selection.
    public partial class ShootingStar
    {
        #region Entry Signal Evaluation
        private bool EvaluateEntrySignals(TickSnapshot tick)
        {
            var pass = new EntryEvaluationPass
            {
                MaximumAcceptedOrders = MaxNewOrdersPerMaintenancePass
            };

            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto &&
                _pendingPolicyDecision != null)
            {
                if (_pendingPolicyDecision.BarOpenTimeUtc != _currentHourlyMevBarOpenTime)
                {
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.Expired,
                        "supersededByNewHourlyBar");
                }
                else
                {
                    if (TryProcessPendingPolicyDecision(tick))
                        pass.AcceptedNewOrders++;
                    RecordEntryEvaluationPass(pass);
                    return pass.AcceptedNewOrders > 0;
                }
            }

            if (_effectiveTradingMode == TradingMode.UserTriggered)
            {
                RecordEntryEvaluationPass(pass);
                return false;
            }

            if (!CanPlaceNewEntry())
            {
                RecordEntrySkip(GetEntryBarrierReason());
                RecordEntryEvaluationPass(pass);
                return false;
            }

            if (tick.UtcNow < _policySamplingCooldownUntilUtc)
            {
                RecordEntryEvaluationPass(pass);
                return false;
            }

            if (_effectiveTradingMode == TradingMode.RuleMappedAuto)
            {
                EvaluateRuleMappedAuto(pass, tick);
            }
            else if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
            {
                if (EvaluatePolicyEntrySignal(tick))
                    pass.AcceptedNewOrders++;
            }
            else if (!IsKnownTradingMode(_effectiveTradingMode))
            {
                LogError("ENTRY", "UNKNOWN_TRADING_MODE",
                    "mode={0}", _effectiveTradingMode);
            }

            if (pass.AcceptedNewOrders > 0 &&
                _effectiveTradingMode == TradingMode.RuleMappedAuto)
            {
                _policySamplingCooldownUntilUtc =
                    tick.UtcNow.Add(PolicySamplingCooldown);
                LogNormal("AUTO_POLICY", "PLACEMENT_COOLDOWN_STARTED",
                    "mode={0} expiresUtc={1:O} acceptedNewOrders={2}",
                    GetTradingModeDisplayName(_effectiveTradingMode),
                    _policySamplingCooldownUntilUtc,
                    pass.AcceptedNewOrders);
            }

            RecordEntryEvaluationPass(pass);
            return pass.AcceptedNewOrders > 0;
        }

        private sealed class EntryEvaluationPass
        {
            public int MaximumAcceptedOrders;
            public int AcceptedNewOrders;
            public int CandidateEvaluations;
            public bool LimitReached =>
                AcceptedNewOrders >= MaximumAcceptedOrders;
        }

        private sealed class RuleMappedCandidate
        {
            public TradeType TradeType;
            public StrategyKind Strategy;
            public double MovementExcessRatio;
            public double AllocationPercent;
        }

        private void EvaluateRuleMappedAuto(
            EntryEvaluationPass pass,
            TickSnapshot tick)
        {
            List<RuleMappedCandidate> candidates = new List<RuleMappedCandidate>();
            AddRuleMappedCandidate(candidates, TradeType.Buy, tick);
            AddRuleMappedCandidate(candidates, TradeType.Sell, tick);
            candidates = candidates
                .OrderByDescending(candidate => candidate.MovementExcessRatio)
                .ThenByDescending(candidate => candidate.AllocationPercent)
                .ThenBy(candidate => candidate.TradeType == TradeType.Buy ? 0 : 1)
                .ToList();

            for (int index = 0; index < candidates.Count; index++)
            {
                if (pass.LimitReached)
                    break;

                RuleMappedCandidate candidate = candidates[index];
                pass.CandidateEvaluations++;
                bool movementPassed = HasRequiredPriceMove(
                    candidate.TradeType,
                    candidate.Strategy,
                    tick);
                bool allocationAvailable = IsSideAllocated(candidate.TradeType);
                LogVerbose("ENTRY", "RULE_MAPPED_CANDIDATE_EVALUATED",
                    "direction={0} strategy={1} priorityRank={2} movementExcessRatio={3:F6} allocationPercent={4:F2} movementPassed={5} allocationAvailable={6}",
                    candidate.TradeType,
                    candidate.Strategy,
                    index + 1,
                    candidate.MovementExcessRatio,
                    candidate.AllocationPercent,
                    movementPassed,
                    allocationAvailable);
                if (!movementPassed || !allocationAvailable)
                    continue;

                if (TryPlaceRuleMappedCandidate(candidate, tick))
                    pass.AcceptedNewOrders++;
                if (_runtimeFaulted)
                    break;
            }
        }

        private void AddRuleMappedCandidate(
            List<RuleMappedCandidate> candidates,
            TradeType tradeType,
            TickSnapshot tick)
        {
            ManualSideStrategy mappedStrategy = GetManualSideStrategy(tradeType);
            if (mappedStrategy == ManualSideStrategy.None)
                return;

            StrategyKind strategy = mappedStrategy == ManualSideStrategy.MeanReversion
                ? StrategyKind.MeanReversion
                : StrategyKind.TrendFollowing;
            candidates.Add(new RuleMappedCandidate
            {
                TradeType = tradeType,
                Strategy = strategy,
                MovementExcessRatio = GetMovementExcessRatio(
                    tradeType,
                    strategy,
                    tick),
                AllocationPercent = GetEffectiveSideAllocationPercent(tradeType)
            });
        }

        private double GetMovementExcessRatio(
            TradeType tradeType,
            StrategyKind strategy,
            TickSnapshot tick)
        {
            double reference;
            double observedPrice;
            double requiredPercent;
            if (strategy == StrategyKind.MeanReversion &&
                tradeType == TradeType.Buy)
            {
                reference = tick.ReferenceHigh;
                observedPrice = tick.Bid;
                requiredPercent = _actualMrDownMovePercent;
                return IsFinitePositive(reference) && requiredPercent > 0.0
                    ? CalculateMovementExcessRatio(
                        (reference - observedPrice) / reference * 100.0,
                        requiredPercent)
                    : double.NegativeInfinity;
            }

            if (strategy == StrategyKind.MeanReversion)
            {
                reference = tick.ReferenceLow;
                observedPrice = tick.Bid;
                requiredPercent = _actualMrUpMovePercent;
                return IsFinitePositive(reference) && requiredPercent > 0.0
                    ? CalculateMovementExcessRatio(
                        (observedPrice - reference) / reference * 100.0,
                        requiredPercent)
                    : double.NegativeInfinity;
            }

            if (tradeType == TradeType.Buy)
            {
                reference = tick.ReferenceLow;
                observedPrice = tick.Bid;
                requiredPercent = _actualTfLongMovePercent;
                return IsFinitePositive(reference) && requiredPercent > 0.0
                    ? CalculateMovementExcessRatio(
                        (observedPrice - reference) / reference * 100.0,
                        requiredPercent)
                    : double.NegativeInfinity;
            }

            reference = tick.ReferenceHigh;
            observedPrice = tick.Bid;
            requiredPercent = _actualTfShortMovePercent;
            return IsFinitePositive(reference) && requiredPercent > 0.0
                ? CalculateMovementExcessRatio(
                    (reference - observedPrice) / reference * 100.0,
                    requiredPercent)
                : double.NegativeInfinity;
        }

        private double CalculateMovementExcessRatio(
            double observedMovementPercent,
            double requiredMovementPercent)
        {
            double ratio = observedMovementPercent / requiredMovementPercent;
            return IsFinite(ratio) ? ratio : double.NegativeInfinity;
        }
        private bool MovementGatePasses(
            double observedMovement,
            double requiredMovement)
        {
            if (!IsFinite(observedMovement) ||
                !IsFinitePositive(requiredMovement))
            {
                return false;
            }
            double tolerance = Math.Max(
                _symbolTickSize * 0.5,
                requiredMovement * 1e-12);
            return observedMovement + tolerance >= requiredMovement;
        }

        private bool TryPlaceRuleMappedCandidate(
            RuleMappedCandidate candidate,
            TickSnapshot tick)
        {
            if (candidate.Strategy == StrategyKind.MeanReversion)
            {
                return candidate.TradeType == TradeType.Buy
                    ? TryPlaceMrBuyOrderAfterDownMove(tick)
                    : TryPlaceMrSellOrderAfterUpMove(tick);
            }

            return candidate.TradeType == TradeType.Buy
                ? TryPlaceTfLongOrderAfterUpMove(tick)
                : TryPlaceTfShortOrderAfterDownMove(tick);
        }

        #endregion

    }
}
