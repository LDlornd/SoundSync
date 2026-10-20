using NAudio.Wave;
using SoundSync.Models;
using SoundSync.Services;
using System.Reflection;
using Xunit;

namespace SoundSync.Tests;

public class ReconnectTests
{
    [Fact]
    public void IntentionalDisconnect_InvalidatesSessionBeforeCaptureEmitsStopped()
    {
        var engine = new AudioEngine();
        var capture = new MemoryCapture();
        int faults = 0;
        engine.ObserveCaptureStop(capture, engine.BeginSession(), _ => { }, _ => faults++);
        typeof(AudioEngine).GetField("loopbackCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, capture);
        engine.Disconnect(new List<DeviceItem>());
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(0, faults);
    }

    [Fact]
    public void OldDelayedStop_CannotAffectReplacementSession()
    {
        var engine = new AudioEngine();
        var old = new MemoryCapture();
        var replacement = new MemoryCapture();
        var errors = new List<string>();
        engine.ObserveCaptureStop(old, engine.BeginSession(), _ => { }, errors.Add);
        engine.Disconnect(new List<DeviceItem>());
        engine.ObserveCaptureStop(replacement, engine.BeginSession(), _ => { }, errors.Add);
        old.EmitStop(new Exception("stale"));
        Assert.Empty(errors);
        replacement.EmitStop(new Exception("current failure"));
        Assert.Single(errors);
    }

    [Fact]
    public void CaptureAndOutputFailTogether_OnlyOneRetryRequestIsRaised()
    {
        var engine = new AudioEngine();
        var capture = new MemoryCapture();
        var output = new MemoryOutput();
        var session = engine.BeginSession();
        int faults = 0;
        engine.ObserveCaptureStop(capture, session, _ => { }, _ => faults++);
        engine.ObserveOutputStop(output, "headset", session, _ => { }, _ => faults++);
        output.Stop();
        capture.EmitStop();
        output.Stop();
        Assert.Equal(1, faults);
    }

    [Fact]
    public void UnexpectedStopWithoutDeviceNotification_StartsRetryAndEventuallyRecovers()
    {
        var delays = new List<double>();
        int attempts = 0;
        var controller = new ReconnectController(() => ++attempts == 6,
            delay => delays.Add(delay.TotalSeconds), () => { }, (_, _) => { });
        var engine = new AudioEngine();
        var capture = new MemoryCapture();
        controller.RequestConnection();
        engine.ObserveCaptureStop(capture, engine.BeginSession(), _ => { }, _ => controller.Retry(true));
        capture.EmitStop(); // No environment watcher or device-change event is involved.
        for (int i = 0; i < 6; i++) controller.Tick(true);
        Assert.Equal(new double[] { 2, 4, 8, 16, 30, 30 }, delays);
        Assert.Equal(6, attempts);
        Assert.False(controller.IsPending);
        Assert.True(controller.WantsConnection);
        Assert.Equal(0, controller.Attempts);
    }

    [Fact]
    public void ManualStop_CancelsAlreadyQueuedRetry()
    {
        int calls = 0;
        var controller = new ReconnectController(() => { calls++; return true; }, _ => { }, () => { }, (_, _) => { });
        controller.RequestConnection();
        controller.Retry(true);
        controller.Cancel();
        controller.Tick(true);
        controller.Retry(true);
        Assert.Equal(0, calls);
        Assert.False(controller.WantsConnection);
        Assert.False(controller.IsPending);
    }

    [Fact]
    public void DisabledReconnect_DoesNotConnect_AndCanBeEnabledAgain()
    {
        int calls = 0;
        var controller = new ReconnectController(() => { calls++; return true; }, _ => { }, () => { }, (_, _) => { });
        controller.RequestConnection();
        controller.Retry(false);
        controller.Tick(false);
        Assert.Equal(0, calls);
        controller.Retry(true);
        controller.Tick(false); // A timer queued before the setting changed must be harmless.
        Assert.Equal(0, calls);
        controller.Retry(true);
        controller.Tick(true);
        Assert.Equal(1, calls);
        Assert.False(controller.IsPending);
    }

    [Fact]
    public void DuplicateFaults_DoNotPostponeOrDuplicatePendingRetry()
    {
        int timers = 0;
        var controller = new ReconnectController(() => true, _ => timers++, () => { }, (_, _) => { });
        controller.RequestConnection();
        controller.Retry(true);
        controller.Retry(true);
        controller.Retry(true);
        Assert.Equal(1, timers);
    }

    private sealed class MemoryCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public int StopCalls { get; private set; }
        public void StartRecording() { }
        public void EmitStop(Exception? error = null) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void StopRecording() { StopCalls++; EmitStop(); }
        public void Dispose() { }
    }

    private sealed class MemoryOutput : IWavePlayer
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState => PlaybackState.Playing;
        public float Volume { get; set; } = 1;
        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public void Init(IWaveProvider waveProvider) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        public void Dispose() { }
    }
}
