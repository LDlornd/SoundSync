using NAudio.Wave;
using SoundSync.Models;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests;

public class SoftwareVolumeTests
{
    // No MMDevice is supplied in these tests. The actual DSP path must work without
    // touching Windows device volume, mute, or any hardware playback endpoint.
    private sealed class ConstantSignal : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, 0.8f, offset, count);
            return count;
        }
    }

    private static void AssertSignal(ISampleProvider pipeline, float expected)
    {
        float[] buffer = new float[256];
        Assert.Equal(buffer.Length, pipeline.Read(buffer, 0, buffer.Length));
        Assert.All(buffer, sample => Assert.Equal(expected, sample, precision: 5));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.25f, 0.2f)]
    [InlineData(1f, 0.8f)]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 0.8f)]
    public void SoftwareVolume_ScalesActualOutputAndClampsToSupportedRange(float volume, float expected)
    {
        var item = new DeviceItem { SoftwareVolume = volume };
        var pipeline = new AudioEngine().CreateOutputPipeline(item, new ConstantSignal());
        AssertSignal(pipeline, expected);
    }

    [Fact]
    public void Outputs_HaveIndependentSoftwareGain()
    {
        var engine = new AudioEngine();
        var first = new DeviceItem { SoftwareVolume = 0.25f };
        var second = new DeviceItem { SoftwareVolume = 0.75f };
        var firstPipeline = engine.CreateOutputPipeline(first, new ConstantSignal());
        var secondPipeline = engine.CreateOutputPipeline(second, new ConstantSignal());
        first.SoftwareVolume = 0.5f;
        AssertSignal(firstPipeline, 0.4f);
        AssertSignal(secondPipeline, 0.6f);
        Assert.Equal(0.8f, first.PeakLevel, precision: 5);
    }

    [Fact]
    public void ChangingGainWhileMuted_KeepsSilenceUntilUnmuted()
    {
        var item = new DeviceItem { SoftwareVolume = 0.5f };
        var pipeline = new AudioEngine().CreateOutputPipeline(item, new ConstantSignal());
        item.IsSoftwareMuted = true;
        item.SoftwareVolume = 0.35f;
        AssertSignal(pipeline, 0f);
        item.IsSoftwareMuted = false;
        AssertSignal(pipeline, 0.28f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceVolumeCompensation_DoesNotResetSoftwareGainOrMute(bool compensation)
    {
        var engine = new AudioEngine();
        var item = new DeviceItem { SoftwareVolume = 0.3f, IsSoftwareMuted = true };
        var pipeline = engine.CreateOutputPipeline(item, new ConstantSignal());
        engine.ApplyMakeUpGain(new List<DeviceItem> { item }, 0.2f, compensation);
        AssertSignal(pipeline, 0f);
        item.IsSoftwareMuted = false;
        AssertSignal(pipeline, 0.24f);
    }

    [Fact]
    public void RebuildingOutput_RestoresSavedGainAndClearsTransientMuteOnDisconnect()
    {
        var engine = new AudioEngine();
        var item = new DeviceItem { SoftwareVolume = 0.4f };
        engine.CreateOutputPipeline(item, new ConstantSignal());
        item.IsSoftwareMuted = true;
        engine.Disconnect(new List<DeviceItem> { item });
        Assert.False(item.IsSoftwareMuted);
        Assert.Null(item.VolumeProvider);
        var pipeline = engine.CreateOutputPipeline(item, new ConstantSignal());
        AssertSignal(pipeline, 0.32f);
    }

    [Fact]
    public void GainChange_AffectsAudioAlreadyQueuedInDelayBuffer()
    {
        var engine = new AudioEngine();
        var item = new DeviceItem { Delay = 100 };
        var pipeline = engine.CreateOutputPipeline(item, new ConstantSignal());
        engine.UpdateRelativeDelays(new List<DeviceItem> { item });
        float[] initial = new float[6000];
        pipeline.Read(initial, 0, initial.Length);
        Assert.Equal(0.8f, initial[^1], precision: 5);
        item.SoftwareVolume = 0f;
        AssertSignal(pipeline, 0f);
        item.SoftwareVolume = 0.5f;
        AssertSignal(pipeline, 0.4f);
    }
}
