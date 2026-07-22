using System.Text.Json;

namespace Slopterm.Server;

public sealed class WindowPosition
{
    public required int X { get; set; }
    public required int Y { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
}

/// <summary>
/// Remembers the app window's last position/size across restarts, so the tray icon's
/// "Open" action can restore it (see BrowserLauncher.TryLaunchChromiumAppMode's
/// --window-position/--window-size flags) instead of the OS/browser picking a default
/// spot every time. Plain JSON, not encrypted - screen coordinates aren't sensitive.
/// Lives alongside vault.json/settings.json but isn't itself vault content: not a named
/// entry in VaultService.ExportBackup, so it naturally doesn't travel with a backup
/// (your desktop layout on one machine has no business being forced onto another).
/// </summary>
public static class WindowPositionStore
{
    private static string PathOnDisk => Path.Combine(Vault.AppPaths.GetVaultDirectory(), "window.json");

    public static WindowPosition? Load()
    {
        if (!File.Exists(PathOnDisk))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WindowPosition>(File.ReadAllText(PathOnDisk));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(WindowPosition position)
    {
        Directory.CreateDirectory(Vault.AppPaths.GetVaultDirectory());
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(position));
    }
}
