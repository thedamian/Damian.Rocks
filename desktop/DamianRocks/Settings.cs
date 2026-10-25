using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DamianRocks;

public sealed class Settings
{
    public string Screen { get; set; } = OverlayManager.AllScreens;
    public bool WelcomeShown { get; set; }
    /// <summary>"left" or "right": the edge of the screen the raised-hands list sits on.</summary>
    public string HandsSide { get; set; } = HandsWindow.SideRight;
    /// <summary>The presenter key, encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? PresenterKeyProtected { get; set; }

    [JsonIgnore]
    public string? PresenterKey
    {
        get
        {
            if (string.IsNullOrEmpty(PresenterKeyProtected)) return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(PresenterKeyProtected), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
        set
        {
            PresenterKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DamianRocks.PresenterKey");

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
