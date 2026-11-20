using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Position protection trailing and ladder management.
    public partial class ShootingStar
    {
        #region Position Trailing and Ladder Management
        private static bool IsPositionTrailingEnabled()
        {
            return PositionTrailing == YesNo.Y;
        }

        private void TrailPositionProtection(Position position, PositionState state, TickSnapshot tick)
        {
            if (_deferredRolloverQuarterStops.ContainsKey(position.Id))
                return;
            bool isBuy = position.TradeType == TradeType.Buy;
            double currentPrice = isBuy ? tick.Bid : tick.Ask;

            // Missing live protection is never accepted as a steady state. Restore both
            // sides from the stored policy, or close the position if restoration fails.
            if (!position.StopLoss.HasValue || !position.TakeProfit.HasValue)
            {
                LogError("PROTECTION", "MISSING_LIVE_PROTECTION",
                    "position {0} is missing {1}; attempting immediate protection restoration",
                    position.Id,
                    !position.StopLoss.HasValue && !position.TakeProfit.HasValue
                        ? "SL and TP"
                        : (!position.StopLoss.HasValue ? "SL" : "TP"));
                StartOrContinuePositionProtectionRecovery(position, state, true);
                return;
            }

            if (!IsPositionTrailingEnabled())
                return;

            double adjustedMinimumTrailStepPips =
                GetEffectiveMinimumPositionTrailStepPips(tick);
            double minimumChange = Math.Max(
                adjustedMinimumTrailStepPips * Symbol.PipSize,
                Symbol.TickSize);

            if (PositionModifyCooldownMilliseconds > 0 &&
                state.LastProtectionModificationTime != DateTime.MinValue &&
                (tick.UtcNow - state.LastProtectionModificationTime).TotalMilliseconds <
                    PositionModifyCooldownMilliseconds)
            {
                return;
            }

            // Compare the live protection with the last prices requested by this bot.
            // If they still match, retain the stored distance. If they differ, adopt
            // only the externally/manual-modified side's current market-relative distance.
            AdoptManualProtectionChanges(position, state, currentPrice);

            // The stored TP distance generates the candidate level. The live TP ratchets only
            // in the risk-reducing direction and is never moved back away from the market.
            double candidateTp = NormalizePrice(isBuy
                ? currentPrice + state.TakeProfitDistance
                : currentPrice - state.TakeProfitDistance);

            // The stored SL distance generates the candidate level. The live SL ratchets only
            // in the risk-reducing direction and is never moved back away from the market.
            double candidateSl = NormalizePrice(isBuy
                ? currentPrice - state.StopLossDistance
                : currentPrice + state.StopLossDistance);

            // Compare candidates with the current broker-effective prices.
            double? effectiveTp = position.TakeProfit;
            double? effectiveSl = position.StopLoss;
            double? newTp = position.TakeProfit;
            double? newSl = position.StopLoss;
            bool modifyTp = false;
            bool modifySl = false;

            if (effectiveTp.HasValue)
            {
                modifyTp = isBuy
                    ? candidateTp <= effectiveTp.Value - minimumChange && candidateTp > currentPrice
                    : candidateTp >= effectiveTp.Value + minimumChange && candidateTp < currentPrice;

                if (modifyTp)
                    newTp = candidateTp;
            }

            if (effectiveSl.HasValue)
            {
                modifySl = isBuy
                    ? candidateSl >= effectiveSl.Value + minimumChange && candidateSl < currentPrice
                    : candidateSl <= effectiveSl.Value - minimumChange && candidateSl > currentPrice;

                if (modifySl)
                    newSl = candidateSl;
            }

            if (modifySl && !IsPositionAbsoluteProtectionDistanceValid(
                    currentPrice,
                    newSl.Value,
                    true,
                    position.Id,
                    "trailing"))
            {
                modifySl = false;
                newSl = position.StopLoss;
            }

            if (modifyTp && !IsPositionAbsoluteProtectionDistanceValid(
                    currentPrice,
                    newTp.Value,
                    false,
                    position.Id,
                    "trailing"))
            {
                modifyTp = false;
                newTp = position.TakeProfit;
            }

            if (!modifyTp && !modifySl)
                return;

            ModificationTelemetry modificationTelemetry = RunTelemetry(
                "BeginPositionModification", () => BeginPositionModification(position), null);
            var result = ModifyPosition(position, newSl, newTp, ProtectionType.Absolute);
            RunTelemetry("CompleteModificationRequest",
                () => CompleteModificationRequest(modificationTelemetry, result));
            if (result == null || !result.IsSuccessful)
            {
                LogError("PROTECTION", "TRAIL_MODIFICATION_FAILED", "Position {0}: TP/SL trailing failed. Error: {1}",
                    position.Id,
                    result == null ? "null result" : result.Error.ToString());
                return;
            }

            state.LastProtectionModificationTime = tick.UtcNow;

            // Record broker-confirmed effective prices, not only requested prices.
            // This prevents a one-tick server normalisation from being misclassified.
            Position confirmedPosition = result.Position ?? position;
            state.LastRequestedStopLoss = confirmedPosition.StopLoss;
            state.LastRequestedTakeProfit = confirmedPosition.TakeProfit;
            MarkCircuitBreakerStopLossModified(confirmedPosition, state);
        }

        private void ManagePositionLadder(Position position, PositionState state, TickSnapshot tick)
        {
            int fractalFactor = state.Capsule == null
                ? FractalFactor
                : state.Capsule.FractalFactor;
            if (state.LadderRecoveryAmbiguous ||
                fractalFactor <= 1 ||
                state.PendingLadderCloseVolume > 0.0)
            {
                return;
            }

            Position livePosition = Positions.FindById(position.Id);
            if (livePosition == null ||
                !livePosition.StopLoss.HasValue ||
                !livePosition.TakeProfit.HasValue)
                return;

            bool isBuy = livePosition.TradeType == TradeType.Buy;
            double marketPrice = isBuy ? tick.Bid : tick.Ask;
            double minimumVolume = Symbol.VolumeInUnitsMin;
            double volumeTolerance = Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6);
            double takeProfitDistance =
                livePosition.TakeProfit.Value - livePosition.EntryPrice;
            double stopLossDistance =
                livePosition.StopLoss.Value - livePosition.EntryPrice;
            bool hasTakeProfitLadderDistance =
                IsFinite(takeProfitDistance) &&
                Math.Abs(takeProfitDistance) >= _symbolTickSize;
            bool hasStopLossLadderDistance =
                IsFinite(stopLossDistance) &&
                Math.Abs(stopLossDistance) >= _symbolTickSize;

            for (int level = fractalFactor; level >= 2; level--)
            {
                int levelBit = 1 << (level - 2);
                double fraction = (fractalFactor - level + 1.0) / fractalFactor;
                double closeFraction = fraction * fraction;
                bool takeProfitReached = hasTakeProfitLadderDistance &&
                    IsLadderLevelReached(
                    isBuy,
                    marketPrice,
                    NormalizePrice(livePosition.EntryPrice +
                        takeProfitDistance * fraction),
                    true);
                bool stopLossReached = hasStopLossLadderDistance &&
                    IsLadderLevelReached(
                    isBuy,
                    marketPrice,
                    NormalizePrice(livePosition.EntryPrice +
                        stopLossDistance * fraction),
                    false);

                if ((state.CompletedTakeProfitLevels & levelBit) == 0 &&
                    takeProfitReached)
                {
                    if (TryClosePositionLadderLevel(
                            livePosition, state, level, levelBit, true,
                            closeFraction, minimumVolume, volumeTolerance))
                    {
                        return;
                    }
                }

                if ((state.CompletedStopLossLevels & levelBit) == 0 &&
                    stopLossReached)
                {
                    if (TryClosePositionLadderLevel(
                            livePosition, state, level, levelBit, false,
                            closeFraction, minimumVolume, volumeTolerance))
                    {
                        return;
                    }
                }
            }
        }

        private static bool IsLadderLevelReached(
            bool isBuy,
            double marketPrice,
            double levelPrice,
            bool takeProfit)
        {
            if (takeProfit)
                return isBuy ? marketPrice >= levelPrice : marketPrice <= levelPrice;

            return isBuy ? marketPrice <= levelPrice : marketPrice >= levelPrice;
        }

        private bool TryClosePositionLadderLevel(
            Position position,
            PositionState state,
            int level,
            int levelBit,
            bool takeProfit,
            double closeFraction,
            double minimumVolume,
            double volumeTolerance)
        {
            double requestedCloseVolume = Symbol.NormalizeVolumeInUnits(
                position.VolumeInUnits * closeFraction,
                RoundingMode.Down);
            double remainingVolume = position.VolumeInUnits - requestedCloseVolume;
            if (!IsTradableVolume(requestedCloseVolume) ||
                remainingVolume < minimumVolume - volumeTolerance)
            {
                if (takeProfit)
                    state.CompletedTakeProfitLevels |= levelBit;
                else
                    state.CompletedStopLossLevels |= levelBit;
                PersistPositionLadderState(position.Id, state);

                LogNormal("TRADE", "LADDER_LEVEL_SKIPPED_VOLUME",
                    "positionId={0} level={1} side={2} closeFraction={3:F6} currentVolume={4:F0} requestedCloseVolume={5:F0} minimumVolume={6:F0}",
                    position.Id, level, takeProfit ? "TP" : "SL",
                    closeFraction, position.VolumeInUnits,
                    requestedCloseVolume, minimumVolume);
                return false;
            }

            string warningKey = string.Format(
                CultureInfo.InvariantCulture,
                "position-ladder-close-{0}-{1}-{2}",
                position.Id, takeProfit ? "tp" : "sl", level);
            state.PendingLadderCloseVolume = requestedCloseVolume;
            TradeResult result = ClosePosition(position, requestedCloseVolume);
            if (result == null || !result.IsSuccessful)
            {
                state.PendingLadderCloseVolume = 0.0;
                LogActiveWarning(warningKey, "TRADE", "LADDER_PARTIAL_CLOSE_RETRY",
                    "positionId={0} level={1} side={2} closeVolume={3:F0} retryable=true brokerError={4}",
                    position.Id, level, takeProfit ? "TP" : "SL",
                    requestedCloseVolume,
                    result == null ? "null result" : result.Error.ToString());
                return true;
            }

            RunTelemetry("FlushPositionPartialClose", () =>
                FlushModificationTelemetry(_positionModificationTelemetry, position.Id, "position", "partialClose", false));
            if (takeProfit)
                state.CompletedTakeProfitLevels |= levelBit;
            else
                state.CompletedStopLossLevels |= levelBit;
            PersistPositionLadderState(position.Id, state);

            ClearWarning(warningKey, "TRADE", "LADDER_PARTIAL_CLOSE_RECOVERED",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "positionId={0} level={1} side={2} outcome=BrokerAccepted",
                    position.Id, level, takeProfit ? "TP" : "SL"));
            Position remainingPosition = Positions.FindById(position.Id) ?? result.Position;
            LogNormal("TRADE", "LADDER_PARTIAL_CLOSE_ACCEPTED",
                "positionId={0} level={1} side={2} closeFraction={3:F6} currentVolume={4:F0} requestedVolume={5:F0} remainingVolume={6}",
                position.Id, level, takeProfit ? "TP" : "SL", closeFraction,
                position.VolumeInUnits,
                requestedCloseVolume,
                remainingPosition == null
                    ? "unavailable"
                    : remainingPosition.VolumeInUnits.ToString(
                        "F0", CultureInfo.InvariantCulture));
            ReconcilePositionLadderHistory(position, state);
            return true;
        }

        private void RestorePositionLadderState(long positionId, PositionState state)
        {
            if (state.LadderRecoveryAmbiguous)
            {
                LogActiveWarning(
                    "position-ladder-ambiguous-" + positionId,
                    "TRADE",
                    "LADDER_RECOVERY_AMBIGUOUS",
                    "positionId={0} outcome=ladderDisabled reason=brokerCommentsCannotPersistPartialCloseProgress",
                    positionId);
                return;
            }

            string key = GetPositionLadderStorageKey(positionId);
            string value = LocalStorage.GetString(key, CircuitBreakerStorageScope);
            if (string.IsNullOrEmpty(value))
                return;

            string[] fields = value.Split('|');
            int configuredFractalFactor = state.Capsule == null
                ? FractalFactor
                : state.Capsule.FractalFactor;
            int storedFractalFactor = 0;
            int completedTakeProfitLevels = 0;
            int completedStopLossLevels = 0;
            bool valid = fields.Length == 4 &&
                fields[0] == "1" &&
                int.TryParse(fields[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out storedFractalFactor) &&
                storedFractalFactor >= 1 && storedFractalFactor <= 5 &&
                int.TryParse(fields[2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out completedTakeProfitLevels) &&
                int.TryParse(fields[3], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out completedStopLossLevels) &&
                completedTakeProfitLevels >= 0 &&
                completedStopLossLevels >= 0;
            if (valid)
            {
                int validLevelMask = (1 << (storedFractalFactor - 1)) - 1;
                valid = (completedTakeProfitLevels & ~validLevelMask) == 0 &&
                    (completedStopLossLevels & ~validLevelMask) == 0;
            }

            if (!valid)
            {
                LogError("TRADE", "INVALID_STORED_LADDER_STATE",
                    "positionId={0} outcome=LadderStateReset", positionId);
                ForgetPositionLadderState(positionId);
                return;
            }

            if (storedFractalFactor != configuredFractalFactor)
            {
                LogNormal("TRADE", "LADDER_STATE_CONFIG_MISMATCH",
                    "positionId={0} storedFractalFactor={1} configuredFractalFactor={2} outcome=LadderStateReset",
                    positionId, storedFractalFactor, configuredFractalFactor);
                ForgetPositionLadderState(positionId);
                return;
            }

            state.CompletedTakeProfitLevels = completedTakeProfitLevels;
            state.CompletedStopLossLevels = completedStopLossLevels;
            LogNormal("TRADE", "LADDER_STATE_RESTORED",
                "positionId={0} fractalFactor={1} completedTakeProfitLevels={2} completedStopLossLevels={3}",
                positionId, configuredFractalFactor,
                completedTakeProfitLevels, completedStopLossLevels);
        }

        private void PersistPositionLadderState(long positionId, PositionState state)
        {
            string key = GetPositionLadderStorageKey(positionId);
            if (state.CompletedTakeProfitLevels == 0 &&
                state.CompletedStopLossLevels == 0)
            {
                ForgetPositionLadderState(positionId);
                return;
            }

            string value = string.Join(
                "|",
                "1",
                (state.Capsule == null
                    ? FractalFactor
                    : state.Capsule.FractalFactor).ToString(CultureInfo.InvariantCulture),
                state.CompletedTakeProfitLevels.ToString(CultureInfo.InvariantCulture),
                state.CompletedStopLossLevels.ToString(CultureInfo.InvariantCulture));
            LocalStorage.SetString(key, value, CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void ForgetPositionLadderState(long positionId)
        {
            LocalStorage.Remove(
                GetPositionLadderStorageKey(positionId),
                CircuitBreakerStorageScope);
            LocalStorage.Flush(CircuitBreakerStorageScope);
        }

        private void ReconcilePositionLadderHistory(Position position, PositionState state)
        {
            HistoricalTrade[] closedTrades = History.FindByPositionId(position.Id);
            ObserveOpenPositionClosingDeals(position.Id, closedTrades);
            if (closedTrades != null)
            {
                foreach (HistoricalTrade trade in closedTrades
                    .OrderBy(item => item.ClosingTime)
                    .ThenBy(item => item.ClosingDealId))
                {
                    if (!state.AccountedClosingDealIds.Add(trade.ClosingDealId))
                        continue;

                    bool validFinancialValues =
                        IsFinite(trade.VolumeInUnits) &&
                        IsFinite(trade.GrossProfit) &&
                        IsFinite(trade.NetProfit) &&
                        IsFinite(trade.Commissions) &&
                        IsFinite(trade.Swap) &&
                        IsFinite(trade.Pips);
                    if (!validFinancialValues || trade.VolumeInUnits <= 0.0)
                    {
                        _tradeStatisticsInvalidRecords++;
                        LogError("TRADE", "LADDER_HISTORY_INVALID",
                            "positionId={0} closingDealId={1} volume={2} grossProfit={3} netProfit={4} commission={5} swap={6} pips={7}",
                            position.Id, trade.ClosingDealId, trade.VolumeInUnits,
                            trade.GrossProfit, trade.NetProfit, trade.Commissions,
                            trade.Swap, trade.Pips);
                        continue;
                    }

                    PositionSnapshot rolloverSnapshot = _positionSnapshots.FirstOrDefault(
                        snapshot =>
                            (snapshot.SourcePositionId == position.Id ||
                             snapshot.RestoredObjectId == position.Id) &&
                            snapshot.RecoveryStatus != SnapshotRecoveryStatus.Restored &&
                            snapshot.RecoveryStatus != SnapshotRecoveryStatus.PermanentlySkipped);
                    if (rolloverSnapshot != null)
                    {
                        rolloverSnapshot.AccountedClosingDealIds.Add(trade.ClosingDealId);
                        rolloverSnapshot.CarriedGrossProfit += trade.GrossProfit;
                        rolloverSnapshot.CarriedNetProfit += trade.NetProfit;
                        rolloverSnapshot.CarriedCommission += trade.Commissions;
                        rolloverSnapshot.CarriedSwap += trade.Swap;
                        rolloverSnapshot.CarriedPips += trade.Pips;
                    }
                    else
                    {
                        state.CarriedGrossProfit += trade.GrossProfit;
                        state.CarriedNetProfit += trade.NetProfit;
                        state.CarriedCommission += trade.Commissions;
                        state.CarriedSwap += trade.Swap;
                        state.CarriedPips += trade.Pips;
                    }

                    if (state.PendingLadderCloseVolume > 0.0)
                        state.PendingLadderCloseVolume -= trade.VolumeInUnits;
                    LogVerbose("TRADE", "LADDER_PARTIAL_CLOSE_ACCOUNTED",
                        "positionId={0} closingDealId={1} volume={2:F0} grossProfit={3:F2} netProfit={4:F2} commission={5:F2} swap={6:F2} pips={7:F2} tradeIdeaId={8}",
                        position.Id, trade.ClosingDealId, trade.VolumeInUnits,
                        trade.GrossProfit, trade.NetProfit, trade.Commissions,
                        trade.Swap, trade.Pips, FormatTradeIdeaId(GetPositionTradeIdeaId(position.Id)));
                }
            }

            if (state.PendingLadderCloseVolume <=
                Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6))
            {
                state.PendingLadderCloseVolume = 0.0;
                ClearWarning("position-ladder-history-" + position.Id,
                    "TRADE", "LADDER_HISTORY_RECONCILED",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "positionId={0} outcome=RealizedProfitAndLossAccounted",
                        position.Id));
            }
            else
            {
                LogActiveWarning("position-ladder-history-" + position.Id,
                    "TRADE", "LADDER_HISTORY_PENDING",
                    "positionId={0} remainingVolumeToReconcile={1:F0}",
                    position.Id, state.PendingLadderCloseVolume);
            }
        }

        private bool ReconcileRolloverLadderHistory(PositionSnapshot snapshot)
        {
            double volumeTolerance = Math.Max(1e-8, Symbol.VolumeInUnitsStep * 1e-6);
            if (snapshot.PendingLadderCloseVolume <= volumeTolerance)
            {
                snapshot.PendingLadderCloseVolume = 0.0;
                return true;
            }

            if (snapshot.SourcePositionId < int.MinValue ||
                    snapshot.SourcePositionId > int.MaxValue)
            {
                LogError("ROLLOVER", "LADDER_HISTORY_POSITION_ID_INVALID",
                    "recoveryId={0} sourcePositionId={1}",
                    snapshot.RecoveryId, snapshot.SourcePositionId);
                return false;
            }

            HistoricalTrade[] closedTrades =
                    History.FindByPositionId((int)snapshot.SourcePositionId);
            if (closedTrades != null)
            {
                foreach (HistoricalTrade trade in closedTrades
                    .OrderBy(item => item.ClosingTime)
                    .ThenBy(item => item.ClosingDealId))
                {
                    if (!snapshot.AccountedClosingDealIds.Add(trade.ClosingDealId))
                        continue;
                    if (Math.Abs(trade.VolumeInUnits - snapshot.VolumeInUnits) <= volumeTolerance ||
                        trade.VolumeInUnits > snapshot.PendingLadderCloseVolume + volumeTolerance)
                    {
                        snapshot.AccountedClosingDealIds.Add(trade.ClosingDealId);
                        continue;
                    }

                    bool validFinancialValues =
                        IsFinite(trade.VolumeInUnits) &&
                        IsFinite(trade.GrossProfit) &&
                        IsFinite(trade.NetProfit) &&
                        IsFinite(trade.Commissions) &&
                        IsFinite(trade.Swap) &&
                        IsFinite(trade.Pips);
                    if (!validFinancialValues || trade.VolumeInUnits <= 0.0)
                    {
                        _tradeStatisticsInvalidRecords++;
                        LogError("ROLLOVER", "LADDER_HISTORY_INVALID",
                            "recoveryId={0} sourcePositionId={1} closingDealId={2} volume={3} netProfit={4}",
                            snapshot.RecoveryId, snapshot.SourcePositionId,
                            trade.ClosingDealId, trade.VolumeInUnits, trade.NetProfit);
                        return false;
                    }

                    snapshot.CarriedGrossProfit += trade.GrossProfit;
                    snapshot.CarriedNetProfit += trade.NetProfit;
                    snapshot.CarriedCommission += trade.Commissions;
                    snapshot.CarriedSwap += trade.Swap;
                    snapshot.CarriedPips += trade.Pips;
                    snapshot.PendingLadderCloseVolume -= trade.VolumeInUnits;
                }
            }

            if (snapshot.PendingLadderCloseVolume > volumeTolerance)
            {
                LogActiveWarning(
                    "rollover-ladder-history-" + snapshot.RecoveryId,
                    "ROLLOVER", "LADDER_HISTORY_PENDING",
                    "recoveryId={0} sourcePositionId={1} remainingVolumeToReconcile={2:F0}",
                    snapshot.RecoveryId, snapshot.SourcePositionId,
                    snapshot.PendingLadderCloseVolume);
                return false;
            }

            snapshot.PendingLadderCloseVolume = 0.0;
            ClearWarning(
                "rollover-ladder-history-" + snapshot.RecoveryId,
                "ROLLOVER", "LADDER_HISTORY_RECONCILED",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "recoveryId={0} sourcePositionId={1} outcome=RealizedProfitAndLossAccounted",
                    snapshot.RecoveryId, snapshot.SourcePositionId));
            return true;
        }

        #endregion

    }
}
