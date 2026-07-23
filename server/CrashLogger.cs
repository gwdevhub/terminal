using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Slopterm.Server;

/// <summary>
/// Global safety net installed as the very first thing Program.cs does, before the port
/// probe, vault init, or window creation can throw. Without this, an unhandled exception
/// on the published Windows build (no console - see the .csproj's WinExe OutputType, only
/// applied for win-x64) just closes the process with zero visible trace: no console to
/// print to, nothing on disk, nothing on screen. This turns that into a crash.log entry
/// plus (on Windows) a message box, so "it flashed and closed" becomes something
/// diagnosable instead of a dead end.
/// </summary>
public static class CrashLogger
{
    private static string LogPath => Path.Combine(Vault.AppPaths.GetVaultDirectory(), "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
    }

    private static void Report(Exception? ex)
    {
        var text = ex?.ToString() ?? "(non-Exception object thrown - no further details available)";

        // Also to stderr - visible whenever there IS a console (plain `dotnet run`/
        // `dotnet build` without -r stays a normal console app on every OS), not just the
        // no-console published Windows build this exists for.
        Console.Error.WriteLine();
        Console.Error.WriteLine("slopterm crashed:");
        Console.Error.WriteLine(text);

        string? loggedTo = null;
        try
        {
            Directory.CreateDirectory(Vault.AppPaths.GetVaultDirectory());
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
            loggedTo = LogPath;
        }
        catch (IOException)
        {
            // The message box below still shows the raw details even when this fails - a
            // crash report that can't write to disk shouldn't also be why the user never
            // learns the process died.
        }

        if (OperatingSystem.IsWindows())
        {
            var message = loggedTo is not null
                ? $"slopterm hit an unexpected error and needs to close:\n\n{ex?.Message}\n\nFull details were saved to:\n{loggedTo}"
                : $"slopterm hit an unexpected error and needs to close:\n\n{text}";
            ShowMessageBox(message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ShowMessageBox(string message)
    {
        try
        {
            MessageBox(nint.Zero, message, "slopterm", MbIconError);
        }
        catch
        {
            // The stderr/log-file output above already covers this - a failed message box
            // is not worth crashing over (again).
        }
    }

    private const uint MbIconError = 0x00000010;

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
