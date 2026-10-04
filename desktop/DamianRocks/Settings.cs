using System;
using System.IO;
using System.Text.Json;

namespace DamianRocks;

public sealed class Settings
{
    public string Screen { get; set; } = OverlayManager.AllScreens;
    public bool WelcomeShown { get; set; }

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DamianRocks");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // corrupt settings: start fresh
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // non-fatal
        }
    }
}
