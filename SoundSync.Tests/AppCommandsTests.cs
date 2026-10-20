using System.Windows.Input;
using SoundSync.Models;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests;

public class AppCommandsTests
{
    [Theory]
    [InlineData(true, false, true, false, false, true)]
    [InlineData(true, false, true, false, true, true)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, false, true, true, false, false)]
    public void Startup_ConnectsOnlyConfiguredTargetsAndPreservesTheirGain(
        bool enabled, bool connected, bool selected, bool isDefault, bool hidden, bool expected)
    {
        var settings = new AppSettings { AutoStartForwarding = enabled };
        var device = new DeviceItem { IsSelected = selected, IsDefaultDevice = isDefault,
            IsHidden = hidden, SoftwareVolume = 0.001f };
        int starts = 0;
        bool result = AppCommands.TryAutoConnect(settings, new[] { device }, connected, () =>
        {
            starts++;
            Assert.Equal(0.001f, device.SoftwareVolume);
        });
        Assert.Equal(expected, result);
        Assert.Equal(expected ? 1 : 0, starts);
    }

    [Fact]
    public void Startup_WithNoAvailableDevices_DoesNotOpenAudioOrRaiseAConnectError()
    {
        bool called = false;
        Assert.False(AppCommands.TryAutoConnect(new AppSettings(), Array.Empty<DeviceItem>(), false,
            () => called = true));
        Assert.False(called);
    }

    [Fact]
    public void Startup_QueuedAfterManualConnection_DoesNotDisconnectOrStartAgain()
    {
        var devices = new[] { new DeviceItem { IsSelected = true } };
        bool connected = false;
        int starts = 0;
        void Connect() { connected = true; starts++; }
        AppCommands.TryAutoConnect(new AppSettings(), devices, connected, Connect);
        AppCommands.TryAutoConnect(new AppSettings(), devices, connected, Connect);
        Assert.True(connected);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(true, Key.C, ModifierKeys.None, false, false, 1)]
    [InlineData(true, Key.M, ModifierKeys.None, false, false, 2)]
    [InlineData(false, Key.C, ModifierKeys.None, false, false, 0)]
    [InlineData(false, Key.M, ModifierKeys.None, false, false, 0)]
    [InlineData(true, Key.C, ModifierKeys.Control, false, false, 0)]
    [InlineData(true, Key.M, ModifierKeys.Alt, false, false, 0)]
    [InlineData(true, Key.C, ModifierKeys.Shift, false, false, 0)]
    [InlineData(true, Key.C, ModifierKeys.None, true, false, 0)]
    [InlineData(true, Key.M, ModifierKeys.None, true, false, 0)]
    [InlineData(true, Key.C, ModifierKeys.None, false, true, 0)]
    [InlineData(true, Key.M, ModifierKeys.None, false, true, 0)]
    [InlineData(true, Key.A, ModifierKeys.None, false, false, 0)]
    public void Shortcut_OnlyDispatchesAnEnabledUnmodifiedInitialPressOutsideEditors(
        bool enabled, Key key, ModifierKeys modifiers, bool repeat, bool editing, int expected)
    {
        int connections = 0, mutes = 0;
        bool handled = AppCommands.TryHandleShortcut(new AppSettings { KeyboardShortcutsEnabled = enabled },
            key, modifiers, repeat, editing, () => connections++, () => mutes++);
        Assert.Equal(expected != 0, handled);
        Assert.Equal(expected == 1 ? 1 : 0, connections);
        Assert.Equal(expected == 2 ? 1 : 0, mutes);
    }
}
