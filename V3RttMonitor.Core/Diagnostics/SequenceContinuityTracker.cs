namespace V3RttMonitor.Core.Diagnostics;

/// <summary>
/// Learns the normal increment of an integer sequence counter and reports only
/// gaps that are exact multiples of that increment. This avoids treating a
/// producer which deliberately emits SEQ 0, 100, 200... as 99 lost frames per
/// received frame.
/// </summary>
public sealed class SequenceContinuityTracker
{
    public const int RequiredConfirmations = 3;

    private readonly Dictionary<long, int> _deltaOccurrences = [];
    private readonly List<long> _unclassifiedPositiveDeltas = [];
    private bool _hasPrevious;
    private long _previous;
    private long? _nominalStep;

    public long ReceivedFrames { get; private set; }
    public long LostFrames { get; private set; }
    public long GapEvents { get; private set; }
    public long Anomalies { get; private set; }
    public long Restarts { get; private set; }
    public long LastSequence { get; private set; } = -1;
    public long? NominalStep => _nominalStep;
    public bool IsStepConfirmed => _nominalStep.HasValue;

    public void Observe(long sequence)
    {
        ReceivedFrames++;
        LastSequence = sequence;

        if (!_hasPrevious)
        {
            _hasPrevious = true;
            _previous = sequence;
            return;
        }

        var delta = sequence - _previous;
        _previous = sequence;

        if (delta < 0)
        {
            Restarts++;
            return;
        }

        if (delta == 0)
        {
            Anomalies++;
            return;
        }

        if (_nominalStep is long step)
        {
            ClassifyPositiveDelta(delta, step);
            return;
        }

        _unclassifiedPositiveDeltas.Add(delta);
        _deltaOccurrences.TryGetValue(delta, out var count);
        _deltaOccurrences[delta] = count + 1;

        // Prefer the smallest repeatedly observed delta. A larger delta is
        // commonly one or more missing samples, whereas the smallest stable
        // delta is the most conservative normal-step estimate.
        var confirmed = _deltaOccurrences
            .Where(item => item.Value >= RequiredConfirmations)
            .Select(item => item.Key)
            .DefaultIfEmpty(0)
            .Min();
        if (confirmed <= 0) return;

        _nominalStep = confirmed;
        foreach (var pendingDelta in _unclassifiedPositiveDeltas)
        {
            ClassifyPositiveDelta(pendingDelta, confirmed);
        }

        _unclassifiedPositiveDeltas.Clear();
        _deltaOccurrences.Clear();
    }

    public SequenceContinuitySnapshot GetSnapshot() => new(
        ReceivedFrames,
        LostFrames,
        GapEvents,
        Anomalies,
        Restarts,
        LastSequence,
        _nominalStep);

    public void Reset()
    {
        _deltaOccurrences.Clear();
        _unclassifiedPositiveDeltas.Clear();
        _hasPrevious = false;
        _previous = 0;
        _nominalStep = null;
        ReceivedFrames = 0;
        LostFrames = 0;
        GapEvents = 0;
        Anomalies = 0;
        Restarts = 0;
        LastSequence = -1;
    }

    private void ClassifyPositiveDelta(long delta, long step)
    {
        if (delta == step) return;

        if (delta > step && delta % step == 0)
        {
            LostFrames += delta / step - 1;
            GapEvents++;
            return;
        }

        // A duplicate, fractional jump, corrupt value, or a change in the
        // producer's SEQ convention is diagnostic evidence, not proven loss.
        Anomalies++;
    }
}

public readonly record struct SequenceContinuitySnapshot(
    long ReceivedFrames,
    long LostFrames,
    long GapEvents,
    long Anomalies,
    long Restarts,
    long LastSequence,
    long? NominalStep)
{
    public bool IsStepConfirmed => NominalStep.HasValue;
    public long ExpectedFrames => ReceivedFrames + LostFrames;
    public double LossRatePercent => ExpectedFrames == 0
        ? 0
        : LostFrames * 100.0 / ExpectedFrames;
}
