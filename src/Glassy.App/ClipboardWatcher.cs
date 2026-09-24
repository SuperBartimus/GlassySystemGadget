using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
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
            var data = System.Windows.Forms.Clipboard.GetDataObject();
            var candidate = ClipboardClassifier.Classify(data, cfg.ClipMaxImageBytes);
            if (candidate == null)
            {
                // Diagnostic for the RDP cross-device report: if WM_CLIPBOARDUPDATE never reaches this method at
                // all, there will be no log line whatsoever for the missed copy - proving the notification itself
                // never arrived, as opposed to arriving with formats GSG doesn't recognize (listed here if so).
                string formats = data != null ? string.Join(",", data.GetFormats()) : "(no data object)";
                AppLog.Write("clipboard update seen but not classified; formats: " + formats);
                return false;
            }
            if (IsRecentOwnWrite(candidate.Item.DedupKey)) return false;
            return engine.Clipboard.Add(candidate, cfg.ClipMaxItems);
        }
        // Another app (or, per Bart, possibly rdpclip.exe mid-transfer across an RDP session) has the clipboard
        // open or the data isn't actually available yet; logged rather than silently dropped, since a capture
        // that never happens and never says why is unfalsifiable - see the RDP cross-device report in the design
        // spec. COMException derives from ExternalException, so this also covers the more common local case.
        catch (ExternalException ex) { AppLog.Write("clipboard capture skipped: " + ex.Message); return false; }
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
