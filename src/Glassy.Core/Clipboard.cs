using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Glassy.Core;

public enum ClipKind { Text, RichText, Image, Files }
public enum ClipDropEffect { Copy, Cut }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    /// <summary>Short text shown in the row and matched by search. For Text/RichText this is the plain-text preview.</summary>
    public string Preview { get; set; } = "";
    /// <summary>Text: the text itself. RichText: the plain-text fallback (used for the preview and for a plain paste target). Else "".</summary>
    public string PlainText { get; set; } = "";
    /// <summary>RichText only: "html" or "rtf" - which blob format holds the formatted payload.</summary>
    public string RichFormat { get; set; } = "";
    /// <summary>Relative filename under the store's blob folder: the formatted payload (RichText) or a PNG (Image). Empty for Text/Files.</summary>
    public string BlobFile { get; set; } = "";
    public long BlobBytes { get; set; }
    public List<string> Files { get; set; } = new();
    public ClipDropEffect FilesEffect { get; set; } = ClipDropEffect.Copy;
    /// <summary>Set by the classifier; the store uses it to detect "this is the same content again" and bump it to the top instead of duplicating.</summary>
    public string DedupKey { get; set; } = "";

    public string TypeLabel => Kind switch
    {
        ClipKind.Text => "Text", ClipKind.RichText => RichFormat.ToUpperInvariant(), ClipKind.Image => "Image",
        _ => Files.Count > 1 ? $"{Files.Count} files" : "File",
    };
}

/// <summary>
/// Turns a clipboard snapshot (a WinForms IDataObject - the real clipboard at runtime, or a synthetic one in tests)
/// into a ClipItem. Pure: no file or clipboard I/O, so it is fully unit-testable with constructed DataObjects.
/// </summary>
public static class ClipboardClassifier
{
    // These three format names are not in System.Windows.Forms.DataFormats, but WinForms' IDataObject accepts any
    // registered format name as a plain string - no manual RegisterClipboardFormat P/Invoke is needed to read them.
    public const string FormatExclude = "ExcludeClipboardContentFromMonitorProcessing";
    public const string FormatCanIncludeHistory = "CanIncludeInClipboardHistory";
    public const string FormatDropEffect = "Preferred DropEffect";
    const int MaxPreview = 160;
    const int DropEffectCutBit = 2;   // DROPEFFECT_MOVE

    public sealed class Candidate
    {
        public ClipItem Item;
        /// <summary>Blob bytes to write to disk under Item.BlobFile once the store assigns a final Id; null when there is no blob (Text/Files).</summary>
        public byte[] Blob;
    }

    /// <summary>Returns null when the source opted out of history, or when nothing usable was found.
    /// honorHistoryFlag controls whether CanIncludeInClipboardHistory=0 counts as opting out - see IsExcluded.</summary>
    public static Candidate Classify(IDataObject data, long maxImageBytes, bool honorHistoryFlag = true)
    {
        if (data == null) return null;
        if (IsExcluded(data, honorHistoryFlag)) return null;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            return ClassifyFiles(data, files);

        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image img)
            return ClassifyImage(img, maxImageBytes);
        // CF_BITMAP (DataFormats.Bitmap) is a live GDI handle - it can be present in the format list yet fail to
        // produce an Image, because a bitmap handle can't cross an RDP session boundary (only pixel bytes can).
        // CF_DIB (DataFormats.Dib) is the same image as a plain byte blob, which does survive; decode it by
        // prepending the 14-byte BITMAPFILEHEADER a .bmp file needs but CF_DIB omits. Confirmed live: an image
        // copied on one machine and RDP'd to another showed up in the clipboard formats list but was never
        // classified until this fallback was added (2026-09-24).
        if (data.GetDataPresent(DataFormats.Dib) && TryReadBytes(data, DataFormats.Dib, out var dibBytes) && DecodeDib(dibBytes) is Image dibImg)
            return ClassifyImage(dibImg, maxImageBytes);

        string plain = PlainTextOf(data);
        if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html && html.Trim().Length > 0)
            return ClassifyRich(html, "html", plain);
        if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf && rtf.Trim().Length > 0)
            return ClassifyRich(rtf, "rtf", plain);

        if (plain.Length > 0)
            return new Candidate { Item = new ClipItem { Kind = ClipKind.Text, PlainText = plain, Preview = Truncate(plain), DedupKey = Hash("T:" + plain) } };

        return null;   // an app-specific format with no text/image/file representation we can use
    }

    /// <summary>FormatExclude is a deliberate, app-authored "don't remember this" signal (what most password
    /// managers set). FormatCanIncludeHistory=0 is murkier: it's Windows' own Clipboard History/Cloud Clipboard
    /// opt-out, which some apps also set for the same reason - but rdpclip.exe (RDP's clipboard bridge) sets it to
    /// 0 on every piece of content it bridges across an RDP session, regardless of source, which made GSG silently
    /// reject all RDP-sourced clips (confirmed by direct probe, 2026-09-24: a plain text copy showed
    /// CanIncludeInClipboardHistory = 0x00000000). honorHistoryFlag (PanelConfig.ClipHonorHistoryFlag, default on)
    /// lets Bart trade that safety net for RDP clipboard sync working.</summary>
    static bool IsExcluded(IDataObject data, bool honorHistoryFlag)
    {
        if (data.GetDataPresent(FormatExclude)) return true;
        if (honorHistoryFlag && data.GetDataPresent(FormatCanIncludeHistory) && TryReadBytes(data, FormatCanIncludeHistory, out var b) && b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0)
            return true;
        return false;
    }

    static string PlainTextOf(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string u) return u;
        if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string t) return t;
        // Same shape as the CF_BITMAP/CF_DIB fix: GetDataPresent can report a text format available while
        // GetData's automatic string conversion still fails - confirmed over RDP (2026-09-24), where the format
        // list showed UnicodeText/Text present but neither converted to a string. CF_UNICODETEXT's raw bytes are
        // null-terminated UTF-16LE; decode those directly rather than relying on the automatic conversion.
        if (data.GetDataPresent(DataFormats.UnicodeText) && TryReadBytes(data, DataFormats.UnicodeText, out var ub))
        {
            string s = Encoding.Unicode.GetString(ub).TrimEnd('\0');
            if (s.Length > 0) return s;
        }
        return "";
    }

    static Candidate ClassifyFiles(IDataObject data, string[] files)
    {
        var effect = ClipDropEffect.Copy;
        if (TryReadBytes(data, FormatDropEffect, out var eb) && eb.Length >= 4 && (BitConverter.ToInt32(eb, 0) & DropEffectCutBit) != 0)
            effect = ClipDropEffect.Cut;
        string preview = files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} items";
        var item = new ClipItem
        {
            Kind = ClipKind.Files, Files = files.ToList(), FilesEffect = effect,
            Preview = Truncate(preview), DedupKey = Hash("F:" + string.Join("|", files.Select(f => f.ToUpperInvariant()))),
        };
        return new Candidate { Item = item };
    }

    static Candidate ClassifyRich(string payload, string format, string plain)
    {
        if (plain.Length == 0) plain = format == "html" ? StripHtml(payload) : "(formatted text)";
        var item = new ClipItem
        {
            Kind = ClipKind.RichText, RichFormat = format, PlainText = plain, Preview = Truncate(plain),
            DedupKey = Hash("R:" + plain), BlobBytes = Encoding.UTF8.GetByteCount(payload),
        };
        return new Candidate { Item = item, Blob = Encoding.UTF8.GetBytes(payload) };
    }

    static Candidate ClassifyImage(Image img, long maxImageBytes)
    {
        byte[] bytes = Encode(img);
        if (bytes.Length > maxImageBytes)
        {
            double ratio = Math.Sqrt((double)maxImageBytes / bytes.Length) * 0.9;
            int w = Math.Max(16, (int)(img.Width * ratio)), h = Math.Max(16, (int)(img.Height * ratio));
            using var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
            bytes = Encode(small);   // one resize pass; a still-over-cap result is accepted rather than looping
        }
        var item = new ClipItem
        {
            Kind = ClipKind.Image, Preview = $"Image {img.Width}x{img.Height}", BlobBytes = bytes.Length, DedupKey = Hash("I:" + Convert.ToHexString(MD5.HashData(bytes))),
        };
        return new Candidate { Item = item, Blob = bytes };
    }

    static byte[] Encode(Image img) { using var ms = new MemoryStream(); img.Save(ms, ImageFormat.Png); return ms.ToArray(); }

    /// <summary>CF_DIB is a BITMAPINFOHEADER (or newer) plus an optional colour table/BI_BITFIELDS masks plus pixel
    /// data - exactly a .bmp file's contents minus its 14-byte BITMAPFILEHEADER. Reconstructing that header and
    /// handing the result to GDI+'s normal BMP decoder is simpler and safer than hand-parsing pixel data.</summary>
    static Image DecodeDib(byte[] dib)
    {
        if (dib == null || dib.Length < 40) return null;
        try
        {
            int headerSize = BitConverter.ToInt32(dib, 0);
            short bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = headerSize >= 40 ? BitConverter.ToInt32(dib, 32) : 0;
            int paletteEntries = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bitCount) : 0;
            int maskBytes = compression == 3 ? 12 : 0;   // BI_BITFIELDS: three DWORD colour masks before the pixels
            int offBits = 14 + headerSize + maskBytes + paletteEntries * 4;

            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(offBits).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
            return Image.FromStream(new MemoryStream(bmp));
        }
        catch (ArgumentException) { return null; }   // malformed/unsupported DIB variant; treat as "no image" rather than crash
    }

    static bool TryReadBytes(IDataObject data, string format, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!data.GetDataPresent(format)) return false;
        var raw = data.GetData(format);
        if (raw is MemoryStream ms) { bytes = ms.ToArray(); return true; }
        if (raw is byte[] b) { bytes = b; return true; }
        return false;
    }

    static string Hash(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)));
    static string Truncate(string s) { s = s.Replace('\r', ' ').Replace('\n', ' '); return s.Length > MaxPreview ? s.Substring(0, MaxPreview - 1) + "…" : s; }
    static string StripHtml(string html) => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
}
