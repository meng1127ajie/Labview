namespace V3RttMonitor.Core.CanBus;

public static class CanLogMerger
{
    public static IReadOnlyList<CanFrame> Merge(
        IReadOnlyList<CanFrame> existing,
        IEnumerable<CanLogSegment> newSegments,
        CanLogMergeMode mode,
        double gapSeconds = 1.0)
    {
        var output = mode == CanLogMergeMode.Replace ? new List<CanFrame>() : existing.ToList();
        var nextSegmentIndex = output.Select(frame => frame.SegmentIndex).DefaultIfEmpty(-1).Max() + 1;
        var currentEnd = output.Select(frame => frame.TimestampSeconds).DefaultIfEmpty(0).Max();

        foreach (var segment in newSegments)
        {
            if (segment.Frames.Count == 0) continue;
            var ordered = segment.Frames.OrderBy(frame => frame.TimestampSeconds).ToArray();
            var segmentStart = ordered[0].TimestampSeconds;
            var offset = mode switch
            {
                CanLogMergeMode.AppendContinuous => currentEnd + EstimateStep(ordered) - segmentStart,
                CanLogMergeMode.AppendWithGap => currentEnd + Math.Max(0, gapSeconds) - segmentStart,
                _ => 0,
            };

            foreach (var frame in ordered)
            {
                output.Add(frame with
                {
                    TimestampSeconds = frame.TimestampSeconds + offset,
                    SegmentIndex = nextSegmentIndex,
                    SourceName = segment.Name,
                });
            }
            currentEnd = output.Max(frame => frame.TimestampSeconds);
            nextSegmentIndex++;
        }

        if (mode == CanLogMergeMode.PreserveOriginalTime)
        {
            output.Sort((left, right) => left.TimestampSeconds.CompareTo(right.TimestampSeconds));
        }
        return output;
    }

    private static double EstimateStep(IReadOnlyList<CanFrame> frames)
    {
        var deltas = new List<double>();
        for (var index = 1; index < Math.Min(frames.Count, 2_000); index++)
        {
            var delta = frames[index].TimestampSeconds - frames[index - 1].TimestampSeconds;
            if (delta > 0 && double.IsFinite(delta)) deltas.Add(delta);
        }
        if (deltas.Count == 0) return 1e-6;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }
}
