using Microsoft.Win32;

namespace Glassy.Core;

/// <summary>"Start with Windows" via HKCU Run. Launches through wscript so no console window appears and no new .exe is needed (Defender ASR blocks fresh .exe files here).</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "GlassySystemGadget";
    static readonly char[] BadChars = { '"', '\r', '\n' };

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

    public static bool IsEnabled(string valueName = DefaultValueName)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(valueName) != null;
    }

    public static void Set(bool enable, string appDll, string valueName = DefaultValueName, string launcherPath = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enable) { k.DeleteValue(valueName, false); return; }
        launcherPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassySystemGadget", "launch.vbs");
        Directory.CreateDirectory(Path.GetDirectoryName(launcherPath)!);
        File.WriteAllText(launcherPath, BuildLauncher(DefaultDotnet(), appDll), System.Text.Encoding.ASCII);
        k.SetValue(valueName, "wscript.exe //B //Nologo \"" + launcherPath + "\"");
    }
}
