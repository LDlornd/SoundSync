using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace SoundSync.Services
{
    /// <summary>
    /// Turns the raw WASAPI and COM failures into something a person can act on.
    /// Windows reports most audio problems as an opaque HRESULT or a bare
    /// "Value does not fall within the expected range", which tells the user nothing.
    /// </summary>
    public static class AudioErrors
    {
        // AUDCLNT_* error codes, from the Windows Core Audio headers.
        private const int UnsupportedFormat = unchecked((int)0x88890008);
        private const int DeviceInUse = unchecked((int)0x8889000A);
        private const int DeviceInvalidated = unchecked((int)0x88890004);
        private const int ExclusiveModeNotAllowed = unchecked((int)0x8889000F);
        private const int EndpointCreateFailed = unchecked((int)0x88890010);
        private const int ServiceNotRunning = unchecked((int)0x88890011);
        private const int BufferSizeError = unchecked((int)0x88890006);
        private const int CpuUsageExceeded = unchecked((int)0x88890017);
        private const int NotInitialized = unchecked((int)0x88890001);
        private const int AlreadyInitialized = unchecked((int)0x88890002);
        private const int NotFound = unchecked((int)0x80070490);
        private const int AccessDenied = unchecked((int)0x80070005);

        /// <summary>
        /// Explains why one output could not be started, naming the device and the concrete
        /// mismatch where there is one.
        /// </summary>
        public static string Explain(Exception error, string deviceName, WaveFormat captureFormat, MMDevice? device)
        {
            string outputFormat = "未知";
            int outputChannels = 0;
            try
            {
                var mix = device?.AudioClient.MixFormat;
                if (mix != null)
                {
                    outputFormat = $"{mix.SampleRate} Hz / {mix.Channels} 声道";
                    outputChannels = mix.Channels;
                }
            }
            catch { }

            string source = $"{captureFormat.SampleRate} Hz / {captureFormat.Channels} 声道";
            string detail = Describe(error, deviceName);

            string mismatch = string.Empty;
            if (outputChannels > 0 && outputChannels != captureFormat.Channels)
            {
                mismatch = $"\n来源为 {captureFormat.Channels} 声道，此输出为 {outputChannels} 声道，" +
                           "需要重新映射声道。";
            }

            return $"{deviceName}\n{detail}\n来源：{source}   输出：{outputFormat}{mismatch}";
        }

        /// <summary>Plain-language description of a single audio failure.</summary>
        public static string Describe(Exception error, string deviceName)
        {
            // Unwrap the layers NAudio and the app add on top of the real cause.
            Exception root = error;
            while (root.InnerException != null) root = root.InnerException;

            if (root is COMException com) return DescribeHResult(com.ErrorCode, deviceName);
            if (root is ArgumentException)
            {
                return "Windows 拒绝了此输出设备的音频格式。可能是" +
                       "来源与输出设备的声道数不一致，或输出设备在" +
                       "使用过程中被重新配置。";
            }
            if (root is UnauthorizedAccessException)
            {
                return "Windows 拒绝访问此设备。请检查“设置 > 隐私 > 麦克风”中的" +
                       "应用权限，或确认设备是否被其他程序独占。";
            }
            if (root is InvalidOperationException) return root.Message;

            int hr = Marshal.GetHRForException(root);
            string known = DescribeHResult(hr, deviceName);
            return known.StartsWith("Windows 报告") ? $"{root.GetType().Name}: {root.Message}" : known;
        }

        /// <summary>Plain-language description of a Windows audio HRESULT.</summary>
        public static string DescribeHResult(int hr, string deviceName) => hr switch
        {
            UnsupportedFormat =>
                "此输出设备不支持该音频格式。其采样率或声道布局" +
                "无法接收来源设备生成的音频。",
            DeviceInUse =>
                "其他程序正以独占模式使用此设备，阻止了其他应用访问。" +
                "请关闭该程序，或在 Windows 声音控制面板的设备属性中关闭" +
                "“允许应用程序独占控制该设备”。",
            DeviceInvalidated =>
                "打开设备时设备已不可用，可能被拔出、禁用，或其驱动" +
                "已重启。请点击“重新扫描设备”。",
            ExclusiveModeNotAllowed =>
                "此设备拒绝共享访问，同一时间只能由一个程序使用。" +
                "请在 Windows 声音控制面板的设备属性中关闭独占模式。",
            EndpointCreateFailed =>
                "Windows 无法创建音频端点。通常是驱动问题，请重新安装或" +
                "更新此设备的音频驱动。",
            ServiceNotRunning =>
                "Windows Audio 服务未运行。请在 services.msc 中启动“Windows Audio”。",
            BufferSizeError =>
                "请求的缓冲区大小不适用于此设备，驱动需要其他大小。",
            CpuUsageExceeded =>
                "电脑处理速度不足，Windows 已停止音频流。请关闭高负载程序，" +
                "或增大缓冲区。",
            NotInitialized => "音频客户端尚未初始化就被使用，这是程序内部调用顺序错误。",
            AlreadyInitialized => "音频客户端被重复初始化，这是程序内部调用顺序错误。",
            NotFound => $"未找到 {deviceName}，设备可能已被拔出或禁用。",
            AccessDenied => "Windows 拒绝访问此设备。",
            0 => "操作失败，系统未提供原因。",
            _ => $"Windows 报告此设备发生错误 0x{hr:X8}。"
        };

        /// <summary>
        /// Why a sample rate change was refused. Windows gives no reason code here, so this
        /// names the causes actually observed, most likely first.
        /// </summary>
        public static string ExplainRateRejection(MMDevice device, int requestedRate)
        {
            string busy = string.Empty;
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i].State != AudioSessionState.AudioSessionStateActive) continue;
                    try
                    {
                        var name = System.Diagnostics.Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName;
                        busy += (busy.Length > 0 ? ", " : "") + name;
                    }
                    catch { }
                }
            }
            catch { }

            if (busy.Length > 0)
            {
                return $"Windows 无法将此输出切换到 {requestedRate} Hz，因为它正在" +
                       $"播放音频（{busy}）。请停止播放后重试。";
            }

            return $"Windows 无法将此输出设置为 {requestedRate} Hz。设备驱动在共享模式下不接受该采样率，" +
                   "或设备正在使用中。列表根据硬件报告生成，" +
                   "驱动报告的某些采样率可能无法实际切换。";
        }
    }
}
