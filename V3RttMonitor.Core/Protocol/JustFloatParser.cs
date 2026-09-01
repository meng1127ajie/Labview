using System.Buffers.Binary;

namespace V3RttMonitor.Core.Protocol;

/// <summary>
/// Parses JustFloat frames: N little-endian floats followed by 0x7F800000.
/// N can be configured or detected from three equally-spaced frame tails.
/// </summary>
public sealed class JustFloatParser
{
    public const int DefaultFloatCount = 66;
    public const int MinimumFloatCount = 4;
    public const int MaximumFloatCount = 1024;
    public const uint TailWord = 0x7F800000u;

    private byte[] _buffer = new byte[16 * 1024];
    private int _count;
    private int _configuredFloatCount;
    private int _detectedFloatCount;
    private bool _locked;

    public int FloatCount => _detectedFloatCount;
    public int PayloadSize => _detectedFloatCount * sizeof(float);
    public int FrameSize => _detectedFloatCount == 0 ? 0 : PayloadSize + sizeof(uint);
    public bool IsAutoDetected => _configuredFloatCount == 0 && _detectedFloatCount > 0;
    public bool IsLocked => _locked;
    public long DiscardedBytes { get; private set; }
    public long Resynchronizations { get; private set; }

    /// <summary>0 enables automatic channel-count detection.</summary>
    public void SetFloatCount(int count)
    {
        if (count != 0 && count is < MinimumFloatCount or > MaximumFloatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"通道数应为0或{MinimumFloatCount}~{MaximumFloatCount}。");
        }
        _configuredFloatCount = count;
        _detectedFloatCount = count;
        _count = 0;
        _locked = false;
        DiscardedBytes = 0;
        Resynchronizations = 0;
    }

    public void Reset()
    {
        _count = 0;
        _locked = false;
        if (_configuredFloatCount == 0)
        {
            _detectedFloatCount = 0;
        }
    }

    public IReadOnlyList<RttFrame> Feed(ReadOnlySpan<byte> data, DateTimeOffset? receivedAt = null)
    {
        Append(data);
        var frames = new List<RttFrame>();
        var stamp = receivedAt ?? DateTimeOffset.UtcNow;

        while (true)
        {
            if (_locked)
            {
                if (_count < FrameSize) break;
                if (IsPlausibleFrame(0, _detectedFloatCount))
                {
                    frames.Add(ParseFrame(0, stamp));
                    RemovePrefix(FrameSize, false);
                    continue;
                }

                _locked = false;
                Resynchronizations++;
                RemovePrefix(1, true);
                if (_configuredFloatCount == 0) _detectedFloatCount = 0;
                continue;
            }

            if (_detectedFloatCount > 0 && TryFindConfiguredFrame(out var configuredStart))
            {
                LockAt(configuredStart);
                continue;
            }

            if (_detectedFloatCount == 0
                && TryDetectFrameLayout(out var detectedCount, out var detectedStart))
            {
                _detectedFloatCount = detectedCount;
                LockAt(detectedStart);
                continue;
            }

            TrimUnsynchronizedBuffer();
            break;
        }
        return frames;
    }

    private void LockAt(int frameStart)
    {
        if (frameStart > 0)
        {
            RemovePrefix(frameStart, true);
            Resynchronizations++;
        }
        _locked = true;
    }

    private bool TryFindConfiguredFrame(out int frameStart)
    {
        frameStart = -1;
        var payloadSize = _detectedFloatCount * sizeof(float);
        var frameSize = payloadSize + sizeof(uint);
        var searchAt = 0;
        while (true)
        {
            var tailAt = FindTail(searchAt);
            if (tailAt < 0) return false;
            if (tailAt >= payloadSize)
            {
                var candidate = tailAt - payloadSize;
                if (_count >= candidate + frameSize * 2
                    && IsPlausibleFrame(candidate, _detectedFloatCount)
                    && IsPlausibleFrame(candidate + frameSize, _detectedFloatCount))
                {
                    frameStart = candidate;
                    return true;
                }
            }
            searchAt = tailAt + 1;
        }
    }

    private bool TryDetectFrameLayout(out int floatCount, out int frameStart)
    {
        floatCount = 0;
        frameStart = -1;
        var tails = FindAllTails();
        var tailSet = tails.ToHashSet();
        for (var i = 0; i + 2 < tails.Count; i++)
        {
            for (var j = i + 1; j + 1 < tails.Count; j++)
            {
                var gap1 = tails[j] - tails[i];
                if (gap1 <= sizeof(uint) || gap1 % sizeof(float) != 0) continue;
                if (!tailSet.Contains(tails[i] + gap1 * 2)) continue;

                var candidateCount = (gap1 - sizeof(uint)) / sizeof(float);
                if (candidateCount is < MinimumFloatCount or > MaximumFloatCount) continue;

                var payloadSize = candidateCount * sizeof(float);
                var candidateStart = tails[i] - payloadSize;
                if (candidateStart < 0) candidateStart = tails[i] + sizeof(uint);

                if (_count >= candidateStart + gap1 * 2
                    && IsPlausibleFrame(candidateStart, candidateCount)
                    && IsPlausibleFrame(candidateStart + gap1, candidateCount))
                {
                    floatCount = candidateCount;
                    frameStart = candidateStart;
                    return true;
                }
            }
        }
        return false;
    }

    private bool IsPlausibleFrame(int offset, int floatCount)
    {
        var payloadSize = floatCount * sizeof(float);
        var frameSize = payloadSize + sizeof(uint);
        if (offset < 0 || offset + frameSize > _count || !HasTailAt(offset + payloadSize)) return false;

        var seq = ReadSingle(offset, KnownFieldIndex.Seq);
        var time = ReadSingle(offset, KnownFieldIndex.TimeMs);
        return float.IsFinite(seq)
            && seq >= 0
            && MathF.Abs(seq - MathF.Round(seq)) < .01f
            && float.IsFinite(time)
            && time >= 0;
    }

    private RttFrame ParseFrame(int offset, DateTimeOffset stamp)
    {
        var values = new float[_detectedFloatCount];
        for (var i = 0; i < values.Length; i++) values[i] = ReadSingle(offset, i);
        var raw = new byte[FrameSize];
        Buffer.BlockCopy(_buffer, offset, raw, 0, FrameSize);
        return new RttFrame(values, raw, stamp, _detectedFloatCount);
    }

    private float ReadSingle(int frameOffset, int fieldIndex)
    {
        var bits = BinaryPrimitives.ReadInt32LittleEndian(
            _buffer.AsSpan(frameOffset + fieldIndex * sizeof(float), sizeof(float)));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private List<int> FindAllTails()
    {
        var result = new List<int>();
        for (var i = 0; i <= _count - sizeof(uint); i++) if (HasTailAt(i)) result.Add(i);
        return result;
    }

    private int FindTail(int start)
    {
        for (var i = Math.Max(0, start); i <= _count - sizeof(uint); i++) if (HasTailAt(i)) return i;
        return -1;
    }

    private bool HasTailAt(int offset) =>
        offset >= 0
        && offset + sizeof(uint) <= _count
        && BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(offset, sizeof(uint))) == TailWord;

    private void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required > _buffer.Length) Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
    }

    private void TrimUnsynchronizedBuffer()
    {
        var keep = (MaximumFloatCount * sizeof(float) + sizeof(uint)) * 3 + sizeof(uint);
        if (_count > keep)
        {
            RemovePrefix(_count - keep, true);
            Resynchronizations++;
        }
    }

    private void RemovePrefix(int length, bool discarded)
    {
        if (length <= 0) return;
        Buffer.BlockCopy(_buffer, length, _buffer, 0, _count - length);
        _count -= length;
        if (discarded) DiscardedBytes += length;
    }

    public static class KnownFieldIndex
    {
        public const int Seq = 0;
        public const int TimeMs = 1;
        public const int RunState = 2;
        public const int CalibrationStatus = 3;
        public const int SpeedRpm = 7;
        public const int VBusV = 8;
        public const int IdA = 9;
        public const int IqA = 10;
    }
}
