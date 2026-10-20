using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SoundSync.Models;

namespace SoundSync.Services;

internal static class AppCommands
{
    internal static bool TryAutoConnect(AppSettings settings, IEnumerable<DeviceItem> devices,
        bool isConnected, Action connect)
    {
        if (!settings.AutoStartForwarding || isConnected ||
            !devices.Any(i => i.IsSelected && !i.IsDefaultDevice)) return false;
        connect();
        return true;
    }

    internal static bool TryHandleShortcut(AppSettings settings, Key key, ModifierKeys modifiers,
        bool isRepeat, bool isEditing, Action toggleConnection, Action toggleMute)
    {
        if (!settings.KeyboardShortcutsEnabled || modifiers != ModifierKeys.None || isRepeat || isEditing)
            return false;
        if (key == Key.C) toggleConnection();
        else if (key == Key.M) toggleMute();
        else return false;
        return true;
    }

    internal static bool IsEditingControl(DependencyObject? source)
    {
        for (var node = source; node != null; node = node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBoxBase or PasswordBox or ComboBox) return true;
        }
        return false;
    }
}
