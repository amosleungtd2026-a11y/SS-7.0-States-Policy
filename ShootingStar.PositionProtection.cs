using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Open-position state and protection initialization.
    public partial class ShootingStar
    {
        #region Open Position Protection State

        private readonly Dictionary<long, PositionState> _positionStates = new Dictionary<long, PositionState>();
        private readonly Dictionary<long, PositionProtectionRecoveryState> _positionProtectionRecoveryStates =
            new Dictionary<long, PositionProtectionRecoveryState>();
        private readonly List<long> _stalePositionProtectionRecoveryIds = new List<long>();
        private readonly List<long> _positionProtectionRecoveryIdBuffer = new List<long>();
        private readonly List<Position> _protectionRecoveryPositionBuffer = new List<Position>();
        private const int PositionProtectionRecoveryMaxAttempts = 3;
        private const int PositionProtectionRecoveryRetrySeconds = 2;
        private const int PositionProtectionRecoveryDeadlineSeconds = 10;
        private const int UnprotectedPositionCloseRetrySeconds = 2;
        private const int UnprotectedPositionCloseTerminalRetrySeconds = 10;
        private const int UnprotectedPositionCloseMaxAttempts = 10;
        private const int UnprotectedPositionCloseDeadlineSeconds = 30;
        private const int AcceptedCloseConfirmationSeconds = 7;

        #endregion

        #region Open Position Protection and Trailing

        private void ManageOpenPositions(TickSnapshot tick)
        {
            // Stable reusable snapshot required: initialization may close an unprotected position.
            CaptureBotPositions(_botPositionSnapshot);
            foreach (var position in _botPositionSnapshot)
            {
                if (!_positionStates.TryGetValue(position.Id, out PositionState state))
                {
                    InitializePositionState(position);
                    if (!_positionStates.TryGetValue(position.Id, out state))
                        continue;
                }

                ReconcilePositionLadderHistory(position, state);
                TrailPositionProtection(position, state, tick);
                if (_positionStates.ContainsKey(position.Id))
                    ManagePositionLadder(position, state, tick);
            }
        }

        private void OnPositionModified(PositionModifiedEventArgs args)
        {
            try
            {
                OnPositionModifiedCore(args);
            }
            catch (Exception exception)
            {
                HandleUnexpectedCallbackException("OnPositionModified", exception, CallbackFailurePolicy.Continue);
            }
        }

        private void OnPositionModifiedCore(PositionModifiedEventArgs args)
        {
            Position position = args.Position;
            if (position.SymbolName == Symbol.Name)
                MarkSafeDistanceSnapshotDirty();

            if (!IsBotPosition(position))
                return;

            RunTelemetry("ObservePositionModified", () => ObservePositionModification(position, false));
            // Do not copy the live TP/SL into LastRequested here. This event is raised
            // for both bot and manual modifications, so doing that would erase the
            // reference values needed to detect a manual change on the next trail check.
            if (!_positionStates.ContainsKey(position.Id))
                InitializePositionState(position);
            if (_positionStates.TryGetValue(position.Id, out PositionState state))
            {
                MarkCircuitBreakerStopLossModified(position, state);
            }

            if (position.StopLoss.HasValue && position.TakeProfit.HasValue &&
                (!_positionProtectionRecoveryStates.TryGetValue(
                    position.Id,
                    out PositionProtectionRecoveryState recovery) ||
                 !recovery.ClosureOnly))
            {
                CompletePositionProtectionRecovery(position.Id);
            }
        }

        private void InitializePositionState(
            Position position,
            PendingOrderTrailingState originatingOrderState = null,
            bool observedAtOpen = false)
        {
            bool originatingCapsuleAvailable =
                originatingOrderState?.Capsule != null;
            PersistentTradeCapsule capsule =
                Account.AccountType == AccountType.Hedged
                    ? originatingOrderState?.Capsule
                    : null;
            bool capsuleRecovered = capsule != null;
            if (!capsuleRecovered)
            {
                capsuleRecovered = TryGetCapsuleFromPosition(
                    position,
                    out Guid _,
                    out capsule);
            }
            bool capsuleRecoveryFailed = !capsuleRecovered &&
                (originatingCapsuleAvailable ||
                 HasPersistentCapsuleMarker(position.Comment));
            if (capsule != null &&
                (!TryRecoverCapsuleFromComment(
                    position.Comment,
                    out Guid _,
                    out PersistentTradeCapsule brokerCapsule,
                    out string _) ||
                 !ValidatePersistentCapsule(
                    brokerCapsule,
                    position.SymbolName,
                    position.TradeType,
                    strategy: capsule.Strategy) ||
                 !AreEquivalentPersistentCapsules(capsule, brokerCapsule)))
            {
                capsuleRecoveryFailed = true;
                LogActiveWarning(
                    "position-capsule-" + position.Id,
                    "TRADE_LIFECYCLE",
                    "POSITION_CAPSULE_UNCONFIRMED",
                    "positionId={0} outcome=restartRecoveryNotGuaranteed",
                    position.Id);
            }
            StrategyKind strategy = capsule == null
                ? GetStrategy(position.Label)
                : capsule.Strategy;
            if (capsuleRecoveryFailed &&
                (!position.StopLoss.HasValue || !position.TakeProfit.HasValue))
            {
                LogError("PROTECTION", "CAPSULE_REQUIRED_FOR_PROTECTION",
                    "positionId={0} outcome=closeAttempt reason=originalProtectionUnavailable",
                    position.Id);
                ClosePositionWithoutProtection(
                    position,
                    "persistent capsule could not be recovered");
                return;
            }
            double expectedTakeProfitPercent = strategy == StrategyKind.MeanReversion
                ? (position.TradeType == TradeType.Buy ? _actualMrBuyTakeProfitPercent : _actualMrSellTakeProfitPercent)
                : (position.TradeType == TradeType.Buy ? _actualTfLongTakeProfitPercent : _actualTfShortTakeProfitPercent);
            double expectedStopLossPercent = strategy == StrategyKind.MeanReversion
                ? (position.TradeType == TradeType.Buy ? _actualMrBuyStopLossPercent : _actualMrSellStopLossPercent)
                : (position.TradeType == TradeType.Buy ? _actualTfLongStopLossPercent : _actualTfShortStopLossPercent);
            double expectedTakeProfitDistance = position.EntryPrice * expectedTakeProfitPercent / 100.0;
            double expectedStopLossDistance = position.EntryPrice * expectedStopLossPercent / 100.0;
            string protectionDistanceSource = "current-strategy-fallback";

            if (capsule != null)
            {
                expectedTakeProfitDistance =
                    capsule.OriginalTakeProfitPips * _symbolPipSize;
                expectedStopLossDistance =
                    capsule.OriginalStopLossPips * _symbolPipSize;
                protectionDistanceSource = "persistent-broker-capsule";
            }
            else if (originatingOrderState != null &&
                IsFinitePositive(originatingOrderState.OriginalTakeProfitPips) &&
                IsFinitePositive(originatingOrderState.OriginalStopLossPips))
            {
                expectedTakeProfitDistance = originatingOrderState.OriginalTakeProfitPips * _symbolPipSize;
                expectedStopLossDistance = originatingOrderState.OriginalStopLossPips * _symbolPipSize;
                protectionDistanceSource = "originating-pending-order";
            }

            if (!IsFinitePositive(expectedTakeProfitDistance) ||
                !IsFinitePositive(expectedStopLossDistance))
            {
                LogNormal("PROTECTION", "INVALID_EXPECTED_DISTANCES_CLOSE", "Position {0}: expected TP/SL distances are invalid; closing position.", position.Id);
                ClosePositionWithoutProtection(position, "invalid expected protection distances");
                return;
            }

            TradeEvidence positionEvidence = GetPositionEvidence(position.Id);
            if (positionEvidence == null && originatingOrderState?.Evidence != null)
            {
                positionEvidence = AddPositionAttributionEvidence(
                    originatingOrderState.Evidence,
                    GetAccountPositionAttribution());
            }
            if (positionEvidence == null &&
                !(observedAtOpen && _ordinaryOrderSubmissionInProgress))
                positionEvidence = CreateUnknownTradeEvidence();

            PositionState state = new PositionState
            {
                Strategy = strategy,
                Capsule = capsule == null ? null : capsule.Copy(),
                CapsuleRecoveryFailed = capsuleRecoveryFailed,
                EntryOrigin = originatingOrderState != null
                    ? originatingOrderState.EntryOrigin
                    : (_ordinaryOrderSubmissionInProgress
                        ? _activeOrderSubmissionOrigin
                        : EntryOriginMode.Unattributed),
                Evidence = positionEvidence == null ? null : positionEvidence.Copy(),
                LogicalEntryUtc = position.EntryTime,
                EntryPrice = position.EntryPrice,
                RolloverAnchorEntryPrice = position.EntryPrice,
                OriginalTakeProfit = capsule != null
                    ? GetExpectedProtectionPrice(
                        position, expectedTakeProfitDistance, false)
                    : position.TakeProfit ?? GetExpectedProtectionPrice(
                        position, expectedTakeProfitDistance, false),
                OriginalStopLoss = capsule != null
                    ? GetExpectedProtectionPrice(
                        position, expectedStopLossDistance, true)
                    : position.StopLoss ?? GetExpectedProtectionPrice(
                        position, expectedStopLossDistance, true),
                OriginalProtectionKnown = capsule != null ||
                    (observedAtOpen &&
                     position.StopLoss.HasValue && position.TakeProfit.HasValue &&
                     (position.TradeType == TradeType.Buy
                         ? position.StopLoss.Value < position.EntryPrice &&
                           position.TakeProfit.Value > position.EntryPrice
                         : position.StopLoss.Value > position.EntryPrice &&
                           position.TakeProfit.Value < position.EntryPrice)),
                TakeProfitDistance = position.TakeProfit.HasValue
                    ? Math.Abs(position.EntryPrice - position.TakeProfit.Value)
                    : expectedTakeProfitDistance,
                StopLossDistance = position.StopLoss.HasValue
                    ? Math.Abs(position.EntryPrice - position.StopLoss.Value)
                    : expectedStopLossDistance,
                LastRequestedTakeProfit = position.TakeProfit,
                LastRequestedStopLoss = position.StopLoss
            };
            RestoreCircuitBreakerPositionState(position.Id, state);
            RestoreScaleUpPositionState(position.Id, state);
            if (!CanUseForAdaptiveOutcomeControls(state.Evidence))
            {
                state.CircuitBreakerEligible = false;
                state.CircuitBreakerOriginalStopUnchanged = false;
                state.ScaleUpEligible = false;
            }
            _positionStates[position.Id] = state;
            SetPositionEntryOrigin(position.Id, state.EntryOrigin);
            if (state.Evidence != null)
                AddPositionEvidence(position.Id, state.Evidence);
            state.LadderRecoveryAmbiguous = capsuleRecoveryFailed ||
                (capsule != null &&
                 originatingOrderState == null &&
                 !observedAtOpen);
            RestorePositionLadderState(position.Id, state);
            ReconcilePositionLadderHistory(position, state);

            if (!position.TakeProfit.HasValue || !position.StopLoss.HasValue)
            {
                LogNormal("PROTECTION", "RECONSTRUCTION_SOURCE",
                    "positionId={0} source={1} originalVolatilityFactor={2:F6} stopLossPips={3:F2} takeProfitPips={4:F2}",
                    position.Id,
                    protectionDistanceSource,
                    originatingOrderState == null ? 1.0 : originatingOrderState.OriginalVolatilityFactor,
                    expectedStopLossDistance / _symbolPipSize,
                    expectedTakeProfitDistance / _symbolPipSize);
                StartOrContinuePositionProtectionRecovery(position, _positionStates[position.Id], true);
            }
        }

        private PendingOrderTrailingState TakeOriginatingPendingOrderState(Position position)
        {
            PendingOrderTrailingState match = null;
            int matchId = 0;
            int matchCount = 0;
            double volumeTolerance = Math.Max(1.0, Symbol.VolumeInUnitsStep * 0.5);

            foreach (KeyValuePair<int, PendingOrderTrailingState> pair in _pendingOrderTrailingStates)
            {
                PendingOrderTrailingState candidate = pair.Value;
                if (candidate == null || candidate.TradeType != position.TradeType ||
                    !string.Equals(candidate.Label, position.Label, StringComparison.Ordinal) ||
                    Math.Abs(candidate.VolumeInUnits - position.VolumeInUnits) > volumeTolerance)
                {
                    continue;
                }

                match = candidate;
                matchId = pair.Key;
                matchCount++;
                if (matchCount > 1)
                    break;
            }

            if (matchCount != 1)
            {
                if (matchCount > 1 && (!position.StopLoss.HasValue || !position.TakeProfit.HasValue))
                {
                    LogActiveWarning("ambiguous-originating-order-" + position.Id,
                        "PROTECTION", "ORIGIN_AMBIGUOUS",
                        "positionId={0} has multiple matching pending-order states; current-strategy fallback will be used",
                        position.Id);
                }
                return null;
            }

            if (match.Evidence == null)
                match.Evidence = GetPendingOrderEvidence(matchId);
            _pendingOrderTrailingStates.Remove(matchId);
            _pendingOrderEvidenceById.Remove(matchId);
            return match;
        }

        #endregion

    }
}
