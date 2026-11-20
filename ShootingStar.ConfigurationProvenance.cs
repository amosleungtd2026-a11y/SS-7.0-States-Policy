using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const int ConfigurationSchemaVersion = 4;
        private const int MarketSessionSchemaVersion = 2;
        private const int ReferencePriceSchemaVersion = 2;
        private const int TradeLineageSchemaVersion = 4;
        private const int RecoveryCompletenessSchemaVersion = 1;
        private const int FinancialStateSchemaVersion = 2;
        private const int LogicalTradeOutcomeSchemaVersion = 2;

        private string _canonicalConfiguration;
        private string _configurationHash;
        private string _configurationHashShort;
        private int _configurationGeneration;
        private bool _configurationProvenanceInitialized;
        private bool _provenanceEntriesBlocked;
        private DateTime _provenanceStartedUtc;

        private static string FormatCanonicalDouble(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatCanonicalBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string EscapeCanonicalValue(string value)
        {
            if (value == null)
                return "<null>";
            return value.Replace("\\", "\\\\")
                .Replace("|", "\\|")
                .Replace("=", "\\=")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static void AppendCanonical(StringBuilder builder, string name, string value)
        {
            if (builder.Length > 0)
                builder.Append('|');
            builder.Append(name);
            builder.Append('=');
            builder.Append(EscapeCanonicalValue(value));
        }

        private string BuildCanonicalConfiguration()
        {
            if (_effectiveTradingMode == TradingMode.ProbabilisticAuto)
                return BuildCanonicalV2Configuration();

            StringBuilder b = new StringBuilder(4096);
            Action<string, string> add = (name, value) => AppendCanonical(b, name, value);
            add("configurationSchemaVersion", ConfigurationSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("sourceReleaseId", SourceReleaseId);
            add("tradingModeName", _effectiveTradingMode.ToString());
            add("tradingModeValue", ((int)_effectiveTradingMode).ToString(CultureInfo.InvariantCulture));
            add("globalVolatility", FormatCanonicalDouble(GlobalVolatility));
            add("minimumPositionTrailStepPercent", FormatCanonicalDouble(MinimumPositionTrailStepPercent));
            add("fractalFactor", FractalFactor.ToString(CultureInfo.InvariantCulture));
            add("totalTradingVolumeInLots", FormatCanonicalDouble(TotalTradingVolumeInLots));
            add("directionalAllocation", FormatCanonicalDouble(DirectionalAllocation));
            add("recoverIdeaIdsFromComments", FormatCanonicalBool(RecoverIdeaIdsFromComments));
            add("transientPlacementRetryCount", TransientPlacementRetryCount.ToString(CultureInfo.InvariantCulture));
            add("transientPlacementRetrySeconds", TransientPlacementRetrySeconds.ToString(CultureInfo.InvariantCulture));
            add("policyDecisionLifetimeMinutes", PolicyDecisionLifetimeMinutes.ToString(CultureInfo.InvariantCulture));
            add("maxNewOrdersPerMaintenancePass", MaxNewOrdersPerMaintenancePass.ToString(CultureInfo.InvariantCulture));
            add("policyRandomSeed", PolicyRandomSeed.ToString(CultureInfo.InvariantCulture));
            add("lowerBandLongUseTrendFollowing", FormatCanonicalBool(LowerBandLongUseTrendFollowing));
            add("lowerBandShortUseTrendFollowing", FormatCanonicalBool(LowerBandShortUseTrendFollowing));
            add("middleBandLongUseTrendFollowing", FormatCanonicalBool(MiddleBandLongUseTrendFollowing));
            add("middleBandShortUseTrendFollowing", FormatCanonicalBool(MiddleBandShortUseTrendFollowing));
            add("upperBandLongUseTrendFollowing", FormatCanonicalBool(UpperBandLongUseTrendFollowing));
            add("upperBandShortUseTrendFollowing", FormatCanonicalBool(UpperBandShortUseTrendFollowing));
            add("momentumEfficiencyLength", MomentumEfficiencyLength.ToString(CultureInfo.InvariantCulture));
            add("mevUpperBoundary", FormatCanonicalDouble(MevUpperBoundary));
            add("mevLowerBoundary", FormatCanonicalDouble(MevLowerBoundary));
            add("mevExtremeThreshold", FormatCanonicalDouble(MevExtremeThreshold));
            add("percentageUnit", FormatCanonicalDouble(PercentageUnit));
            add("moveUnits", FormatCanonicalDouble(MoveUnits));
            add("distanceRatio", FormatCanonicalDouble(DistanceRatio));
            add("stopLossUnits", FormatCanonicalDouble(StopLossUnits));
            add("reward", FormatCanonicalDouble(Reward));
            add("marketSessionOverrides", MarketSessionOverrides ?? string.Empty);
            add("positionTrailing", PositionTrailing.ToString());
            add("safeDistanceMultiplier", FormatCanonicalDouble(SafeDistanceMultiplier));
            add("maximumPendingOrderRiskPercent", FormatCanonicalDouble(MaximumPendingOrderRiskPercent));
            add("checkEveryNTicks", CheckEveryNTicks.ToString(CultureInfo.InvariantCulture));
            add("positionModifyCooldownMilliseconds", PositionModifyCooldownMilliseconds.ToString(CultureInfo.InvariantCulture));
            add("atrFastPeriod", AtrFastPeriod.ToString(CultureInfo.InvariantCulture));
            add("atrSlowPeriod", AtrSlowPeriod.ToString(CultureInfo.InvariantCulture));
            add("minNormalizedAtrRatio", FormatCanonicalDouble(MinNormalizedAtrRatio));
            add("maxNormalizedAtrRatio", FormatCanonicalDouble(MaxNormalizedAtrRatio));
            add("atrNormalizationSensitivity", FormatCanonicalDouble(AtrNormalizationSensitivity));
            add("minHourlyVolatilityFactor", FormatCanonicalDouble(MinHourlyVolatilityFactor));
            add("maxHourlyVolatilityFactor", FormatCanonicalDouble(MaxHourlyVolatilityFactor));
            add("utcHourlyVolatilityValues", UtcHourlyVolatilityValues);
            add("tfPendingOrderCancellationMoveMultiplier", FormatCanonicalDouble(TfPendingOrderCancellationMoveMultiplier));
            add("rolloverRestorationMaxAttempts", RolloverRestorationMaxAttempts.ToString(CultureInfo.InvariantCulture));
            add("rolloverRestorationRetrySeconds", RolloverRestorationRetrySeconds.ToString(CultureInfo.InvariantCulture));
            add("rolloverPositionRestorationMaxAttempts", RolloverPositionRestorationMaxAttempts.ToString(CultureInfo.InvariantCulture));
            add("rolloverPositionRestorationRetrySeconds", RolloverPositionRestorationRetrySeconds.ToString(CultureInfo.InvariantCulture));
            add("rolloverTransientRetrySeconds", RolloverTransientRetrySeconds.ToString(CultureInfo.InvariantCulture));
            add("rolloverTransientMaxWaitMinutes", RolloverTransientMaxWaitMinutes.ToString(CultureInfo.InvariantCulture));
            add("referenceBucketCount", "480");
            add("referenceMaximumStalenessMinutes", "2");
            add("referencePriceDomain", "Bid");
            add("referenceHistoricalSource", "BidMinuteBarsWithBidTickFallback");
            add("referencePriceSchemaVersion", ReferencePriceSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("marketSessionSchemaVersion", MarketSessionSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("tradeLineageSchemaVersion", TradeLineageSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("recoveryCompletenessSchemaVersion", RecoveryCompletenessSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("financialStateSchemaVersion", FinancialStateSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("logicalTradeOutcomeSchemaVersion", LogicalTradeOutcomeSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("riskConversionMode", "BrokerOnly");
            add("conversionFallbackEnabled", "false");
            add("conversionRetryMinutes", "5");
            add("conversionRecoveryWindowMinutes", "120");
            return b.ToString();
        }

        private string BuildCanonicalV2Configuration()
        {
            StringBuilder b = new StringBuilder(2048);
            Action<string, string> add =
                (name, value) => AppendCanonical(b, name, value);

            add("configurationSchemaVersion",
                ConfigurationSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("decisionModelId", V2DecisionModelId);
            add("decisionModelVersion",
                V2ConfigurationSchemaVersion.ToString(CultureInfo.InvariantCulture));
            add("sourceReleaseId", SourceReleaseId);
            add("policyRandomSeed",
                PolicyRandomSeed.ToString(CultureInfo.InvariantCulture));
            add("momentumEfficiencyLength",
                MomentumEfficiencyLength.ToString(CultureInfo.InvariantCulture));
            add("mevLowerBoundary", FormatCanonicalDouble(MevLowerBoundary));
            add("mevUpperBoundary", FormatCanonicalDouble(MevUpperBoundary));
            add("mevExtremeThreshold", FormatCanonicalDouble(MevExtremeThreshold));
            add("hourlyPersistenceSeconds",
                V2HourlyPersistenceSeconds.ToString(CultureInfo.InvariantCulture));
            add("dailyPersistenceSeconds",
                V2DailyPersistenceSeconds.ToString(CultureInfo.InvariantCulture));
            add("minimumQualifyingRefreshes",
                V2MinimumQualifyingRefreshes.ToString(CultureInfo.InvariantCulture));
            add("hourlyHysteresis", FormatCanonicalDouble(V2HourlyHysteresis));
            add("dailyHysteresis", FormatCanonicalDouble(V2DailyHysteresis));
            add("hourlyConfidenceWeight",
                FormatCanonicalDouble(V2HourlyStrengthWeight));
            add("dailyConfidenceWeight",
                FormatCanonicalDouble(V2DailyStrengthWeight));
            add("reversalWatchActivationSeconds",
                V2ReversalWatchActivationSeconds.ToString(CultureInfo.InvariantCulture));
            add("reversalWatchMinimumRefreshes",
                V2ReversalWatchMinimumRefreshes.ToString(CultureInfo.InvariantCulture));
            add("reversalWatchInvalidGraceSeconds",
                V2ReversalWatchInvalidGraceSeconds.ToString(CultureInfo.InvariantCulture));
            add("reversalWatchActivationThreshold",
                FormatCanonicalDouble(V2ReversalWatchActivationThreshold));
            add("reversalWatchClearThreshold",
                FormatCanonicalDouble(V2ReversalWatchClearThreshold));
            add("bossMaximumHoldProbability",
                FormatCanonicalDouble(V2BossMaximumHoldProbability));
            add("bossMaximumContrarySellProbability",
                FormatCanonicalDouble(V2BossMaximumContrarySellProbability));
            add("bossMaximumContraryBuyProbability",
                FormatCanonicalDouble(V2BossMaximumContraryBuyProbability));
            add("extremeCountertrendStopProbability",
                FormatCanonicalDouble(ExtremeCountertrendStopProbability));
            add("stopOrderMultiplier",
                FormatCanonicalDouble(StopOrderMultiplier));
            add("globalVolatility", FormatCanonicalDouble(GlobalVolatility));
            add("totalTradingVolumeInLots",
                FormatCanonicalDouble(TotalTradingVolumeInLots));
            add("directionalAllocation",
                FormatCanonicalDouble(DirectionalAllocation));
            add("percentageUnit", FormatCanonicalDouble(PercentageUnit));
            add("moveUnits", FormatCanonicalDouble(MoveUnits));
            add("distanceRatio", FormatCanonicalDouble(DistanceRatio));
            add("stopLossUnits", FormatCanonicalDouble(StopLossUnits));
            add("reward", FormatCanonicalDouble(Reward));
            add("fractalFactor", FractalFactor.ToString(CultureInfo.InvariantCulture));
            add("safeDistanceMultiplier",
                FormatCanonicalDouble(SafeDistanceMultiplier));
            add("maximumPendingOrderRiskPercent",
                FormatCanonicalDouble(MaximumPendingOrderRiskPercent));
            add("minimumPositionTrailStepPercent",
                FormatCanonicalDouble(MinimumPositionTrailStepPercent));
            add("atrFastPeriod", AtrFastPeriod.ToString(CultureInfo.InvariantCulture));
            add("atrSlowPeriod", AtrSlowPeriod.ToString(CultureInfo.InvariantCulture));
            add("minNormalizedAtrRatio",
                FormatCanonicalDouble(MinNormalizedAtrRatio));
            add("maxNormalizedAtrRatio",
                FormatCanonicalDouble(MaxNormalizedAtrRatio));
            add("atrNormalizationSensitivity",
                FormatCanonicalDouble(AtrNormalizationSensitivity));
            add("minHourlyVolatilityFactor",
                FormatCanonicalDouble(MinHourlyVolatilityFactor));
            add("maxHourlyVolatilityFactor",
                FormatCanonicalDouble(MaxHourlyVolatilityFactor));
            add("utcHourlyVolatilityValues", UtcHourlyVolatilityValues);
            add("referenceBucketCount", "480");
            add("referenceMaximumStalenessMinutes", "2");
            add("referencePriceDomain", "Bid");
            add("policyDecisionLifetimeMinutes",
                PolicyDecisionLifetimeMinutes.ToString(CultureInfo.InvariantCulture));
            add("transientPlacementRetryCount",
                TransientPlacementRetryCount.ToString(CultureInfo.InvariantCulture));
            add("transientPlacementRetrySeconds",
                TransientPlacementRetrySeconds.ToString(CultureInfo.InvariantCulture));
            add("tfPendingOrderCancellationMoveMultiplier",
                FormatCanonicalDouble(TfPendingOrderCancellationMoveMultiplier));
            return b.ToString();
        }

        private static string ComputeSha256Hex(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder b = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    b.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        private bool TryInitializeConfigurationProvenance()
        {
            try
            {
                _provenanceStartedUtc = Server.TimeInUtc;
                _configurationGeneration = 1;
                _canonicalConfiguration = BuildCanonicalConfiguration();
                _configurationHash = ComputeSha256Hex(_canonicalConfiguration);
                _configurationHashShort = _configurationHash.Substring(0, 12);
                _configurationProvenanceInitialized = true;
                _provenanceEntriesBlocked = false;
                LogBotProvenance();
                return true;
            }
            catch (Exception exception)
            {
                _configurationProvenanceInitialized = false;
                _provenanceEntriesBlocked = true;
                LogError("STARTUP", "PROVENANCE_INITIALIZATION_FAILED",
                    "sourceReleaseId={0} exceptionType={1} message={2}",
                    SourceReleaseId, exception.GetType().Name, exception.Message);
                return false;
            }
        }

        private void CommitConfigurationProvenanceChange(string changedFields)
        {
            string oldHash = _configurationHash ?? "uninitialized";
            string canonical = BuildCanonicalConfiguration();
            string newHash = ComputeSha256Hex(canonical);
            if (string.Equals(oldHash, newHash, StringComparison.Ordinal))
                return;
            _canonicalConfiguration = canonical;
            _configurationHash = newHash;
            _configurationHashShort = newHash.Substring(0, 12);
            _configurationGeneration++;
            _configurationProvenanceInitialized = true;
            _provenanceEntriesBlocked = false;
            LogNormal("PROVENANCE", "CONFIGURATION_CHANGED",
                "generation={0} oldConfigurationHash={1} newConfigurationHash={2} changedFields={3} commitUtc={4:O} existingPendingOrders={5} existingPositions={6} existingObjectsUnchanged=true",
                _configurationGeneration, oldHash, newHash, changedFields,
                Server.TimeInUtc,
                PendingOrders.Where(IsBotOrder).Count(),
                Positions.Where(IsBotPosition).Count());
        }

        private void LogBotProvenance()
        {
            string apiVersion = typeof(Robot).Assembly.GetName().Version == null
                ? "unknown" : typeof(Robot).Assembly.GetName().Version.ToString();
            LogNormal("PROVENANCE", "BOT_PROVENANCE",
                "sourceReleaseId={0} configurationSchemaVersion={1} configurationGeneration={2} configurationHash={3} configurationHashShort={4} symbol={5} accountType={6} accountCurrency={7} platformApiVersion={8} referencePriceDomain=Bid tradeLineagePersistence={9} riskConversionMode=BrokerOnly conversionFallbackEnabled=false conversionRetryMinutes=5 conversionRecoveryWindowMinutes=120 logicalTradeOutcomeSchemaVersion={10} financialStateSchemaVersion={11} recoveryCompletenessSchemaVersion={12}",
                SourceReleaseId, ConfigurationSchemaVersion, _configurationGeneration,
                _configurationHash, _configurationHashShort, Symbol.Name,
                Account.AccountType, Account.Asset == null ? "unknown" : Account.Asset.Name,
                apiVersion, ShouldPersistTradeIdeaLineage,
                LogicalTradeOutcomeSchemaVersion, FinancialStateSchemaVersion,
                RecoveryCompletenessSchemaVersion);
        }

    }
}
