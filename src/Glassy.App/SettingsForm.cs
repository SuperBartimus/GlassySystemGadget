using System.Diagnostics;
using System.Globalization;
using Glassy.Core;

namespace Glassy.App;

/// <summary>Multi-page settings: General (window, performance, panel order) plus one page per hardware type. Every edit applies live.</summary>
public sealed class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(28, 20, 40), Bg2 = Color.FromArgb(42, 31, 60), Fg = Color.FromArgb(235, 225, 250), Dim = Color.FromArgb(170, 155, 195);
    readonly AppConfig cfg; readonly Engine engine; readonly Action changed;
    readonly ListBox nav = new(); readonly Panel host = new();
    static readonly PanelKind[] Kinds = { PanelKind.Cpu, PanelKind.Ram, PanelKind.Gpu, PanelKind.Network, PanelKind.Drives, PanelKind.TopProcesses, PanelKind.Uptime, PanelKind.Clipboard, PanelKind.Battery };

    static string PageName(PanelKind k) => k switch
    {
        PanelKind.Cpu => "CPU", PanelKind.Ram => "Memory", PanelKind.Gpu => "GPU", PanelKind.Network => "Network",
        PanelKind.Drives => "Drives", PanelKind.TopProcesses => "Top processes", PanelKind.Clipboard => "Clipboard", PanelKind.Battery => "Battery", _ => "Uptime",
    };

    public SettingsForm(AppConfig cfg, Engine engine, Action changed)
    {
        this.cfg = cfg; this.engine = engine; this.changed = changed;
        Text = "Glassy System Gadget - Settings"; ClientSize = new Size(820, 640); MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);
        nav.Dock = DockStyle.Left; nav.Width = 170; nav.BackColor = Bg2; nav.ForeColor = Fg; nav.BorderStyle = BorderStyle.None;
        nav.Font = new Font("Segoe UI", 10.5f); nav.ItemHeight = 30; nav.DrawMode = DrawMode.OwnerDrawFixed;
        nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.Graphics.FillRectangle(new SolidBrush(e.State.HasFlag(DrawItemState.Selected) ? Color.FromArgb(90, 50, 130) : Bg2), e.Bounds);
            TextRenderer.DrawText(e.Graphics, nav.Items[e.Index].ToString(), nav.Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        nav.Items.Add("General"); foreach (var k in Kinds) nav.Items.Add(PageName(k)); nav.Items.Add("About");
        host.Dock = DockStyle.Fill; host.AutoScroll = true; host.BackColor = Bg;
        Controls.Add(host); Controls.Add(nav);
        nav.SelectedIndexChanged += (_, _) => ShowPage(nav.SelectedIndex);
        nav.SelectedIndex = 0;
    }

    /// <summary>Headless check (--selftest): builds every page against live data and returns how many were built.</summary>
    internal int BuildAllPages()
    {
        for (int i = 0; i <= Kinds.Length + 1; i++) ShowPage(i);
        return Kinds.Length + 2;
    }

    void ShowPage(int i)
    {
        foreach (Control c in host.Controls) c.Dispose();
        host.Controls.Clear();
        host.Controls.Add(i == 0 ? GeneralPage() : i == Kinds.Length + 1 ? AboutPage() : KindPage(Kinds[i - 1]));
    }

    // ------------------------------------------------------------ control helpers
    TableLayoutPanel Table()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 14, 18, 8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }
    void Row(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 7) });
        c.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(c);
    }
    Label Note(string s) => new() { Text = s, AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0), Margin = new Padding(0, 4, 0, 4) };
    Label Heading(string s) => new() { Text = s, AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Padding = new Padding(18, 14, 0, 0), Dock = DockStyle.Top };

    NumericUpDown Num(decimal min, decimal max, decimal val, int dec, decimal inc, Action<decimal> set)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = inc, Width = 110, BackColor = Bg2, ForeColor = Fg };
        n.Value = Math.Clamp(val, min, max);
        n.ValueChanged += (_, _) => { set(n.Value); changed(); };
        return n;
    }
    CheckBox Chk(string text, bool val, Action<bool> set)
    {
        var c = new CheckBox { Text = text, Checked = val, AutoSize = true };
        c.CheckedChanged += (_, _) => { set(c.Checked); changed(); };
        return c;
    }
    ComboBox Combo<T>(T cur, Action<T> set) where T : struct, Enum
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat, BackColor = Bg2, ForeColor = Fg };
        foreach (var v in Enum.GetValues<T>()) c.Items.Add(v);
        c.SelectedItem = cur;
        c.SelectedIndexChanged += (_, _) => { set((T)c.SelectedItem!); changed(); };
        return c;
    }
    Button Btn(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Bg2, ForeColor = Fg, Padding = new Padding(8, 2, 8, 2) };
        b.FlatAppearance.BorderColor = Color.FromArgb(120, 80, 170); b.Click += (_, _) => click(); return b;
    }

    // ------------------------------------------------------------ General page
    Control GeneralPage()
    {
        var g = cfg.Global; var page = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var t = Table();
        Row(t, "Window mode", Combo(g.Mode, v => g.Mode = v));
        Row(t, "Dock to screen edge", Combo(g.Dock, v => g.Dock = v));
        var mon = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat, BackColor = Bg2, ForeColor = Fg };
        var scr = Screen.AllScreens; for (int i = 0; i < scr.Length; i++) mon.Items.Add($"Monitor {i + 1}{(scr[i].Primary ? " (primary)" : "")}");
        mon.SelectedIndex = Math.Clamp(g.Monitor, 0, scr.Length - 1); mon.SelectedIndexChanged += (_, _) => { g.Monitor = mon.SelectedIndex; changed(); };
        Row(t, "Monitor (used when docked)", mon);
        Row(t, "Widget width (px)", Num(240, 900, g.Width, 0, 10, v => g.Width = (int)v));
        var op = new TrackBar { Minimum = 20, Maximum = 100, TickFrequency = 10, Value = (int)Math.Clamp(g.Opacity * 100, 20, 100), Width = 220, AutoSize = false, Height = 30 };
        op.ValueChanged += (_, _) => { g.Opacity = op.Value / 100.0; changed(); };
        Row(t, "Panel opacity", op);
        Row(t, "", Chk("Click-through (launch the app again to reopen Settings)", g.ClickThrough, v => g.ClickThrough = v));
        Row(t, "", Chk("Lock position (no dragging)", g.LockPosition, v => g.LockPosition = v));
        Row(t, "", Chk("Start with Windows", g.StartWithWindows, v => g.StartWithWindows = v));
        page.Controls.Add(t);

        var perf = Table(); perf.Dock = DockStyle.Top;
        Row(perf, "Process priority", Combo(g.Priority, v => g.Priority = v));
        Row(perf, "Update interval (ms)", Num(100, 5000, g.TickMs, 0, 100, v => g.TickMs = (int)v));
        Row(perf, "", Chk("Efficiency mode (EcoQoS: runs on efficient cores when possible)", g.EcoQos, v => g.EcoQos = v));
        Row(perf, "", Chk("Release unused memory periodically", g.TrimMemory, v => g.TrimMemory = v));

        var order = new CheckedListBox { Height = 190, Width = 260, BackColor = Bg2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true };
        void FillOrder(int select)
        {
            order.Items.Clear();
            foreach (var p in cfg.Panels) order.Items.Add(PageName(p.Kind), p.Enabled);
            if (order.Items.Count > 0) order.SelectedIndex = Math.Clamp(select, 0, order.Items.Count - 1);
        }
        FillOrder(0);
        order.ItemCheck += (_, e) =>
        {
            if (e.Index >= cfg.Panels.Count) return;
            // Battery has no "enabled" user preference - visibility is hardware detection only (see KindPage).
            if (cfg.Panels[e.Index].Kind == PanelKind.Battery) { e.NewValue = CheckState.Checked; return; }
            cfg.Panels[e.Index].Enabled = e.NewValue == CheckState.Checked; changed();
        };
        void Move(int d)
        {
            int i = order.SelectedIndex, j = i + d; if (i < 0 || j < 0 || j >= cfg.Panels.Count) return;
            (cfg.Panels[i], cfg.Panels[j]) = (cfg.Panels[j], cfg.Panels[i]); FillOrder(j); changed();
        }
        var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        btns.Controls.Add(Btn("Move up", () => Move(-1))); btns.Controls.Add(Btn("Move down", () => Move(1)));
        var orderRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18, 4, 0, 8) };
        orderRow.Controls.Add(order); orderRow.Controls.Add(btns);

        var reset = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18, 4, 0, 16) };
        reset.Controls.Add(Btn("Reset everything to defaults", () =>
        {
            if (MessageBox.Show(this, "Reset all settings, colours and panel order to defaults?", "Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var d = AppConfig.CreateDefault(); cfg.Global = d.Global; cfg.Panels.Clear(); cfg.Panels.AddRange(d.Panels); changed(); ShowPage(nav.SelectedIndex);
        }));

        var stack = new Panel { Dock = DockStyle.Top, AutoSize = true };
        stack.Controls.Add(reset); stack.Controls.Add(orderRow);
        stack.Controls.Add(new Label { Text = "Panel order (top of list = top of widget) and visibility", AutoSize = true, Padding = new Padding(18, 8, 0, 2), Dock = DockStyle.Top });
        stack.Controls.Add(perf);
        stack.Controls.Add(new Label { Text = "Performance", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Padding = new Padding(18, 12, 0, 0), Dock = DockStyle.Top });
        stack.Controls.Add(page);
        stack.Controls.Add(Heading("General"));
        // Dock.Top stacks in reverse add order, so the heading added last ends up first.
        return stack;
    }

    // ------------------------------------------------------------ About page
    Control AboutPage()
    {
        var stack = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var t = Table();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Row(t, "Version", new Label { Text = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev", AutoSize = true });
        Row(t, "Author", new Label { Text = "Bart Strauss", AutoSize = true });
        var link = new LinkLabel { Text = "github.com/SuperBartimus/GlassySystemGadget", AutoSize = true, LinkColor = Color.FromArgb(180, 140, 255) };
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://github.com/SuperBartimus/GlassySystemGadget") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
        Row(t, "GitHub", link);
        Row(t, "License", new Label { Text = "MIT", AutoSize = true });
        stack.Controls.Add(t);
        stack.Controls.Add(Heading("About"));
        return stack;
    }

    // ------------------------------------------------------------ hardware pages
    static string Duration(double s) => s >= 3600 ? $"{s / 3600:0.#} h" : s >= 90 ? $"{s / 60:0.#} min" : $"{s:0.#} s";

    string TimeInfo(PanelConfig p)
    {
        var t = new TimeLayout((int)(cfg.Global.Width - 2 * Engine.Margin - 16), p.SplitPercent, p.LiveSeconds, p.HistorySeconds, cfg.Global.TickMs);
        double tick = cfg.Global.TickMs / 1000.0;
        return $"Live side: {t.LiveCells} px, {t.TicksPerLive * tick:0.##} s per pixel = {Duration(t.LiveCells * t.TicksPerLive * tick)}.   " +
               $"History side: {t.HistCells} px, {Duration(t.TicksPerHist * tick)} per pixel = {Duration(t.HistCells * t.TicksPerHist * tick)}.";
    }

    Control KindPage(PanelKind kind)
    {
        var p = cfg.Panels.FirstOrDefault(x => x.Kind == kind);
        var stack = new Panel { Dock = DockStyle.Top, AutoSize = true };
        if (p == null) { stack.Controls.Add(Note("This panel is not in the current config. Use Reset on the General page.")); return stack; }
        bool series = kind == PanelKind.Cpu || kind == PanelKind.Ram || kind == PanelKind.Gpu || kind == PanelKind.Network || kind == PanelKind.Drives;
        var t = Table();
        if (kind == PanelKind.Battery)
            Row(t, "", Note("Shown automatically when Windows reports a battery; there is no manual on/off for it."));
        else
            Row(t, "", Chk("Show this panel", p.Enabled, v => p.Enabled = v));
        if (series)
        {
            var info = Note(TimeInfo(p)); void Refresh() => info.Text = TimeInfo(p);
            Row(t, kind == PanelKind.Drives ? "Activity graph height (px)" : "Height (px)", Num(kind == PanelKind.Drives ? 20 : 50, 500, p.Height, 0, 5, v => p.Height = (int)v));
            if (kind == PanelKind.Drives)
            {
                Row(t, "", Chk("Show activity graph (read / write)", p.ShowActivity, v => p.ShowActivity = v));
                Row(t, "", Chk("Show space bar", p.ShowSpaceBar, v => p.ShowSpaceBar = v));
            }
            Row(t, "Live side shows (seconds)", Num(2, 7200, (decimal)p.LiveSeconds, 0, 5, v => { p.LiveSeconds = (double)v; Refresh(); }));
            Row(t, "History side shows (hours)", Num(0.05m, 336, (decimal)(p.HistorySeconds / 3600), 2, 0.5m, v => { p.HistorySeconds = (double)v * 3600; Refresh(); }));
            Row(t, "History share of width (%)", Num(30, 95, p.SplitPercent, 0, 1, v => { p.SplitPercent = (int)v; Refresh(); }));
            Row(t, "Older history keeps", Combo(p.History, v => p.History = v));
            Row(t, "Vertical scale", Combo(p.Scale, v => p.Scale = v));
            Row(t, "Fixed scale maximum", Num(1, 1_000_000_000, (decimal)p.FixedMax, 0, 10, v => p.FixedMax = (double)v));
            t.Controls.Add(new Label()); t.Controls.Add(info);
            t.Controls.Add(new Label()); t.Controls.Add(Note("Changing the durations or split restarts that graph's history."));
        }
        else if (kind == PanelKind.TopProcesses)
        {
            Row(t, "Rows", Num(1, 15, p.Count, 0, 1, v => p.Count = (int)v));
            Row(t, "Sort by", Combo(p.SortByMemory ? SortBy.Memory : SortBy.Cpu, v => p.SortByMemory = v == SortBy.Memory));
            Row(t, "Bar is full and red at (%)", Num(1, 100, (decimal)p.BarFullScale, 0, 5, v => p.BarFullScale = (double)v));
            t.Controls.Add(new Label());
            t.Controls.Add(Note("Percent of total CPU (or of physical memory when sorting by memory). Lower it so small processes show a visible bar; 100 means the bar only fills for a process using the whole machine."));
        }
        else if (kind == PanelKind.Clipboard)
        {
            Row(t, "Pane height (px)", Num(80, 900, p.Height, 0, 5, v => p.Height = (int)v));
            Row(t, "Max preview lines per item", Num(1, 40, p.ClipMaxPreviewLines, 0, 1, v => p.ClipMaxPreviewLines = (int)v));
            Row(t, "Items kept", Num(5, 2000, p.ClipMaxItems, 0, 5, v => p.ClipMaxItems = (int)v));
            Row(t, "Image size cap (MB)", Num(0.1m, 50m, (decimal)p.ClipMaxImageBytes / 1048576m, 1, 0.5m, v => p.ClipMaxImageBytes = (long)(v * 1048576m)));
            Row(t, "", Chk("Play a sound when something new is copied", p.ClipPlaySound, v => p.ClipPlaySound = v));
            Row(t, "", Chk("Put the most recent item back on the clipboard at startup", p.ClipRestoreOnStartup, v => p.ClipRestoreOnStartup = v));
            Row(t, "", Chk("Search box starts open", p.ClipSearchExpanded, v => p.ClipSearchExpanded = v));
            Row(t, "Scroll bar width (px, 0 hides it)", Num(0, 6, p.ClipScrollBarWidth, 0, 1, v => p.ClipScrollBarWidth = (int)v));
            Row(t, "", Chk("Skip anything marked \"exclude from clipboard history\" (recommended)", p.ClipHonorHistoryFlag, v => p.ClipHonorHistoryFlag = v));
            t.Controls.Add(new Label());
            t.Controls.Add(Note("Most password managers mark what they copy this way, so GSG skips it automatically. Turning " +
                "this off trades that safety net for something else: Remote Desktop's own clipboard bridge (rdpclip.exe) marks " +
                "everything it carries between an RDP session and the local machine the same way, for its own unrelated reasons - " +
                "so with this on, nothing copied on the other side of an RDP connection is ever captured here. Turn it off only " +
                "if you rely on RDP clipboard sync and accept that GSG can no longer tell that case apart from a password manager " +
                "asking to be skipped."));
            t.Controls.Add(new Label());
            t.Controls.Add(Note("A row grows to fit its content - short clips take less space - up to the max preview lines above " +
                "(an image counts its scaled height as roughly that many lines). Longer text is truncated with … rather than cut " +
                "off silently; a long file list shows a \"+N more\" line instead of listing every path."));
            t.Controls.Add(new Label());
            t.Controls.Add(Note("Pinned items are never deleted by the item-count limit. Anything you copy - including plain " +
                "text like an API key - is stored as plain text on disk unless skipped above. There is no encryption."));
        }
        else if (kind == PanelKind.Battery)
        {
            Row(t, "Height (px)", Num(50, 300, p.Height, 0, 5, v => p.Height = (int)v));
            Row(t, "Battery icon side", Combo(p.BatteryIconLeft ? Side.Left : Side.Right, v => p.BatteryIconLeft = v == Side.Left));
            t.Controls.Add(new Label());
            t.Controls.Add(Note("Green graph line and glyph while charging; yellow to orange to red while discharging, by charge level."));
        }

        // Dock.Top stacks in reverse add order: the last control added ends up at the top.
        if (series && p.Lines.Count > 0) { stack.Controls.Add(LinesGrid(p)); stack.Controls.Add(LineButtons(p)); }
        if (kind == PanelKind.Network) stack.Controls.Add(NetworkExtras(p));
        if (kind == PanelKind.Drives) stack.Controls.Add(DrivesExtras(p));
        if (kind == PanelKind.Clipboard) stack.Controls.Add(ClipboardExtras(p));
        stack.Controls.Add(t);
        stack.Controls.Add(Heading(PageName(kind)));
        return stack;
    }

    enum SortBy { Cpu, Memory }
    enum Side { Left, Right }

    CheckedListBox ChecklistBox(int height) => new()
    {
        Height = height, Width = 420, BackColor = Bg2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true,
    };

    Control NetworkExtras(PanelConfig p)
    {
        var box = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 18, 8) };
        var list = ChecklistBox(120);
        var all = new RadioButton { Text = "All physical adapters (one combined graph)", AutoSize = true, Checked = !p.SeparateAdapters, Dock = DockStyle.Top };
        var sel = new RadioButton { Text = "Selected adapters (one graph each; tick them below)", AutoSize = true, Checked = p.SeparateAdapters, Dock = DockStyle.Top };
        var names = engine.CurrentAdapters.ToList(); foreach (var a in p.Adapters.Where(a => !names.Contains(a))) names.Add(a);
        foreach (var n in names) list.Items.Add(n, p.Adapters.Contains(n));
        list.Enabled = p.SeparateAdapters;
        sel.CheckedChanged += (_, _) => { p.SeparateAdapters = sel.Checked; list.Enabled = sel.Checked; changed(); };
        list.ItemCheck += (_, e) =>
        {
            var n = (string)list.Items[e.Index];
            if (e.NewValue == CheckState.Checked) { if (!p.Adapters.Contains(n)) p.Adapters.Add(n); } else p.Adapters.Remove(n);
            changed();
        };
        var head = new Label { Text = "Adapters", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 4) };
        list.Dock = DockStyle.Top;
        box.Controls.Add(list); box.Controls.Add(sel); box.Controls.Add(all); box.Controls.Add(head);
        return box;
    }

    Control DrivesExtras(PanelConfig p)
    {
        var box = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 18, 8) };
        var list = ChecklistBox(130); list.Dock = DockStyle.Top;
        var now = engine.CurrentVolumes.ToList();
        foreach (var v in now)
        {
            string kind = v.Kind switch { VolKind.Network => "network", VolKind.Optical => "CD/DVD", VolKind.Removable => "removable", _ => "local" };
            string ro = v.ReadOnly ? ", read-only" : "";
            list.Items.Add($"{v.Letter}:  {(v.Label == "" ? "" : v.Label + "  ")}({kind}{ro}, {Fmt.Bytes(v.Total)})", !p.HiddenDrives.Contains(v.Letter, StringComparer.OrdinalIgnoreCase));
        }
        var letters = now.Select(v => v.Letter).ToList();
        foreach (var h in p.HiddenDrives.Where(h => !letters.Contains(h, StringComparer.OrdinalIgnoreCase)))
        { list.Items.Add($"{h}:  (not connected)", false); letters.Add(h); }
        list.ItemCheck += (_, e) =>
        {
            if (e.Index >= letters.Count) return;
            string l = letters[e.Index];
            p.HiddenDrives.RemoveAll(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
            if (e.NewValue != CheckState.Checked) p.HiddenDrives.Add(l);
            changed();
        };
        var head = new Label { Text = "Drives (untick to hide; new drives appear automatically)", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 4) };
        box.Controls.Add(list); box.Controls.Add(head);
        return box;
    }

    Control ClipboardExtras(PanelConfig p)
    {
        var box = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 18, 8) };
        var soundLabel = new Label { AutoSize = true, ForeColor = Dim, Text = p.ClipSoundFile == "" ? "System sound" : Path.GetFileName(p.ClipSoundFile), Margin = new Padding(0, 6, 10, 0) };
        var pick = Btn("Choose sound file...", () =>
        {
            using var dlg = new OpenFileDialog { Filter = "Sound files (*.wav)|*.wav|All files (*.*)|*.*", CheckFileExists = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            p.ClipSoundFile = dlg.FileName; soundLabel.Text = Path.GetFileName(dlg.FileName); changed();
        });
        var reset = Btn("Use system sound", () => { p.ClipSoundFile = ""; soundLabel.Text = "System sound"; changed(); });
        var soundRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 8) };
        soundRow.Controls.Add(soundLabel); soundRow.Controls.Add(pick); soundRow.Controls.Add(reset);

        var countLabel = new Label { AutoSize = true, ForeColor = Dim, Text = $"{engine.Clipboard.Items.Count} items currently stored ({engine.Clipboard.Items.Count(i => i.Pinned)} pinned).", Margin = new Padding(0, 6, 0, 4) };
        var clear = Btn("Clear history (keep pinned)", () =>
        {
            if (MessageBox.Show(this, "Delete every unpinned clipboard item? This cannot be undone.", "Clear clipboard history", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            int n = engine.Clipboard.ClearUnpinned();
            countLabel.Text = $"{engine.Clipboard.Items.Count} items currently stored ({engine.Clipboard.Items.Count(i => i.Pinned)} pinned).";
            changed();
        });
        var clearRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 0, 0, 8) };
        clearRow.Controls.Add(countLabel); clearRow.Controls.Add(clear);

        // Dock.Top stacks in reverse add order, so this list is added bottom-section-first.
        box.Controls.Add(soundRow);
        box.Controls.Add(new Label { Text = "Sound", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 2) });
        box.Controls.Add(clearRow);
        box.Controls.Add(new Label { Text = "History", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 2) });
        return box;
    }

    Control LineButtons(PanelConfig p)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18, 0, 0, 4) };
        f.Controls.Add(Btn("Show all lines", () => { foreach (var l in p.Lines) l.Visible = true; changed(); Reload(); }));
        f.Controls.Add(Btn("Hide all but first", () => { for (int i = 0; i < p.Lines.Count; i++) p.Lines[i].Visible = i == 0; changed(); Reload(); }));
        return f;
        void Reload() => ShowPage(nav.SelectedIndex);
    }

    Control LinesGrid(PanelConfig p)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Top, Height = Math.Min(360, 34 + p.Lines.Count * 24), AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = Bg2, GridColor = Color.FromArgb(70, 55, 95), BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ScrollBars = ScrollBars.Vertical,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(60, 40, 90); grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Bg2; grid.DefaultCellStyle.ForeColor = Fg; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(90, 50, 130); grid.DefaultCellStyle.SelectionForeColor = Fg;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Show", Width = 50 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Line", ReadOnly = true, Width = 200 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Colour (click)", ReadOnly = true, Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Width", Width = 60 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Glow", Width = 50 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Fill", Width = 50 });
        foreach (var l in p.Lines)
        {
            int r = grid.Rows.Add(l.Visible, l.Label, l.Color, l.Width.ToString("0.0#", CultureInfo.CurrentCulture), l.Glow, l.Fill);
            grid.Rows[r].Tag = l; Swatch(grid.Rows[r].Cells[2], l.Color);
        }
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Rows[e.RowIndex].Tag is not LineConfig l) return;
            var c = grid.Rows[e.RowIndex].Cells;
            switch (e.ColumnIndex)
            {
                case 0: l.Visible = (bool)c[0].Value; break;
                case 4: l.Glow = (bool)c[4].Value; break;
                case 5: l.Fill = (bool)c[5].Value; break;
                case 3:
                    if (float.TryParse(c[3].Value?.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, out var w)) l.Width = Math.Clamp(w, 0.5f, 8f);
                    c[3].Value = l.Width.ToString("0.0#", CultureInfo.CurrentCulture); break;
                default: return;
            }
            changed();
        };
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 2 || grid.Rows[e.RowIndex].Tag is not LineConfig l) return;
            using var dlg = new ColorDialog { FullOpen = true, Color = ColorFromHex(l.Color) };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            l.Color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"; grid.Rows[e.RowIndex].Cells[2].Value = l.Color; Swatch(grid.Rows[e.RowIndex].Cells[2], l.Color); changed();
        };
        return grid;
    }

    static Color ColorFromHex(string h)
    {
        try { return ColorTranslator.FromHtml(h); } catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { return Color.White; }
    }
    static void Swatch(DataGridViewCell c, string hex)
    {
        var col = ColorFromHex(hex); c.Style.BackColor = col; c.Style.SelectionBackColor = col;
        var fg = col.GetBrightness() > 0.55 ? Color.Black : Color.White; c.Style.ForeColor = fg; c.Style.SelectionForeColor = fg;
    }
}
