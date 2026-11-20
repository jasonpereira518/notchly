// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream Services/LaunchAtLogin.swift (SMAppService): the per-user Run key.
using Islet.Core;
using Microsoft.Win32;

namespace Islet.App.Platform.Shell;

public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppIdentity.Id) is string;
        }
    }

    /// <summary>Points the Run value at this executable, or removes it.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
            key.SetValue(AppIdentity.Id, $"\"{exe}\" --background");
        }
        else if (key.GetValue(AppIdentity.Id) is not null)
        {
            key.DeleteValue(AppIdentity.Id);
        }
    }
}
