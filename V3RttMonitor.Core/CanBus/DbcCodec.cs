namespace V3RttMonitor.Core.CanBus;

public static class DbcCodec
{
    public static IReadOnlyList<int> GetPhysicalBits(DbcSignal signal)
    {
        if (signal.Length <= 0 || signal.Length > 64) return [];
        var bits = new List<int>(signal.Length);
        if (signal.ByteOrder == DbcByteOrder.Intel)
        {
            for (var index = 0; index < signal.Length; index++) bits.Add(signal.StartBit + index);
        }
        else
        {
            var bit = signal.StartBit;
            for (var index = 0; index < signal.Length; index++)
            {
                bits.Add(bit);
                bit = bit % 8 == 0 ? bit + 15 : bit - 1;
            }
        }
        return bits;
    }

    public static DbcSignalValidation ValidateSignal(DbcMessage message, DbcSignal signal)
    {
        var errors = new List<string>();
        if (signal.Length is < 1 or > 64) errors.Add("长度必须为1~64位。");
        if (signal.StartBit < 0) errors.Add("起始位不能小于0。");
        if (!double.IsFinite(signal.Factor) || signal.Factor == 0) errors.Add("系数必须是非零有限数值。");
        if (!double.IsFinite(signal.Offset)) errors.Add("偏移必须是有限数值。");
        var bits = GetPhysicalBits(signal);
        if (bits.Any(bit => bit < 0 || bit >= message.Length * 8)) errors.Add($"信号超出报文{message.Length}字节范围。");
        return new DbcSignalValidation { Signal = signal, Errors = errors, PhysicalBits = bits };
    }

    public static IReadOnlyList<DbcBitCell> BuildBitLayout(DbcMessage message)
    {
        var usage = new Dictionary<int, List<DbcSignal>>();
        foreach (var signal in message.Signals)
        {
            foreach (var bit in GetPhysicalBits(signal).Where(bit => bit >= 0 && bit < message.Length * 8))
            {
                if (!usage.TryGetValue(bit, out var signals)) usage[bit] = signals = [];
                signals.Add(signal);
            }
        }
        return Enumerable.Range(0, message.Length * 8)
            .Select(bit => new DbcBitCell(bit, bit / 8, bit % 8, usage.TryGetValue(bit, out var signals) ? signals : []))
            .ToArray();
    }

    public static bool TryDecode(DbcMessage message, DbcSignal signal, ReadOnlySpan<byte> data, out DecodedCanSignal decoded)
    {
        decoded = null!;
        var validation = ValidateSignal(message, signal);
        var availableBits = data.Length * 8;
        if (!validation.IsValid || validation.PhysicalBits.Any(bit => bit >= availableBits)) return false;

        ulong raw = 0;
        if (signal.ByteOrder == DbcByteOrder.Intel)
        {
            for (var index = 0; index < validation.PhysicalBits.Count; index++)
            {
                var bit = validation.PhysicalBits[index];
                raw |= (ulong)((data[bit / 8] >> (bit % 8)) & 1) << index;
            }
        }
        else
        {
            foreach (var bit in validation.PhysicalBits) raw = (raw << 1) | (uint)((data[bit / 8] >> (bit % 8)) & 1);
        }

        var signed = ToSigned(raw, signal.Length, signal.IsSigned);
        double physical;
        if (signal.ValueType == DbcSignalValueType.Float32 && signal.Length == 32)
        {
            physical = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        }
        else if (signal.ValueType == DbcSignalValueType.Float64 && signal.Length == 64)
        {
            physical = BitConverter.Int64BitsToDouble(unchecked((long)raw));
        }
        else
        {
            var numericRaw = signal.IsSigned ? (double)signed : raw;
            physical = numericRaw * signal.Factor + signal.Offset;
        }

        signal.Choices.TryGetValue(signal.IsSigned ? signed : unchecked((long)raw), out var choice);
        decoded = new DecodedCanSignal
        {
            Message = message,
            Signal = signal,
            RawUnsigned = raw,
            RawSigned = signed,
            PhysicalValue = physical,
            ChoiceText = choice,
        };
        return true;
    }

    public static IReadOnlyList<DecodedCanSignal> DecodeMessage(DbcMessage message, ReadOnlySpan<byte> data)
    {
        long? muxValue = null;
        var multiplexer = message.Signals.FirstOrDefault(signal => signal.IsMultiplexer);
        if (multiplexer is not null && TryDecode(message, multiplexer, data, out var muxDecoded))
        {
            muxValue = multiplexer.IsSigned ? muxDecoded.RawSigned : unchecked((long)muxDecoded.RawUnsigned);
        }

        var result = new List<DecodedCanSignal>();
        foreach (var signal in message.Signals)
        {
            if (signal.MultiplexerValue is int expected && muxValue != expected) continue;
            if (TryDecode(message, signal, data, out var decoded)) result.Add(decoded);
        }
        return result;
    }

    public static bool TryEncode(DbcMessage message, DbcSignal signal, double physicalValue, Span<byte> data, out string? error)
    {
        error = null;
        var validation = ValidateSignal(message, signal);
        var availableBits = data.Length * 8;
        if (!validation.IsValid || validation.PhysicalBits.Any(bit => bit >= availableBits))
        {
            error = string.Join(" ", validation.Errors.DefaultIfEmpty("数据长度不足。"));
            return false;
        }

        ulong raw;
        if (signal.ValueType == DbcSignalValueType.Float32 && signal.Length == 32)
        {
            raw = unchecked((uint)BitConverter.SingleToInt32Bits((float)physicalValue));
        }
        else if (signal.ValueType == DbcSignalValueType.Float64 && signal.Length == 64)
        {
            raw = unchecked((ulong)BitConverter.DoubleToInt64Bits(physicalValue));
        }
        else
        {
            var unscaled = Math.Round((physicalValue - signal.Offset) / signal.Factor, MidpointRounding.AwayFromZero);
            var minimum = signal.IsSigned ? -(Math.Pow(2, signal.Length - 1)) : 0;
            var maximum = signal.IsSigned ? Math.Pow(2, signal.Length - 1) - 1 : Math.Pow(2, signal.Length) - 1;
            if (unscaled < minimum || unscaled > maximum)
            {
                error = $"物理值换算后的原始值{unscaled}超出{signal.Length}位范围。";
                return false;
            }
            raw = signal.IsSigned ? unchecked((ulong)(long)unscaled) : (ulong)unscaled;
            if (signal.Length < 64) raw &= (1UL << signal.Length) - 1;
        }

        if (signal.ByteOrder == DbcByteOrder.Intel)
        {
            for (var index = 0; index < validation.PhysicalBits.Count; index++) SetBit(data, validation.PhysicalBits[index], (raw & (1UL << index)) != 0);
        }
        else
        {
            for (var index = 0; index < validation.PhysicalBits.Count; index++)
            {
                var sourceBit = signal.Length - 1 - index;
                SetBit(data, validation.PhysicalBits[index], (raw & (1UL << sourceBit)) != 0);
            }
        }
        return true;
    }

    private static long ToSigned(ulong raw, int length, bool signed)
    {
        if (!signed) return unchecked((long)raw);
        if (length == 64) return unchecked((long)raw);
        var signMask = 1UL << (length - 1);
        return (raw & signMask) == 0 ? (long)raw : unchecked((long)(raw | ~((1UL << length) - 1)));
    }

    private static void SetBit(Span<byte> data, int bit, bool value)
    {
        var mask = (byte)(1 << (bit % 8));
        if (value) data[bit / 8] |= mask;
        else data[bit / 8] &= (byte)~mask;
    }
}
