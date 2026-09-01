﻿﻿﻿﻿﻿﻿﻿﻿﻿namespace V3RttMonitor.Core.Protocol;

/// <summary>
/// 单帧 RTT 数据 - 支持动态字段数量。
/// </summary>
public sealed class RttFrame
{
    /// <summary>本帧包含的 float 数量（由解析器自动检测）</summary>
    public int FloatCount { get; }

    public RttFrame(float[] values, byte[] rawBytes, DateTimeOffset receivedAt, int floatCount)
    {
        if (values.Length != floatCount)
        {
            throw new ArgumentException($"Expected {floatCount} values, got {values.Length}.", nameof(values));
        }

        Values = values;
        RawBytes = rawBytes;
        ReceivedAt = receivedAt;
        FloatCount = floatCount;
    }

    public float[] Values { get; }
    public byte[] RawBytes { get; }
    public DateTimeOffset ReceivedAt { get; }
    
    /// <summary>序列号（索引 0）</summary>
    public long Sequence => ToCounter(Values[JustFloatParser.KnownFieldIndex.Seq]);
    
    /// <summary>时间戳 ms（索引 1）</summary>
    public double TimeMs => Values[JustFloatParser.KnownFieldIndex.TimeMs];

    /// <summary>按索引访问字段值</summary>
    public float this[int index] => Values[index];
    public float this[RttField field] => Values[(int)field];

    private static long ToCounter(float value) =>
        float.IsFinite(value) ? checked((long)MathF.Round(value)) : 0L;
}
