using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
    bool ownWrite;   // set around our own SetDataObject call so the resulting WM_CLIPBOARDUPDATE is not re-captured

    public ClipboardWatcher(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        Win32.AddClipboardFormatListener(hwnd);
    }

    /// <summary>Call from WndProc on WM_CLIPBOARDUPDATE. Returns true if a new item was actually captured (worth a sound/re-render).</summary>
    public bool OnClipboardUpdate(Engine engine, PanelConfig cfg)
    {
        if (ownWrite) { ownWrite = false; return false; }
        try
        {
            var data = System.Windows.Forms.Clipboard.GetDataObject();
            var candidate = ClipboardClassifier.Classify(data, cfg.ClipMaxImageBytes);
            if (candidate == null) return false;
            return engine.Clipboard.Add(candidate, cfg.ClipMaxItems);
        }
        catch (ExternalException) { return false; }   // another app has the clipboard open right now (COMException derives from this too)
    }

    public void RestoreToClipboard(ClipItem item, ClipboardStore store)
    {
        var data = new System.Windows.Forms.DataObject();
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
                using (var ms = new MemoryStream(store.ReadBlobBytes(item))) using (var img = Image.FromStream(ms)) data.SetImage(img);
                break;
            case ClipKind.Files:
                var sc = new StringCollection(); sc.AddRange(item.Files.ToArray()); data.SetFileDropList(sc);
                int effect = item.FilesEffect == ClipDropEffect.Cut ? 2 : 1;   // DROPEFFECT_MOVE : DROPEFFECT_COPY
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
                break;
        }
        ownWrite = true;
        try { System.Windows.Forms.Clipboard.SetDataObject(data, true); }
        catch (ExternalException) { ownWrite = false; }   // clipboard busy; leave it as it was rather than half-write
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
