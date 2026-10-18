using NAudio.Wave;
using SoundSync.Models;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests;

public class SignalMeterTests
{
    private static byte[] Tone(float amplitude) => Enumerable.Repeat(amplitude, 64)
        .SelectMany(v => BitConverter.GetBytes(v)).ToArray();

    private static float ReadOutput(AudioEngine engine, byte[] samples, int count, float expected, float softwareVolume = 1f)
    {
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        buffer.AddSamples(samples, 0, count);
        var item = new DeviceItem { SoftwareVolume = softwareVolume };
        var pipeline = engine.CreateOutputPipeline(item, buffer.ToSampleProvider());
        float[] result = new float[count / 4];
        Assert.Equal(result.Length, pipeline.Read(result, 0, result.Length));
        Assert.All(result, value => Assert.Equal(expected, value, precision: 5));
        return item.PeakLevel;
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(8f)]
    public void CompensationOff_UsesActualSignalAndDoesNotQueryAnInverseVolumeGain(float reportedGain)
    {
        int reads = 0;
        var engine = new AudioEngine(_ => { reads++; return reportedGain; });
        engine.ApplyMakeUpGain(new List<DeviceItem>(), 0.39f, enabled: false);
        byte[] captured = Tone(0.1f);
        byte[] scratch = Array.Empty<byte>();
        byte[] audio = engine.PrepareCapturedAudio(captured, captured.Length, true, ref scratch);

        Assert.Equal(0, reads);
        Assert.Same(captured, audio);
        Assert.Equal(0.1f, engine.SourcePeakLevel, precision: 5);
        Assert.Equal(0.1f, ReadOutput(engine, audio, captured.Length, 0.1f), precision: 5);
    }

    [Fact]
    public void UnattenuatedVirtualCapture_SystemVolumeChangeCannotMakeMetersStrongerWhenCompensationIsOff()
    {
        float reportedGain = 1f;
        var engine = new AudioEngine(_ => reportedGain);
        byte[] captured = Tone(0.1f);
        byte[] scratch = Array.Empty<byte>();
        engine.ApplyMakeUpGain(new List<DeviceItem>(), 1f, enabled: false);
        engine.PrepareCapturedAudio(captured, captured.Length, true, ref scratch);
        float atFullVolume = engine.SourcePeakLevel;

        reportedGain = 8f;
        engine.ApplyMakeUpGain(new List<DeviceItem>(), 0.39f, enabled: false);
        byte[] audio = engine.PrepareCapturedAudio(captured, captured.Length, true, ref scratch);
        Assert.Equal(atFullVolume, engine.SourcePeakLevel);
        Assert.Equal(0.1f, ReadOutput(engine, audio, captured.Length, 0.1f), precision: 5);
    }

    [Fact]
    public void CompensationOn_SourceMeterStaysRawAndOutputMeterReflectsActualCompensatedSamplesOnce()
    {
        var engine = new AudioEngine(_ => 6f);
        engine.ApplyMakeUpGain(new List<DeviceItem>(), 0.39f, enabled: true);
        byte[] captured = Tone(0.1f);
        byte[] scratch = Array.Empty<byte>();
        byte[] audio = engine.PrepareCapturedAudio(captured, captured.Length, true, ref scratch);
        Assert.Equal(0.1f, engine.SourcePeakLevel, precision: 5);
        Assert.Equal(0.6f, ReadOutput(engine, audio, captured.Length, 0.3f, softwareVolume: 0.5f), precision: 5);
        Assert.Equal(0.1f, BitConverter.ToSingle(captured, 0), precision: 5);
    }

    [Fact]
    public void SourceMeter_FollowsRealAttenuationInsteadOfCorrectingItAway()
    {
        var engine = new AudioEngine(_ => 8f);
        engine.ApplyMakeUpGain(new List<DeviceItem>(), 0.1f, enabled: false);
        byte[] scratch = Array.Empty<byte>();
        byte[] full = Tone(0.1f);
        engine.PrepareCapturedAudio(full, full.Length, true, ref scratch);
        byte[] reduced = Tone(0.03f);
        engine.PrepareCapturedAudio(reduced, reduced.Length, true, ref scratch);
        Assert.Equal(0.03f, engine.SourcePeakLevel, precision: 5);
    }

    [Fact]
    public void SourceMeter_IgnoresUnusedCaptureBufferCapacity()
    {
        var engine = new AudioEngine(_ => 1f);
        byte[] scratch = Array.Empty<byte>();
        byte[] captured = Tone(0.1f).Concat(Tone(0.9f)).ToArray();
        engine.PrepareCapturedAudio(captured, captured.Length / 2, true, ref scratch);
        Assert.Equal(0.1f, engine.SourcePeakLevel, precision: 5);
    }
}
