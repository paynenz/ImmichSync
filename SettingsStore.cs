using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ImmichSync;

internal sealed record SyncSettings(Uri Server, string ApiKey, Album Album, string DownloadFolder, int IntervalMinutes);

internal static class SettingsStore
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmichSync");
    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ImmichSync";

    public static SyncSettings? Load()
    {
        if (!File.Exists(SettingsPath))
            return null;

        var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath))
            ?? throw new InvalidDataException("The saved settings are empty.");
        byte[] key = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedKey), null, DataProtectionScope.CurrentUser);
        try
        {
            return new SyncSettings(ImmichClient.ParseServer(stored.Server), Encoding.UTF8.GetString(key),
                stored.Album, stored.DownloadFolder, stored.IntervalMinutes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Save(SyncSettings settings)
    {
        byte[] key = Encoding.UTF8.GetBytes(settings.ApiKey);
        string protectedKey;
        try
        {
            protectedKey = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var stored = new StoredSettings(settings.Server.AbsoluteUri, protectedKey, settings.Album,
            settings.DownloadFolder, settings.IntervalMinutes);
        Directory.CreateDirectory(DataFolder);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    public static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private sealed record StoredSettings(string Server, string ProtectedKey, Album Album,
        string DownloadFolder, int IntervalMinutes);
}
