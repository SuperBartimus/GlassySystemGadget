using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using Glassy.Core;

namespace Glassy.App;

/// <summary>
/// The Win32/WinForms-Clipboard glue for the Clipboard panel: captures on WM_CLIPBOARDUPDATE, writes an item back
/// to the OS clipboard, opens an item in its default app, and plays the capture sound. No drawing here - the
/// renderer owns that - and no storage logic either - ClipboardStore (Core) owns that.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    readonly IntPtr hwnd;
    // Remembers dedup keys we ourselves just wrote to the OS clipboard, each for a short window, so the resulting
    // WM_CLIPBOARDUPDATE isn't re-captured as a new item. A single one-shot flag isn't enough for two reasons seen
    // with scroll-driven selection: (1) Clipboard.SetDataObject(data, true) (the "keep after app exits" flush) can
    // fire more than one WM_CLIPBOARDUPDATE for the same write, and (2) fast scrolling calls this repeatedly before
    // earlier echoes have arrived, so whichever echo shows up must match the write that produced it, not just the
    // most recent write. A short per-key time window handles any echo count and any amount of overlap; genuinely
    // new content (a different dedup key) is never held back by it.
    readonly List<(string key, long atMs)> recentWrites = new();
    const int EchoWindowMs = 500;

    public ClipboardWatcher(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        Win32.AddClipboardFormatListener(hwnd);
    }

    /// <summary>Call from WndProc on WM_CLIPBOARDUPDATE. Returns true if a new item was actually captured (worth a sound/re-render).</summary>
    public bool OnClipboardUpdate(Engine engine, PanelConfig cfg)
    {
        try
        {
            var data = ReadRaw();
            var candidate = data != null ? ClipboardClassifier.Classify(data, cfg.ClipMaxImageBytes, cfg.ClipHonorHistoryFlag) : null;
            if (candidate == null)
            {
                // Diagnostic for the RDP cross-device report: if WM_CLIPBOARDUPDATE never reaches this method at
                // all, there will be no log line whatsoever for the missed copy - proving the notification itself
                // never arrived, as opposed to arriving with formats GSG doesn't recognize (listed here if so).
                string formats = data != null ? string.Join(",", data.GetFormats()) : "(clipboard could not be opened)";
                AppLog.Write("clipboard update seen but not classified; formats: " + formats);
                return false;
            }
            if (IsRecentOwnWrite(candidate.Item.DedupKey)) return false;
            return engine.Clipboard.Add(candidate, cfg.ClipMaxItems);
        }
        // Another app has the clipboard open right now; logged rather than silently dropped, since a capture that
        // never happens and never says why is unfalsifiable. COMException derives from ExternalException.
        catch (ExternalException ex) { AppLog.Write("clipboard capture skipped: " + ex.Message); return false; }
    }

    /// <summary>Reads the clipboard via raw Win32 calls (OpenClipboard/GetClipboardData/GlobalLock), bypassing
    /// WinForms' IDataObject/COM layer entirely. Added 2026-09-24 after IDataObject.GetData() proved unreliable
    /// over RDP: GetDataPresent correctly reported text/HTML/RTF formats as available, but GetData returned
    /// neither a string nor readable bytes for any of them - the same failure shape regardless of format, pointing
    /// at the COM marshaling layer itself rather than any one format's conversion. This mirrors what a native app
    /// (the old 8GadgetPack Clipboarder gadget, confirmed working over this same RDP connection) would do. Returns
    /// null if the clipboard couldn't even be opened (another app has it open); never throws.</summary>
    System.Windows.Forms.DataObject ReadRaw()
    {
        bool opened = false;
        for (int i = 0; i < 10 && !opened; i++) { opened = Win32.OpenClipboard(hwnd); if (!opened) Thread.Sleep(30); }
        if (!opened) return null;
        try
        {
            var data = new System.Windows.Forms.DataObject();
            if (ReadFormatBytes(Win32.CF_UNICODETEXT) is byte[] textBytes)
            {
                string s = Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
                if (s.Length > 0) data.SetData(System.Windows.Forms.DataFormats.UnicodeText, s);
            }
            if (ReadFormatBytes(Win32.RegisterClipboardFormatW("HTML Format")) is byte[] htmlBytes)
            {
                string s = Encoding.UTF8.GetString(htmlBytes).TrimEnd('\0');
                if (s.Length > 0) data.SetData(System.Windows.Forms.DataFormats.Html, s);
            }
            if (ReadFormatBytes(Win32.RegisterClipboardFormatW("Rich Text Format")) is byte[] rtfBytes)
            {
                string s = Encoding.UTF8.GetString(rtfBytes).TrimEnd('\0');
                if (s.Length > 0) data.SetData(System.Windows.Forms.DataFormats.Rtf, s);
            }
            if (ReadFormatBytes(Win32.CF_DIB) is byte[] dibBytes)
                data.SetData(System.Windows.Forms.DataFormats.Dib, dibBytes);
            if (ReadFormatBytes(Win32.CF_HDROP) is byte[] hdropBytes)
            {
                var files = ParseHDrop(hdropBytes);
                if (files.Count > 0) { var sc = new StringCollection(); sc.AddRange(files.ToArray()); data.SetFileDropList(sc); }
            }
            // Exclusion tags (password managers) and "Preferred DropEffect" are custom registered formats too -
            // read the same way so ClipboardClassifier.IsExcluded and cut-vs-copy keep working over this path.
            if (ReadFormatBytes(Win32.RegisterClipboardFormatW(ClipboardClassifier.FormatExclude)) is byte[] exBytes)
                data.SetData(ClipboardClassifier.FormatExclude, exBytes);
            if (ReadFormatBytes(Win32.RegisterClipboardFormatW(ClipboardClassifier.FormatCanIncludeHistory)) is byte[] histBytes)
                data.SetData(ClipboardClassifier.FormatCanIncludeHistory, histBytes);
            if (ReadFormatBytes(Win32.RegisterClipboardFormatW(ClipboardClassifier.FormatDropEffect)) is byte[] deBytes)
                data.SetData(ClipboardClassifier.FormatDropEffect, deBytes);
            return data;
        }
        finally { Win32.CloseClipboard(); }
    }

    /// <summary>One format's raw bytes, or null if absent/unreadable. Never calls GlobalFree - the handle from
    /// GetClipboardData is owned by the system, only Lock/Unlock are ours to call.</summary>
    static byte[] ReadFormatBytes(uint format)
    {
        if (format == 0 || !Win32.IsClipboardFormatAvailable(format)) return null;
        IntPtr h = Win32.GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        IntPtr p = Win32.GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            int size = (int)Win32.GlobalSize(h);
            if (size <= 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            return bytes;
        }
        finally { Win32.GlobalUnlock(h); }
    }

    /// <summary>CF_HDROP payload: a DROPFILES header (20 bytes: DWORD pFiles offset, POINT, BOOL fNC, BOOL fWide)
    /// followed by the file list - null-separated, double-null-terminated, wide (UTF-16LE) when fWide is set,
    /// which Explorer always sets on modern Windows.</summary>
    static List<string> ParseHDrop(byte[] raw)
    {
        var files = new List<string>();
        if (raw.Length < Win32.DROPFILES_OFFSET + 2) return files;
        bool wide = BitConverter.ToInt32(raw, 16) != 0;
        int start = Win32.DROPFILES_OFFSET;
        if (wide)
        {
            while (start + 1 < raw.Length)
            {
                int end = start;
                while (end + 1 < raw.Length && !(raw[end] == 0 && raw[end + 1] == 0)) end += 2;
                if (end == start) break;   // empty entry = double-null terminator: end of list
                files.Add(Encoding.Unicode.GetString(raw, start, end - start));
                start = end + 2;
            }
        }
        else
        {
            while (start < raw.Length)
            {
                int end = start;
                while (end < raw.Length && raw[end] != 0) end++;
                if (end == start) break;
                files.Add(Encoding.Default.GetString(raw, start, end - start));
                start = end + 1;
            }
        }
        return files;
    }

    bool IsRecentOwnWrite(string dedupKey)
    {
        long now = Environment.TickCount64;
        recentWrites.RemoveAll(w => now - w.atMs > EchoWindowMs);
        return recentWrites.Any(w => w.key == dedupKey);
    }

    public void RestoreToClipboard(ClipItem item, ClipboardStore store)
    {
        var data = new System.Windows.Forms.DataObject();
        Image img = null; MemoryStream imgStream = null;
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Text:
                    data.SetText(item.PlainText); break;
                case ClipKind.RichText:
                    data.SetText(item.PlainText);
                    string payload = store.ReadBlobText(item);
                    data.SetData(item.RichFormat == "html" ? System.Windows.Forms.DataFormats.Html : System.Windows.Forms.DataFormats.Rtf, payload);
                    break;
                case ClipKind.Image:
                    // Both the stream and the decoded Image must outlive this method: Clipboard.SetDataObject(data,
                    // true)'s "keep after app exits" flush renders the image into the real clipboard formats at
                    // flush time, not at SetImage() time. Disposing either one before that flush ran (the previous
                    // `using (...) using (...) data.SetImage(img);` one-liner did both, immediately) silently
                    // produced a 0-byte image on the clipboard - invisible here, but Gmail's paste later refused it
                    // outright ("This file is 0 bytes, so it will not be attached"). Both are disposed in the
                    // `finally` below, after SetDataObject has actually run.
                    imgStream = new MemoryStream(store.ReadBlobBytes(item));
                    img = Image.FromStream(imgStream);
                    data.SetImage(img);
                    break;
                case ClipKind.Files:
                    var sc = new StringCollection(); sc.AddRange(item.Files.ToArray()); data.SetFileDropList(sc);
                    int effect = item.FilesEffect == ClipDropEffect.Cut ? 2 : 1;   // DROPEFFECT_MOVE : DROPEFFECT_COPY
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
                    break;
            }
            recentWrites.Add((item.DedupKey, Environment.TickCount64));
            try { System.Windows.Forms.Clipboard.SetDataObject(data, true); store.SelectedId = item.Id; }
            catch (ExternalException) { recentWrites.RemoveAll(w => w.key == item.DedupKey); }   // clipboard busy; leave it as it was rather than half-write
        }
        finally { img?.Dispose(); imgStream?.Dispose(); }
    }

    /// <summary>Opens the item's content in whatever the OS has associated with its type. Returns false if there was nothing to open (a Files item whose file no longer exists).</summary>
    public bool OpenInApp(ClipItem item, ClipboardStore store)
    {
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Files:
                    string first = item.Files.FirstOrDefault(f => File.Exists(f) || Directory.Exists(f));
                    if (first == null) return false;
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
                    return true;
                case ClipKind.Text:
                    return OpenTemp(item.PlainText, ".txt");
                case ClipKind.RichText:
                    return OpenTemp(store.ReadBlobText(item), item.RichFormat == "html" ? ".html" : ".rtf");
                case ClipKind.Image:
                    string png = Path.Combine(Path.GetTempPath(), "glassy-clip-" + item.Id + ".png");
                    File.WriteAllBytes(png, store.ReadBlobBytes(item));
                    Process.Start(new ProcessStartInfo(png) { UseShellExecute = true });
                    return true;
                default: return false;
            }
        }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException) { return false; }
    }

    static bool OpenTemp(string text, string ext)
    {
        string path = Path.Combine(Path.GetTempPath(), "glassy-clip-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllText(path, text);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }

    public static void PlaySound(PanelConfig cfg)
    {
        if (!cfg.ClipPlaySound) return;
        try
        {
            if (cfg.ClipSoundFile != "" && File.Exists(cfg.ClipSoundFile)) new SoundPlayer(cfg.ClipSoundFile).Play();
            else SystemSounds.Asterisk.Play();
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException) { }   // a missing/bad sound file must never crash capture
    }

    public void Dispose() => Win32.RemoveClipboardFormatListener(hwnd);
}
