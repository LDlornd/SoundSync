using System.IO;
using SoundSync.Models;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests;

public class AppSettingsStoreTests
{
    [Fact]
    public void Preferences_SaveImmediatelyBeforeWindowGeometryExists_AndSurviveRestart()
    {
        string path = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { AutoStartForwarding = false, KeyboardShortcutsEnabled = true };
            Assert.True(double.IsNaN(settings.WindowLeft));
            new AppSettingsStore(path).Save(settings);
            var loaded = new AppSettingsStore(path).Load();
            Assert.False(loaded.AutoStartForwarding);
            Assert.True(loaded.KeyboardShortcutsEnabled);
            Assert.True(double.IsNaN(loaded.WindowLeft));
            Assert.True(double.IsNaN(loaded.WindowTop));

            loaded.AutoStartForwarding = true;
            loaded.KeyboardShortcutsEnabled = false;
            new AppSettingsStore(path).Save(loaded);
            var restarted = new AppSettingsStore(path).Load();
            Assert.True(restarted.AutoStartForwarding);
            Assert.False(restarted.KeyboardShortcutsEnabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacySettings_UseIndependentStartupDefaultAndDisableShortcuts_KeepingAudioSettings()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"AutoConnectOnLaunch\":false,\"IndependentVolumes\":false,\"WindowWidth\":1200}");
            var settings = new AppSettingsStore(path).Load();
            Assert.True(settings.AutoStartForwarding);
            Assert.False(settings.KeyboardShortcutsEnabled);
            Assert.False(settings.IndependentVolumes);
            Assert.Equal(1200, settings.WindowWidth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingSettings_StartWithAutoForwardingOnAndShortcutsOff()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettingsStore(path).Load();
        Assert.True(settings.AutoStartForwarding);
        Assert.False(settings.KeyboardShortcutsEnabled);
        Assert.False(File.Exists(path));
    }
}
