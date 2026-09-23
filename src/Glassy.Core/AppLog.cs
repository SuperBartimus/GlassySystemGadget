namespace Glassy.Core;

/// <summary>Tiny append-only log next to the config. Never throws: it is used from error paths.</summary>
public static class AppLog
{
    static readonly object Gate = new();
    public static string PathOfLog => Path.Combine(Path.GetDirectoryName(ConfigStore.DefaultPath)!, "glassy.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var p = PathOfLog; Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Move(p, p + ".old", true);
                File.AppendAllText(p, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
