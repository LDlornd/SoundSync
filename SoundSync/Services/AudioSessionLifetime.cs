using System.Threading;

namespace SoundSync.Services;

/// <summary>Invalidated before stopping streams, so their final notifications are harmless.</summary>
internal sealed class AudioSessionLifetime
{
    private int state; // 0 running/starting, 1 fault reported, 2 deliberately ended
    internal bool IsActive => Volatile.Read(ref state) == 0;
    internal bool TryReportFault() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
    internal void End() => Interlocked.Exchange(ref state, 2);
}
