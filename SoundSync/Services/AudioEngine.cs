using SoundSync.Models;
using SoundSync.Services.Providers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;

using MeteringSampleProvider = SoundSync.Services.Providers.MeteringSampleProvider;

namespace SoundSync.Services
{
    public class AudioEngine : IAudioEngine
    {
        private MMDeviceEnumerator? enumerator;
        private IWaveIn? loopbackCapture;

        /// <summary>True when the source is captured before the output device's volume.</summary>
        private volatile bool usingPreVolumeCapture;

        /// <summary>Whether this session is reading the signal before any volume is applied.</summary>
        public bool IsPreVolumeCapture => usingPreVolumeCapture;
        private readonly List<WasapiOut> outputStreams = new List<WasapiOut>();
        private readonly List<BufferedWaveProvider> buffers = new List<BufferedWaveProvider>();
        private volatile bool isConnected = false;
        private volatile AudioSessionLifetime? sessionLifetime;

        public bool IsConnected => isConnected;

        private readonly Func<MMDevice?, float> readEndpointGain;

        public AudioEngine() : this(MakeUpGainForDevice) { }

        internal AudioEngine(Func<MMDevice?, float> readEndpointGain)
        {
            this.readEndpointGain = readEndpointGain;
        }

        public List<MMDevice> GetActiveRenderDevices()
        {
            enumerator ??= new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        public void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action<string> onDisconnectedCallback)
        {
            if (isConnected) return;
            var session = BeginSession();

            try
            {
                enumerator ??= new MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                captureSource = defaultDevice;

                // Prefer capturing the render streams themselves rather than the output
                // device. Whether an endpoint applies its volume before the loopback tap
                // turns out to depend on the device - measured here, an HDMI output scaled
                // with the slider while a USB headset did not - so compensating for it means
                // guessing, and guessing wrong means amplifying a signal that was never
                // attenuated. Capturing before the endpoint removes the question.
                usingPreVolumeCapture = false;
                if (ProcessLoopbackCapture.IsSupported)
                {
                    try
                    {
                        var mix = defaultDevice.AudioClient.MixFormat;
                        var preVolume = new ProcessLoopbackCapture(mix.SampleRate, mix.Channels);
                        preVolume.StartRecording();
                        preVolume.StopRecording();      // prove it activates before committing
                        preVolume.Dispose();

                        loopbackCapture = new ProcessLoopbackCapture(mix.SampleRate, mix.Channels);
                        usingPreVolumeCapture = true;
                    }
                    catch (Exception ex)
                    {
                        logCallback("已改用输出设备捕获：" + ex.Message);
                    }
                }

                loopbackCapture ??= new WasapiLoopbackCapture();
                var captureFormat = loopbackCapture.WaveFormat;

                var failures = new List<string>();

                // The caller normally passes an already-filtered list, but honour IsSelected
                // here too: an unticked device must never be opened, whoever calls this.
                foreach (var deviceItem in selectedDevices.Where(d => d.IsSelected))
                {
                    var device = deviceItem.Device;
                    if (device.ID == defaultDevice.ID)
                    {
                        continue;
                    }

                    WasapiOut? wasapiOut = null;
                    try
                    {
                    wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                    var buffer = new BufferedWaveProvider(captureFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(100),
                        DiscardOnBufferOverflow = true
                    };

                    // Convert to exactly what this endpoint expects before anything else
                    // touches the stream, so WASAPI is never asked to adapt a layout it
                    // cannot handle. See FormatAdapter for why the order matters.
                    WaveFormat outputFormat = GetOutputFormat(device, captureFormat);
                    ISampleProvider sampleProvider = FormatAdapter.Adapt(
                        buffer.ToSampleProvider(), outputFormat);

                    var outputPipeline = CreateOutputPipeline(deviceItem, sampleProvider);
                    wasapiOut.Init(FormatAdapter.AdaptToWaveProvider(outputPipeline, outputFormat));
                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    ObserveOutputStop(wasapiOut, deviceItem.Name, session, logCallback, onDisconnectedCallback);
                    if (wasapiOut.PlaybackState != PlaybackState.Playing)
                        ReportUnexpectedStop(session, deviceItem.Name + " 的播放流未能保持运行。", logCallback, onDisconnectedCallback);
                    buffers.Add(buffer);
                    deviceItem.OutputBuffer = buffer;
                    deviceItem.EndpointLatencyMs = 30;
                    }
                    catch (Exception deviceError)
                    {
                        // One unusable output must not take the whole session down: drop
                        // it, remember why, and keep mirroring to the rest.
                        try { wasapiOut?.Dispose(); } catch { }
                        deviceItem.VolumeProvider = null;
                        deviceItem.EqualizerProvider = null;
                        deviceItem.DelayProvider = null;
                        deviceItem.OutputBuffer = null;
                        string reason = AudioErrors.Explain(deviceError, deviceItem.Name, captureFormat, device);
                        failures.Add(reason);
                        logCallback(reason);
                    }
                }

                if (outputStreams.Count == 0)
                {
                    throw new InvalidOperationException(failures.Count > 0
                        ? "没有可启动的输出设备。\n\n" + string.Join("\n\n", failures)
                        : "请至少勾选一个非默认设备。默认设备是音频来源，不能同时作为转发目标。");
                }

                if (failures.Count > 0)
                {
                    logCallback($"正在向 {outputStreams.Count} 个设备转发音频；已跳过 {failures.Count} 个设备。");
                }

                UpdateRelativeDelays(selectedDevices);

                // The phone stream is a bonus, not the job. If its port is taken - another
                // copy of SoundSync, or anything else on 8090 - mirroring must still run.
                try
                {
                    networkStreamer.Start(captureFormat.SampleRate, captureFormat.Channels, 8090);
                }
                catch (Exception streamError)
                {
                    logCallback("本机音频转发已启动，但手机收听服务启动失败：" +
                                streamError.Message +
                                " 另一个 SoundSync 或其他程序正在使用 8090 端口。");
                }

                bool captureIsFloat = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat
                                      && captureFormat.BitsPerSample == 32;

                // Scratch space for the distribution layer, reused so the capture callback
                // does not allocate on every packet.
                byte[] distribution = Array.Empty<byte>();

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    if (!ReferenceEquals(sessionLifetime, session) || !session.IsActive) return;
                    // ---- layer 2: distribution -------------------------------------
                    //
                    // Keep the source meter raw. Optional endpoint-volume compensation is
                    // applied only to the copy distributed to outputs and network listeners.
                    // Whether endpoint loopback includes that attenuation depends on the
                    // device, so its Windows volume must not be used to infer meter levels.
                    //
                    // Per-consumer volume, equaliser and delay come after this, in layer 3,
                    // so no consumer's settings can leak into another's.
                    int sourceBytes = args.BytesRecorded;
                    byte[] source = PrepareCapturedAudio(args.Buffer, sourceBytes, captureIsFloat, ref distribution);
                    networkStreamer.BroadcastAudio(source, sourceBytes);

                    const int TargetBufferDurationMs = 30;
                    int targetBytes = (int)((TargetBufferDurationMs * captureFormat.AverageBytesPerSecond) / 1000.0);
                    targetBytes -= targetBytes % captureFormat.BlockAlign;

                    int toleranceBytes = (int)((10 * captureFormat.AverageBytesPerSecond) / 1000.0);
                    toleranceBytes -= toleranceBytes % captureFormat.BlockAlign;

                    foreach (var buffer in buffers)
                    {
                        int currentBytes = buffer.BufferedBytes;
                        if (currentBytes > targetBytes + toleranceBytes)
                        {
                            int bytesToDiscard = currentBytes - targetBytes;
                            bytesToDiscard -= bytesToDiscard % captureFormat.BlockAlign;
                            if (bytesToDiscard > 0)
                            {
                                byte[] temp = new byte[bytesToDiscard];
                                buffer.Read(temp, 0, bytesToDiscard);
                            }
                        }
                        else if (currentBytes < targetBytes - toleranceBytes)
                        {
                            int bytesToPad = targetBytes - currentBytes;
                            bytesToPad -= bytesToPad % captureFormat.BlockAlign;
                            if (bytesToPad > 0)
                            {
                                byte[] silence = new byte[bytesToPad];
                                buffer.AddSamples(silence, 0, bytesToPad);
                            }
                        }
                        buffer.AddSamples(source, 0, sourceBytes);
                    }
                };
                // Windows kills the capture when the source endpoint is reconfigured or
                // unplugged. Without this the app sits there believing it is still mirroring
                // while every output has gone quiet.
                ObserveCaptureStop(loopbackCapture, session, logCallback, onDisconnectedCallback);

                loopbackCapture.StartRecording();
                if (!session.IsActive) throw new InvalidOperationException("音频流在启动期间已停止。");
                isConnected = true;
            }
            catch (Exception ex)
            {
                Disconnect(selectedDevices);
                throw new InvalidOperationException(
                    ex is InvalidOperationException ? ex.Message
                    : "无法启动音频转发。\n\n" + AudioErrors.Describe(ex, "音频引擎"), ex);
            }
        }

        internal AudioSessionLifetime BeginSession()
        {
            sessionLifetime?.End();
            return sessionLifetime = new AudioSessionLifetime();
        }

        internal void ObserveCaptureStop(IWaveIn capture, AudioSessionLifetime session,
            Action<string> log, Action<string> stopped)
        {
            capture.RecordingStopped += (_, e) => ReportUnexpectedStop(session,
                e.Exception == null ? "来源设备的捕获流意外停止。" : AudioErrors.Describe(e.Exception, "捕获设备"),
                log, stopped);
        }

        internal void ObserveOutputStop(IWavePlayer output, string name, AudioSessionLifetime session,
            Action<string> log, Action<string> stopped)
        {
            output.PlaybackStopped += (_, e) => ReportUnexpectedStop(session,
                e.Exception == null ? name + " 的播放流意外停止。" : AudioErrors.Describe(e.Exception, name),
                log, stopped);
        }

        private void ReportUnexpectedStop(AudioSessionLifetime session, string reason,
            Action<string> log, Action<string> stopped)
        {
            if (!ReferenceEquals(sessionLifetime, session) || !session.TryReportFault()) return;
            log("音频转发意外停止：" + reason);
            stopped(reason);
        }

        /// <summary>Measures actual captured samples, then optionally compensates audio for distribution.</summary>
        internal byte[] PrepareCapturedAudio(byte[] captured, int count, bool isFloat, ref byte[] distribution)
        {
            RefreshDistributionGain();
            if (!isFloat) return captured;

            // A device's volume setting does not tell us where its loopback tap sits.
            // In particular, virtual endpoints can keep capture at full strength while
            // their Windows volume changes. Never divide a meter by that setting.
            float peak = 0f;
            for (int i = 0; i + 3 < count; i += 4)
            {
                float level = Math.Abs(BitConverter.ToSingle(captured, i));
                if (level > peak) peak = level;
            }
            SourcePeakLevel = Math.Clamp(peak, 0f, 1f);

            float gain = distributionGain;
            if (Math.Abs(gain - 1.0f) <= 0.001f) return captured;
            if (distribution.Length < count) distribution = new byte[count];
            for (int i = 0; i + 3 < count; i += 4)
            {
                float value = BitConverter.ToSingle(captured, i) * gain;
                BitConverter.TryWriteBytes(distribution.AsSpan(i, 4), SoftLimit(value));
            }
            return distribution;
        }

        /// <summary>Builds the per-output DSP path without opening or changing any Windows device.</summary>
        internal ISampleProvider CreateOutputPipeline(DeviceItem item, ISampleProvider source)
        {
            // Meter before this output's processing, so its gain does not hide incoming audio.
            var meter = new MeteringSampleProvider(source, peak =>
                item.PeakLevel = Math.Clamp(peak, 0f, 1f));
            var equalizer = new EqualizerSampleProvider(meter)
            {
                BassDb = item.Bass,
                MidDb = item.Mid,
                TrebleDb = item.Treble
            };
            item.EqualizerProvider = equalizer;
            var delay = new DelaySampleProvider(equalizer) { DelayMilliseconds = 0 };
            item.DelayProvider = delay;

            // Gain goes LAST: muting or adjusting volume also affects audio already queued
            // in the delay stage, instead of waiting up to 500 ms for that queue to drain.
            var volume = new VolumeSampleProvider(delay);
            item.VolumeProvider = volume;
            return volume;
        }

        /// <summary>Format this endpoint expects, falling back to the captured one.</summary>
        private static WaveFormat GetOutputFormat(MMDevice device, WaveFormat fallback)
        {
            try { return device.AudioClient.MixFormat; }
            catch { return fallback; }
        }

        public void Disconnect(List<DeviceItem> activeDevices)
        {
            // End the session BEFORE StopRecording/Stop emits final events.
            sessionLifetime?.End();
            sessionLifetime = null;
            isConnected = false;
            if (loopbackCapture != null)
            {
                try { loopbackCapture.StopRecording(); } catch { }
                try { loopbackCapture.Dispose(); } catch { }
                loopbackCapture = null;
            }

            foreach (var stream in outputStreams)
            {
                try { stream.Stop(); } catch { }
                try { stream.Dispose(); } catch { }
            }
            outputStreams.Clear();
            buffers.Clear();

            foreach (var item in activeDevices)
            {
                item.VolumeProvider = null;
                item.IsSoftwareMuted = false;
                item.EqualizerProvider = null;
                item.DelayProvider = null;
                item.OutputBuffer = null;
                item.PeakLevel = 0f;
            }

            SourcePeakLevel = 0f;
            captureSource = null;
            usingPreVolumeCapture = false;
            isConnected = false;
        }

        /// <summary>
        /// Highest make-up gain allowed when undoing the default device's volume. At 8x the
        /// signal is already lifted by 18 dB, and going further would mostly raise the noise
        /// floor of a stream that had almost nothing left in it.
        /// </summary>
        private const float MaxMakeUpGain = 8.0f;

        /// <summary>Above this the curve bends instead of the signal being cut off.</summary>
        private const float LimitKnee = 0.85f;

        /// <summary>
        /// Keeps a lifted signal inside range without the hard edge of a clamp.
        ///
        /// Cutting a waveform off flat at 1.0 replaces its peaks with straight lines, which
        /// is heard as harshness and as the level lurching about on loud passages. Below the
        /// knee nothing is touched at all; above it the curve tapers so a peak that would
        /// have overshot lands just under full scale instead of being sliced.
        /// </summary>
        private static float SoftLimit(float sample)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude <= LimitKnee) return sample;

            float over = magnitude - LimitKnee;
            float headroom = 1.0f - LimitKnee;
            float shaped = LimitKnee + headroom * (over / (over + headroom));

            return sample < 0 ? -shaped : shaped;
        }

        /// <summary>
        /// Gain applied once, at the distribution layer, to undo the default device's volume.
        /// Every consumer draws from the result, so no one inherits the master's setting.
        /// </summary>
        private volatile float distributionGain = 1.0f;

        /// <summary>The device being captured, so its level in decibels can be read.</summary>
        private MMDevice? captureSource;

        /// <summary>Whether the master volume should be divided back out at all.</summary>
        private volatile bool makeUpEnabled = true;

        /// <summary>Ticks of Environment.TickCount64 when the level was last read.</summary>
        private long lastGainCheck;

        /// <summary>
        /// Re-reads the source's level and updates the distribution gain.
        ///
        /// The engine does this itself, on a short interval, rather than waiting to be told.
        /// Relying on a volume notification reaching the UI and the UI passing it down left
        /// the correction frozen at whatever it was when mirroring started - so turning the
        /// PC volume down afterwards still dragged every consumer down with it.
        /// </summary>
        private void RefreshDistributionGain()
        {
            long now = Environment.TickCount64;
            if (now - lastGainCheck < 200) return;
            lastGainCheck = now;

            // Nothing to undo when the signal never had the volume applied to it.
            if (usingPreVolumeCapture)
            {
                distributionGain = 1.0f;
                return;
            }

            float wanted = makeUpEnabled ? readEndpointGain(captureSource) : 1.0f;
            distributionGain = wanted;
        }

        /// <summary>Peak of the actual captured source, before any SoundSync audio compensation.</summary>
        public float SourcePeakLevel { get; private set; }

        /// <summary>
        /// Cancels the default device's volume out of the mirrored signal.
        ///
        /// Intended for endpoint loopback paths that include the source device's attenuation.
        /// Other devices may capture before that control; inverse gain then over-amplifies
        /// their audio, so compensation must be disabled for those paths. Signal meters
        /// measure samples directly and never infer attenuation from this control.
        /// </summary>
        public static float MakeUpGainFor(float defaultDeviceVolume)
        {
            if (defaultDeviceVolume <= 0.001f) return 1.0f;   // nothing to recover from silence
            return Math.Clamp(1.0f / defaultDeviceVolume, 1.0f, MaxMakeUpGain);
        }

        /// <summary>
        /// Gain that undoes an endpoint's volume, worked out from the decibel value Windows
        /// reports rather than from the 0..1 scalar.
        ///
        /// The scalar is a perceptual position on the slider, not an amplitude: at 40% Windows
        /// is applying -13.9 dB, which is a factor of 0.202, not 0.4. Measured against a known
        /// tone on this machine, amplitude = 10^(dB/20) predicts the captured level exactly at
        /// every point, while treating the scalar as a factor under-compensates by half.
        /// </summary>
        public static float MakeUpGainForDecibels(float decibels)
        {
            if (float.IsNaN(decibels) || float.IsInfinity(decibels)) return 1.0f;

            double factor = Math.Pow(10.0, decibels / 20.0);
            if (factor <= 0.0001) return 1.0f;                // effectively silent, nothing to lift

            return (float)Math.Clamp(1.0 / factor, 1.0, MaxMakeUpGain);
        }

        /// <summary>Reads the endpoint's level in decibels and returns the gain that undoes it.</summary>
        public static float MakeUpGainForDevice(MMDevice? device)
        {
            if (device == null) return 1.0f;
            try
            {
                if (device.AudioEndpointVolume.Mute) return 1.0f;
                return MakeUpGainForDecibels(device.AudioEndpointVolume.MasterVolumeLevel);
            }
            catch { return 1.0f; }
        }

        /// <summary>Applies the make-up gain to every live output.</summary>
        public void ApplyMakeUpGain(List<DeviceItem> activeDevices, float defaultDeviceVolume, bool enabled)
        {
            // Record the preference; the value itself is re-read from the endpoint's decibel
            // level on a short interval, so it stays right even if nothing tells us again.
            makeUpEnabled = enabled;
            lastGainCheck = 0;
            RefreshDistributionGain();

            // Distribution compensation is upstream of each output's software volume.
            // Leave that gain and its transient mute untouched when the source volume changes.
        }

        public void UpdateRelativeDelays(List<DeviceItem> activeDevices)
        {
            // Each output holds back by its own setting. Subtracting the smallest setting
            // across outputs, as this used to, meant a single mirrored output always came
            // out at zero however far the slider moved - the earliest device is always
            // itself. Delay is additive only: the default device plays straight from
            // Windows and cannot be pulled earlier.
            foreach (var item in activeDevices)
            {
                if (item.DelayProvider == null) continue;
                item.DelayProvider.DelayMilliseconds = Math.Max(0, item.Delay);
            }
        }
    }
}
