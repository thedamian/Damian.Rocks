using System;
using Microsoft.Win32;

namespace DamianRocks;

/// <summary>Per-user "start with Windows" via the HKCU Run key.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DamianRocks";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled, AppOptions options)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        string command = $"\"{exe}\"" + (options.IsDefaultServer ? "" : $" --server \"{options.DisplayUri}\"");
        key.SetValue(ValueName, command);
    }
}
