using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Shared state models, snapshots, and attribution evidence.
    public partial class ShootingStar
    {
        #region Nested State and Snapshot Models

        public enum BotLogLevel
        {
            ErrorsOnly,
            Normal,
            Verbose
        }
        public enum YesNo
        {
            Y,
            N
        }
        public enum StartupTradingMode
        {
            Auto = 0,
            Manual = 1,
            UserTriggered = 2
        }
        private enum TradingMode
        {
            ProbabilisticAuto = 0,
            RuleMappedAuto = 1,
            UserTriggered = 2
        }
        private enum EntryOriginMode
        {
            ProbabilisticAuto,
            RuleMappedAuto,
            UserTriggered,
            RolloverRestoration,
            Unattributed
        }
        private enum ManualSideStrategy
        {
            None,
            MeanReversion,
            TrendFollowing
        }
        private enum ManualRankBand
        {
            Lower,
            Middle,
            Upper
        }
        private enum CallbackFailurePolicy
        {
            Continue,
            StopBot
        }

        private enum StrategyKind
        {
            MeanReversion,
            TrendFollowing
        }

        private static TradingMode MapStartupTradingMode(StartupTradingMode mode)
        {
            switch (mode)
            {
                case StartupTradingMode.Auto:
                    return TradingMode.ProbabilisticAuto;
                case StartupTradingMode.Manual:
                    return TradingMode.RuleMappedAuto;
                case StartupTradingMode.UserTriggered:
                    return TradingMode.UserTriggered;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mode),
                        mode,
                        "The configured trading mode is not supported.");
            }
        }

        private static string GetTradingModeDisplayName(TradingMode mode)
        {
            switch (mode)
            {
                case TradingMode.ProbabilisticAuto:
                    return "Probabilistic Auto";
                case TradingMode.RuleMappedAuto:
                    return "Rule-Mapped Auto";
                case TradingMode.UserTriggered:
                    return "User-Triggered";
                default:
                    return "Unknown";
            }
        }

        private static bool TryParseTradingModeDisplayName(
            string displayName,
            out TradingMode mode)
        {
            switch (displayName)
            {
                case "Probabilistic Auto":
                    mode = TradingMode.ProbabilisticAuto;
                    return true;
                case "Rule-Mapped Auto":
                    mode = TradingMode.RuleMappedAuto;
                    return true;
                case "User-Triggered":
                    mode = TradingMode.UserTriggered;
                    return true;
                default:
                    mode = default(TradingMode);
                    return false;
            }
        }

        private static bool IsKnownTradingMode(TradingMode mode)
        {
            return mode == TradingMode.ProbabilisticAuto ||
                   mode == TradingMode.RuleMappedAuto ||
                   mode == TradingMode.UserTriggered;
        }

        private static bool RunTradingModeSelfTests()
        {
            return (int)StartupTradingMode.Auto == 0 &&
                (int)StartupTradingMode.Manual == 1 &&
                (int)StartupTradingMode.UserTriggered == 2 &&
                MapStartupTradingMode(StartupTradingMode.Auto) ==
                    TradingMode.ProbabilisticAuto &&
                MapStartupTradingMode(StartupTradingMode.Manual) ==
                    TradingMode.RuleMappedAuto &&
                MapStartupTradingMode(StartupTradingMode.UserTriggered) ==
                    TradingMode.UserTriggered &&
                IsKnownTradingMode(TradingMode.ProbabilisticAuto) &&
                IsKnownTradingMode(TradingMode.RuleMappedAuto) &&
                IsKnownTradingMode(TradingMode.UserTriggered) &&
                !IsKnownTradingMode((TradingMode)int.MaxValue) &&
                TryParseTradingModeDisplayName(
                    GetTradingModeDisplayName(TradingMode.ProbabilisticAuto),
                    out TradingMode probabilisticMode) &&
                probabilisticMode == TradingMode.ProbabilisticAuto &&
                TryParseTradingModeDisplayName(
                    GetTradingModeDisplayName(TradingMode.RuleMappedAuto),
                    out TradingMode ruleMappedMode) &&
                ruleMappedMode == TradingMode.RuleMappedAuto &&
                TryParseTradingModeDisplayName(
                    GetTradingModeDisplayName(TradingMode.UserTriggered),
                    out TradingMode userTriggeredMode) &&
                userTriggeredMode == TradingMode.UserTriggered;
        }

        private enum BossSignal
        {
            Hold,
            Long,
            Short
        }

        private enum RolloverPhase
        {
            Normal,
            Flattening,
            WaitingForMarketOpen,
            Restoring,
            RecoveryCleanup,
            RecoveryCooldown,
            FatalStop
        }
        private enum AtrUpdateOutcome
        {
            Updated,
            Unchanged,
            RetainedPreviousValues,
            Fatal
        }

        private enum RestorationOutcome
        {
            Restored,
            PermanentlySkipped,
            PermanentFailure,
            TransientMarketCondition,
            OperationalFailure
        }
        private enum OpeningGapDirection
        {
            Neutral,
            Favourable,
            Unfavourable
        }

        private enum RolloverProtectionState
        {
            Conventional,
            StopLossCrossedEntry,
            TakeProfitCrossedEntry,
            BothCrossedEntry,
            MissingProtection,
            InvalidProtection
        }

        private enum SnapshotRecoveryStatus
        {
            Captured,
            WaitingForEligibility,
            RestoreInProgress,
            Restored,
            PermanentlySkipped,
            Quarantined,
            Failed
        }

        private enum RecoveryExposureClassification
        {
            ExpectedAndProtected,
            ExpectedButUnprotected,
            ExpectedButInvalid,
            UnexpectedBotExposure
        }

        private enum LifecycleContinuityEvidence
        {
            Unknown,
            Continuous,
            RestartGap
        }

        private enum SegmentProvenanceEvidence
        {
            Unknown,
            BrokerObserved,
            RolloverReconstructed,
            Mixed
        }

        private enum PositionAttributionEvidence
        {
            NotApplicable,
            Exclusive,
            AmbiguousNetted,
            Unknown
        }

        private sealed class TradeEvidence
        {
            public LifecycleContinuityEvidence LifecycleContinuity;
            public SegmentProvenanceEvidence SegmentProvenance;
            public PositionAttributionEvidence PositionAttribution;

            public TradeEvidence Copy()
            {
                return new TradeEvidence
                {
                    LifecycleContinuity = LifecycleContinuity,
                    SegmentProvenance = SegmentProvenance,
                    PositionAttribution = PositionAttribution
                };
            }
        }

        private static TradeEvidence CreateUnknownTradeEvidence()
        {
            return new TradeEvidence
            {
                LifecycleContinuity = LifecycleContinuityEvidence.Unknown,
                SegmentProvenance = SegmentProvenanceEvidence.Unknown,
                PositionAttribution = PositionAttributionEvidence.Unknown
            };
        }

        private static TradeEvidence CreatePendingOrderEvidence(
            LifecycleContinuityEvidence lifecycleContinuity,
            SegmentProvenanceEvidence segmentProvenance)
        {
            return new TradeEvidence
            {
                LifecycleContinuity = lifecycleContinuity,
                SegmentProvenance = segmentProvenance,
                PositionAttribution = PositionAttributionEvidence.NotApplicable
            };
        }

        private static TradeEvidence CombineTradeEvidence(
            TradeEvidence current,
            TradeEvidence incoming)
        {
            if (current == null)
                return incoming == null ? null : incoming.Copy();
            if (incoming == null)
                return current.Copy();

            return new TradeEvidence
            {
                LifecycleContinuity = CombineLifecycleContinuity(
                    current.LifecycleContinuity,
                    incoming.LifecycleContinuity),
                SegmentProvenance = CombineSegmentProvenance(
                    current.SegmentProvenance,
                    incoming.SegmentProvenance),
                PositionAttribution = CombinePositionAttribution(
                    current.PositionAttribution,
                    incoming.PositionAttribution)
            };
        }

        private static LifecycleContinuityEvidence CombineLifecycleContinuity(
            LifecycleContinuityEvidence current,
            LifecycleContinuityEvidence incoming)
        {
            if (current == LifecycleContinuityEvidence.Unknown ||
                incoming == LifecycleContinuityEvidence.Unknown)
                return LifecycleContinuityEvidence.Unknown;
            if (current == LifecycleContinuityEvidence.RestartGap ||
                incoming == LifecycleContinuityEvidence.RestartGap)
                return LifecycleContinuityEvidence.RestartGap;
            return LifecycleContinuityEvidence.Continuous;
        }

        private static SegmentProvenanceEvidence CombineSegmentProvenance(
            SegmentProvenanceEvidence current,
            SegmentProvenanceEvidence incoming)
        {
            if (current == SegmentProvenanceEvidence.Unknown ||
                incoming == SegmentProvenanceEvidence.Unknown)
                return SegmentProvenanceEvidence.Unknown;
            if (current == SegmentProvenanceEvidence.Mixed ||
                incoming == SegmentProvenanceEvidence.Mixed)
                return SegmentProvenanceEvidence.Mixed;
            if (current == incoming)
                return current;
            return SegmentProvenanceEvidence.Mixed;
        }

        private static PositionAttributionEvidence CombinePositionAttribution(
            PositionAttributionEvidence current,
            PositionAttributionEvidence incoming)
        {
            if (current == PositionAttributionEvidence.Unknown ||
                incoming == PositionAttributionEvidence.Unknown)
                return PositionAttributionEvidence.Unknown;
            if (current == PositionAttributionEvidence.NotApplicable)
                return incoming;
            if (incoming == PositionAttributionEvidence.NotApplicable)
                return current;
            if (current == PositionAttributionEvidence.AmbiguousNetted ||
                incoming == PositionAttributionEvidence.AmbiguousNetted)
                return PositionAttributionEvidence.AmbiguousNetted;
            return PositionAttributionEvidence.Exclusive;
        }

        private static TradeEvidence AddRolloverReconstructionEvidence(
            TradeEvidence evidence)
        {
            if (evidence == null)
                return CreateUnknownTradeEvidence();
            return CombineTradeEvidence(
                evidence,
                new TradeEvidence
                {
                    LifecycleContinuity = LifecycleContinuityEvidence.Continuous,
                    SegmentProvenance = SegmentProvenanceEvidence.RolloverReconstructed,
                    PositionAttribution = PositionAttributionEvidence.NotApplicable
                });
        }

        private static TradeEvidence AddPositionAttributionEvidence(
            TradeEvidence evidence,
            PositionAttributionEvidence attribution)
        {
            if (evidence == null)
                evidence = CreateUnknownTradeEvidence();
            return CombineTradeEvidence(
                evidence,
                new TradeEvidence
                {
                    LifecycleContinuity = LifecycleContinuityEvidence.Continuous,
                    SegmentProvenance = SegmentProvenanceEvidence.BrokerObserved,
                    PositionAttribution = attribution
                });
        }

        private static bool CanUseForLogicalMetrics(TradeEvidence evidence)
        {
            return evidence != null &&
                evidence.LifecycleContinuity == LifecycleContinuityEvidence.Continuous &&
                evidence.SegmentProvenance != SegmentProvenanceEvidence.Unknown &&
                evidence.PositionAttribution == PositionAttributionEvidence.Exclusive;
        }

        private static bool CanUseForAdaptiveOutcomeControls(TradeEvidence evidence)
        {
            return CanUseForLogicalMetrics(evidence);
        }

        private static string GetLifecycleCompletenessLabel(TradeEvidence evidence)
        {
            if (evidence == null ||
                evidence.LifecycleContinuity == LifecycleContinuityEvidence.Unknown ||
                evidence.SegmentProvenance == SegmentProvenanceEvidence.Unknown ||
                evidence.PositionAttribution == PositionAttributionEvidence.Unknown)
                return "unknown";
            if (evidence.PositionAttribution == PositionAttributionEvidence.AmbiguousNetted)
                return "partialNettedAssociation";
            if (evidence.LifecycleContinuity == LifecycleContinuityEvidence.RestartGap)
                return "partialSinceRestart";
            return evidence.SegmentProvenance == SegmentProvenanceEvidence.RolloverReconstructed ||
                   evidence.SegmentProvenance == SegmentProvenanceEvidence.Mixed
                ? "rolloverReconstructed"
                : "observedThisRun";
        }

        private static bool RunTradeEvidenceSelfTests()
        {
            TradeEvidence continuous = new TradeEvidence
            {
                LifecycleContinuity = LifecycleContinuityEvidence.Continuous,
                SegmentProvenance = SegmentProvenanceEvidence.BrokerObserved,
                PositionAttribution = PositionAttributionEvidence.Exclusive
            };
            TradeEvidence restarted = new TradeEvidence
            {
                LifecycleContinuity = LifecycleContinuityEvidence.RestartGap,
                SegmentProvenance = SegmentProvenanceEvidence.BrokerObserved,
                PositionAttribution = PositionAttributionEvidence.Exclusive
            };
            TradeEvidence netted = new TradeEvidence
            {
                LifecycleContinuity = LifecycleContinuityEvidence.Continuous,
                SegmentProvenance = SegmentProvenanceEvidence.BrokerObserved,
                PositionAttribution = PositionAttributionEvidence.AmbiguousNetted
            };
            TradeEvidence rollover = AddRolloverReconstructionEvidence(continuous);
            TradeEvidence restartRollover = AddRolloverReconstructionEvidence(restarted);

            return CanUseForLogicalMetrics(continuous) &&
                CanUseForAdaptiveOutcomeControls(rollover) &&
                rollover.SegmentProvenance == SegmentProvenanceEvidence.Mixed &&
                restartRollover.LifecycleContinuity == LifecycleContinuityEvidence.RestartGap &&
                !CanUseForLogicalMetrics(restartRollover) &&
                !CanUseForLogicalMetrics(netted) &&
                GetLifecycleCompletenessLabel(restartRollover) == "partialSinceRestart" &&
                GetLifecycleCompletenessLabel(netted) == "partialNettedAssociation" &&
                CombineTradeEvidence(continuous, CreateUnknownTradeEvidence())
                    .LifecycleContinuity == LifecycleContinuityEvidence.Unknown &&
                CombineTradeEvidence(netted, continuous).PositionAttribution ==
                    PositionAttributionEvidence.AmbiguousNetted &&
                AddRolloverReconstructionEvidence(null).LifecycleContinuity ==
                    LifecycleContinuityEvidence.Unknown &&
                IsPartialCloseHistoryReconciled(0.0, 0.01) &&
                !IsPartialCloseHistoryReconciled(0.02, 0.01) &&
                !IsPartialCloseHistoryReconciled(double.NaN, 0.01);
        }
        private readonly struct TickSnapshot
        {
            public TickSnapshot(
                double bid,
                double ask,
                DateTime utcNow,
                double volatilityFactor,
                double referenceHigh,
                double referenceLow)
            {
                Bid = bid;
                Ask = ask;
                UtcNow = utcNow;
                VolatilityFactor = volatilityFactor;
                ReferenceHigh = referenceHigh;
                ReferenceLow = referenceLow;
            }

            public double Bid { get; }
            public double Ask { get; }
            public DateTime UtcNow { get; }
            public double VolatilityFactor { get; }
            public double ReferenceHigh { get; }
            public double ReferenceLow { get; }

            public int UtcHourKey
            {
                get { return GetUtcHourKey(UtcNow); }
            }
        }
        private sealed class RolloverContext
        {
            public RolloverPhase Phase = RolloverPhase.Normal;
            public bool SnapshotContainedTrades;
            public int SavedTicksUntilMaintenance;
        }
        private sealed class DailyEquityTrackingState
        {
            public bool IsInitialized;
            public DateTime PeriodStartUtc;
            public DateTime PeriodEndUtc;
            public DateTime FirstSampleUtc;
            public DateTime LastSampleUtc;
            public DateTime PeakUtc;
            public DateTime TroughUtc;
            public DateTime DrawdownPeakUtc;
            public DateTime DrawdownTroughUtc;
            public double OpeningEquity;
            public double LatestEquity;
            public double PeakEquity;
            public double TroughEquity;
            public double DrawdownPeakEquity;
            public double DrawdownTroughEquity;
            public double MaximumDrawdown;
            public long SampleCount;
            public string OpeningBasis;
        }
        private sealed class PendingOrderCancellationRetryState
        {
            public DateTime StartedUtc;
            public DateTime NextEligibleAttemptUtc;
            public int AttemptCount;
            public int SuccessfulAttemptCount;
            public int FailedAttemptCount;
            public string InitialReason;
            public string LatestReason;
            public readonly HashSet<string> ReasonsSeen =
                new HashSet<string>(StringComparer.Ordinal);
            public string LastFailureSignature;
        }

        private sealed class PendingOrderTrailingState
        {
            public StrategyKind Strategy;
            public PersistentTradeCapsule Capsule;
            public bool CapsuleRecoveryFailed;
            public EntryOriginMode EntryOrigin;
            public TradeEvidence Evidence;
            public int LastUtcHourKey;
            public double LastVolatilityFactor;
            // Immutable, broker-ready creation/adoption geometry. Keep this separate
            // from LastVolatilityFactor, which may change while the order trails.
            public double OriginalVolatilityFactor;
            public double OriginalStopLossPips;
            public double OriginalTakeProfitPips;
            public TradeType TradeType;
            public double VolumeInUnits;
            public string Label;
            // Fixed for the life of the pending order. These values define the
            // reference-proximity trailing gate and must not move when the order trails.
            public double OriginalTargetPrice;
            public double MovementReferencePrice;
        }
        private sealed class SafeDistanceLevelSnapshot
        {
            public readonly long Id;
            public readonly double EntryOrTargetPrice;
            public readonly double? StopLossPrice;
            public readonly double? TakeProfitPrice;

            public SafeDistanceLevelSnapshot(long id, double entryOrTargetPrice,
                double? stopLossPrice, double? takeProfitPrice)
            {
                Id = id;
                EntryOrTargetPrice = entryOrTargetPrice;
                StopLossPrice = stopLossPrice;
                TakeProfitPrice = takeProfitPrice;
            }
        }

        private sealed class ProvisionalSafeDistancePendingOrder
        {
            public readonly SafeDistanceLevelSnapshot Levels;
            public readonly DateTime RegisteredUtc;

            public ProvisionalSafeDistancePendingOrder(
                SafeDistanceLevelSnapshot levels,
                DateTime registeredUtc)
            {
                Levels = levels;
                RegisteredUtc = registeredUtc;
            }
        }

        private sealed class PositionProtectionRecoveryState
        {
            public long PositionId;
            public DateTime StartedUtc;
            public DateTime NextAttemptUtc;
            public int Attempts;
            public string LastFailure;
            public bool ClosureOnly;
            public DateTime ClosureStartedUtc;
            public DateTime NextCloseAttemptUtc;
            public int CloseAttempts;
            public string LastCloseFailure;
            public bool TerminalCloseFailureLogged;
            public bool CloseRequestAccepted;
            public DateTime CloseRequestAcceptedUtc;
        }

        private sealed class PositionState
        {
            public StrategyKind Strategy;
            public PersistentTradeCapsule Capsule;
            public bool CapsuleRecoveryFailed;
            public EntryOriginMode EntryOrigin;
            public TradeEvidence Evidence;
            // Actual entry of the current live position, used by live protection logic.
            public double EntryPrice;
            // Historical entry anchor retained across rollover restorations. Together with
            // Original SL/TP, this defines the stable reopening region on later rollovers.
            public double RolloverAnchorEntryPrice;
            public double OriginalTakeProfit;
            public double OriginalStopLoss;
            public bool OriginalProtectionKnown;
            public bool CircuitBreakerEligible;
            public bool CircuitBreakerOriginalStopUnchanged;
            public bool ScaleUpEligible;
            public double TakeProfitDistance;
            public double StopLossDistance;
            public double? LastRequestedTakeProfit;
            public double? LastRequestedStopLoss;
            public DateTime LastProtectionModificationTime = DateTime.MinValue;
            // Stable logical-trade telemetry carried across physical rollover segments.
            public Guid LogicalTradeId = Guid.NewGuid();
            public DateTime LogicalEntryUtc;
            public int RolloverSegment;
            public int RestorationCount;
            public double CarriedGrossProfit;
            public double CarriedNetProfit;
            public double CarriedCommission;
            public double CarriedSwap;
            public double CarriedPips;
            public double CarriedActiveDurationSeconds;
            public int CompletedTakeProfitLevels;
            public int CompletedStopLossLevels;
            public readonly HashSet<int> AccountedClosingDealIds = new HashSet<int>();
            public double PendingLadderCloseVolume;
            public bool LadderRecoveryAmbiguous;
        }

        private sealed class PositionSnapshot
        {
            public Guid RecoveryId = Guid.NewGuid();
            public SnapshotRecoveryStatus RecoveryStatus = SnapshotRecoveryStatus.Captured;
            public bool FailureCounted;
            public long? RestoredObjectId;
            public DateTime? ResolvedUtc;
            public StrategyKind Strategy;
            public PersistentTradeCapsule Capsule;
            public bool CapsuleRecoveryFailed;
            public EntryOriginMode EntryOrigin;
            public TradeType TradeType;
            public long SourcePositionId;
            public Guid? TradeIdeaId;
            public TradeEvidence Evidence;
            public Guid LogicalTradeId;
            public DateTime LogicalEntryUtc;
            public int RolloverSegment;
            public int RestorationCount;
            public double CarriedGrossProfit;
            public double CarriedNetProfit;
            public double CarriedCommission;
            public double CarriedSwap;
            public double CarriedPips;
            public double CarriedActiveDurationSeconds;
            public int CompletedTakeProfitLevels;
            public int CompletedStopLossLevels;
            public double PendingLadderCloseVolume;
            public bool LadderRecoveryAmbiguous;
            public readonly HashSet<int> AccountedClosingDealIds = new HashSet<int>();
            public double VolumeInUnits;
            public string Label;
            public double EntryPrice;
            public double PhysicalEntryPrice;
            public double? LastClosingPrice;
            public RolloverProtectionState ProtectionState;
            public string ResolutionReason;
            public bool LogicalTradeFinalized;
            // Fixed protection prices captured when the position was first opened.
            // These define the rollover reopening region and are not changed by trailing.
            public double? OriginalStopLoss;
            public double? OriginalTakeProfit;
            public bool OriginalProtectionKnown;
            public bool ScaleUpEligible;
            // Live absolute levels are retained for snapshot diagnostics.
            public double? StopLoss;
            public double? TakeProfit;
            // Entry-relative distances are applied when restoring protection.
            public double? StopLossPips;
            public double? TakeProfitPips;
            public int RestorationAttempts;
            public DateTime NextRestorationAttemptUtc = DateTime.MinValue;
            public string LastRestorationFailure;
        }

        private sealed class PendingOrderSnapshot
        {
            public Guid RecoveryId = Guid.NewGuid();
            public SnapshotRecoveryStatus RecoveryStatus = SnapshotRecoveryStatus.Captured;
            public bool FailureCounted;
            public long? RestoredObjectId;
            public DateTime? ResolvedUtc;
            public StrategyKind Strategy;
            public PersistentTradeCapsule Capsule;
            public bool CapsuleRecoveryFailed;
            public EntryOriginMode EntryOrigin;
            public TradeType TradeType;
            public PendingOrderType OrderType;
            public int SourceOrderId;
            public Guid? SubmissionCorrelationId;
            public TradeEvidence Evidence;
            public double VolumeInUnits;
            public double TargetPrice;
            public double OriginalTargetPrice;
            public double MovementReferencePrice;
            public string Label;
            public string ResolutionReason;
            public double? StopLoss;
            public double? TakeProfit;
            // Rollover-time relative protection distances used for restoration.
            public double? StopLossPips;
            public double? TakeProfitPips;
            public DateTime? ExpirationTime;
            public int RestorationAttempts;
            public DateTime FirstRestorationAttemptUtc = DateTime.MinValue;
            public DateTime NextRestorationAttemptUtc = DateTime.MinValue;
            public string LastRestorationFailure;
        }

        #endregion


    }
}
