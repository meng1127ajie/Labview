using System.Buffers.Binary;

namespace V3RttMonitor.Core.Hss;

/// <summary>
/// Decodes the UM08002 HSS byte stream: one U32 timestamp followed by each
/// configured memory block in declaration order. HSS_Read returns complete samples.
/// </summary>
public sealed class HssSampleDecoder(IReadOnlyList<HssVariableSelection> variables)
{
    private readonly IReadOnlyList<HssVariableSelection> _variables = variables;
    private uint _previousTimestamp;
    private ulong _timestampWrapBase;
    private bool _hasTimestamp;
    private long _sampleIndex;

    public int SampleSize => sizeof(uint) + _variables.Sum(item => item.ByteCount);

    public IReadOnlyList<HssSample> Decode(ReadOnlySpan<byte> data, DateTimeOffset? receivedAt = null)
    {
        if (SampleSize <= sizeof(uint)) throw new InvalidOperationException("HSS变量列表为空。");
        if (data.Length % SampleSize != 0)
        {
            throw new InvalidDataException($"HSS数据长度{data.Length}不是单样本{SampleSize}字节的整数倍。");
        }

        var result = new List<HssSample>(data.Length / SampleSize);
        var stamp = receivedAt ?? DateTimeOffset.UtcNow;
        for (var offset = 0; offset < data.Length; offset += SampleSize)
        {
            var rawTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
            if (_hasTimestamp && rawTimestamp < _previousTimestamp && _previousTimestamp - rawTimestamp > uint.MaxValue / 2)
            {
                _timestampWrapBase += 1UL << 32;
            }
            _hasTimestamp = true;
            _previousTimestamp = rawTimestamp;
            var timestamp = _timestampWrapBase + rawTimestamp;

            var values = new double[_variables.Count];
            var valueOffset = offset + sizeof(uint);
            for (var i = 0; i < _variables.Count; i++)
            {
                var variable = _variables[i];
                values[i] = DecodeValue(data.Slice(valueOffset, variable.ByteCount), variable.NumericType);
                valueOffset += variable.ByteCount;
            }
            result.Add(new HssSample(_sampleIndex++, timestamp, values, stamp));
        }
        return result;
    }

    public void Reset()
    {
        _previousTimestamp = 0;
        _timestampWrapBase = 0;
        _hasTimestamp = false;
        _sampleIndex = 0;
    }

    public static double DecodeValue(ReadOnlySpan<byte> bytes, ElfNumericType type) => type switch
    {
        ElfNumericType.UInt8 => bytes[0],
        ElfNumericType.Int8 => unchecked((sbyte)bytes[0]),
        ElfNumericType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        ElfNumericType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        ElfNumericType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        ElfNumericType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
        ElfNumericType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        ElfNumericType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        ElfNumericType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
        ElfNumericType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
        _ => throw new NotSupportedException($"不支持的HSS数值类型：{type}"),
    };
}
