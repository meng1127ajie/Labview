namespace V3RttMonitor.Core.CanBus;

public sealed record DbcSourceDatabase(string Name, string Path, DbcDatabase Database);

public sealed record DbcMergeConflict(CanFrameKey Key, string PreviousSource, string WinningSource, string MessageName);

public sealed record DbcMergeResult
{
    public required DbcDatabase Database { get; init; }
    public IReadOnlyList<DbcMergeConflict> Conflicts { get; init; } = [];
}

public static class DbcDatabaseMerger
{
    /// <summary>
    /// Merges databases in priority order. Later sources replace earlier definitions
    /// for the same standard/extended frame key, and every replacement is reported.
    /// </summary>
    public static DbcMergeResult Merge(IEnumerable<DbcSourceDatabase> sources)
    {
        var sourceList = sources.ToArray();
        var merged = new DbcDatabase { Name = sourceList.Length == 0 ? "CAN_Database" : string.Join("_", sourceList.Select(item => DbcParser.SanitizeName(item.Name))) };
        var conflicts = new List<DbcMergeConflict>();
        var winners = new Dictionary<CanFrameKey, (DbcMessage Message, string Source)>();
        var order = new List<CanFrameKey>();

        foreach (var source in sourceList)
        {
            foreach (var message in source.Database.Messages)
            {
                if (winners.TryGetValue(message.Key, out var previous))
                {
                    conflicts.Add(new(message.Key, previous.Source, source.Name, message.Name));
                }
                else
                {
                    order.Add(message.Key);
                }
                winners[message.Key] = (message, source.Name);
            }
        }

        foreach (var key in order)
        {
            if (winners.TryGetValue(key, out var winner)) merged.Messages.Add(winner.Message);
        }
        return new DbcMergeResult { Database = merged, Conflicts = conflicts };
    }
}
