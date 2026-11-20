using System;
using System.Globalization;
using System.Text;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const byte PersistentCapsuleVersion = 2;
        private const char PersistentCapsuleSeparator = '~';
        private const int PersistentCapsulePayloadLength = 44;

        private sealed class PersistentTradeCapsule
        {
            public StrategyKind Strategy;
            public TradeType TradeType;
            public ushort SymbolFingerprint;
            public int FractalFactor;
            public double OriginalTargetPrice;
            public double MovementReferencePrice;
            public double OriginalStopLossPips;
            public double OriginalTakeProfitPips;
            public double OriginalVolumeInUnits;
            public double MovePercent;
            public double LimitDistancePercent;
            public double StopLossPercent;
            public double TakeProfitPercent;

            public PersistentTradeCapsule Copy()
            {
                return (PersistentTradeCapsule)MemberwiseClone();
            }
        }

        private static bool TryEncodePersistentCapsule(
            PersistentTradeCapsule capsule,
            out string encoded,
            out string failure)
        {
            encoded = null;
            failure = null;
            if (capsule == null ||
                capsule.FractalFactor < 1 ||
                capsule.FractalFactor > 5 ||
                !IsFinitePositive(capsule.OriginalTargetPrice) ||
                !IsFinitePositive(capsule.MovementReferencePrice) ||
                !IsFinitePositive(capsule.OriginalStopLossPips) ||
                !IsFinitePositive(capsule.OriginalTakeProfitPips) ||
                !IsFinitePositive(capsule.OriginalVolumeInUnits))
            {
                failure = "invalid capsule values";
                return false;
            }

            byte strategy;
            if (capsule.Strategy == StrategyKind.MeanReversion)
                strategy = 0;
            else if (capsule.Strategy == StrategyKind.TrendFollowing)
                strategy = 1;
            else
            {
                failure = "unsupported strategy";
                return false;
            }

            byte tradeType;
            if (capsule.TradeType == TradeType.Buy)
                tradeType = 0;
            else if (capsule.TradeType == TradeType.Sell)
                tradeType = 1;
            else
            {
                failure = "unsupported trade direction";
                return false;
            }

            uint stopLossMilliPips;
            uint takeProfitMilliPips;
            uint volume;
            ushort movePermille;
            ushort limitPermille;
            ushort stopLossPermille;
            ushort takeProfitPermille;
            if (!TryScaleToUInt32(
                    capsule.OriginalStopLossPips,
                    1000.0,
                    out stopLossMilliPips))
            {
                failure = "originalStopLossPips exceeds the capsule range";
                return false;
            }
            if (!TryScaleToUInt32(
                    capsule.OriginalTakeProfitPips,
                    1000.0,
                    out takeProfitMilliPips))
            {
                failure = "originalTakeProfitPips exceeds the capsule range";
                return false;
            }
            if (!TryEncodeCapsuleVolume(capsule.OriginalVolumeInUnits, out volume))
            {
                failure = string.Format(
                    CultureInfo.InvariantCulture,
                    "originalVolumeInUnits={0:R} cannot be represented",
                    capsule.OriginalVolumeInUnits);
                return false;
            }
            if (!TryScaleToUInt16(capsule.MovePercent, 1000.0, out movePermille))
            {
                failure = "movePercent exceeds the capsule range";
                return false;
            }
            if (!TryScaleToUInt16(capsule.LimitDistancePercent, 1000.0, out limitPermille))
            {
                failure = "limitDistancePercent exceeds the capsule range";
                return false;
            }
            if (!TryScaleToUInt16(capsule.StopLossPercent, 1000.0, out stopLossPermille))
            {
                failure = "stopLossPercent exceeds the capsule range";
                return false;
            }
            if (!TryScaleToUInt16(capsule.TakeProfitPercent, 1000.0, out takeProfitPermille))
            {
                failure = "takeProfitPercent exceeds the capsule range";
                return false;
            }

            byte[] payload = new byte[PersistentCapsulePayloadLength];
            int offset = 0;
            payload[offset++] = PersistentCapsuleVersion;
            WriteUInt16(payload, ref offset, capsule.SymbolFingerprint);
            payload[offset++] = strategy;
            payload[offset++] = tradeType;
            payload[offset++] = (byte)capsule.FractalFactor;
            WriteDouble(payload, ref offset, capsule.OriginalTargetPrice);
            WriteDouble(payload, ref offset, capsule.MovementReferencePrice);
            WriteUInt32(payload, ref offset, stopLossMilliPips);
            WriteUInt32(payload, ref offset, takeProfitMilliPips);
            WriteUInt32(payload, ref offset, volume);
            WriteUInt16(payload, ref offset, movePermille);
            WriteUInt16(payload, ref offset, limitPermille);
            WriteUInt16(payload, ref offset, stopLossPermille);
            WriteUInt16(payload, ref offset, takeProfitPermille);
            ushort checksum = ComputeCapsuleChecksum(payload, 0, offset);
            WriteUInt16(payload, ref offset, checksum);

            encoded = Convert.ToBase64String(payload)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return true;
        }

        private static bool TryDecodePersistentCapsule(
            string encoded,
            out PersistentTradeCapsule capsule,
            out string failure)
        {
            capsule = null;
            failure = null;
            if (string.IsNullOrEmpty(encoded))
            {
                failure = "capsule payload is missing";
                return false;
            }

            byte[] payload;
            try
            {
                string base64 = encoded.Replace('-', '+').Replace('_', '/');
                int padding = (4 - base64.Length % 4) % 4;
                base64 = base64.PadRight(base64.Length + padding, '=');
                payload = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                failure = "capsule payload is not valid base64";
                return false;
            }

            if (payload.Length != PersistentCapsulePayloadLength)
            {
                failure = "capsule payload is truncated or has an unsupported length";
                return false;
            }

            int dataLength = payload.Length - 2;
            ushort expectedChecksum = ReadUInt16(payload, dataLength);
            if (ComputeCapsuleChecksum(payload, 0, dataLength) != expectedChecksum)
            {
                failure = "capsule checksum mismatch";
                return false;
            }

            int offset = 0;
            byte version = payload[offset++];
            if (version != 1 && version != PersistentCapsuleVersion)
            {
                failure = "unsupported capsule schema";
                return false;
            }

            ushort symbolFingerprint = ReadUInt16(payload, offset);
            offset += 2;
            byte strategyValue = payload[offset++];
            byte tradeTypeValue = payload[offset++];
            int fractalFactor = payload[offset++];
            if ((strategyValue > 1) ||
                (tradeTypeValue > 1) ||
                fractalFactor < 1 ||
                fractalFactor > 5)
            {
                failure = "capsule contains invalid strategy, direction, or Fractal Factor";
                return false;
            }

            double originalTarget = ReadDouble(payload, ref offset);
            double movementReference = ReadDouble(payload, ref offset);
            uint stopLossMilliPips = ReadUInt32(payload, ref offset);
            uint takeProfitMilliPips = ReadUInt32(payload, ref offset);
            uint encodedVolume = ReadUInt32(payload, ref offset);
            ushort movePermille = ReadUInt16(payload, ref offset);
            ushort limitPermille = ReadUInt16(payload, ref offset);
            ushort stopLossPermille = ReadUInt16(payload, ref offset);
            ushort takeProfitPermille = ReadUInt16(payload, ref offset);

            double originalVolume;
            if (version == 1)
            {
                originalVolume = encodedVolume;
            }
            else
            {
                float decodedVolume = BitConverter.Int32BitsToSingle(
                    unchecked((int)encodedVolume));
                if (!float.IsFinite(decodedVolume) || decodedVolume <= 0.0f)
                {
                    failure = "capsule contains an invalid encoded volume";
                    return false;
                }

                originalVolume = decodedVolume;
            }

            if (!IsFinitePositive(originalTarget) ||
                !IsFinitePositive(movementReference) ||
                stopLossMilliPips == 0 ||
                takeProfitMilliPips == 0 ||
                encodedVolume == 0)
            {
                failure = "capsule contains impossible numeric values";
                return false;
            }

            capsule = new PersistentTradeCapsule
            {
                Strategy = strategyValue == 0
                    ? StrategyKind.MeanReversion
                    : StrategyKind.TrendFollowing,
                TradeType = tradeTypeValue == 0
                    ? TradeType.Buy
                    : TradeType.Sell,
                SymbolFingerprint = symbolFingerprint,
                FractalFactor = fractalFactor,
                OriginalTargetPrice = originalTarget,
                MovementReferencePrice = movementReference,
                OriginalStopLossPips = stopLossMilliPips / 1000.0,
                OriginalTakeProfitPips = takeProfitMilliPips / 1000.0,
                OriginalVolumeInUnits = originalVolume,
                MovePercent = movePermille / 1000.0,
                LimitDistancePercent = limitPermille / 1000.0,
                StopLossPercent = stopLossPermille / 1000.0,
                TakeProfitPercent = takeProfitPermille / 1000.0
            };
            return true;
        }

        private static bool RunPersistentCapsuleSelfTests()
        {
            var expected = new PersistentTradeCapsule
            {
                Strategy = StrategyKind.TrendFollowing,
                TradeType = TradeType.Sell,
                SymbolFingerprint = ComputeSymbolFingerprint("TEST"),
                FractalFactor = 4,
                OriginalTargetPrice = 1.23456,
                MovementReferencePrice = 1.34567,
                OriginalStopLossPips = 12.345,
                OriginalTakeProfitPips = 23.456,
                OriginalVolumeInUnits = 10000.25,
                MovePercent = 1.234,
                LimitDistancePercent = 2.345,
                StopLossPercent = 3.456,
                TakeProfitPercent = 4.567
            };
            if (!TryEncodePersistentCapsule(
                    expected,
                    out string encoded,
                    out string _) ||
                !TryDecodePersistentCapsule(
                    encoded,
                    out PersistentTradeCapsule decoded,
                    out string _) ||
                !AreEquivalentPersistentCapsules(expected, decoded))
            {
                return false;
            }

            int corruptionIndex = encoded.Length / 2;
            char replacement = encoded[corruptionIndex] == 'A' ? 'B' : 'A';
            string corrupted = encoded.Substring(0, corruptionIndex) +
                replacement +
                encoded.Substring(corruptionIndex + 1);
            return !TryDecodePersistentCapsule(
                corrupted,
                out PersistentTradeCapsule _,
                out string _);
        }

        private static bool TryRecoverCapsuleFromComment(
            string comment,
            out Guid lineageId,
            out PersistentTradeCapsule capsule,
            out string failure)
        {
            lineageId = Guid.Empty;
            capsule = null;
            failure = null;
            if (!TryParseLineageComment(comment, out lineageId))
            {
                failure = "lineage comment is missing or invalid";
                return false;
            }

            int separatorIndex = comment.IndexOf(PersistentCapsuleSeparator);
            if (separatorIndex < 0)
            {
                failure = "comment contains lineage only";
                return false;
            }

            return TryDecodePersistentCapsule(
                comment.Substring(separatorIndex + 1),
                out capsule,
                out failure);
        }

        private bool ValidatePersistentCapsule(
            PersistentTradeCapsule capsule,
            string symbolName,
            TradeType tradeType,
            StrategyKind strategy)
        {
            return capsule != null &&
                capsule.SymbolFingerprint == ComputeSymbolFingerprint(symbolName) &&
                capsule.TradeType == tradeType &&
                capsule.Strategy == strategy;
        }

        private bool HasMatchingPersistentCapsule(
            string comment,
            Guid lineageId,
            PersistentTradeCapsule expected)
        {
            return TryRecoverCapsuleFromComment(
                    comment,
                    out Guid actualLineageId,
                    out PersistentTradeCapsule actual,
                    out string _) &&
                actualLineageId == lineageId &&
                ValidatePersistentCapsule(
                    actual,
                    Symbol.Name,
                    expected.TradeType,
                    expected.Strategy) &&
                AreEquivalentPersistentCapsules(expected, actual);
        }

        private static bool AreEquivalentPersistentCapsules(
            PersistentTradeCapsule expected,
            PersistentTradeCapsule actual)
        {
            return TryEncodePersistentCapsule(expected, out string expectedPayload, out string _) &&
                TryEncodePersistentCapsule(actual, out string actualPayload, out string _) &&
                string.Equals(expectedPayload, actualPayload, StringComparison.Ordinal);
        }

        private bool TryGetCapsuleFromOrder(
            PendingOrder order,
            out PersistentTradeCapsule capsule)
        {
            capsule = null;
            if (!TryRecoverCapsuleFromComment(
                    order.Comment,
                    out Guid _,
                    out PersistentTradeCapsule recovered,
                    out string failure))
            {
                if (HasPersistentCapsuleMarker(order.Comment))
                {
                    LogError("TRADE_LIFECYCLE", "ORDER_CAPSULE_RECOVERY_FAILED",
                        "orderId={0} outcome=orderCancellationRequired reason={1}",
                        order.Id,
                        failure ?? "invalid capsule");
                }
                return false;
            }

            if (!ValidatePersistentCapsule(
                    recovered,
                    order.SymbolName,
                    order.TradeType,
                    GetStrategy(order.Label)))
            {
                LogError("TRADE_LIFECYCLE", "ORDER_CAPSULE_MISMATCH",
                    "orderId={0} outcome=orderCancellationRequired",
                    order.Id);
                return false;
            }

            capsule = recovered;
            return true;
        }

        private bool TryGetCapsuleFromPosition(
            Position position,
            out Guid lineageId,
            out PersistentTradeCapsule capsule)
        {
            if (!TryRecoverCapsuleFromComment(
                    position.Comment,
                    out lineageId,
                    out capsule,
                    out string failure))
            {
                if (HasPersistentCapsuleMarker(position.Comment))
                {
                    LogError("TRADE_LIFECYCLE", "POSITION_CAPSULE_RECOVERY_FAILED",
                        "positionId={0} outcome=protectionRecoveryRequired reason={1}",
                        position.Id,
                        failure ?? "invalid capsule");
                }
                return false;
            }

            if (Account.AccountType != AccountType.Hedged)
            {
                capsule = null;
                LogError("TRADE_LIFECYCLE", "NETTED_POSITION_CAPSULE_AMBIGUOUS",
                    "positionId={0} outcome=capsuleRejected reason=positionMayAggregateMultipleEntries",
                    position.Id);
                return false;
            }

            if (!ValidatePersistentCapsule(
                    capsule,
                    position.SymbolName,
                    position.TradeType,
                    GetStrategy(position.Label)))
            {
                capsule = null;
                LogError("TRADE_LIFECYCLE", "POSITION_CAPSULE_MISMATCH",
                    "positionId={0} outcome=protectionRecoveryRequired",
                    position.Id);
                return false;
            }

            return true;
        }

        private static bool HasPersistentCapsuleMarker(string comment)
        {
            int lineageLength = LineageCommentPrefix.Length + 32;
            return comment != null &&
                comment.Length > lineageLength &&
                comment.StartsWith(LineageCommentPrefix, StringComparison.Ordinal) &&
                comment[lineageLength] == PersistentCapsuleSeparator;
        }

        private string GetPersistentTradeComment(
            Guid? lineageId,
            PersistentTradeCapsule capsule)
        {
            if (!lineageId.HasValue || lineageId.Value == Guid.Empty)
                return null;
            if (capsule == null)
                return ShouldPersistTradeIdeaLineage
                    ? FormatLineageComment(lineageId.Value)
                    : null;

            if (!TryEncodePersistentCapsule(capsule, out string encoded, out string failure))
            {
                LogError("TRADE_LIFECYCLE", "CAPSULE_ENCODING_FAILED",
                    "tradeIdeaId={0} reason={1}",
                    lineageId.Value, failure);
                return null;
            }

            return FormatLineageComment(lineageId.Value) +
                PersistentCapsuleSeparator +
                encoded;
        }

        private void VerifyPersistentCapsuleComment(
            Guid? lineageId,
            PersistentTradeCapsule expected,
            string actualComment,
            string kind,
            int objectId)
        {
            if (expected == null)
            {
                VerifyLineageComment(lineageId, actualComment, kind, objectId);
                return;
            }

            if (!TryRecoverCapsuleFromComment(
                    actualComment,
                    out Guid actualLineageId,
                    out PersistentTradeCapsule actual,
                    out string failure) ||
                !lineageId.HasValue ||
                actualLineageId != lineageId.Value ||
                !ValidatePersistentCapsule(
                    actual,
                    Symbol.Name,
                    expected.TradeType,
                    expected.Strategy) ||
                !AreEquivalentPersistentCapsules(expected, actual))
            {
                LogActiveWarning(
                    "capsule-comment-" + kind + "-" + objectId,
                    "TRADE_LIFECYCLE",
                    "CAPSULE_COMMENT_UNCONFIRMED",
                    "kind={0} objectId={1} reason={2} outcome=frozenConfigurationRecoveryNotGuaranteed",
                    kind,
                    objectId,
                    failure ?? "capsule mismatch");
                return;
            }

            ClearWarning(
                "capsule-comment-" + kind + "-" + objectId,
                "TRADE_LIFECYCLE",
                "CAPSULE_COMMENT_CONFIRMED",
                "broker returned the expected frozen trade capsule");
        }

        private static ushort ComputeSymbolFingerprint(string symbolName)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(symbolName ?? string.Empty);
            return ComputeCapsuleChecksum(bytes, 0, bytes.Length);
        }

        private static ushort ComputeCapsuleChecksum(byte[] bytes, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int index = offset; index < offset + count; index++)
            {
                crc ^= (ushort)(bytes[index] << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0
                        ? (crc << 1) ^ 0x1021
                        : crc << 1);
            }

            return crc;
        }

        private static bool TryScaleToUInt32(double value, double scale, out uint result)
        {
            result = 0;
            if (!IsFinite(value) || value <= 0.0)
                return false;
            double scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
            if (scaled < 1.0 || scaled > uint.MaxValue)
                return false;
            result = (uint)scaled;
            return true;
        }

        private static bool TryEncodeCapsuleVolume(double value, out uint encoded)
        {
            encoded = 0;
            if (!IsFinitePositive(value) || value > float.MaxValue)
                return false;

            float compactValue = (float)value;
            if (!float.IsFinite(compactValue) || compactValue <= 0.0f)
                return false;

            encoded = unchecked((uint)BitConverter.SingleToInt32Bits(compactValue));
            return true;
        }

        private static bool TryScaleToUInt16(double value, double scale, out ushort result)
        {
            result = 0;
            if (!IsFinite(value) || value < 0.0)
                return false;
            double scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
            if (scaled < 0.0 || scaled > ushort.MaxValue)
                return false;
            result = (ushort)scaled;
            return true;
        }

        private static void WriteUInt16(byte[] bytes, ref int offset, ushort value)
        {
            bytes[offset++] = (byte)value;
            bytes[offset++] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] bytes, ref int offset, uint value)
        {
            bytes[offset++] = (byte)value;
            bytes[offset++] = (byte)(value >> 8);
            bytes[offset++] = (byte)(value >> 16);
            bytes[offset++] = (byte)(value >> 24);
        }

        private static void WriteDouble(byte[] bytes, ref int offset, double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            for (int index = 0; index < sizeof(double); index++)
                bytes[offset++] = (byte)(bits >> (index * 8));
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static ushort ReadUInt16(byte[] bytes, ref int offset)
        {
            ushort value = ReadUInt16(bytes, offset);
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] bytes, ref int offset)
        {
            uint value =
                (uint)bytes[offset] |
                ((uint)bytes[offset + 1] << 8) |
                ((uint)bytes[offset + 2] << 16) |
                ((uint)bytes[offset + 3] << 24);
            offset += 4;
            return value;
        }

        private static double ReadDouble(byte[] bytes, ref int offset)
        {
            long bits = 0;
            for (int index = 0; index < sizeof(double); index++)
                bits |= (long)bytes[offset++] << (index * 8);
            return BitConverter.Int64BitsToDouble(bits);
        }
    }
}
