using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoundSync.Services
{
    /// <summary>
    /// Registers the app to launch when the user signs in, using the per-user Run key.
    /// HKEY_CURRENT_USER needs no administrator rights, and the entry follows the user
    /// rather than the machine.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SoundSync";

        /// <summary>Argument that tells a fresh instance to connect by itself.</summary>
        public const string AutoConnectArgument = "--autoconnect";

        /// <summary>Full path of the running executable, quoted for the registry.</summary>
        private static string ExecutablePath
        {
            get
            {
                string path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                return path;
            }
        }

        /// <summary>True when this process was started with the auto-connect argument.</summary>
        public static bool LaunchedForAutoConnect()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, AutoConnectArgument, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Whether Windows is currently set to start this app at sign-in.</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>True when the registered command still points at this executable.</summary>
        public static bool IsCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key?.GetValue(ValueName) is not string value) return false;
                return value.IndexOf(ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adds or removes the sign-in entry. Returns an empty string on success, or a
        /// sentence explaining what went wrong.
        /// </summary>
        public static string SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return "Windows 无法打开当前账户的开机启动注册表项。";

                if (!enabled)
                {
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return string.Empty;
                }

                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    return "无法确定程序路径，不能注册开机启动。";

                key.SetValue(ValueName, $"\"{exe}\" {AutoConnectArgument}", RegistryValueKind.String);
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows 拒绝修改当前账户的开机启动列表。系统策略或安全软件" +
                       "可能阻止了程序添加启动项。";
            }
            catch (Exception ex)
            {
                return $"无法更新启动项：{ex.Message}";
            }
        }
    }
}
