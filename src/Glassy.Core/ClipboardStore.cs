using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glassy.Core;

/// <summary>
/// Persisted clipboard history: newest first, JSON index plus one blob file per RichText/Image item.
/// Pure storage logic (dedup, trim, pin, search, delete) - no OS clipboard access, so it is fully unit-testable.
/// </summary>
public sealed class ClipboardStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    readonly string dir;

    /// <summary>Newest first. Mutated in place (never reassigned), so a reference taken once stays live.</summary>
    public List<ClipItem> Items { get; } = new();

    /// <summary>Id of whichever item is currently on the OS clipboard - "" if none (nothing captured or restored
    /// yet this run). Not persisted: it reflects live OS clipboard state, which a restart doesn't preserve either.
    /// Set here on every genuine capture; the App layer sets it too after a successful restore-to-clipboard.</summary>
    public string SelectedId { get; set; } = "";

    public ClipboardStore(string dir) { this.dir = dir; Load(); }

    string IndexPath => Path.Combine(dir, "index.json");
    string BlobDir => Path.Combine(dir, "blobs");
    string BlobPath(string fileName) => Path.Combine(BlobDir, fileName);

    /// <summary>Adds a classified candidate, deduping by content and trimming to maxItems (pinned items are exempt). Returns true if it changed anything worth a re-render/sound.</summary>
    public bool Add(ClipboardClassifier.Candidate candidate, int maxItems)
    {
        if (candidate?.Item == null) return false;
        var item = candidate.Item;
        var dup = Items.FirstOrDefault(i => i.DedupKey == item.DedupKey);
        if (dup != null) { Items.Remove(dup); item.Pinned = dup.Pinned; DeleteBlob(dup); }   // same content again: replace, keep its pin

        if (candidate.Blob != null)
        {
            Directory.CreateDirectory(BlobDir);
            string ext = item.Kind == ClipKind.Image ? "png" : item.RichFormat;
            item.BlobFile = item.Id + "." + ext;
            File.WriteAllBytes(BlobPath(item.BlobFile), candidate.Blob);
        }

        Items.Insert(0, item);
        Trim(maxItems);
        Save();
        SelectedId = item.Id;
        return true;
    }

    void Trim(int maxItems)
    {
        var unpinned = Items.Where(i => !i.Pinned).ToList();
        foreach (var extra in unpinned.Skip(Math.Max(0, maxItems))) { Items.Remove(extra); DeleteBlob(extra); }
    }

    public bool Delete(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id); if (item == null) return false;
        Items.Remove(item); DeleteBlob(item); Save(); return true;
    }

    public bool TogglePin(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id); if (item == null) return false;
        item.Pinned = !item.Pinned; Save(); return true;
    }

    /// <summary>Removes every unpinned item and its blob.</summary>
    public int ClearUnpinned()
    {
        var doomed = Items.Where(i => !i.Pinned).ToList();
        foreach (var d in doomed) { Items.Remove(d); DeleteBlob(d); }
        if (doomed.Count > 0) Save();
        return doomed.Count;
    }

    public IEnumerable<ClipItem> Search(string query) =>
        string.IsNullOrWhiteSpace(query) ? Items : Items.Where(i => i.Preview.Contains(query, StringComparison.OrdinalIgnoreCase));

    void DeleteBlob(ClipItem item)
    {
        if (item.BlobFile == "") return;
        try { File.Delete(BlobPath(item.BlobFile)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public byte[] ReadBlobBytes(ClipItem item) => item.BlobFile == "" ? Array.Empty<byte>() : File.ReadAllBytes(BlobPath(item.BlobFile));
    public string ReadBlobText(ClipItem item) => Encoding.UTF8.GetString(ReadBlobBytes(item));

    void Load()
    {
        Items.Clear();
        if (!File.Exists(IndexPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllBytes(IndexPath), Opts);
            if (loaded != null) Items.AddRange(loaded);
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is NotSupportedException)
        {
            try { File.Move(IndexPath, IndexPath + ".bad", true); } catch (IOException) { }
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = IndexPath + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Items, Opts));
            File.Move(tmp, IndexPath, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
