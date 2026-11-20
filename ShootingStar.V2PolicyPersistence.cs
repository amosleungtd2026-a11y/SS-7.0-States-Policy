using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const int V2PersistenceSchemaVersion = 1;
        private const string V2StorageKeyPrefix = "SS7-V2";
        private static readonly JsonSerializerOptions V2JsonOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private sealed class V2DecisionReservation
        {
            public int SchemaVersion { get; set; }
            public string DecisionKey { get; set; }
            public string ModelId { get; set; }
            public string Symbol { get; set; }
            public DateTime BarOpenUtc { get; set; }
            public DateTime ReservedUtc { get; set; }
            public string ConfigurationHash { get; set; }
            public string ConfigurationJson { get; set; }
            public string DrawKeyBase64 { get; set; }
            public string Regime { get; set; }
            public string HourlyState { get; set; }
            public string DailyState { get; set; }
            public double HourlyMev { get; set; }
            public double DailyMev { get; set; }
            public double HourlyStrength { get; set; }
            public double DailyStrength { get; set; }
            public double RegimeConfidence { get; set; }
            public double LongProbability { get; set; }
            public double ShortProbability { get; set; }
            public double HoldProbability { get; set; }
            public string IntegrityHash { get; set; }
        }

        private sealed class V2DecisionJournalEntry
        {
            public int SchemaVersion { get; set; }
            public string DecisionKey { get; set; }
            public int Revision { get; set; }
            public string PreviousDigest { get; set; }
            public string Phase { get; set; }
            public double BossDraw { get; set; }
            public string BossOutcome { get; set; }
            public double WifeDraw { get; set; }
            public double WifeStopProbability { get; set; }
            public string Direction { get; set; }
            public string Strategy { get; set; }
            public string OrderKind { get; set; }
            public double TargetPrice { get; set; }
            public double MovementReferencePrice { get; set; }
            public double MovePercent { get; set; }
            public double StopLossPercent { get; set; }
            public double TakeProfitPercent { get; set; }
            public double VolatilityFactor { get; set; }
            public double LimitDistancePercent { get; set; }
            public double EntryDistancePercent { get; set; }
            public double VolumeInUnits { get; set; }
            public string Label { get; set; }
            public string Description { get; set; }
            public DateTime CreatedUtc { get; set; }
            public DateTime ExpiresUtc { get; set; }
            public double DirectionProbability { get; set; }
            public double StrategyProbability { get; set; }
            public string SubmissionCorrelationId { get; set; }
            public int AttemptCount { get; set; }
            public int RetryCount { get; set; }
            public int? PendingOrderId { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public string Digest { get; set; }
        }

        private bool TryReserveV2Decision(
            DateTime barOpenUtc,
            byte[] drawKey,
            double longProbability,
            double shortProbability,
            double holdProbability,
            out string decisionKey)
        {
            decisionKey = null;
            string identity = string.Join(
                "|",
                V2DecisionModelId,
                Symbol.Name,
                barOpenUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            byte[] identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            decisionKey = ToLowerHex(identityHash, 16);
            string storageKey = V2StorageKeyPrefix + "-R-" + decisionKey;
            string latestKey = V2StorageKeyPrefix + "-L-" +
                ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(Symbol.Name)), 12);

            var reservation = new V2DecisionReservation
            {
                SchemaVersion = V2PersistenceSchemaVersion,
                DecisionKey = decisionKey,
                ModelId = V2DecisionModelId,
                Symbol = Symbol.Name,
                BarOpenUtc = barOpenUtc.ToUniversalTime(),
                ReservedUtc = Server.TimeInUtc,
                ConfigurationHash = _configurationHash ?? "unavailable",
                ConfigurationJson = _canonicalConfiguration ?? "unavailable",
                DrawKeyBase64 = Convert.ToBase64String(drawKey),
                Regime = _v2CombinedRegime.ToString(),
                HourlyState = _v2HourlyTrend.State.ToString(),
                DailyState = _v2DailyTrend.State.ToString(),
                HourlyMev = _currentHourlyMev,
                DailyMev = _currentDailyMev,
                HourlyStrength = _v2HourlyStrength,
                DailyStrength = _v2DailyStrength,
                RegimeConfidence = _v2RegimeConfidence,
                LongProbability = longProbability,
                ShortProbability = shortProbability,
                HoldProbability = holdProbability
            };
            reservation.IntegrityHash = ComputeReservationHash(reservation);
            string serialized = JsonSerializer.Serialize(reservation, V2JsonOptions);

            try
            {
                LocalStorage.Reload(LocalStorageScope.Instance);
                string latestValue = LocalStorage.GetString(
                    latestKey,
                    LocalStorageScope.Instance);
                string previousDecisionKey;
                long previousBarTicks;
                if (!TryParseLatestDecisionPointer(
                        latestValue,
                        out previousDecisionKey,
                        out previousBarTicks))
                {
                    LogError("POLICY", "V2_LATEST_POINTER_CORRUPT",
                        "decisionKey={0} outcome=failClosed",
                        decisionKey);
                    return false;
                }

                if (!string.IsNullOrEmpty(previousDecisionKey) &&
                    previousDecisionKey != decisionKey)
                {
                    if (barOpenUtc.ToUniversalTime().Ticks <= previousBarTicks)
                    {
                        LogError("POLICY", "V2_BAR_ORDER_REGRESSION",
                            "decisionKey={0} previousDecisionKey={1} outcome=failClosed",
                            decisionKey,
                            previousDecisionKey);
                        return false;
                    }

                    string previousJournalPrefix =
                        V2StorageKeyPrefix + "-J-" + previousDecisionKey;
                    LocalStorage.Remove(
                        V2StorageKeyPrefix + "-R-" + previousDecisionKey,
                        LocalStorageScope.Instance);
                    LocalStorage.Remove(
                        previousJournalPrefix + "-A",
                        LocalStorageScope.Instance);
                    LocalStorage.Remove(
                        previousJournalPrefix + "-B",
                        LocalStorageScope.Instance);
                    LocalStorage.Flush(LocalStorageScope.Instance);
                    LocalStorage.Reload(LocalStorageScope.Instance);
                    if (LocalStorage.GetString(
                            V2StorageKeyPrefix + "-R-" + previousDecisionKey,
                            LocalStorageScope.Instance) != null ||
                        LocalStorage.GetString(
                            previousJournalPrefix + "-A",
                            LocalStorageScope.Instance) != null ||
                        LocalStorage.GetString(
                            previousJournalPrefix + "-B",
                            LocalStorageScope.Instance) != null)
                    {
                        LogError("POLICY", "V2_PREVIOUS_RESERVATION_CLEANUP_FAILED",
                            "decisionKey={0} previousDecisionKey={1} outcome=failClosed",
                            decisionKey,
                            previousDecisionKey);
                        return false;
                    }
                }

                if (LocalStorage.GetString(storageKey, LocalStorageScope.Instance) != null)
                {
                    LogError("POLICY", "V2_DECISION_RESERVATION_EXISTS",
                        "decisionKey={0} barOpenUtc={1:O} outcome=failClosed",
                        decisionKey,
                        barOpenUtc);
                    return false;
                }

                LocalStorage.SetString(
                    storageKey,
                    serialized,
                    LocalStorageScope.Instance);
                LocalStorage.SetString(
                    latestKey,
                    decisionKey + "|" +
                        barOpenUtc.ToUniversalTime().Ticks.ToString(
                            CultureInfo.InvariantCulture),
                    LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
                LocalStorage.Reload(LocalStorageScope.Instance);
                string readBack = LocalStorage.GetString(
                    storageKey,
                    LocalStorageScope.Instance);
                V2DecisionReservation verified = readBack == null
                    ? null
                    : JsonSerializer.Deserialize<V2DecisionReservation>(
                        readBack,
                        V2JsonOptions);
                if (!string.Equals(readBack, serialized, StringComparison.Ordinal) ||
                    !string.Equals(
                        LocalStorage.GetString(latestKey, LocalStorageScope.Instance),
                        decisionKey + "|" +
                            barOpenUtc.ToUniversalTime().Ticks.ToString(
                                CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) ||
                    verified == null ||
                    !string.Equals(
                        verified.IntegrityHash,
                        ComputeReservationHash(verified),
                        StringComparison.Ordinal))
                {
                    LogError("POLICY", "V2_DECISION_RESERVATION_VERIFY_FAILED",
                        "decisionKey={0} outcome=failClosed",
                        decisionKey);
                    return false;
                }

                LogVerbose("POLICY", "V2_DECISION_RESERVED",
                    "decisionKey={0} barOpenUtc={1:O} configurationHash={2} outcome=reservedBeforeDraw",
                    decisionKey,
                    barOpenUtc,
                    reservation.ConfigurationHash);
                return true;
            }
            catch (Exception exception)
            {
                LogError("POLICY", "V2_DECISION_RESERVATION_FAILED",
                    "decisionKey={0} exceptionType={1} message={2} outcome=failClosed",
                    decisionKey,
                    exception.GetType().Name,
                    exception.Message);
                return false;
            }
        }

        private void ReconcileExistingV2Decision(
            string decisionKey,
            DateTime barOpenUtc)
        {
            string reservationKey = V2StorageKeyPrefix + "-R-" + decisionKey;
            string journalPrefix = V2StorageKeyPrefix + "-J-" + decisionKey;
            try
            {
                LocalStorage.Reload(LocalStorageScope.Instance);
                string serializedReservation = LocalStorage.GetString(
                    reservationKey,
                    LocalStorageScope.Instance);
                if (serializedReservation == null)
                    return;

                V2DecisionReservation reservation =
                    JsonSerializer.Deserialize<V2DecisionReservation>(
                        serializedReservation,
                        V2JsonOptions);
                if (reservation == null ||
                    reservation.SchemaVersion != V2PersistenceSchemaVersion ||
                    reservation.DecisionKey != decisionKey ||
                    reservation.ModelId != V2DecisionModelId ||
                    reservation.Symbol != Symbol.Name ||
                    reservation.BarOpenUtc != barOpenUtc.ToUniversalTime() ||
                    reservation.ConfigurationHash != _configurationHash ||
                    reservation.IntegrityHash != ComputeReservationHash(reservation))
                {
                    LogError("POLICY", "V2_EXISTING_RESERVATION_INVALID",
                        "decisionKey={0} barOpenUtc={1:O} outcome=failClosed",
                        decisionKey,
                        barOpenUtc);
                    return;
                }

                string serializedA = LocalStorage.GetString(
                    journalPrefix + "-A",
                    LocalStorageScope.Instance);
                string serializedB = LocalStorage.GetString(
                    journalPrefix + "-B",
                    LocalStorageScope.Instance);
                V2DecisionJournalEntry entryA;
                V2DecisionJournalEntry entryB;
                if (!TryParseJournalEntry(serializedA, out entryA) ||
                    !TryParseJournalEntry(serializedB, out entryB))
                {
                    LogError("POLICY", "V2_EXISTING_JOURNAL_INVALID",
                        "decisionKey={0} outcome=failClosed",
                        decisionKey);
                    return;
                }

                V2DecisionJournalEntry latest =
                    GetLatestJournalEntry(entryA, entryB);
                if ((entryA != null && entryB != null &&
                     (latest == null ||
                      Math.Abs(entryA.Revision - entryB.Revision) != 1 ||
                      (entryA.Revision > entryB.Revision
                          ? entryA.PreviousDigest != entryB.Digest
                          : entryB.PreviousDigest != entryA.Digest))) ||
                    (latest != null && latest.DecisionKey != decisionKey))
                {
                    LogError("POLICY", "V2_EXISTING_JOURNAL_CHAIN_INVALID",
                        "decisionKey={0} outcome=failClosed",
                        decisionKey);
                    return;
                }

                Guid correlationId;
                if (latest != null &&
                    Guid.TryParse(
                        latest.SubmissionCorrelationId,
                        out correlationId))
                {
                    foreach (PendingOrder order in PendingOrders)
                    {
                        if (GetSubmissionCorrelationId(order.Id) == correlationId)
                        {
                            LogNormal("POLICY", "V2_RESTART_ORDER_RECONCILED",
                                "decisionKey={0} pendingOrderId={1} phase={2} outcome=existingOrderRetained",
                                decisionKey,
                                order.Id,
                                latest.Phase);
                            return;
                        }
                    }

                    foreach (Position position in Positions)
                    {
                        if (GetPositionTradeIdeaId(position.Id) == correlationId)
                        {
                            LogNormal("POLICY", "V2_RESTART_FILL_RECONCILED",
                                "decisionKey={0} positionId={1} phase={2} outcome=existingPositionRetained",
                                decisionKey,
                                position.Id,
                                latest.Phase);
                            return;
                        }
                    }
                }

                LogError("POLICY", "V2_RESTART_DECISION_FAIL_CLOSED",
                    "decisionKey={0} phase={1} status={2} outcome=noReroll",
                    decisionKey,
                    latest == null ? "reservationOnly" : latest.Phase,
                    latest == null ? "unknown" : latest.Status);
            }
            catch (Exception exception)
            {
                LogError("POLICY", "V2_RESTART_RECONCILIATION_FAILED",
                    "decisionKey={0} exceptionType={1} message={2} outcome=failClosed",
                    decisionKey,
                    exception.GetType().Name,
                    exception.Message);
            }
        }

        private static bool RunV2PersistenceSerializationSelfTest()
        {
            try
            {
                var reservation = new V2DecisionReservation
                {
                    SchemaVersion = V2PersistenceSchemaVersion,
                    DecisionKey = "0123456789abcdef0123456789abcdef",
                    ModelId = V2DecisionModelId,
                    Symbol = "TEST",
                    BarOpenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ReservedUtc = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
                    ConfigurationHash = "config",
                    ConfigurationJson = "frozen",
                    DrawKeyBase64 = Convert.ToBase64String(new byte[32]),
                    Regime = "ConfirmedUptrend",
                    HourlyState = "Up",
                    DailyState = "Up",
                    HourlyMev = 0.4,
                    DailyMev = 0.5,
                    HourlyStrength = 0.5,
                    DailyStrength = 0.5,
                    RegimeConfidence = 0.5,
                    LongProbability = 0.7,
                    ShortProbability = 0.1,
                    HoldProbability = 0.2
                };
                reservation.IntegrityHash = ComputeReservationHash(reservation);
                string serializedReservation =
                    JsonSerializer.Serialize(reservation, V2JsonOptions);
                V2DecisionReservation parsedReservation =
                    JsonSerializer.Deserialize<V2DecisionReservation>(
                        serializedReservation,
                        V2JsonOptions);

                var journal = new V2DecisionJournalEntry
                {
                    SchemaVersion = V2PersistenceSchemaVersion,
                    DecisionKey = reservation.DecisionKey,
                    Revision = 1,
                    PreviousDigest = string.Empty,
                    Phase = "Reserved",
                    BossOutcome = "notDrawn",
                    Status = "Reserved",
                    Reason = "selfTest"
                };
                journal.Digest = ComputeJournalHash(journal);
                string serializedJournal =
                    JsonSerializer.Serialize(journal, V2JsonOptions);
                V2DecisionJournalEntry parsedJournal;
                return parsedReservation != null &&
                       parsedReservation.IntegrityHash ==
                           ComputeReservationHash(parsedReservation) &&
                       TryParseJournalEntry(serializedJournal, out parsedJournal) &&
                       parsedJournal != null &&
                       parsedJournal.Revision == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryParseLatestDecisionPointer(
            string value,
            out string decisionKey,
            out long barOpenTicks)
        {
            decisionKey = null;
            barOpenTicks = 0;
            if (value == null)
                return true;

            string[] parts = value.Split('|');
            if (parts.Length != 2 || parts[0].Length != 32 ||
                !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out barOpenTicks) ||
                barOpenTicks <= 0)
            {
                return false;
            }

            for (int index = 0; index < parts[0].Length; index++)
            {
                char character = parts[0][index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            decisionKey = parts[0];
            return true;
        }

        private bool PersistV2DecisionJournal(V2DecisionJournalEntry update)
        {
            if (update == null || string.IsNullOrEmpty(update.DecisionKey))
                return false;

            string baseKey = V2StorageKeyPrefix + "-J-" + update.DecisionKey;
            string keyA = baseKey + "-A";
            string keyB = baseKey + "-B";
            try
            {
                LocalStorage.Reload(LocalStorageScope.Instance);
                string serializedA = LocalStorage.GetString(
                    keyA,
                    LocalStorageScope.Instance);
                string serializedB = LocalStorage.GetString(
                    keyB,
                    LocalStorageScope.Instance);
                V2DecisionJournalEntry entryA;
                V2DecisionJournalEntry entryB;
                if (!TryParseJournalEntry(serializedA, out entryA) ||
                    !TryParseJournalEntry(serializedB, out entryB))
                {
                    LogError("POLICY", "V2_DECISION_JOURNAL_CORRUPT",
                        "decisionKey={0} outcome=failClosed",
                        update.DecisionKey);
                    return false;
                }

                V2DecisionJournalEntry latest = GetLatestJournalEntry(entryA, entryB);
                if (entryA != null && entryB != null && latest == null)
                {
                    LogError("POLICY", "V2_DECISION_JOURNAL_KEY_MISMATCH",
                        "decisionKey={0} outcome=failClosed",
                        update.DecisionKey);
                    return false;
                }
                if (entryA != null && entryB != null &&
                    (Math.Abs(entryA.Revision - entryB.Revision) != 1 ||
                     (entryA.Revision > entryB.Revision
                         ? entryA.PreviousDigest != entryB.Digest
                         : entryB.PreviousDigest != entryA.Digest)))
                {
                    LogError("POLICY", "V2_DECISION_JOURNAL_CHAIN_INVALID",
                        "decisionKey={0} outcome=failClosed",
                        update.DecisionKey);
                    return false;
                }

                update.SchemaVersion = V2PersistenceSchemaVersion;
                update.Revision = latest == null ? 1 : latest.Revision + 1;
                update.PreviousDigest = latest == null ? string.Empty : latest.Digest;
                update.Digest = ComputeJournalHash(update);
                string serialized = JsonSerializer.Serialize(update, V2JsonOptions);
                string targetKey = update.Revision % 2 == 1 ? keyA : keyB;
                LocalStorage.SetString(
                    targetKey,
                    serialized,
                    LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
                LocalStorage.Reload(LocalStorageScope.Instance);
                string readBack = LocalStorage.GetString(
                    targetKey,
                    LocalStorageScope.Instance);
                V2DecisionJournalEntry verified;
                if (!string.Equals(readBack, serialized, StringComparison.Ordinal) ||
                    !TryParseJournalEntry(readBack, out verified) ||
                    verified.Revision != update.Revision)
                {
                    LogError("POLICY", "V2_DECISION_JOURNAL_VERIFY_FAILED",
                        "decisionKey={0} revision={1} outcome=failClosed",
                        update.DecisionKey,
                        update.Revision);
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                LogError("POLICY", "V2_DECISION_JOURNAL_WRITE_FAILED",
                    "decisionKey={0} exceptionType={1} message={2} outcome=failClosed",
                    update.DecisionKey,
                    exception.GetType().Name,
                    exception.Message);
                return false;
            }
        }

        private V2DecisionJournalEntry BuildV2DecisionJournalEntry(
            PendingPolicyDecision decision,
            string phase,
            string reason)
        {
            return new V2DecisionJournalEntry
            {
                DecisionKey = decision.DecisionKey,
                Phase = phase,
                BossDraw = decision.BossRandomDraw,
                BossOutcome = decision.BossOutcome,
                WifeDraw = IsFinite(decision.WifeRandomDraw)
                    ? decision.WifeRandomDraw
                    : 0.0,
                WifeStopProbability = IsFinite(decision.WifeStopProbability)
                    ? decision.WifeStopProbability
                    : 0.0,
                Direction = decision.Direction.ToString(),
                Strategy = decision.Strategy.ToString(),
                OrderKind = decision.OrderKind.ToString(),
                TargetPrice = decision.TargetPrice,
                MovementReferencePrice = decision.MovementReferencePrice,
                MovePercent = decision.MovePercent,
                StopLossPercent = decision.StopLossPercent,
                TakeProfitPercent = decision.TakeProfitPercent,
                VolatilityFactor = decision.VolatilityFactor,
                LimitDistancePercent = decision.EffectiveLimitDistancePercent,
                EntryDistancePercent = decision.EffectiveEntryDistancePercent,
                VolumeInUnits = decision.VolumeInUnits,
                Label = decision.Label,
                Description = decision.Description,
                CreatedUtc = decision.CreatedUtc,
                ExpiresUtc = decision.ExpiresUtc,
                DirectionProbability = decision.DirectionProbability,
                StrategyProbability = decision.StrategyProbability,
                SubmissionCorrelationId = FormatSubmissionCorrelationId(
                    decision.SubmissionCorrelationId),
                AttemptCount = decision.AttemptCount,
                RetryCount = decision.RetryCount,
                PendingOrderId = decision.PendingOrderId,
                Status = decision.Status.ToString(),
                Reason = reason
            };
        }

        private static bool TryParseJournalEntry(
            string serialized,
            out V2DecisionJournalEntry entry)
        {
            entry = null;
            if (serialized == null)
                return true;
            try
            {
                entry = JsonSerializer.Deserialize<V2DecisionJournalEntry>(
                    serialized,
                    V2JsonOptions);
                return entry != null &&
                       entry.SchemaVersion == V2PersistenceSchemaVersion &&
                       entry.Revision > 0 &&
                       string.Equals(
                           entry.Digest,
                           ComputeJournalHash(entry),
                           StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static V2DecisionJournalEntry GetLatestJournalEntry(
            V2DecisionJournalEntry entryA,
            V2DecisionJournalEntry entryB)
        {
            if (entryA == null)
                return entryB;
            if (entryB == null)
                return entryA;
            if (entryA.DecisionKey != entryB.DecisionKey)
                return null;
            return entryA.Revision > entryB.Revision ? entryA : entryB;
        }

        private static string ComputeReservationHash(
            V2DecisionReservation reservation)
        {
            string existing = reservation.IntegrityHash;
            reservation.IntegrityHash = null;
            string canonical = JsonSerializer.Serialize(reservation, V2JsonOptions);
            reservation.IntegrityHash = existing;
            return Sha256Hex(canonical);
        }

        private static string ComputeJournalHash(V2DecisionJournalEntry entry)
        {
            string existing = entry.Digest;
            entry.Digest = null;
            string canonical = JsonSerializer.Serialize(entry, V2JsonOptions);
            entry.Digest = existing;
            return Sha256Hex(canonical);
        }

        private static string Sha256Hex(string value)
        {
            return ToLowerHex(
                SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)),
                32);
        }

        private static string ToLowerHex(byte[] bytes, int byteCount)
        {
            var builder = new StringBuilder(byteCount * 2);
            for (int index = 0; index < byteCount; index++)
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
