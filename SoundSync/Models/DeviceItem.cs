using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoundSync;
using SoundSync.Services.Providers;


namespace SoundSync.Models
{
    public class DeviceItem : INotifyPropertyChanged
    {
        public MMDevice Device { get; set; } = null!;
        public string Name => Device.FriendlyName;
        public bool IsDefaultDevice { get; set; }

        private bool applyingSystemVolume;
        private bool applyingSampleRate;

        /// <summary>Sample rates this endpoint's hardware reports as playable.</summary>
        public List<int> SupportedRates { get; private set; } = new List<int>();

        /// <summary>
        /// Windows' shared-mode sample rate for this endpoint - the "Default Format" of
        /// the Sound control panel. Setting it changes the real system configuration.
        /// </summary>
        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                if (applyingSampleRate || value <= 0 || value == _sampleRate) return;
                applyingSampleRate = true;
                try
                {
                    if (Services.SystemAudioConfig.SetSampleRate(Device, value))
                    {
                        _sampleRate = value;
                        SampleRateStatus = string.Empty;
                        SampleRateTooltip = RateTooltipBase;
                        FormatChangedCallback?.Invoke(this);
                    }
                    else
                    {
                        // Snap back to what Windows actually has. Leaving the rejected value
                        // on screen would claim a change that never happened.
                        _sampleRate = Services.SystemAudioConfig.GetSampleRate(Device);
                        SampleRateStatus = "未成功";
                        SampleRateTooltip = Services.AudioErrors.ExplainRateRejection(Device, value);
                    }
                }
                finally
                {
                    applyingSampleRate = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SampleRateStatus));
                    OnPropertyChanged(nameof(SampleRateTooltip));
                }
            }
        }
        private int _sampleRate;

        /// <summary>Empty when the last rate change succeeded, otherwise a short reason.</summary>
        private string _sampleRateStatus = string.Empty;
        public string SampleRateStatus
        {
            get => _sampleRateStatus;
            private set { _sampleRateStatus = value; OnPropertyChanged(); }
        }

        private const string RateTooltipBase =
            "此设备的 Windows 采样率，与“声音控制面板 > 属性 > 高级 > 默认格式”相同。\n\n列表显示硬件报告支持的采样率。与来源设备保持一致可省去实时重采样，降低延迟和 CPU 占用。\n\n正在播放音频的设备通常无法切换采样率。";

        private string _sampleRateTooltip = RateTooltipBase;
        /// <summary>Explains the picker, or why the last change was refused.</summary>
        public string SampleRateTooltip
        {
            get => _sampleRateTooltip;
            private set { _sampleRateTooltip = value; OnPropertyChanged(); }
        }

        private bool _isHidden;
        /// <summary>Hidden rows stay configured but are filtered out of the list.</summary>
        public bool IsHidden
        {
            get => _isHidden;
            set { _isHidden = value; OnPropertyChanged(); }
        }

        /// <summary>Badge text shown beside the name: SOURCE for the current default.</summary>
        public string DefaultBadge => IsDefaultDevice ? "系统默认" : string.Empty;

        /// <summary>
        /// False for the system default device. Its audio is what gets captured and it plays
        /// straight from Windows, so it never passes through the delay or equaliser stages -
        /// those controls would move without changing a thing.
        /// </summary>
        public bool CanProcessAudio => !IsDefaultDevice;

        private bool _autoAlignDelay;
        /// <summary>
        /// Let the app keep this output's delay matched to the slowest mirrored output,
        /// rechecked every two seconds.
        /// </summary>
        public bool AutoAlignDelay
        {
            get => _autoAlignDelay;
            set { _autoAlignDelay = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEditDelay)); }
        }

        /// <summary>The slider is for hand tuning, so it locks while the app is driving it.</summary>
        public bool CanEditDelay => CanProcessAudio && !AutoAlignDelay;

        private bool _syncVolumeWithDefault;
        /// <summary>Follow the default device's Windows volume, keeping the ratio below.</summary>
        public bool SyncVolumeWithDefault
        {
            get => _syncVolumeWithDefault;
            set { _syncVolumeWithDefault = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// This output's volume divided by the default device's, captured when the sync was
        /// switched on. Keeping the ratio preserves the balance you had set up rather than
        /// slamming every speaker to the same number.
        /// </summary>
        public float VolumeRatioToDefault { get; set; } = 1.0f;

        /// <summary>Sync options only make sense for a ticked, non-default output.</summary>
        public bool ShowSyncOptions => !IsDefaultDevice && IsSelected;

        // ---- how this endpoint is wired up ------------------------------------------
        //
        // Windows already knows, and hands it over in the endpoint property store, so
        // reading it costs one property lookup at load time.

        /// <summary>PKEY_AudioEndpoint_FormFactor.</summary>
        private static readonly PropertyKey FormFactorKey =
            new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

        /// <summary>PKEY_Device_EnumeratorName - "HDAUDIO", "USB", "ROOT" for virtual ones.</summary>
        private static readonly PropertyKey EnumeratorKey =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

        private string _connectionBadge = string.Empty;
        /// <summary>Short label describing how this output is connected.</summary>
        public string ConnectionBadge
        {
            get => _connectionBadge;
            private set { _connectionBadge = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasConnectionBadge)); }
        }

        public bool HasConnectionBadge => ConnectionBadge.Length > 0;

        private string _connectionTooltip = string.Empty;
        public string ConnectionTooltip
        {
            get => _connectionTooltip;
            private set { _connectionTooltip = value; OnPropertyChanged(); }
        }

        /// <summary>Reads the connection type Windows reports for this endpoint.</summary>
        public void LoadConnectionKind()
        {
            int formFactor = -1;
            string enumerator = string.Empty;
            try
            {
                var props = Device.Properties;
                if (props.Contains(FormFactorKey)) formFactor = Convert.ToInt32(props[FormFactorKey].Value);
                if (props.Contains(EnumeratorKey)) enumerator = Convert.ToString(props[EnumeratorKey].Value) ?? string.Empty;
            }
            catch { }

            // EndpointFormFactor: 7 UnknownDigitalPassthrough, 8 SPDIF, 9 DigitalAudioDisplayDevice
            if (formFactor == 9)
            {
                ConnectionBadge = "HDMI 数字 · 较慢";
                ConnectionTooltip =
                    "Windows 将此设备识别为 HDMI 或 DisplayPort 数字显示设备。\n\n线缆本身延迟很低；电视或显示器在信号离开电脑后还会处理音频，通常增加数十毫秒延迟。这部分发生在电脑外，软件无法测量或抵消。\n\n因此它通常是房间里最晚发声的设备。" + NewParagraph + WhySlowestShouldBeDefault;
            }
            else if (formFactor == 8 || formFactor == 7)
            {
                ConnectionBadge = "数字直通";
                ConnectionTooltip =
                    "S/PDIF 或光纤等数字输出。\n\n另一端的功放、回音壁或解码器会引入额外延迟，电脑上的软件无法测量。请将它视作较慢的设备。" + NewParagraph + WhySlowestShouldBeDefault;
            }
            else if (string.Equals(enumerator, "ROOT", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionBadge = "虚拟设备";
                ConnectionTooltip =
                    "由软件创建的虚拟设备，例如串流或音频捕获驱动。\n\n它没有直接连接扬声器，转发到此设备是在给另一个程序提供音频，因此无法按实际发声时间与其对齐。";
            }
            else if (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionBadge = "USB";
                ConnectionTooltip =
                    "USB 音频设备。USB 连接会引入几毫秒的小幅、稳定延迟，通常远小于电视，但略高于声卡上的模拟接口。\n\n通常适合作为转发设备，可添加延时以匹配更慢的设备。" + NewParagraph + WhySlowestShouldBeDefault;
            }
            else if (formFactor >= 0)
            {
                ConnectionBadge = "模拟输出 · 较快";
                ConnectionTooltip =
                    "声卡上的模拟输出，例如扬声器或耳机插孔。\n\n信号经过声卡转换器后直接输出，通常没有后续处理，因此往往最早发声。\n\n适合作为转发设备，可添加延时以匹配更慢的设备。" + NewParagraph + WhySlowestShouldBeDefault;
            }
        }

        /// <summary>Dims the controls that have no effect on the default device.</summary>
        public double ProcessingOpacity => IsDefaultDevice ? 0.35 : 1.0;

        /// <summary>Explains the delay slider, including why it is dead on the default device.</summary>
        public string DelayTooltip => IsDefaultDevice
            ? "此设备是系统默认输出，无法调节延时。\n\n它是 SoundSync 的音频来源，由 Windows 直接播放，SoundSync 不会处理它。请为较早发声的转发设备添加延时，或将另一个设备设为默认。"
            : "为此设备添加延时以匹配其他设备。范围为 0 至 500 毫秒，步长为 1 毫秒。\n\n只能延后，不能提前播放。默认设备由 Windows 直接播放，因此应延迟较早发声的转发设备。\n\n先点击滑块，再用方向键可每次调整 1 毫秒，鼠标滚轮每格调整 1 毫秒。";

        private static readonly string NewParagraph = Environment.NewLine + Environment.NewLine;

        /// <summary>
        /// The one rule that decides which device should be the system default. Repeated in
        /// every connection tooltip because it is the thing people get backwards.
        /// </summary>
        private static readonly string WhySlowestShouldBeDefault =
            "为什么建议把最慢的设备设为默认\nSoundSync 捕获默认设备的音频，并复制到其他设备。默认设备直接由 Windows 播放，SoundSync 无法为它添加延时，也无法让它提前发声。\n\n延时只能添加到转发设备。如果默认设备最快，其他较慢设备已落后，SoundSync 无法靠延迟默认设备来补偿。\n\n将最慢的设备设为默认后，其他设备通常会较早发声，可分别为它们添加延时来对齐。";

        /// <summary>Explains the SYSTEM DEFAULT badge and the rule behind choosing it.</summary>
        public string DefaultBadgeTooltip =>
            "Windows 当前将系统音频发送到此设备。\n\n这是 SoundSync 捕获的音频来源，不能同时作为转发目标，因此其勾选框、延时和均衡器不可用。" + NewParagraph + WhySlowestShouldBeDefault;

        /// <summary>Explains the SET AS DEFAULT button, including when you would want it.</summary>
        public string SetAsDefaultTooltip =>
            "将此设备设为 Windows 所有音频角色的默认输出。\n\nSoundSync 从默认设备捕获音频，因此这也会改变转发来源。如果正在转发，会先停止；之后再次点击“连接”即可继续。" + NewParagraph + WhySlowestShouldBeDefault;

        /// <summary>Reads the current rate and the supported list from Windows.</summary>
        public void LoadSystemFormat()
        {
            try
            {
                SupportedRates = Services.SystemAudioConfig.GetSupportedRates(Device);
                applyingSampleRate = true;
                _sampleRate = Services.SystemAudioConfig.GetSampleRate(Device);
                applyingSampleRate = false;
                OnPropertyChanged(nameof(SupportedRates));
                OnPropertyChanged(nameof(SampleRate));
            }
            catch { }
        }

        /// <summary>
        /// Windows volume of this endpoint, 0..1. Writing it moves the real system
        /// slider; Windows changes made elsewhere are pushed back here by
        /// <see cref="AttachSystemVolume"/>, so the two stay in sync.
        /// </summary>
        public float SystemVolume
        {
            get
            {
                try { return Device.AudioEndpointVolume.MasterVolumeLevelScalar; }
                catch { return 0f; }
            }
            set
            {
                if (applyingSystemVolume) return;
                try
                {
                    float clamped = Math.Clamp(value, 0f, 1f);
                    if (Math.Abs(Device.AudioEndpointVolume.MasterVolumeLevelScalar - clamped) < 0.0005f) return;
                    Device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
                    if (clamped > 0f && Device.AudioEndpointVolume.Mute)
                        Device.AudioEndpointVolume.Mute = false;
                }
                catch { }
                OnPropertyChanged();
                OnPropertyChanged(nameof(SystemVolumePercent));
            }
        }

        /// <summary>Same value as a percentage, for the readout next to the slider.</summary>
        public string SystemVolumePercent => $"{SystemVolume * 100:F0}%";

        /// <summary>
        /// Mirrors Windows-side volume changes back into the UI. <paramref name="post"/>
        /// marshals to the UI thread - the notification arrives on an audio thread.
        /// </summary>
        public void AttachSystemVolume(Action<Action> post)
        {
            try
            {
                Device.AudioEndpointVolume.OnVolumeNotification += _ => post(() =>
                {
                    applyingSystemVolume = true;
                    OnPropertyChanged(nameof(SystemVolume));
                    OnPropertyChanged(nameof(SystemVolumePercent));
                    applyingSystemVolume = false;
                });
            }
            catch { }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSyncOptions)); }
        }

        private float _volume = 1.0f;
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                OnPropertyChanged();
                if (VolumeProvider != null)
                {
                    VolumeProvider.Volume = _volume;
                }
            }
        }

        private int _delay = 0;
        public int Delay
        {
            get => _delay;
            set
            {
                _delay = value;
                OnPropertyChanged();
                DelayChangedCallback?.Invoke();
            }
        }

        private float _bass = 0f;
        public float Bass
        {
            get => _bass;
            set
            {
                _bass = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.BassDb = _bass;
                }
            }
        }

        private float _mid = 0f;
        public float Mid
        {
            get => _mid;
            set
            {
                _mid = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.MidDb = _mid;
                }
            }
        }

        private float _treble = 0f;
        public float Treble
        {
            get => _treble;
            set
            {
                _treble = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.TrebleDb = _treble;
                }
            }
        }

        private float _peakLevel = 0f;
        public float PeakLevel
        {
            get => _peakLevel;
            set
            {
                _peakLevel = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Buffer feeding this output, used to measure the real delay.</summary>
        public NAudio.Wave.BufferedWaveProvider? OutputBuffer { get; set; }

        /// <summary>Fixed part of the delay: what was asked of WASAPI, in milliseconds.</summary>
        public int EndpointLatencyMs { get; set; }

        private double _measuredLatencyMs;
        /// <summary>
        /// Delay actually observed on this output: how much audio is waiting in its buffer,
        /// plus the endpoint latency, plus whatever the user dialled in. Measured, not guessed.
        /// </summary>
        public double MeasuredLatencyMs
        {
            get => _measuredLatencyMs;
            private set
            {
                if (Math.Abs(_measuredLatencyMs - value) < 0.5) return;
                _measuredLatencyMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MeasuredLatencyText));
            }
        }

        /// <summary>Reading shown next to the delay slider.</summary>
        public string MeasuredLatencyText => OutputBuffer == null ? "--" : $"~{MeasuredLatencyMs:F0} ms";

        /// <summary>Re-reads the buffer occupancy. Called on a timer while connected.</summary>
        public void RefreshMeasuredLatency()
        {
            var buffer = OutputBuffer;
            if (buffer == null) { MeasuredLatencyMs = 0; return; }
            try
            {
                double buffered = buffer.BufferedBytes * 1000.0 / buffer.WaveFormat.AverageBytesPerSecond;
                MeasuredLatencyMs = buffered + EndpointLatencyMs + Math.Max(0, Delay);
            }
            catch { }
        }

        public VolumeSampleProvider? VolumeProvider { get; set; }
        public EqualizerSampleProvider? EqualizerProvider { get; set; }
        public DelaySampleProvider? DelayProvider { get; set; }

        // Action callback to trigger delay updates without direct MainWindow coupling
        public Action? DelayChangedCallback { get; set; }

        /// <summary>
        /// Raised after this endpoint's sample rate changed. Windows tears the audio engine
        /// down for a device whose format changes, which kills a loopback capture reading
        /// from it, so whoever is mirroring needs to restart.
        /// </summary>
        public Action<DeviceItem>? FormatChangedCallback { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
