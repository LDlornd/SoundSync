using System;

namespace SoundSync.Services;

/// <summary>UI-thread retry scheduling; never infers a failure from audio silence.</summary>
internal sealed class ReconnectController(
    Func<bool> tryConnect, Action<TimeSpan> schedule, Action stopTimer, Action<int, TimeSpan> showPending)
{
    internal bool WantsConnection { get; private set; }
    internal bool IsPending { get; private set; }
    internal int Attempts { get; private set; }

    internal void RequestConnection() => WantsConnection = true;

    internal void ConnectionSucceeded()
    {
        WantsConnection = true;
        StopRetries();
    }

    internal void StopRetries()
    {
        IsPending = false;
        Attempts = 0;
        stopTimer();
    }

    internal void Cancel()
    {
        WantsConnection = false;
        StopRetries();
    }

    internal void Retry(bool enabled)
    {
        if (!enabled || !WantsConnection || IsPending) return;
        var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, Math.Min(Attempts, 4)), 30));
        IsPending = true;
        schedule(delay);
        showPending(Attempts + 1, delay);
    }

    internal void Tick(bool enabled)
    {
        if (!IsPending) return;
        IsPending = false;
        stopTimer();
        if (!enabled || !WantsConnection) return;
        Attempts++;
        if (tryConnect()) ConnectionSucceeded();
        else Retry(enabled);
    }
}
