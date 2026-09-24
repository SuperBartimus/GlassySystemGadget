using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class ClassifierTests
{
    static DataObject D() => new();

    [Fact]
    public void Plain_text_classifies_as_Text_with_a_stable_dedup_key()
    {
        var a = ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, "hello world"), 1_000_000);
        var b = ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, "hello world"), 1_000_000);
        Assert.Equal(ClipKind.Text, a.Item.Kind);
        Assert.Equal("hello world", a.Item.Preview);
        Assert.Equal(a.Item.DedupKey, b.Item.DedupKey);   // same content -> same key, regardless of when captured
        Assert.NotEqual(a.Item.DedupKey, ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, "different"), 1_000_000).Item.DedupKey);
    }

    /// <summary>Reproduces the RDP cross-device text bug (2026-09-24): GetDataPresent(UnicodeText) can be true
    /// while GetData(UnicodeText) doesn't come back as a string - confirmed live, where the format list showed
    /// UnicodeText/Text present but neither converted. Forces that exact shape (the format is present, but reading
    /// it back gives raw UTF-16LE bytes, not a string) without needing a real RDP session to do it.</summary>
    [Fact]
    public void Text_reachable_only_as_raw_UTF16_bytes_like_over_RDP_still_classifies()
    {
        var data = D();
        data.SetData(DataFormats.UnicodeText, new MemoryStream(Encoding.Unicode.GetBytes("IPICO Support Clarifications\0")));
        var candidate = ClipboardClassifier.Classify(data, 1_000_000);
        Assert.NotNull(candidate);
        Assert.Equal(ClipKind.Text, candidate.Item.Kind);
        Assert.Equal("IPICO Support Clarifications", candidate.Item.Preview);
    }

    [Fact]
    public void Long_text_preview_is_truncated_and_newlines_flattened()
    {
        var c = ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, "line one\r\nline two " + new string('x', 300)), 1_000_000);
        Assert.DoesNotContain('\n', c.Item.Preview); Assert.True(c.Item.Preview.Length <= 160);
    }

    [Fact]
    public void Html_present_classifies_as_RichText_html_with_plain_fallback_and_blob()
    {
        var d = D(); d.SetData(DataFormats.Html, "<html><body><b>hi</b></body></html>"); d.SetData(DataFormats.UnicodeText, "hi");
        var c = ClipboardClassifier.Classify(d, 1_000_000);
        Assert.Equal(ClipKind.RichText, c.Item.Kind); Assert.Equal("html", c.Item.RichFormat);
        Assert.Equal("hi", c.Item.PlainText); Assert.NotNull(c.Blob);
        Assert.Contains("<b>hi</b>", System.Text.Encoding.UTF8.GetString(c.Blob));
    }

    [Fact]
    public void Rtf_present_without_html_classifies_as_RichText_rtf()
    {
        var d = D(); d.SetData(DataFormats.Rtf, @"{\rtf1 hello}"); d.SetData(DataFormats.UnicodeText, "hello");
        var c = ClipboardClassifier.Classify(d, 1_000_000);
        Assert.Equal(ClipKind.RichText, c.Item.Kind); Assert.Equal("rtf", c.Item.RichFormat);
    }

    [Fact]
    public void Html_without_a_plain_text_fallback_gets_a_tag_stripped_preview()
    {
        var d = D(); d.SetData(DataFormats.Html, "<p>plain <b>words</b> only</p>");
        var c = ClipboardClassifier.Classify(d, 1_000_000);
        Assert.Contains("plain", c.Item.Preview); Assert.DoesNotContain('<', c.Item.Preview);
    }

    [Fact]
    public void File_drop_captures_paths_and_defaults_to_copy_effect()
    {
        var c = ClipboardClassifier.Classify(new DataObject(DataFormats.FileDrop, new[] { @"C:\a.txt" }), 1_000_000);
        Assert.Equal(ClipKind.Files, c.Item.Kind); Assert.Equal(new[] { @"C:\a.txt" }, c.Item.Files);
        Assert.Equal(ClipDropEffect.Copy, c.Item.FilesEffect); Assert.Equal("a.txt", c.Item.Preview);
    }

    [Fact]
    public void File_drop_with_multiple_files_previews_a_count_and_detects_cut()
    {
        var d = D(); d.SetData(DataFormats.FileDrop, new[] { @"C:\a.txt", @"C:\b.txt" });
        d.SetData(ClipboardClassifier.FormatDropEffect, new MemoryStream(BitConverter.GetBytes(2)));   // DROPEFFECT_MOVE
        var c = ClipboardClassifier.Classify(d, 1_000_000);
        Assert.Equal("2 items", c.Item.Preview); Assert.Equal(ClipDropEffect.Cut, c.Item.FilesEffect);
    }

    [Fact]
    public void Image_classifies_with_dimensions_and_downscales_when_over_the_cap()
    {
        using var img = new Bitmap(400, 300);
        using (var g = Graphics.FromImage(img)) g.Clear(Color.Red);   // solid colour compresses tiny either way, but the resize path still runs
        var d = D(); d.SetData(DataFormats.Bitmap, img);
        var full = ClipboardClassifier.Classify(d, 1_000_000_000);
        Assert.Equal(ClipKind.Image, full.Item.Kind); Assert.Contains("400x300", full.Item.Preview);
        var capped = ClipboardClassifier.Classify(d, 200);   // cap far below even a tiny PNG: forces the resize branch
        Assert.True(capped.Blob.Length <= full.Blob.Length);
    }

    [Fact]
    public void Exclude_tag_and_zero_history_flag_are_both_honored()
    {
        var d1 = D(); d1.SetData(DataFormats.UnicodeText, "secret"); d1.SetData(ClipboardClassifier.FormatExclude, "1");
        Assert.Null(ClipboardClassifier.Classify(d1, 1_000_000));
        var d2 = D(); d2.SetData(DataFormats.UnicodeText, "secret"); d2.SetData(ClipboardClassifier.FormatCanIncludeHistory, new MemoryStream(BitConverter.GetBytes(0)));
        Assert.Null(ClipboardClassifier.Classify(d2, 1_000_000));
        var d3 = D(); d3.SetData(DataFormats.UnicodeText, "ok"); d3.SetData(ClipboardClassifier.FormatCanIncludeHistory, new MemoryStream(BitConverter.GetBytes(1)));
        Assert.NotNull(ClipboardClassifier.Classify(d3, 1_000_000));   // a nonzero value is not an exclusion
    }

    /// <summary>rdpclip.exe sets CanIncludeInClipboardHistory=0 on everything it bridges across an RDP session,
    /// unrelated to any app opting out - confirmed by a direct probe of a real RDP-sourced clip, 2026-09-24. The
    /// honorHistoryFlag parameter (PanelConfig.ClipHonorHistoryFlag in Settings) lets that be turned off without
    /// touching the separate, more deliberate ExcludeClipboardContentFromMonitorProcessing signal.</summary>
    [Fact]
    public void Zero_history_flag_can_be_ignored_via_honorHistoryFlag_but_the_explicit_exclude_tag_still_wins()
    {
        var d1 = D(); d1.SetData(DataFormats.UnicodeText, "from rdpclip"); d1.SetData(ClipboardClassifier.FormatCanIncludeHistory, new MemoryStream(BitConverter.GetBytes(0)));
        Assert.Null(ClipboardClassifier.Classify(d1, 1_000_000, honorHistoryFlag: true));
        Assert.NotNull(ClipboardClassifier.Classify(d1, 1_000_000, honorHistoryFlag: false));

        var d2 = D(); d2.SetData(DataFormats.UnicodeText, "secret"); d2.SetData(ClipboardClassifier.FormatExclude, "1");
        d2.SetData(ClipboardClassifier.FormatCanIncludeHistory, new MemoryStream(BitConverter.GetBytes(0)));
        Assert.Null(ClipboardClassifier.Classify(d2, 1_000_000, honorHistoryFlag: false));   // the deliberate tag is never optional
    }

    [Fact]
    public void Empty_or_unsupported_data_yields_nothing()
    {
        Assert.Null(ClipboardClassifier.Classify(null, 1_000_000));
        Assert.Null(ClipboardClassifier.Classify(D(), 1_000_000));                          // no formats at all
        var custom = D(); custom.SetData("SomeApp.PrivateFormat", new byte[] { 1, 2, 3 });
        Assert.Null(ClipboardClassifier.Classify(custom, 1_000_000));                       // a format we don't understand and no text fallback
    }
}

public class ClipboardStoreTests
{
    static string TmpDir() => Path.Combine(Path.GetTempPath(), "glassy-clip-test-" + Guid.NewGuid().ToString("N"));
    static ClipboardClassifier.Candidate Text(string s) => ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, s), 1_000_000);

    [Fact]
    public void Add_then_reload_round_trips_items_and_a_blob()
    {
        var dir = TmpDir();
        var html = new DataObject(); html.SetData(DataFormats.Html, "<b>x</b>"); html.SetData(DataFormats.UnicodeText, "x");
        var store = new ClipboardStore(dir);
        store.Add(Text("one"), 50);
        store.Add(ClipboardClassifier.Classify(html, 1_000_000), 50);
        Assert.Equal(2, store.Items.Count); Assert.Equal("x", store.Items[0].Preview);   // newest first
        Assert.NotEqual("", store.Items[0].BlobFile);

        var reloaded = new ClipboardStore(dir);
        Assert.Equal(2, reloaded.Items.Count);
        Assert.Equal("<b>x</b>", reloaded.ReadBlobText(reloaded.Items[0]));
    }

    [Fact]
    public void Recopying_the_same_text_moves_it_to_the_top_and_keeps_its_pin_instead_of_duplicating()
    {
        var store = new ClipboardStore(TmpDir());
        store.Add(Text("a"), 50); store.Add(Text("b"), 50);
        store.TogglePin(store.Items.First(i => i.Preview == "a").Id);
        store.Add(Text("a"), 50);   // "a" copied again
        Assert.Equal(2, store.Items.Count);                 // not 3 - replaced, not duplicated
        Assert.Equal("a", store.Items[0].Preview);           // bumped to the top
        Assert.True(store.Items[0].Pinned);                  // pin carried forward
    }

    [Fact]
    public void A_capture_marks_itself_as_the_selected_row()
    {
        var store = new ClipboardStore(TmpDir());
        Assert.Equal("", store.SelectedId);
        store.Add(Text("a"), 50);
        Assert.Equal(store.Items[0].Id, store.SelectedId);
        store.Add(Text("b"), 50);
        Assert.Equal(store.Items[0].Id, store.SelectedId);   // the newer capture, not "a"
    }

    [Fact]
    public void Trim_drops_oldest_unpinned_but_never_a_pinned_item()
    {
        var store = new ClipboardStore(TmpDir());
        for (int i = 0; i < 5; i++) store.Add(Text("item" + i), 100);
        store.TogglePin(store.Items.Last().Id);   // pin "item0", the oldest
        store.Add(Text("newest"), 3);             // cap of 3: would otherwise evict item0 too
        Assert.Contains(store.Items, i => i.Preview == "item0" && i.Pinned);
        Assert.True(store.Items.Count(i => !i.Pinned) <= 3);
    }

    [Fact]
    public void Delete_removes_the_item_and_its_blob_file()
    {
        var dir = TmpDir(); var store = new ClipboardStore(dir);
        var html = new DataObject(); html.SetData(DataFormats.Html, "<i>y</i>"); html.SetData(DataFormats.UnicodeText, "y");
        store.Add(ClipboardClassifier.Classify(html, 1_000_000), 50);
        var blobPath = Path.Combine(dir, "blobs", store.Items[0].BlobFile);
        Assert.True(File.Exists(blobPath));
        Assert.True(store.Delete(store.Items[0].Id));
        Assert.Empty(store.Items); Assert.False(File.Exists(blobPath));
        Assert.False(store.Delete("not-an-id"));
    }

    [Fact]
    public void ClearUnpinned_keeps_pinned_items_and_search_filters_by_preview()
    {
        var store = new ClipboardStore(TmpDir());
        store.Add(Text("apple"), 50); store.Add(Text("banana"), 50);
        store.TogglePin(store.Items.First(i => i.Preview == "apple").Id);
        Assert.Equal(1, store.ClearUnpinned());
        Assert.Single(store.Items); Assert.Equal("apple", store.Items[0].Preview);
        store.Add(Text("Apricot"), 50);
        Assert.Equal(2, store.Search("ap").Count());       // case-insensitive, matches apple and Apricot
        Assert.Equal(store.Items.Count, store.Search("").Count());
    }

    [Fact]
    public void A_corrupt_index_is_kept_as_bad_and_the_store_starts_empty()
    {
        var dir = TmpDir(); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.json"), "{ not json");
        var store = new ClipboardStore(dir);
        Assert.Empty(store.Items);
        Assert.True(File.Exists(Path.Combine(dir, "index.json.bad")));
    }
}

public class ClipboardConfigTests
{
    [Fact]
    public void Default_config_includes_an_enabled_clipboard_panel()
    {
        var cfg = AppConfig.CreateDefault();
        var p = cfg.Panels.Single(x => x.Kind == PanelKind.Clipboard);
        Assert.True(p.Enabled); Assert.True(p.ClipMaxItems > 0);
    }

    [Fact]
    public void An_old_config_that_already_migrated_Drives_still_gains_the_clipboard_panel()
    {
        // Regression: Migrate() used to return early once the Drives step had nothing to do, silently
        // skipping every later migration step for a config that had already been through round 2.
        var cfg = new AppConfig { Panels = { new PanelConfig { Kind = PanelKind.Cpu }, AppConfig.DrivesPanel(true) } };
        ConfigStore.Migrate(cfg);
        Assert.Contains(cfg.Panels, p => p.Kind == PanelKind.Clipboard && p.Enabled);
    }

    [Fact]
    public void Migrate_is_idempotent_and_never_adds_a_second_clipboard_panel()
    {
        var cfg = AppConfig.CreateDefault();
        ConfigStore.Migrate(cfg); ConfigStore.Migrate(cfg);
        Assert.Single(cfg.Panels, p => p.Kind == PanelKind.Clipboard);
    }
}

public class ClipboardEngineTests
{
    static AppConfig ClipboardOnly() { var c = AppConfig.CreateDefault(); c.Panels = new List<PanelConfig> { AppConfig.ClipboardPanel(true) }; return c; }

    [Fact]
    public void Clipboard_panel_gets_one_section_sized_from_its_own_height()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glassy-clip-eng-" + Guid.NewGuid().ToString("N"));
        var cfg = ClipboardOnly(); cfg.Panels[0].Height = 260;
        using var e = new Engine(cfg, clipboard: new ClipboardStore(dir));
        Assert.Single(e.Panels); Assert.Equal(PanelKind.Clipboard, e.Panels[0].Cfg.Kind);
        Assert.Equal(260, e.Panels[0].Height); Assert.Null(e.Panels[0].Source);   // not a graph
    }

    [Fact]
    public void A_short_configured_height_is_clamped_to_the_minimum()
    {
        var cfg = ClipboardOnly(); cfg.Panels[0].Height = 10;
        using var e = new Engine(cfg, clipboard: new ClipboardStore(Path.Combine(Path.GetTempPath(), "glassy-clip-eng2-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(Engine.ClipMinHeight, e.Panels[0].Height);
    }

    [Fact]
    public void Engine_exposes_the_same_store_items_added_through_it()
    {
        var store = new ClipboardStore(Path.Combine(Path.GetTempPath(), "glassy-clip-eng3-" + Guid.NewGuid().ToString("N")));
        using var e = new Engine(ClipboardOnly(), clipboard: store);
        e.Clipboard.Add(ClipboardClassifier.Classify(new DataObject(DataFormats.UnicodeText, "hi"), 1_000_000), 50);
        Assert.Single(e.Clipboard.Items); Assert.Same(store.Items, e.Clipboard.Items);
    }
}

/// <summary>
/// One real OS-clipboard round trip, run on a dedicated STA thread (WinForms Clipboard requires STA; xunit's own
/// thread is MTA). Bart's actual clipboard content is saved before and restored after, best-effort, so this test
/// does not leave his clipboard holding test data.
/// </summary>
public class LiveClipboardRoundTripTest
{
    [Fact]
    public void Real_clipboard_text_round_trips_through_the_classifier()
    {
        Exception failure = null; string preview = null;
        var t = new Thread(() =>
        {
            string saved = null; bool hadSaved = false;
            try
            {
                try { if (System.Windows.Forms.Clipboard.ContainsText()) { saved = System.Windows.Forms.Clipboard.GetText(); hadSaved = true; } }
                catch (System.Runtime.InteropServices.ExternalException) { }   // another app has the clipboard locked right now; nothing to save

                const string marker = "glassy-clipboard-test-4f2b";
                System.Windows.Forms.Clipboard.SetText(marker);
                var candidate = ClipboardClassifier.Classify(System.Windows.Forms.Clipboard.GetDataObject(), 1_000_000);
                preview = candidate?.Item.Preview;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { if (hadSaved) System.Windows.Forms.Clipboard.SetText(saved); else System.Windows.Forms.Clipboard.Clear(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(5000);
        Assert.Null(failure);
        Assert.Equal("glassy-clipboard-test-4f2b", preview);
    }

    /// <summary>Reproduces the RDP cross-device bug (2026-09-24): CF_BITMAP (DataFormats.Bitmap) can be listed as
    /// present yet fail to produce an Image, because a live GDI handle can't cross an RDP session boundary - only
    /// CF_DIB's raw bytes can. Uses a real Windows-generated DIB (round-tripped through the actual OS clipboard,
    /// not hand-built bytes) with the Bitmap format deliberately stripped out, so this exercises the same decode
    /// path a real RDP-redirected image would hit.</summary>
    [Fact]
    public void An_image_reachable_only_via_raw_DIB_bytes_like_over_RDP_still_classifies()
    {
        Exception failure = null; Glassy.Core.ClipItem item = null;
        var t = new Thread(() =>
        {
            System.Windows.Forms.IDataObject saved = null;
            try
            {
                try { saved = System.Windows.Forms.Clipboard.GetDataObject(); } catch (System.Runtime.InteropServices.ExternalException) { }

                using var bmp = new System.Drawing.Bitmap(12, 8);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.FromArgb(255, 30, 200, 90));
                System.Windows.Forms.Clipboard.SetImage(bmp);

                var real = System.Windows.Forms.Clipboard.GetDataObject();
                var dibOnly = new System.Windows.Forms.DataObject();
                dibOnly.SetData(System.Windows.Forms.DataFormats.Dib, real.GetData(System.Windows.Forms.DataFormats.Dib));

                var candidate = ClipboardClassifier.Classify(dibOnly, 5_000_000);
                item = candidate?.Item;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { if (saved != null) System.Windows.Forms.Clipboard.SetDataObject(saved, true); else System.Windows.Forms.Clipboard.Clear(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(5000);
        Assert.Null(failure);
        Assert.NotNull(item);
        Assert.Equal(ClipKind.Image, item.Kind);
        Assert.Equal("Image 12x8", item.Preview);
    }
}

public class ClipboardLayoutTests
{
    static float[] Uniform(int count, float h) { var a = new float[count]; Array.Fill(a, h); return a; }

    [Fact]
    public void Row_icons_are_ordered_delete_pin_open_right_to_left_with_no_overlap_and_anchored_to_the_top()
    {
        var g = ClipboardLayout.Row(10, 300, 50);
        Assert.True(g.DeleteX > g.PinX + Engine.ClipIcon);
        Assert.True(g.PinX > g.OpenX + Engine.ClipIcon);
        Assert.True(g.HeaderRight < g.OpenX);      // the header (type + age) line must stop short of the icons
        Assert.Equal(300f, g.TextRight);           // the body below the icon strip can use the full row width
        Assert.True(g.TextLeft > g.TypeIconX);
        Assert.Equal(300f - Engine.ClipIcon, g.DeleteX);
        Assert.Equal(50f + Engine.ClipIconTopPad, g.IconY);   // top-anchored, not centred in the row
    }

    [Fact]
    public void Row_height_grows_with_line_count_up_to_the_cap_and_never_below_one_line()
    {
        Assert.Equal(ClipboardLayout.RowHeightForLines(1), ClipboardLayout.RowHeightForLines(0));   // never shorter than one line
        Assert.True(ClipboardLayout.RowHeightForLines(3) > ClipboardLayout.RowHeightForLines(1));
        Assert.Equal(ClipboardLayout.MaxRowHeight(11), ClipboardLayout.RowHeightForLines(11));
        Assert.True(ClipboardLayout.RowHeightForImage(100) > ClipboardLayout.RowHeightForImage(20));
    }

    [Fact]
    public void Scroll_clamps_to_zero_when_everything_fits_and_to_overflow_otherwise()
    {
        Assert.Equal(0, ClipboardLayout.MaxScroll(400, false, Uniform(3, 30)));            // 3 short rows easily fit a 400px pane
        float over = ClipboardLayout.MaxScroll(100, false, Uniform(20, 30));               // 20 rows do not fit a 100px pane
        Assert.True(over > 0);
        Assert.Equal(over, ClipboardLayout.ClampScroll(999999, 100, false, Uniform(20, 30)));
        Assert.Equal(0, ClipboardLayout.ClampScroll(-50, 100, false, Uniform(20, 30)));
    }

    [Fact]
    public void Taller_rows_use_more_scroll_room_than_shorter_ones_for_the_same_item_count()
    {
        var shortRows = ClipboardLayout.MaxScroll(150, false, Uniform(5, 20));
        var tallRows = ClipboardLayout.MaxScroll(150, false, Uniform(5, 80));
        Assert.True(tallRows > shortRows);
    }

    [Fact]
    public void ScrollToShow_leaves_an_already_visible_row_alone()
    {
        // panel 200 tall, header 26 -> list area 174px; rows are 30px, so row 3 (top=90,bottom=120) is fully visible at scroll 0
        var scroll = ClipboardLayout.ScrollToShow(3, 200, false, Uniform(10, 30), 0);
        Assert.Equal(0, scroll);
    }

    [Fact]
    public void ScrollToShow_aligns_to_top_when_the_row_is_above_the_view_and_bottom_when_below()
    {
        var rows = Uniform(10, 30);
        // scrolled to show rows starting at 150; row 2 (top=60) is above that -> snap its top into view
        Assert.Equal(60, ClipboardLayout.ScrollToShow(2, 200, false, rows, 150));
        // list area is 174px; row 8 (top=240,bottom=270) is below a scroll of 0 -> snap its bottom into view
        Assert.Equal(270 - (200 - Engine.ClipHeader), ClipboardLayout.ScrollToShow(8, 200, false, rows, 0));
    }

    [Fact]
    public void ScrollThumb_is_hidden_when_everything_fits_and_proportional_otherwise()
    {
        Assert.Equal(0, ClipboardLayout.ScrollThumb(400, false, Uniform(3, 30), 0).height);   // 3 short rows fit easily
        var (top0, h) = ClipboardLayout.ScrollThumb(200, false, Uniform(20, 30), 0);
        Assert.True(h > 0 && h < 200 - Engine.ClipHeader);   // visible, but shorter than the track
        Assert.Equal(0, top0);                                // scrolled to the very top
        var (topMax, _) = ClipboardLayout.ScrollThumb(200, false, Uniform(20, 30), ClipboardLayout.MaxScroll(200, false, Uniform(20, 30)));
        Assert.True(topMax > top0);                            // scrolled to the bottom: thumb moved down
    }

    [Fact]
    public void Hit_test_finds_the_search_toggle_in_the_header_and_ignores_the_rest_of_the_header()
    {
        var (idx, hit) = ClipboardLayout.HitTest(panelTop: 0, panelHeight: 200, panelWidth: 300, searchVisible: false, Uniform(5, 30), scrollOffset: 0, mouseX: 290, mouseY: 10);
        Assert.Equal(ClipHit.Search, hit); Assert.Equal(-1, idx);
        var (idx2, hit2) = ClipboardLayout.HitTest(0, 200, 300, false, Uniform(5, 30), 0, mouseX: 50, mouseY: 10);
        Assert.Equal(ClipHit.None, hit2); Assert.Equal(-1, idx2);
    }

    [Fact]
    public void Hit_test_maps_a_click_to_the_right_row_and_the_right_icon()
    {
        float rowTop = 0 + Engine.ClipHeader;   // first row, no scroll, no search row
        var g = ClipboardLayout.Row(Engine.Margin + 10, 300 - Engine.Margin - 10, rowTop);
        var rows = Uniform(5, 30); float iconY = g.IconY + 2;
        var (rowIdx, rowHit) = ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, mouseX: g.TextLeft + 2, mouseY: rowTop + rows[0] - 1);
        Assert.Equal((0, ClipHit.Row), (rowIdx, rowHit));
        var (delIdx, delHit) = ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, mouseX: g.DeleteX + 2, mouseY: iconY);
        Assert.Equal((0, ClipHit.Delete), (delIdx, delHit));
        var (pinIdx, pinHit) = ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, mouseX: g.PinX + 2, mouseY: iconY);
        Assert.Equal((0, ClipHit.Pin), (pinIdx, pinHit));
        var (openIdx, openHit) = ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, mouseX: g.OpenX + 2, mouseY: iconY);
        Assert.Equal((0, ClipHit.Open), (openIdx, openHit));
    }

    [Fact]
    public void Hit_test_walks_variable_row_heights_to_find_the_right_index()
    {
        var rows = new float[] { 20, 50, 30 };   // row 0: 0-20, row 1: 20-70, row 2: 70-100
        Assert.Equal(0, ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, 100, Engine.ClipHeader + 10).index);
        Assert.Equal(1, ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, 100, Engine.ClipHeader + 25).index);
        Assert.Equal(2, ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, 100, Engine.ClipHeader + 90).index);
        Assert.Equal(-1, ClipboardLayout.HitTest(0, 300, 300, false, rows, 0, 100, Engine.ClipHeader + 150).index);   // past the last row
    }

    [Fact]
    public void Hit_test_accounts_for_scroll_offset_and_refuses_a_row_past_the_item_count()
    {
        var rows = Uniform(3, 30);
        var (idx, hit) = ClipboardLayout.HitTest(0, 300, 300, false, rows, scrollOffset: 30, mouseX: 100, mouseY: Engine.ClipHeader + 5);
        Assert.Equal(1, idx); Assert.Equal(ClipHit.Row, hit);   // scrolled down one row, so the row now at the top is index 1
        var (idxOut, hitOut) = ClipboardLayout.HitTest(0, 300, 300, false, rows, scrollOffset: 300, mouseX: 100, mouseY: Engine.ClipHeader + 5);
        Assert.Equal(ClipHit.None, hitOut); Assert.Equal(-1, idxOut);
    }

    [Fact]
    public void When_the_search_row_is_visible_the_list_starts_one_header_lower()
    {
        var rows = Uniform(5, 30);
        var (idxNoSearch, _) = ClipboardLayout.HitTest(0, 300, 300, searchVisible: false, rows, scrollOffset: 0, mouseX: 100, mouseY: Engine.ClipHeader + 5);
        var (idxAtSameY, hitAtSameY) = ClipboardLayout.HitTest(0, 300, 300, searchVisible: true, rows, scrollOffset: 0, mouseX: 100, mouseY: Engine.ClipHeader + 5);
        Assert.Equal(0, idxNoSearch);
        Assert.Equal(ClipHit.None, hitAtSameY);   // that y now falls inside the search row, not row 0
        var (idxLower, hitLower) = ClipboardLayout.HitTest(0, 300, 300, searchVisible: true, rows, scrollOffset: 0, mouseX: 100, mouseY: Engine.ClipHeader * 2 + 5);
        Assert.Equal(0, idxLower); Assert.Equal(ClipHit.Row, hitLower);
    }

    [Fact]
    public void Hit_test_outside_the_panel_bounds_returns_nothing()
    {
        Assert.Equal((-1, ClipHit.None), ClipboardLayout.HitTest(50, 200, 300, false, Uniform(5, 30), 0, 100, 40));    // above the panel
        Assert.Equal((-1, ClipHit.None), ClipboardLayout.HitTest(50, 200, 300, false, Uniform(5, 30), 0, 100, 300));   // below the panel
    }
}

public class ClipboardIsolationTests
{
    [Fact]
    public void Two_different_config_paths_get_two_different_clipboard_folders()
    {
        // Regression: the clipboard store used to always resolve to the real %AppData% folder regardless of
        // --config, so a throwaway test config would still read and write Bart's real clipboard history.
        var a = Engine.ClipboardDirFor(@"C:\temp\one\config.json");
        var b = Engine.ClipboardDirFor(@"C:\temp\two\config.json");
        Assert.NotEqual(a, b);
        Assert.EndsWith("Clipboard", a);
    }

    [Fact]
    public void An_engine_built_with_a_configPath_does_not_touch_the_real_AppData_clipboard_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glassy-isolate-" + Guid.NewGuid().ToString("N"));
        var cfgPath = Path.Combine(dir, "config.json");
        var realDir = Engine.DefaultClipboardDir;
        bool realExistedBefore = Directory.Exists(realDir);
        using var e = new Engine(AppConfig.CreateDefault(), configPath: cfgPath);
        e.Clipboard.Add(ClipboardClassifier.Classify(new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.UnicodeText, "isolation-check"), 1_000_000), 50);
        Assert.True(File.Exists(Path.Combine(Engine.ClipboardDirFor(cfgPath), "index.json")));
        Assert.Equal(realExistedBefore, Directory.Exists(realDir));   // the real folder's existence is unchanged either way
        if (realExistedBefore) Assert.DoesNotContain(new ClipboardStore(realDir).Items, i => i.Preview == "isolation-check");
    }
}

public class ClipboardPreviewConfigTests
{
    [Fact]
    public void Default_clipboard_panel_caps_previews_at_eleven_lines()
    {
        var p = AppConfig.CreateDefault().Panels.Single(x => x.Kind == PanelKind.Clipboard);
        Assert.Equal(11, p.ClipMaxPreviewLines);
    }

    [Fact]
    public void ClipMaxPreviewLines_round_trips_through_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "glassy-test-" + Guid.NewGuid().ToString("N"), "config.json");
        var cfg = AppConfig.CreateDefault();
        cfg.Panels.Single(x => x.Kind == PanelKind.Clipboard).ClipMaxPreviewLines = 5;
        ConfigStore.Save(cfg, path);
        var back = ConfigStore.Load(path);
        Assert.Equal(5, back.Panels.Single(x => x.Kind == PanelKind.Clipboard).ClipMaxPreviewLines);
    }
}
