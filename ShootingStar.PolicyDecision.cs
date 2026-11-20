using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Policy decision evaluation, retry, and consumption workflow.
    public partial class ShootingStar
    {
        #region Policy Decision Workflow
        private void InitializePolicyRandom()
        {
            if (PolicyRandomSeed < 0 || PolicyRandomSeed > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PolicyRandomSeed),
                    "Policy random seed must be between 0 and 2147483647.");
            }

            _policyRandom = new Random(PolicyRandomSeed);
            LogNormal("STOCHASTIC", "POLICY_RANDOM_INITIALIZED",
                "seed={0} seededRandom=true",
                PolicyRandomSeed);
        }

        private bool EvaluatePolicyEntrySignal(TickSnapshot tick)
        {
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                return EvaluateV2PolicyEntrySignal(tick);

            if (!_hasValidHourlyMev || !_hasValidDailyMev)
            {
                RecordEntrySkip(!_hasValidHourlyMev
                    ? "hourlyMomentumEfficiencyUnavailable"
                    : "dailyMomentumEfficiencyUnavailable");
                return false;
            }

            if (!_currentPolicySignalEligible)
            {
                RecordMevNoEntry(
                    _currentHourlyMev,
                    _currentDailyMev,
                    _currentHourlyMevSignal,
                    _currentDailyMevContext,
                    _currentPolicySignalReason,
                    tick.UtcNow);
                return false;
            }

            if (_currentHourlyMevBarOpenTime == _lastPolicyDecisionBarOpenTime ||
                _currentHourlyMevBarOpenTime == _lastConsumedPolicyBarOpenTime ||
                tick.UtcNow < _policySamplingCooldownUntilUtc)
            {
                return false;
            }

            double longProbability;
            double shortProbability;
            double holdProbability;
            double alignedProbability;
            double contraryProbability;
            double bossRandomDraw;
            BossSignal alignedBossSignal = GetSetupAlignedBossSignal(
                _currentHourlyMevSignal);
            string setupName =
                _currentHourlyMevSignal + "/" + _currentDailyMevContext;

            if (!IsBossAlignedSellEligible(
                    _currentHourlyMevSignal,
                    _currentDaughterScore))
            {
                _lastPolicyDecisionBarOpenTime = _currentHourlyMevBarOpenTime;
                LogNormal("POLICY", "BOSS_ALIGNED_SELL_REJECTED",
                    "barOpenUtc={0:O} setupName={1} daughterScore={2:F4} reason=alignedSellRequiresNegativeDaughterScore",
                    _currentHourlyMevBarOpenTime,
                    setupName,
                    _currentDaughterScore);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.NoTradeIneligible,
                    "alignedSellRequiresNegativeDaughterScore");
                return false;
            }

            _lastPolicyDecisionBarOpenTime = _currentHourlyMevBarOpenTime;
            BossSignal sampledBossSignal = GenerateBossSignal(
                _currentHourlyMevSignal,
                _currentWeightedScore,
                _currentDaughterScore,
                BossMaximumHoldProbability,
                BossMaximumContrarySellProbability,
                BossMaximumContraryBuyProbability,
                _policyRandom,
                out longProbability,
                out shortProbability,
                out holdProbability,
                out alignedProbability,
                out contraryProbability,
                out bossRandomDraw);
            if (sampledBossSignal == BossSignal.Hold)
            {
                LogNormal("POLICY", "BOSS_HOLD",
                    "schemaVersion=3 seed={0} barOpenUtc={1:O} setupName={2} score={3:F4} alignedDirection={4} alignedProbability={5:F4} contraryProbability={6:F4} longProbability={7:F4} shortProbability={8:F4} holdProbability={9:F4} bossRandomDraw={10:F6} outcome=Hold",
                    PolicyRandomSeed,
                    _currentHourlyMevBarOpenTime,
                    setupName,
                    _currentWeightedScore,
                    alignedBossSignal,
                    alignedProbability,
                    contraryProbability,
                    longProbability,
                    shortProbability,
                    holdProbability,
                    bossRandomDraw);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.Hold,
                    "holdSampled");
                return false;
            }

            TradeType tradeType = sampledBossSignal == BossSignal.Long
                ? TradeType.Buy
                : TradeType.Sell;
            WifeProbabilityMatrix wifeProbabilityMatrix =
                GetWifeProbabilityMatrix();
            double wifeTrendFollowingProbability;
            bool wifeSetupAligned;
            string wifeRejectionReason;
            if (!TryGetWifeTrendFollowingProbability(
                    _currentHourlyMevSignal,
                    sampledBossSignal,
                    wifeProbabilityMatrix,
                    out wifeTrendFollowingProbability,
                    out wifeSetupAligned,
                    out wifeRejectionReason))
            {
                LogNormal("POLICY", "WIFE_SELECTION_REJECTED",
                    "schemaVersion=1 seed={0} barOpenUtc={1:O} setupName={2} alignedDirection={3} bossOutcome={4} side={5} score={6:F4} bossRandomDraw={7:F6} reason={8}",
                    PolicyRandomSeed,
                    _currentHourlyMevBarOpenTime,
                    setupName,
                    alignedBossSignal,
                    sampledBossSignal,
                    tradeType,
                    _currentWeightedScore,
                    bossRandomDraw,
                    wifeRejectionReason);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.NoTradeIneligible,
                    wifeRejectionReason);
                return false;
            }

            double wifeDraw = _policyRandom.NextDouble();
            StrategyKind strategy = SelectWifeStrategy(
                wifeTrendFollowingProbability,
                wifeDraw);
            double meanReversionProbability =
                1.0 - wifeTrendFollowingProbability;
            string wifeRelation = wifeSetupAligned ? "aligned" : "contrary";
            double conditionalDirectionProbability =
                sampledBossSignal == alignedBossSignal
                    ? alignedProbability
                    : contraryProbability;

            LogNormal("POLICY", "SIGNAL_SAMPLED",
                "schemaVersion=4 seed={0} barOpenUtc={1:O} setupName={2} hourlyMev={3:F4} dailyMev={4:F4} sonScore={5:F4} daughterScore={6:F4} score={7:F4} alignedDirection={8} alignedProbability={9:F4} contraryProbability={10:F4} longProbability={11:F4} shortProbability={12:F4} holdProbability={13:F4} bossRandomDraw={14:F6} wifeTfProbability={15:F4} wifeRandomDraw={16:F6} hourlyState={17} dailyContext={18} bossOutcome={19} side={20} strategy={21} meanReversionProbability={22:F4} selectedDirectionProbability={23:F4} wifeRelation={24}",
                PolicyRandomSeed,
                _currentHourlyMevBarOpenTime,
                setupName,
                _currentHourlyMev,
                _currentDailyMev,
                _currentSonScore,
                _currentDaughterScore,
                _currentWeightedScore,
                alignedBossSignal,
                alignedProbability,
                contraryProbability,
                longProbability,
                shortProbability,
                holdProbability,
                bossRandomDraw,
                wifeTrendFollowingProbability,
                wifeDraw,
                _currentHourlyMevSignal,
                _currentDailyMevContext,
                sampledBossSignal,
                tradeType,
                strategy,
                meanReversionProbability,
                conditionalDirectionProbability,
                wifeRelation);

            bool allocationAvailable = IsSideAllocated(tradeType);
            bool movementGatePassed = HasRequiredPriceMove(tradeType, strategy, tick);
            if (!allocationAvailable || !movementGatePassed)
            {
                string failureCode = !allocationAvailable
                    ? "directionalAllocationUnavailable"
                    : "movementGateFailed";
                LogNormal("AUTO_POLICY", "POLICY_CANDIDATE_INELIGIBLE",
                    "schemaVersion=1 barOpenUtc={0:O} setupName={1} score={2:F4} longProbability={3:F4} shortProbability={4:F4} holdProbability={5:F4} direction={6} strategy={7} failureCode={8} allocationPercent={9:F2} movementGatePassed={10}",
                    _currentHourlyMevBarOpenTime,
                    setupName,
                    _currentWeightedScore,
                    longProbability,
                    shortProbability,
                    holdProbability,
                    tradeType,
                    strategy,
                    failureCode,
                    GetEffectiveSideAllocationPercent(tradeType),
                    movementGatePassed);
                ConsumePolicyBar(
                    _currentHourlyMevBarOpenTime,
                    PolicyDecisionStatus.NoTradeIneligible,
                    failureCode);
                return false;
            }

            LogVerbose("AUTO_POLICY", "POLICY_CANDIDATE_ELIGIBLE",
                "schemaVersion=1 barOpenUtc={0:O} setupName={1} direction={2} strategy={3} allocationPercent={4:F2} movementGatePassed=true",
                _currentHourlyMevBarOpenTime,
                setupName,
                tradeType,
                strategy,
                GetEffectiveSideAllocationPercent(tradeType));

            DateTime nowUtc = tick.UtcNow;
            _pendingPolicyDecision = new PendingPolicyDecision
            {
                BarOpenTimeUtc = _currentHourlyMevBarOpenTime,
                Direction = tradeType,
                Strategy = strategy,
                CreatedUtc = nowUtc,
                NextAttemptUtc = nowUtc,
                ExpiresUtc = nowUtc.AddMinutes(PolicyDecisionLifetimeMinutes),
                SetupName = setupName,
                DirectionProbability = tradeType == TradeType.Buy
                    ? longProbability
                    : shortProbability,
                WifeTrendFollowingProbability = wifeTrendFollowingProbability,
                WifeRandomDraw = wifeDraw,
                StrategyProbability = strategy == StrategyKind.TrendFollowing
                    ? wifeTrendFollowingProbability
                    : meanReversionProbability,
                SonScore = _currentSonScore,
                DaughterScore = _currentDaughterScore,
                Status = PolicyDecisionStatus.Ready
            };
            LogNormal("AUTO_POLICY", "POLICY_DECISION_CREATED",
                "schemaVersion=2 barOpenUtc={0:O} setupName={1} direction={2} strategy={3} directionProbability={4:F4} strategyProbability={5:F4} wifeTfProbability={6:F4} wifeRandomDraw={7:F6} submissionCorrelationId=none createdUtc={8:O} expiresUtc={9:O}",
                _pendingPolicyDecision.BarOpenTimeUtc,
                _pendingPolicyDecision.SetupName,
                _pendingPolicyDecision.Direction,
                _pendingPolicyDecision.Strategy,
                _pendingPolicyDecision.DirectionProbability,
                _pendingPolicyDecision.StrategyProbability,
                _pendingPolicyDecision.WifeTrendFollowingProbability,
                _pendingPolicyDecision.WifeRandomDraw,
                _pendingPolicyDecision.CreatedUtc,
                _pendingPolicyDecision.ExpiresUtc);

            return TryProcessPendingPolicyDecision(tick);
        }

        private bool TryProcessPendingPolicyDecision(TickSnapshot tick)
        {
            PendingPolicyDecision decision = _pendingPolicyDecision;
            if (decision == null)
                return false;

            ExpirePendingPolicyDecisionIfRequired(tick.UtcNow, true);
            decision = _pendingPolicyDecision;
            if (decision == null)
                return false;

            if (tick.UtcNow < decision.NextAttemptUtc)
                return false;

            decision.AttemptCount++;
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "Submitting",
                        "placementAttemptReserved")))
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.RuntimeFault,
                    "v2SubmittingJournalFailed");
                return false;
            }
            LogNormal("AUTO_POLICY", "POLICY_PLACEMENT_ATTEMPT",
                "barOpenUtc={0:O} submissionCorrelationId={1} attemptNumber={2} maximumTotalAttempts={3} direction={4} strategy={5} bid={6:R} ask={7:R}",
                decision.BarOpenTimeUtc,
                FormatSubmissionCorrelationId(decision.SubmissionCorrelationId),
                decision.AttemptCount,
                1 + TransientPlacementRetryCount,
                decision.Direction,
                decision.Strategy,
                tick.Bid,
                tick.Ask);
            PolicyPlacementResult result = TryPlacePolicySelectedOrder(
                decision,
                tick);

            if (!result.Submitted)
            {
                HandlePolicyPlacementFailure(decision, result);
                return false;
            }

            decision.PendingOrderId = result.PendingOrderId;
            decision.Status = PolicyDecisionStatus.Submitted;
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "Submitted",
                        result.Code)))
            {
                LogError("POLICY", "V2_SUBMISSION_JOURNAL_FAILED_AFTER_ACCEPT",
                    "decisionKey={0} pendingOrderId={1} outcome=failClosed",
                    decision.DecisionKey,
                    decision.PendingOrderId);
            }
            RecordDailyPolicySnapshot(
                decision.Direction,
                decision.Strategy,
                decision.SonScore,
                decision.DaughterScore);
            _policySamplingCooldownUntilUtc =
                tick.UtcNow.Add(PolicySamplingCooldown);
            LogVerbose("POLICY", "PENDING_ORDER_PLACED",
                "boss={0} wife={1} sonScore={2:F4} daughterScore={3:F4} hourlyMev={4:F4} dailyMev={5:F4} weightedScore={6:F4}",
                decision.Direction,
                decision.Strategy,
                decision.SonScore,
                decision.DaughterScore,
                _currentHourlyMev,
                _currentDailyMev,
                _currentWeightedScore);
            LogNormal("POLICY", "PENDING_ORDER_COOLDOWN_STARTED",
                "side={0} strategy={1} cooldownUntilUtc={2:O}",
                decision.Direction,
                decision.Strategy,
                _policySamplingCooldownUntilUtc);
            ConsumePendingPolicyDecision(
                PolicyDecisionStatus.Submitted,
                result.Code);
            return true;
        }

        private PolicyPlacementResult GetPolicyDecisionBarrier(DateTime utcNow)
        {
            if (_runtimeFaulted)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.RuntimeFault,
                    "runtimeFaulted");
            }

            if (_startupStopRequested)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Expired,
                    "startupStopRequested");
            }

            if (_rolloverContext.Phase != RolloverPhase.Normal)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.RolloverBlocked,
                    "rolloverPhase" + _rolloverContext.Phase);
            }

            MarketSessionState sessionState = GetMarketSessionState(utcNow);
            if (sessionState == MarketSessionState.PreCloseFlattening ||
                sessionState == MarketSessionState.Closed ||
                sessionState == MarketSessionState.MaintenanceBreak)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Expired,
                    "marketSession" + sessionState);
            }

            if (sessionState != MarketSessionState.Open)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.SessionBlocked,
                    "marketSession" + sessionState);
            }

            if (!HasExecutableMarketQuote(utcNow))
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "executableQuoteUnavailable");
            }

            if (!IsStartupOperational ||
                !_atrConfigurationReady ||
                !_referenceRangeReady ||
                _strategyInitializationInProgress ||
                !_hasValidHourlyMev ||
                !_hasValidDailyMev)
            {
                return PolicyPlacementResult.Failure(
                    PlacementFailureClass.Transient,
                    "strategyReadinessUnavailable");
            }

            return null;
        }

        private void HandlePolicyPlacementFailure(
            PendingPolicyDecision decision,
            PolicyPlacementResult result)
        {
            decision.LastFailureCode = result.Code;
            decision.LastFailureDetail = result.Detail;
            decision.LastFailureRequestIssued = result.RequestIssued;

            switch (result.FailureClass)
            {
                case PlacementFailureClass.Transient:
                case PlacementFailureClass.SessionBlocked:
                    SchedulePolicyPlacementRetry(decision, result);
                    return;
                case PlacementFailureClass.RolloverBlocked:
                case PlacementFailureClass.Expired:
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.Expired,
                        result.Code);
                    return;
                case PlacementFailureClass.RuntimeFault:
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.RuntimeFault,
                        result.Code);
                    return;
                case PlacementFailureClass.Permanent:
                default:
                    ConsumePendingPolicyDecision(
                        PolicyDecisionStatus.PermanentlyRejected,
                        result.Code ?? "unclassifiedPlacementFailure");
                    return;
            }
        }

        private void SchedulePolicyPlacementRetry(
            PendingPolicyDecision decision,
            PolicyPlacementResult result)
        {
            if (decision.RetryCount >= TransientPlacementRetryCount)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.RetryExhausted,
                    result.Code);
                return;
            }

            DateTime nextAttemptUtc;
            try
            {
                nextAttemptUtc = Server.TimeInUtc.AddSeconds(
                    TransientPlacementRetrySeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "nextRetryTimeOutOfRange");
                return;
            }

            if (nextAttemptUtc >= decision.ExpiresUtc)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "nextRetryBeyondDecisionLifetime");
                return;
            }

            decision.RetryCount++;
            decision.NextAttemptUtc = nextAttemptUtc;
            decision.Status = PolicyDecisionStatus.WaitingToRetry;
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "RetryScheduled",
                        result.Code)))
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.RuntimeFault,
                    "v2RetryJournalFailed");
                return;
            }
            LogNormal("AUTO_POLICY", "PLACEMENT_RETRY_SCHEDULED",
                "barOpenUtc={0:O} submissionCorrelationId={1} strategy={2} direction={3} retry={4} maximumRetries={5} attemptCount={6} nextAttemptUtc={7:O} failureClass={8} failureCode={9} requestIssued={10} failureDetail={11}",
                decision.BarOpenTimeUtc,
                FormatSubmissionCorrelationId(decision.SubmissionCorrelationId),
                decision.Strategy,
                decision.Direction,
                decision.RetryCount,
                TransientPlacementRetryCount,
                decision.AttemptCount,
                decision.NextAttemptUtc,
                result.FailureClass,
                result.Code,
                result.RequestIssued,
                result.Detail ?? "none");
        }

        private void ConsumePendingPolicyDecision(
            PolicyDecisionStatus finalStatus,
            string reason)
        {
            PendingPolicyDecision decision = _pendingPolicyDecision;
            if (decision == null)
                return;

            decision.Status = finalStatus;
            _lastConsumedPolicyBarOpenTime = decision.BarOpenTimeUtc;
            if (!PersistV2DecisionJournal(
                    BuildV2DecisionJournalEntry(
                        decision,
                        "Terminal",
                        reason)))
            {
                LogError("POLICY", "V2_TERMINAL_JOURNAL_FAILED",
                    "decisionKey={0} status={1} outcome=failClosed",
                    decision.DecisionKey,
                    finalStatus);
            }
            LogNormal("AUTO_POLICY", "POLICY_DECISION_TERMINATED",
                "schemaVersion=2 barOpenUtc={0:O} submissionCorrelationId={1} strategy={2} direction={3} status={4} attempts={5} retries={6} pendingOrderId={7} wifeTfProbability={8:F4} wifeRandomDraw={9:F6} reason={10} lastFailureCode={11} lastFailureRequestIssued={12} lastFailureDetail={13}",
                decision.BarOpenTimeUtc,
                FormatSubmissionCorrelationId(decision.SubmissionCorrelationId),
                decision.Strategy,
                decision.Direction,
                finalStatus,
                decision.AttemptCount,
                decision.RetryCount,
                decision.PendingOrderId.HasValue
                    ? decision.PendingOrderId.Value.ToString(CultureInfo.InvariantCulture)
                    : "none",
                decision.WifeTrendFollowingProbability,
                decision.WifeRandomDraw,
                reason,
                decision.LastFailureCode ?? "none",
                decision.LastFailureRequestIssued,
                decision.LastFailureDetail ?? "none");
            _pendingPolicyDecision = null;
        }

        private void ConsumePolicyBar(
            DateTime barOpenUtc,
            PolicyDecisionStatus status,
            string reason)
        {
            _lastConsumedPolicyBarOpenTime = barOpenUtc;
            LogNormal("AUTO_POLICY", "POLICY_DECISION_TERMINATED",
                "barOpenUtc={0:O} status={1} attempts=0 retries=0 reason={2}",
                barOpenUtc,
                status,
                reason);
        }

        private void ExpirePendingPolicyDecisionIfRequired(
            DateTime utcNow,
            bool checkCurrentHourlyBar)
        {
            PendingPolicyDecision decision = _pendingPolicyDecision;
            if (decision == null)
                return;

            if (utcNow >= decision.ExpiresUtc)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "decisionLifetimeExceeded");
                return;
            }

            if (checkCurrentHourlyBar &&
                _currentHourlyMevBarOpenTime != decision.BarOpenTimeUtc)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "supersededByNewHourlyBar");
                return;
            }

            if (_runtimeFaulted || _startupStopRequested)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.RuntimeFault,
                    _runtimeFaulted ? "runtimeFaulted" : "startupStopRequested");
                return;
            }

            if (_rolloverContext.Phase != RolloverPhase.Normal)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "rolloverPhase" + _rolloverContext.Phase);
                return;
            }

            MarketSessionState sessionState = GetMarketSessionState(utcNow);
            if (sessionState == MarketSessionState.PreCloseFlattening ||
                sessionState == MarketSessionState.Closed ||
                sessionState == MarketSessionState.MaintenanceBreak)
            {
                ConsumePendingPolicyDecision(
                    PolicyDecisionStatus.Expired,
                    "marketSession" + sessionState);
            }
        }

        private static BossSignal GetSetupAlignedBossSignal(
            HourlyMevSignal setup)
        {
            switch (setup)
            {
                case HourlyMevSignal.ConfirmedBreakoutLong:
                case HourlyMevSignal.PanicReversalLong:
                    return BossSignal.Long;
                case HourlyMevSignal.ConfirmedBreakdownShort:
                case HourlyMevSignal.ExtremePositiveReversalShort:
                    return BossSignal.Short;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(setup),
                        setup,
                        "A valid hourly MEV setup is required for Boss sampling.");
            }
        }

        private static bool IsBossAlignedSellEligible(
            HourlyMevSignal setup,
            double daughterScore)
        {
            return GetSetupAlignedBossSignal(setup) != BossSignal.Short ||
                   (IsFinite(daughterScore) && daughterScore < 0.0);
        }

        private static void GetBossProbabilities(
            BossSignal alignedDirection,
            double score,
            double maximumHoldProbability,
            double maximumContraryProbability,
            out double alignedProbability,
            out double contraryProbability,
            out double holdProbability)
        {
            if (alignedDirection != BossSignal.Long &&
                alignedDirection != BossSignal.Short)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alignedDirection));
            }

            if (!IsFinite(score) || score < -1.0 || score > 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(score), "Weighted score must be between -1 and 1.");
            if (!IsProbability(maximumHoldProbability) ||
                !IsProbability(maximumContraryProbability) ||
                maximumHoldProbability + maximumContraryProbability >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumHoldProbability),
                    "Maximum Hold and contrary probabilities must be valid and sum to less than one.");
            }

            double strength = Clamp01(Math.Abs(score));
            holdProbability =
                maximumHoldProbability * (1.0 - strength);
            contraryProbability =
                maximumContraryProbability * (1.0 - strength);
            alignedProbability =
                1.0 - holdProbability - contraryProbability;

            alignedProbability = Clamp01(alignedProbability);
            contraryProbability = Clamp01(contraryProbability);
            holdProbability = Clamp01(holdProbability);
            double probabilityTotal =
                alignedProbability + contraryProbability + holdProbability;
            if (!IsFinite(probabilityTotal) || probabilityTotal <= 0.0)
                throw new InvalidOperationException(
                    "Boss probabilities could not be normalized.");

            if (Math.Abs(probabilityTotal - 1.0) > 1e-12)
            {
                alignedProbability /= probabilityTotal;
                contraryProbability /= probabilityTotal;
                holdProbability /= probabilityTotal;
            }
        }

        private static bool IsProbability(double value)
        {
            return IsFinite(value) && value >= 0.0 && value <= 1.0;
        }

        private static void GetBossProbabilities(
            BossSignal alignedDirection,
            double score,
            double maximumHoldProbability,
            double maximumContrarySellProbability,
            double maximumContraryBuyProbability,
            out double longProbability,
            out double shortProbability,
            out double holdProbability,
            out double alignedProbability,
            out double contraryProbability)
        {
            double maximumContraryProbability =
                alignedDirection == BossSignal.Long
                    ? maximumContrarySellProbability
                    : maximumContraryBuyProbability;
            GetBossProbabilities(
                alignedDirection,
                score,
                maximumHoldProbability,
                maximumContraryProbability,
                out alignedProbability,
                out contraryProbability,
                out holdProbability);

            if (alignedDirection == BossSignal.Long)
            {
                longProbability = alignedProbability;
                shortProbability = contraryProbability;
            }
            else
            {
                longProbability = contraryProbability;
                shortProbability = alignedProbability;
            }
        }

        private static BossSignal GenerateBossSignal(
            HourlyMevSignal setup,
            double score,
            double daughterScore,
            double maximumHoldProbability,
            double maximumContrarySellProbability,
            double maximumContraryBuyProbability,
            Random random,
            out double longProbability,
            out double shortProbability,
            out double holdProbability,
            out double alignedProbability,
            out double contraryProbability,
            out double randomDraw)
        {
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            BossSignal alignedDirection = GetSetupAlignedBossSignal(setup);
            if (!IsBossAlignedSellEligible(setup, daughterScore))
                throw new InvalidOperationException(
                    "Setup-aligned Sell requires a negative Daughter score.");

            GetBossProbabilities(
                alignedDirection,
                score,
                maximumHoldProbability,
                maximumContrarySellProbability,
                maximumContraryBuyProbability,
                out longProbability,
                out shortProbability,
                out holdProbability,
                out alignedProbability,
                out contraryProbability);

            randomDraw = random.NextDouble();
            return SelectBossSignal(
                alignedDirection,
                alignedProbability,
                contraryProbability,
                longProbability,
                shortProbability,
                holdProbability,
                randomDraw);
        }

        private static BossSignal SelectBossSignal(
            BossSignal alignedDirection,
            double alignedProbability,
            double contraryProbability,
            double longProbability,
            double shortProbability,
            double holdProbability,
            double randomDraw)
        {
            double probabilityTotal =
                longProbability + shortProbability + holdProbability;
            if (!IsFinite(longProbability) ||
                !IsFinite(shortProbability) ||
                !IsFinite(holdProbability) ||
                !IsFinite(alignedProbability) ||
                !IsFinite(contraryProbability) ||
                longProbability < 0.0 ||
                shortProbability < 0.0 ||
                holdProbability < 0.0 ||
                alignedProbability < 0.0 ||
                contraryProbability < 0.0 ||
                holdProbability >= 1.0 ||
                Math.Abs(probabilityTotal - 1.0) > 1e-10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(probabilityTotal), "Boss probabilities must be finite and sum to one.");
            }

            if (!IsFinite(randomDraw) || randomDraw < 0.0 || randomDraw >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(randomDraw));
            if (randomDraw < holdProbability)
                return BossSignal.Hold;

            return randomDraw < holdProbability + alignedProbability
                ? alignedDirection
                : alignedDirection == BossSignal.Long
                    ? BossSignal.Short
                    : BossSignal.Long;
        }

        private static StrategyKind SelectWifeStrategy(
            double trendFollowingProbability,
            double draw)
        {
            if (!IsFinite(trendFollowingProbability) ||
                trendFollowingProbability < 0.0 ||
                trendFollowingProbability > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(trendFollowingProbability));
            }
            if (!IsFinite(draw) || draw < 0.0 || draw >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(draw));

            return draw < trendFollowingProbability
                ? StrategyKind.TrendFollowing
                : StrategyKind.MeanReversion;
        }

        private struct WifeProbabilityMatrix
        {
            public double BreakoutAlignedTfProbability;
            public double BreakoutContraryTfProbability;
            public double BreakdownAlignedTfProbability;
            public double BreakdownContraryTfProbability;
            public double PanicReversalAlignedTfProbability;
            public double PanicReversalContraryTfProbability;
            public double ExtremeReversalAlignedTfProbability;
            public double ExtremeReversalContraryTfProbability;
            public bool RejectContraryReversalSelections;
        }

        private WifeProbabilityMatrix GetWifeProbabilityMatrix()
        {
            return new WifeProbabilityMatrix
            {
                BreakoutAlignedTfProbability = WifeBreakoutAlignedTfProbability,
                BreakoutContraryTfProbability = WifeBreakoutContraryTfProbability,
                BreakdownAlignedTfProbability = WifeBreakdownAlignedTfProbability,
                BreakdownContraryTfProbability = WifeBreakdownContraryTfProbability,
                PanicReversalAlignedTfProbability = WifePanicReversalAlignedTfProbability,
                PanicReversalContraryTfProbability = WifePanicReversalContraryTfProbability,
                ExtremeReversalAlignedTfProbability = WifeExtremeReversalAlignedTfProbability,
                ExtremeReversalContraryTfProbability = WifeExtremeReversalContraryTfProbability,
                RejectContraryReversalSelections = RejectContraryReversalSelections
            };
        }

        private static bool TryGetWifeTrendFollowingProbability(
            HourlyMevSignal setup,
            BossSignal selectedDirection,
            WifeProbabilityMatrix matrix,
            out double trendFollowingProbability,
            out bool setupAligned,
            out string rejectionReason)
        {
            trendFollowingProbability = double.NaN;
            setupAligned = false;
            rejectionReason = "unsupportedSetupDirection";

            switch (setup)
            {
                case HourlyMevSignal.ConfirmedBreakoutLong:
                    if (selectedDirection == BossSignal.Long)
                    {
                        trendFollowingProbability = matrix.BreakoutAlignedTfProbability;
                        setupAligned = true;
                    }
                    else if (selectedDirection == BossSignal.Short)
                    {
                        trendFollowingProbability = matrix.BreakoutContraryTfProbability;
                    }
                    else
                    {
                        return false;
                    }
                    break;
                case HourlyMevSignal.ConfirmedBreakdownShort:
                    if (selectedDirection == BossSignal.Short)
                    {
                        trendFollowingProbability = matrix.BreakdownAlignedTfProbability;
                        setupAligned = true;
                    }
                    else if (selectedDirection == BossSignal.Long)
                    {
                        trendFollowingProbability = matrix.BreakdownContraryTfProbability;
                    }
                    else
                    {
                        return false;
                    }
                    break;
                case HourlyMevSignal.PanicReversalLong:
                    if (selectedDirection == BossSignal.Long)
                    {
                        trendFollowingProbability = matrix.PanicReversalAlignedTfProbability;
                        setupAligned = true;
                    }
                    else if (selectedDirection == BossSignal.Short)
                    {
                        if (matrix.RejectContraryReversalSelections)
                        {
                            rejectionReason = "contraryReversalSelectionRejected";
                            return false;
                        }
                        trendFollowingProbability = matrix.PanicReversalContraryTfProbability;
                    }
                    else
                    {
                        return false;
                    }
                    break;
                case HourlyMevSignal.ExtremePositiveReversalShort:
                    if (selectedDirection == BossSignal.Short)
                    {
                        trendFollowingProbability = matrix.ExtremeReversalAlignedTfProbability;
                        setupAligned = true;
                    }
                    else if (selectedDirection == BossSignal.Long)
                    {
                        if (matrix.RejectContraryReversalSelections)
                        {
                            rejectionReason = "contraryReversalSelectionRejected";
                            return false;
                        }
                        trendFollowingProbability = matrix.ExtremeReversalContraryTfProbability;
                    }
                    else
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }

            if (!IsProbability(trendFollowingProbability))
                throw new ArgumentOutOfRangeException(
                    nameof(matrix),
                    "The selected Wife probability must be finite and from 0 to 1.");

            rejectionReason = null;
            return true;
        }

        private static bool RunPolicySamplingSelfTests()
        {
            GetBossProbabilities(
                BossSignal.Long,
                0.40,
                0.15,
                0.07,
                0.12,
                out double bullishLongProbability,
                out double bullishShortProbability,
                out double bullishHoldProbability,
                out double bullishAlignedProbability,
                out double bullishContraryProbability);
            GetBossProbabilities(
                BossSignal.Short,
                -0.40,
                0.15,
                0.07,
                0.12,
                out double bearishLongProbability,
                out double bearishShortProbability,
                out double bearishHoldProbability,
                out double bearishAlignedProbability,
                out double bearishContraryProbability);
            GetBossProbabilities(
                BossSignal.Long,
                1.0,
                0.15,
                0.07,
                0.12,
                out double fullLongProbability,
                out double noShortProbability,
                out double noHoldProbability,
                out double fullAlignedProbability,
                out double noContraryProbability);

            return Math.Abs(bullishHoldProbability - 0.09) <= 1e-12 &&
                Math.Abs(bullishAlignedProbability - 0.868) <= 1e-12 &&
                Math.Abs(bullishContraryProbability - 0.042) <= 1e-12 &&
                Math.Abs(bullishLongProbability - 0.868) <= 1e-12 &&
                Math.Abs(bullishShortProbability - 0.042) <= 1e-12 &&
                Math.Abs(bearishHoldProbability - 0.09) <= 1e-12 &&
                Math.Abs(bearishAlignedProbability - 0.838) <= 1e-12 &&
                Math.Abs(bearishContraryProbability - 0.072) <= 1e-12 &&
                Math.Abs(bearishLongProbability - 0.072) <= 1e-12 &&
                Math.Abs(bearishShortProbability - 0.838) <= 1e-12 &&
                SelectBossSignal(
                    BossSignal.Long,
                    bullishAlignedProbability,
                    bullishContraryProbability,
                    bullishLongProbability,
                    bullishShortProbability,
                    bullishHoldProbability,
                    0.089) == BossSignal.Hold &&
                SelectBossSignal(
                    BossSignal.Long,
                    bullishAlignedProbability,
                    bullishContraryProbability,
                    bullishLongProbability,
                    bullishShortProbability,
                    bullishHoldProbability,
                    0.090) == BossSignal.Long &&
                SelectBossSignal(
                    BossSignal.Long,
                    bullishAlignedProbability,
                    bullishContraryProbability,
                    bullishLongProbability,
                    bullishShortProbability,
                    bullishHoldProbability,
                    bullishHoldProbability + bullishAlignedProbability) ==
                    BossSignal.Short &&
                SelectBossSignal(
                    BossSignal.Short,
                    bearishAlignedProbability,
                    bearishContraryProbability,
                    bearishLongProbability,
                    bearishShortProbability,
                    bearishHoldProbability,
                    0.090) == BossSignal.Short &&
                SelectBossSignal(
                    BossSignal.Short,
                    bearishAlignedProbability,
                    bearishContraryProbability,
                    bearishLongProbability,
                    bearishShortProbability,
                    bearishHoldProbability,
                    bearishHoldProbability + bearishAlignedProbability) ==
                    BossSignal.Long &&
                fullLongProbability == 1.0 &&
                noShortProbability == 0.0 &&
                noHoldProbability == 0.0 &&
                fullAlignedProbability == 1.0 &&
                noContraryProbability == 0.0 &&
                IsBossAlignedSellEligible(
                    HourlyMevSignal.ConfirmedBreakdownShort,
                    -0.01) &&
                !IsBossAlignedSellEligible(
                    HourlyMevSignal.ConfirmedBreakdownShort,
                    0.0) &&
                GetSetupAlignedBossSignal(
                    HourlyMevSignal.PanicReversalLong) == BossSignal.Long &&
                GetSetupAlignedBossSignal(
                    HourlyMevSignal.ExtremePositiveReversalShort) == BossSignal.Short &&
                SelectWifeStrategy(0.25, 0.249) == StrategyKind.TrendFollowing &&
                SelectWifeStrategy(0.25, 0.25) == StrategyKind.MeanReversion &&
                SelectWifeStrategy(0.0, 0.999) == StrategyKind.MeanReversion &&
                SelectWifeStrategy(1.0, 0.0) == StrategyKind.TrendFollowing &&
                RunWifeMatrixSelfTests();
        }

        private static bool RunWifeMatrixSelfTests()
        {
            WifeProbabilityMatrix matrix = new WifeProbabilityMatrix
            {
                BreakoutAlignedTfProbability = 0.80,
                BreakoutContraryTfProbability = 0.15,
                BreakdownAlignedTfProbability = 0.70,
                BreakdownContraryTfProbability = 0.15,
                PanicReversalAlignedTfProbability = 0.25,
                PanicReversalContraryTfProbability = 0.70,
                ExtremeReversalAlignedTfProbability = 0.15,
                ExtremeReversalContraryTfProbability = 0.75,
                RejectContraryReversalSelections = true
            };

            return WifeProbabilityMatches(
                    HourlyMevSignal.ConfirmedBreakoutLong,
                    BossSignal.Long,
                    matrix,
                    0.80,
                    true) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.ConfirmedBreakoutLong,
                    BossSignal.Short,
                    matrix,
                    0.15,
                    false) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.ConfirmedBreakdownShort,
                    BossSignal.Short,
                    matrix,
                    0.70,
                    true) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.ConfirmedBreakdownShort,
                    BossSignal.Long,
                    matrix,
                    0.15,
                    false) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.PanicReversalLong,
                    BossSignal.Long,
                    matrix,
                    0.25,
                    true) &&
                WifeProbabilityRejected(
                    HourlyMevSignal.PanicReversalLong,
                    BossSignal.Short,
                    matrix) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.ExtremePositiveReversalShort,
                    BossSignal.Short,
                    matrix,
                    0.15,
                    true) &&
                WifeProbabilityRejected(
                    HourlyMevSignal.ExtremePositiveReversalShort,
                    BossSignal.Long,
                    matrix) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.PanicReversalLong,
                    BossSignal.Short,
                    new WifeProbabilityMatrix
                    {
                        PanicReversalContraryTfProbability = 0.70,
                        RejectContraryReversalSelections = false
                    },
                    0.70,
                    false) &&
                WifeProbabilityMatches(
                    HourlyMevSignal.ExtremePositiveReversalShort,
                    BossSignal.Long,
                    new WifeProbabilityMatrix
                    {
                        ExtremeReversalContraryTfProbability = 0.75,
                        RejectContraryReversalSelections = false
                    },
                    0.75,
                    false);
        }

        private static bool WifeProbabilityMatches(
            HourlyMevSignal setup,
            BossSignal direction,
            WifeProbabilityMatrix matrix,
            double expectedProbability,
            bool expectedAligned)
        {
            double probability;
            bool aligned;
            string reason;
            return TryGetWifeTrendFollowingProbability(
                    setup,
                    direction,
                    matrix,
                    out probability,
                    out aligned,
                    out reason) &&
                Math.Abs(probability - expectedProbability) <= 1e-12 &&
                aligned == expectedAligned &&
                reason == null;
        }

        private static bool WifeProbabilityRejected(
            HourlyMevSignal setup,
            BossSignal direction,
            WifeProbabilityMatrix matrix)
        {
            double probability;
            bool aligned;
            string reason;
            return !TryGetWifeTrendFollowingProbability(
                    setup,
                    direction,
                    matrix,
                    out probability,
                    out aligned,
                    out reason) &&
                reason == "contraryReversalSelectionRejected";
        }

        #endregion

    }
}
