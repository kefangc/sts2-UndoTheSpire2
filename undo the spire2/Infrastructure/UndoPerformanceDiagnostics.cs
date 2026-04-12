using System.Text;

namespace UndoTheSpire2;

internal readonly record struct UndoPerformanceSnapshot(
    long PacketCacheHit,
    long PacketCacheMiss,
    long CardLocationFallbackCount,
    long HistoryPrefixHit,
    long HistoryPrefixMiss,
    long HandLayoutRefreshCount,
    long CombatUiNotifyCount,
    long IntentRefreshCount);

internal static class UndoPerformanceDiagnostics
{
    private static long _packetCacheHit;
    private static long _packetCacheMiss;
    private static long _cardLocationFallbackCount;
    private static long _historyPrefixHit;
    private static long _historyPrefixMiss;
    private static long _handLayoutRefreshCount;
    private static long _combatUiNotifyCount;
    private static long _intentRefreshCount;

    public static void RecordPacketCacheHit()
    {
        Interlocked.Increment(ref _packetCacheHit);
    }

    public static void RecordPacketCacheMiss()
    {
        Interlocked.Increment(ref _packetCacheMiss);
    }

    public static void RecordCardLocationFallback()
    {
        Interlocked.Increment(ref _cardLocationFallbackCount);
    }

    public static void RecordHistoryPrefixHit()
    {
        Interlocked.Increment(ref _historyPrefixHit);
    }

    public static void RecordHistoryPrefixMiss()
    {
        Interlocked.Increment(ref _historyPrefixMiss);
    }

    public static void RecordHandLayoutRefresh(int count = 1)
    {
        if (count <= 0)
            return;

        Interlocked.Add(ref _handLayoutRefreshCount, count);
    }

    public static void RecordCombatUiNotify()
    {
        Interlocked.Increment(ref _combatUiNotifyCount);
    }

    public static void RecordIntentRefresh()
    {
        Interlocked.Increment(ref _intentRefreshCount);
    }

    public static UndoPerformanceSnapshot CaptureSnapshot()
    {
        return new UndoPerformanceSnapshot(
            Volatile.Read(ref _packetCacheHit),
            Volatile.Read(ref _packetCacheMiss),
            Volatile.Read(ref _cardLocationFallbackCount),
            Volatile.Read(ref _historyPrefixHit),
            Volatile.Read(ref _historyPrefixMiss),
            Volatile.Read(ref _handLayoutRefreshCount),
            Volatile.Read(ref _combatUiNotifyCount),
            Volatile.Read(ref _intentRefreshCount));
    }

    public static string FormatDelta(UndoPerformanceSnapshot baseline)
    {
        UndoPerformanceSnapshot current = CaptureSnapshot();
        StringBuilder builder = new();
        AppendMetric(builder, "packet_cache_hit", current.PacketCacheHit - baseline.PacketCacheHit);
        AppendMetric(builder, "packet_cache_miss", current.PacketCacheMiss - baseline.PacketCacheMiss);
        AppendMetric(builder, "card_location_fallback_count", current.CardLocationFallbackCount - baseline.CardLocationFallbackCount);
        AppendMetric(builder, "history_prefix_hit", current.HistoryPrefixHit - baseline.HistoryPrefixHit);
        AppendMetric(builder, "history_prefix_miss", current.HistoryPrefixMiss - baseline.HistoryPrefixMiss);
        AppendMetric(builder, "hand_layout_refresh_count", current.HandLayoutRefreshCount - baseline.HandLayoutRefreshCount);
        AppendMetric(builder, "combat_ui_notify_count", current.CombatUiNotifyCount - baseline.CombatUiNotifyCount);
        AppendMetric(builder, "intent_refresh_count", current.IntentRefreshCount - baseline.IntentRefreshCount);
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string name, long value)
    {
        if (builder.Length > 0)
            builder.Append(' ');

        builder.Append(name);
        builder.Append('=');
        builder.Append(value);
    }
}
