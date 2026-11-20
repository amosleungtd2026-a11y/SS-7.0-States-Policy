using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Strategy state, hourly volatility, and directional sizing.
    public partial class ShootingStar
    {
        #region ATR and Adjusted Trading Values

        private Bars _hourlyBars;
        private Bars _dailyBars;
        private AverageTrueRange _fastHourlyAtr;
        private AverageTrueRange _slowHourlyAtr;
        private double _atrRatio = 1.0;
        private double _normalizedAtrRatio = 1.0;
        private DateTime _lastProcessedCompletedHourlyBarOpenTime = DateTime.MinValue;

        private enum HourlyMevSignal
        {
            None,
            PanicReversalLong,
            ConfirmedBreakoutLong,
            ConfirmedBreakdownShort,
            ExtremePositiveReversalShort
        }

        private enum DailyMevContext
        {
            Unavailable,
            DeepNegative,
            MidNegative,
            Neutral,
            MidPositive,
            HighPositive
        }

        private bool _hasValidHourlyMev;
        private double _currentHourlyMev = double.NaN;
        private double _previousHourlyMev = double.NaN;
        private double _currentHourlyMevSigma21 = double.NaN;
        private bool _hasValidDailyMev;
        private double _currentDailyMev = double.NaN;
        private DateTime _lastHourlyMevRefreshUtc = DateTime.MinValue;
        private DateTime _lastDailyMevRefreshUtc = DateTime.MinValue;
        private HourlyMevSignal _currentHourlyMevSignal;
        private DailyMevContext _currentDailyMevContext;
        private double _currentDaughterScore;
        private double _currentWeightedScore;
        private string _currentSonCurveName = "none";
        private double _currentSonCurveStrength = double.NaN;
        private double _currentSonCurveFloor = double.NaN;
        private double _currentSonCurveCeiling = double.NaN;
        private double _currentSonCurveExponent = double.NaN;
        private double _currentSonRawMagnitude = double.NaN;
        private double _currentReversalEvidence = double.NaN;
        private double _currentNormalizedReversalEvidence = double.NaN;
        private double _currentReversalConfirmation = double.NaN;
        private bool _currentPolicySignalEligible;
        private string _currentPolicySignalReason = "unavailable";
        private DateTime _currentHourlyMevBarOpenTime = DateTime.MinValue;
        private DateTime _lastPolicyDecisionBarOpenTime = DateTime.MinValue;
        private DateTime _lastConsumedPolicyBarOpenTime = DateTime.MinValue;
        private DateTime _nextPolicyEntryEvaluationUtc = DateTime.MinValue;
        private DailyMevContext _daughterCandidateContext = DailyMevContext.Unavailable;
        private DateTime _daughterCandidateFirstSeenUtc = DateTime.MinValue;
        private static readonly TimeSpan RealtimeEvaluationInterval = TimeSpan.FromSeconds(1.0);

        private enum PolicyDecisionStatus
        {
            Ready,
            WaitingToRetry,
            Submitted,
            Hold,
            PermanentlyRejected,
            RetryExhausted,
            Expired,
            RuntimeFault,
            NoTradeIneligible
        }

        private enum PlacementFailureClass
        {
            None,
            Transient,
            Permanent,
            RuntimeFault,
            SessionBlocked,
            RolloverBlocked,
            Expired
        }

        private sealed class PolicyPlacementResult
        {
            public bool Submitted;
            public bool RequestIssued;
            public PlacementFailureClass FailureClass;
            public string Code;
            public string Detail;
            public int? PendingOrderId;

            public static PolicyPlacementResult Success(
                int? pendingOrderId,
                string code,
                bool requestIssued)
            {
                return new PolicyPlacementResult
                {
                    Submitted = true,
                    RequestIssued = requestIssued,
                    FailureClass = PlacementFailureClass.None,
                    Code = code,
                    PendingOrderId = pendingOrderId
                };
            }

            public static PolicyPlacementResult Failure(
                PlacementFailureClass failureClass,
                string code,
                string detail = null,
                bool requestIssued = false)
            {
                return new PolicyPlacementResult
                {
                    Submitted = false,
                    RequestIssued = requestIssued,
                    FailureClass = failureClass,
                    Code = code,
                    Detail = detail
                };
            }
        }

        private sealed class PendingPolicyDecision
        {
            public DateTime BarOpenTimeUtc;
            public TradeType Direction;
            public StrategyKind Strategy;
            public int AttemptCount;
            public int RetryCount;
            public DateTime CreatedUtc;
            public DateTime NextAttemptUtc;
            public DateTime ExpiresUtc;
            public string DecisionKey;
            public Guid? SubmissionCorrelationId;
            public string SetupName;
            public string BossOutcome;
            public double BossRandomDraw;
            public double WifeStopProbability;
            public double DirectionProbability;
            public double StrategyProbability;
            public double WifeTrendFollowingProbability;
            public double WifeRandomDraw;
            public V2OrderKind OrderKind;
            public double FrozenStopOrderMultiplier;
            public double EffectiveLimitDistancePercent;
            public double EffectiveEntryDistancePercent;
            public double VolatilityFactor;
            public double TargetPrice;
            public double MovementReferencePrice;
            public double MovePercent;
            public double StopLossPercent;
            public double TakeProfitPercent;
            public double VolumeInUnits;
            public string Label;
            public string Description;
            public double SonScore;
            public double DaughterScore;
            public PolicyDecisionStatus Status;
            public string LastFailureCode;
            public string LastFailureDetail;
            public bool LastFailureRequestIssued;
            public int? PendingOrderId;
        }

        private PendingPolicyDecision _pendingPolicyDecision;

        private StrategyKind _activeLongEntryStrategy = StrategyKind.MeanReversion;
        private StrategyKind _activeShortEntryStrategy = StrategyKind.MeanReversion;
        private double _currentSonScore;
        private DateTime _policySamplingCooldownUntilUtc = DateTime.MinValue;
        private static readonly TimeSpan PolicySamplingCooldown = TimeSpan.FromMinutes(5.0);

        private double _actualMrUpMovePercent;
        private double _actualMrSellLimitDistancePercent;
        private double _actualMrSellTakeProfitPercent;
        private double _actualMrSellStopLossPercent;
        private double _actualMrDownMovePercent;
        private double _actualMrBuyLimitDistancePercent;
        private double _actualMrBuyTakeProfitPercent;
        private double _actualMrBuyStopLossPercent;
        private double _actualTfLongMovePercent;
        private double _actualTfBuyLimitDistancePercent;
        private double _actualTfLongTakeProfitPercent;
        private double _actualTfLongStopLossPercent;
        private double _actualTfShortMovePercent;
        private double _actualTfSellLimitDistancePercent;
        private double _actualTfShortTakeProfitPercent;
        private double _actualTfShortStopLossPercent;
        private Random _policyRandom;

        #endregion

        #region Hourly Volatility State
        private readonly double[] _utcHourlyVolatilityFactors = Enumerable.Repeat(1.0, 24).ToArray();
        #endregion

        #region Directional Allocation
        private bool IsValidDirectionalAllocation(double value)
        {
            return IsFinite(value) && value >= 0.0 && value <= 100.0;
        }
        private void ApplyDirectionalAllocationParameter()
        {
            if (!IsValidDirectionalAllocation(DirectionalAllocation))
                throw new ArgumentOutOfRangeException(nameof(DirectionalAllocation),
                    "Long Allocation (%) must be a finite number from 0 to 100.");
            _effectiveDirectionalAllocation = DirectionalAllocation;
        }
        #endregion

        #region Hourly Volatility Factors

        private void ParseAndNormalizeHourlyVolatilityValues()
        {
            string input = UtcHourlyVolatilityValues ?? string.Empty;
            string[] tokens = input.Split(
                new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 24)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture,
                        "UTC Hour Volatility Values must contain exactly 24 numbers, one for each UTC hour from 00 through 23. Detected {0}.",
                        tokens.Length));
            }
            var rawValues = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                double value;
                if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    value <= 0.0 || !IsFinite(value))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.InvariantCulture,
                            "UTC Hour Volatility Values item {0} ('{1}') must be a finite positive number using '.' as the decimal separator.",
                            i + 1,
                            tokens[i]));
                }
                rawValues[i] = value;
            }
            double minimum = rawValues.Min();
            double maximum = rawValues.Max();
            if (Math.Abs(maximum - minimum) <= 1e-12)
            {
                for (int hour = 0; hour < 24; hour++)
                    _utcHourlyVolatilityFactors[hour] = 1.0;
            }
            else
            {
                for (int hour = 0; hour < rawValues.Length; hour++)
                {
                    double normalizedValue = (rawValues[hour] - minimum) / (maximum - minimum);
                    _utcHourlyVolatilityFactors[hour] = MinHourlyVolatilityFactor +
                        normalizedValue * (MaxHourlyVolatilityFactor - MinHourlyVolatilityFactor);
                }
            }
            double factorMinimum = _utcHourlyVolatilityFactors.Min();
            double factorMaximum = _utcHourlyVolatilityFactors.Max();
            LogNormal("STARTUP", "VF",
                "supplied={0} range={1:F3}-{2:F3} currentHour={3:D2} currentVF={4:F3}",
                rawValues.Length, factorMinimum, factorMaximum,
                Server.TimeInUtc.Hour, GetCurrentHourlyVolatilityFactor());
            LogVerbose("STARTUP", "VF_VALUES", "hours=24 factors=[{0}]",
                string.Join(",", _utcHourlyVolatilityFactors.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
        }
        private static int GetUtcHourKey(DateTime utc)
        {
            return utc.Year * 100000 + utc.DayOfYear * 100 + utc.Hour;
        }

        private double GetCurrentHourlyVolatilityFactor()
        {
            int utcHour = Server.TimeInUtc.Hour;
            return _utcHourlyVolatilityFactors[utcHour];
        }

        private double ApplyHourlyVolatilityToVolume(
            double baseVolumeInUnits,
            double volatilityFactor)
        {
            return NormalizeVolume(baseVolumeInUnits / volatilityFactor);
        }
        private void CaptureBotPositions(List<Position> snapshot)
        {
            snapshot.Clear();
            foreach (var position in Positions)
            {
                if (IsBotPosition(position))
                    snapshot.Add(position);
            }
        }
        private void CaptureBotPendingOrders(List<PendingOrder> snapshot)
        {
            snapshot.Clear();
            foreach (var order in PendingOrders)
            {
                if (IsBotOrder(order))
                    snapshot.Add(order);
            }
        }
        private void RemoveStalePendingOrderTrailingStates()
        {
            _activeBotPendingOrderIds.Clear();
            foreach (var order in PendingOrders)
            {
                if (IsBotOrder(order))
                    _activeBotPendingOrderIds.Add(order.Id);
            }

            _stalePendingOrderStateIds.Clear();
            foreach (int orderId in _pendingOrderTrailingStates.Keys)
            {
                if (!_activeBotPendingOrderIds.Contains(orderId))
                    _stalePendingOrderStateIds.Add(orderId);
            }

            foreach (int orderId in _stalePendingOrderStateIds)
            {
                _pendingOrderTrailingStates.Remove(orderId);
                _pendingOrderEvidenceById.Remove(orderId);
                EndSafeSeparationBlock(orderId, "StaleStateCleanup", true);
                _pendingOrderNoChangeLogTimes.Remove(orderId);
                _rejectedPendingOrderModifications.Remove(orderId);
                ResolveProvisionalSafeDistancePendingOrder(
                    orderId,
                    ProvisionalSafeDistanceResolution.StaleCleanup);
            }
        }

        #endregion


    }
}
