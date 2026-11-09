using Microsoft.Win32;

namespace Glassy.Core;

/// <summary>"Start with Windows" via HKCU Run. Launches through wscript so no console window appears and no new .exe is needed (Defender ASR blocks fresh .exe files here).
/// install.ps1 instead drops a Startup-folder shortcut. Both at once start the app twice at logon, and the second start
/// signals the first to open Settings - so the Run key wins and the shortcut is removed whenever both exist.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "GlassySystemGadget";
    static readonly char[] BadChars = { '"', '\r', '\n' };

    /// <summary>The shortcut install.ps1 creates (keep the name in step with New-HiddenStartupShortcut).</summary>
    public static string DefaultShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Glassy System Gadget.lnk");

    /// <summary>VBScript that starts the app hidden. A path containing a quote would break out of the VBScript string, so it is refused.</summary>
    public static string BuildLauncher(string dotnetExe, string appDll)
    {
        if (dotnetExe.IndexOfAny(BadChars) >= 0 || appDll.IndexOfAny(BadChars) >= 0)
            throw new ArgumentException("Path contains a character that cannot be embedded safely.");
        return "CreateObject(\"WScript.Shell\").Run \"\"\"" + dotnetExe + "\"\" \"\"" + appDll + "\"\"\", 0, False" + Environment.NewLine;
    }

    public static string DefaultDotnet()
    {
        var p = Environment.ProcessPath;
        return p != null && Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    }

    static bool HasRunValue(string valueName)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(valueName) != null;
    }

    /// <summary>True when either the Run value or the installer's shortcut will start the app at logon.</summary>
    public static bool IsEnabled(string valueName = DefaultValueName, string shortcutPath = null)
        => HasRunValue(valueName) || File.Exists(shortcutPath ?? DefaultShortcut);

    /// <summary>Deletes the installer shortcut when the Run value also exists, so logon starts the app once.</summary>
    public static void RemoveDuplicate(string valueName = DefaultValueName, string shortcutPath = null)
    {
        shortcutPath ??= DefaultShortcut;
        if (HasRunValue(valueName) && File.Exists(shortcutPath)) File.Delete(shortcutPath);
    }

    public static void Set(bool enable, string appDll, string valueName = DefaultValueName, string launcherPath = null, string shortcutPath = null)
    {
        shortcutPath ??= DefaultShortcut;
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);   // on: the Run value replaces it; off: it must go too
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enable) { k.DeleteValue(valueName, false); return; }
        launcherPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassySystemGadget", "launch.vbs");
        Directory.CreateDirectory(Path.GetDirectoryName(launcherPath)!);
        File.WriteAllText(launcherPath, BuildLauncher(DefaultDotnet(), appDll), System.Text.Encoding.ASCII);
        k.SetValue(valueName, "wscript.exe //B //Nologo \"" + launcherPath + "\"");
    }
}
