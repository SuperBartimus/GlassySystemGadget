using System.Diagnostics;
using System.Runtime.InteropServices;
using Glassy.Core;
using Microsoft.Win32;
using static Glassy.App.Win32;

namespace Glassy.App;

/// <summary>The widget window: a per-pixel-alpha layered popup that the renderer paints once per tick.</summary>
public sealed class MainWindow : Form
{
    readonly string cfgPath; readonly AppConfig cfg; readonly Engine engine;
    D2DRenderer renderer;                        // null until created; dropped and rebuilt after a GPU device loss
    int pxW, pxH, failStreak, seenLayout; bool presentLogged;
    readonly System.Windows.Forms.Timer timer = new(), saveTimer = new() { Interval = 800 }, trimTimer = new() { Interval = 30000 };
    readonly uint appBarMsg = RegisterWindowMessage("GlassySystemGadget.AppBar");
    readonly ContextMenuStrip menu = new();
    readonly ToolStripMenuItem lockItem = new("Lock position");
    IntPtr screenDc, memDc, dib, bits; int dibW, dibH;
    Point pos;                                   // top-left in screen pixels
    bool locked, dragging, barRegistered, bottomLock, lastStartup;
    Point dragStart, posStart;
    SettingsForm settings;
    ClipboardWatcher clipWatcher;
    readonly TextBox clipSearch = new()
    {
        Visible = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(42, 31, 60), ForeColor = Color.FromArgb(235, 225, 250),
    };
    float clipScroll;
    bool clipSearchVisible;

    public MainWindow(AppConfig cfg, string cfgPath)
    {
        this.cfg = cfg; this.cfgPath = cfgPath;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        engine = new Engine(cfg, configPath: cfgPath);
        lastStartup = Startup.IsEnabled();

        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        lockItem.Click += (_, _) => { cfg.Global.LockPosition = !cfg.Global.LockPosition; lockItem.Checked = cfg.Global.LockPosition; Dirty(); };
        menu.Items.Add(lockItem);
        menu.Items.Add("Reset position", null, (_, _) => { cfg.Global.X = 40; cfg.Global.Y = 40; ApplyAll(); Dirty(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        ContextMenuStrip = menu;

        timer.Tick += (_, _) =>
        {
            engine.Tick();
            if (engine.LayoutVersion != seenLayout)   // a drive or adapter appeared or vanished: the widget grows or shrinks
            {
                seenLayout = engine.LayoutVersion; SetSize(); ApplyPositionAndDock(); PositionClipSearch();
                AppLog.Write($"layout changed: {engine.Panels.Count} sections, {pxW}x{pxH}px");
            }
            RenderFrame();
        };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        trimTimer.Tick += (_, _) => { if (cfg.Global.TrimMemory) TrimMemory(); };
        SystemEvents.SessionSwitch += OnSession;

        clipSearch.TextChanged += (_, _) => { clipScroll = 0; RenderFrame(); };
        Controls.Add(clipSearch);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
    }
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        screenDc = GetDC(IntPtr.Zero); memDc = CreateCompatibleDC(screenDc);
        clipWatcher = new ClipboardWatcher(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        lockItem.Checked = cfg.Global.LockPosition;
        AppLog.Write($"started: mode={cfg.Global.Mode} dock={cfg.Global.Dock} pos={cfg.Global.X},{cfg.Global.Y} screens={Screen.AllScreens.Length}");
        ApplyAll(); timer.Start(); trimTimer.Start();
        BeginInvoke(TrimMemory);
        var clipCfg = ClipConfig();
        clipSearchVisible = clipCfg?.ClipSearchExpanded ?? false;   // a startup default only; the panel's own search icon overrides it live from here on
        PositionClipSearch();
        if (clipCfg is { ClipRestoreOnStartup: true } && engine.Clipboard.Items.Count > 0)
            BeginInvoke(() => clipWatcher.RestoreToClipboard(engine.Clipboard.Items[0], engine.Clipboard));
    }

    PanelConfig ClipConfig() => cfg.Panels.FirstOrDefault(p => p.Kind == PanelKind.Clipboard && p.Enabled);

    // ------------------------------------------------------------ applying config
    /// <summary>Re-applies the whole config. Cheap enough to call on every settings edit.</summary>
    public void ApplyAll()
    {
        var g = cfg.Global;
        ApplyPriority(g);
        engine.Apply(cfg);
        timer.Interval = Math.Clamp(g.TickMs, 100, 5000);
        seenLayout = engine.LayoutVersion; SetSize();
        var ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex = g.ClickThrough ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)ex);
        ApplyZOrder();
        ApplyPositionAndDock();
        PositionClipSearch();
        bool want = g.StartWithWindows;
        if (want != lastStartup) { try { Startup.Set(want, typeof(MainWindow).Assembly.Location); lastStartup = want; } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { } }
        RenderFrame();
    }

    void SetSize()
    {
        float scale = DeviceDpi / 96f;
        pxW = (int)Math.Ceiling(cfg.Global.Width * scale); pxH = (int)Math.Ceiling(engine.TotalHeight * scale);   // same rounding as D2DRenderer.Resize
        EnsureDib(pxW, pxH);
    }

    static void ApplyPriority(GlobalConfig g)
    {
        using var me = Process.GetCurrentProcess();
        try { me.PriorityClass = g.Priority == ProcPriority.Idle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        // EcoQoS ("efficiency mode"): on hybrid CPUs this steers the process to E-cores and lowers its clock demand.
        var st = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = g.EcoQos ? 1u : 0u };
        SetProcessInformation(me.Handle, 4, ref st, Marshal.SizeOf<PowerThrottling>());
    }

    void EnsureDib(int w, int h)
    {
        if (dib != IntPtr.Zero && w == dibW && h == dibH) return;
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDc, dib); dibW = w; dibH = h;
    }

    void ApplyZOrder()
    {
        var mode = cfg.Global.Mode;
        bottomLock = mode == WindowMode.Bottom;
        var after = mode == WindowMode.TopMost ? HWND_TOPMOST : bottomLock ? HWND_BOTTOM : HWND_NOTOPMOST;
        SetWindowPos(Handle, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    Screen ScreenFor(int index) { var all = Screen.AllScreens; return all[Math.Clamp(index, 0, all.Length - 1)]; }

    void ApplyPositionAndDock()
    {
        var g = cfg.Global;
        if (g.Dock == DockEdge.None)
        {
            if (barRegistered) RemoveBar();
            pos = new Point(g.X, g.Y); ClampToScreens();
            return;
        }
        if (!barRegistered) { var a = Abd(); SHAppBarMessage(ABM_NEW, ref a); barRegistered = true; }
        var d = Abd(); var b = ScreenFor(g.Monitor).Bounds; bool left = g.Dock == DockEdge.Left;
        d.uEdge = left ? ABE_LEFT : ABE_RIGHT; d.rc = new RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        if (left) d.rc.right = d.rc.left + pxW; else d.rc.left = d.rc.right - pxW;
        SHAppBarMessage(ABM_SETPOS, ref d);
        pos = new Point(d.rc.left, d.rc.top);
    }

    APPBARDATA Abd() => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = Handle, uCallbackMessage = appBarMsg };
    void RemoveBar() { var a = Abd(); SHAppBarMessage(ABM_REMOVE, ref a); barRegistered = false; }

    /// <summary>Keeps at least a 60 px corner of the widget on some monitor so it cannot be lost after a display change.</summary>
    void ClampToScreens()
    {
        var r = new Rectangle(pos.X, pos.Y, Math.Max(1, pxW), Math.Max(1, pxH));
        if (Screen.AllScreens.Any(s => { var i = Rectangle.Intersect(s.WorkingArea, r); return i.Width >= 60 && i.Height >= 60; })) return;
        var wa = Screen.PrimaryScreen.WorkingArea; pos = new Point(wa.Left + 40, wa.Top + 40);
    }

    // ------------------------------------------------------------ drawing
    bool TryEnsureRenderer()
    {
        if (renderer != null) return true;
        try
        {
            renderer = new D2DRenderer();
            AppLog.Write($"renderer ready: {renderer.DeviceKind}; remote session={SystemInformation.TerminalServerSession}; dpi={DeviceDpi}");
            return true;
        }
        catch (Exception ex) { if (failStreak++ % 60 == 0) AppLog.Write("renderer create failed: " + ex.Message); return false; }
    }

    void RenderFrame()
    {
        if (locked || dib == IntPtr.Zero || !TryEnsureRenderer()) return;
        try
        {
            renderer.Resize(cfg.Global.Width, engine.TotalHeight, DeviceDpi / 96f);
            renderer.ClipScroll = clipScroll; renderer.ClipSearchVisible = clipSearchVisible; renderer.ClipSearchQuery = clipSearch.Text;
            renderer.Render(engine, cfg.Global);
            renderer.CopyTo(bits);
            if (!Present() && !presentLogged) { presentLogged = true; AppLog.Write($"UpdateLayeredWindow failed, Win32 error {Marshal.GetLastWin32Error()}"); }
            failStreak = 0;
        }
        catch (Exception ex)
        {   // GPU device loss (RDP, driver update, sleep/resume) surfaces here: rebuild the renderer on the next tick instead of crashing
            if (failStreak++ % 60 == 0) AppLog.Write("render failed, rebuilding renderer: " + ex.Message);
            renderer.Dispose(); renderer = null;
        }
    }

    bool Present()
    {
        var size = new SIZE { cx = dibW, cy = dibH }; var src = new POINT();
        var p = new POINT { x = pos.X, y = pos.Y };
        var bf = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        return UpdateLayeredWindow(Handle, screenDc, ref p, ref size, memDc, ref src, 0, ref bf, 2);
    }

    // ------------------------------------------------------------ input
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HandleClipboardClick(e.Location)) return;
        if (e.Button != MouseButtons.Left || cfg.Global.LockPosition || cfg.Global.Dock != DockEdge.None) return;
        dragging = true; dragStart = Cursor.Position; posStart = pos; Capture = true;
    }

    /// <summary>The items currently shown and their measured row heights - reads the renderer's own cache, so
    /// hit-testing and scrolling always agree with what was last drawn.</summary>
    (IReadOnlyList<ClipItem> items, float[] heights) ClipRows()
    {
        var items = D2DRenderer.ClipVisibleItems(engine, clipSearch.Text);
        var heights = renderer != null
            ? renderer.ClipRowHeights(items, engine.Clipboard, ClipboardLayout.BodyWidth(cfg.Global.Width), Math.Max(1, ClipConfig()?.ClipMaxPreviewLines ?? 11))
            : Array.Empty<float>();
        return (items, heights);
    }

    /// <summary>The scroll wheel is the whole selection mechanism (Bart: "I should not have to click to select" -
    /// no window activation, so he never has to leave whatever app he's actually using). One notch moves the
    /// selection one row and immediately restores that row to the OS clipboard; the view follows the selection
    /// into view rather than scrolling freely.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var clipCfg = ClipConfig(); if (clipCfg == null) return;
        float s = DeviceDpi / 96f; float dipY = e.Y / s;
        var panel = engine.Panels.FirstOrDefault(p => p.Cfg.Kind == PanelKind.Clipboard && dipY >= p.Top && dipY < p.Top + p.Height);
        if (panel == null) return;
        var (items, heights) = ClipRows();
        if (items.Count == 0) return;

        int idx = Math.Max(0, IndexOfSelected(items));
        idx = Math.Clamp(idx - e.Delta / 120, 0, items.Count - 1);
        clipScroll = ClipboardLayout.ScrollToShow(idx, panel.Height, clipSearchVisible, heights, clipScroll);
        if (engine.Clipboard.SelectedId != items[idx].Id) clipWatcher.RestoreToClipboard(items[idx], engine.Clipboard);
        RenderFrame();
    }

    int IndexOfSelected(IReadOnlyList<ClipItem> items)
    {
        string id = engine.Clipboard.SelectedId;
        for (int i = 0; i < items.Count; i++) if (items[i].Id == id) return i;
        return -1;
    }

    /// <summary>Returns true when the click was handled by the Clipboard panel (row action or empty panel space),
    /// so the caller must not also start a whole-window drag from the same mouse-down.</summary>
    bool HandleClipboardClick(Point physical)
    {
        var clipCfg = ClipConfig(); if (clipCfg == null) return false;
        var panel = engine.Panels.FirstOrDefault(p => p.Cfg.Kind == PanelKind.Clipboard); if (panel == null) return false;
        float s = DeviceDpi / 96f; float dipX = physical.X / s, dipY = physical.Y / s;
        var (items, heights) = ClipRows();
        var (idx, hit) = ClipboardLayout.HitTest(panel.Top, panel.Height, cfg.Global.Width, clipSearchVisible, heights, clipScroll, dipX, dipY);
        switch (hit)
        {
            case ClipHit.Search:
                clipSearchVisible = !clipSearchVisible; clipScroll = 0; PositionClipSearch(); RenderFrame(); return true;
            case ClipHit.Row:
                clipWatcher.RestoreToClipboard(items[idx], engine.Clipboard); return true;
            case ClipHit.Pin:
                engine.Clipboard.TogglePin(items[idx].Id); RenderFrame(); return true;
            case ClipHit.Delete:
                engine.Clipboard.Delete(items[idx].Id);
                var (_, heightsAfter) = ClipRows();
                clipScroll = ClipboardLayout.ClampScroll(clipScroll, panel.Height, clipSearchVisible, heightsAfter);
                RenderFrame(); return true;
            case ClipHit.Open:
                clipWatcher.OpenInApp(items[idx], engine.Clipboard); return true;
            default:
                // Inside the Clipboard panel but on no control (its background, or empty space below the last row):
                // swallow the click so grabbing a list item never starts dragging the whole widget.
                return dipY >= panel.Top && dipY < panel.Top + panel.Height;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        var c = Cursor.Position; pos = new Point(posStart.X + c.X - dragStart.X, posStart.Y + c.Y - dragStart.Y); Present();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false; Capture = false; Snap();
        cfg.Global.X = pos.X; cfg.Global.Y = pos.Y; Present(); Dirty();
    }

    void Snap()
    {
        const int d = 14; var wa = Screen.FromPoint(new Point(pos.X + pxW / 2, pos.Y + 10)).WorkingArea;
        if (Math.Abs(pos.X - wa.Left) < d) pos.X = wa.Left; else if (Math.Abs(pos.X + pxW - wa.Right) < d) pos.X = wa.Right - pxW;
        if (Math.Abs(pos.Y - wa.Top) < d) pos.Y = wa.Top; else if (Math.Abs(pos.Y + pxH - wa.Bottom) < d) pos.Y = wa.Bottom - pxH;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WINDOWPOSCHANGING && bottomLock)
        {   // stay behind everything, even when Windows tries to raise us
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            wp.hwndInsertAfter = HWND_BOTTOM; wp.flags &= ~SWP_NOZORDER; Marshal.StructureToPtr(wp, m.LParam, false);
        }
        else if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)3; return; }   // MA_NOACTIVATE
        else if (appBarMsg != 0 && m.Msg == appBarMsg && m.WParam == (IntPtr)1) { ApplyPositionAndDock(); Present(); }   // ABN_POSCHANGED
        else if (m.Msg == WM_CLIPBOARDUPDATE) { OnClipboardChanged(); }
        base.WndProc(ref m);
    }

    void OnClipboardChanged()
    {
        var clipCfg = ClipConfig(); if (clipCfg == null || clipWatcher == null) return;
        // A genuinely new item always lands at the top of the (newest-first) list, so jump the view there too -
        // otherwise a capture while scrolled down would highlight a selection row the user can't see.
        if (clipWatcher.OnClipboardUpdate(engine, clipCfg)) { clipScroll = 0; ClipboardWatcher.PlaySound(clipCfg); RenderFrame(); }
    }

    void PositionClipSearch()
    {
        var panel = engine.Panels.FirstOrDefault(p => p.Cfg.Kind == PanelKind.Clipboard);
        if (panel == null || !clipSearchVisible) { clipSearch.Visible = false; return; }
        float s = DeviceDpi / 96f;
        int x = (int)((Engine.Margin + 10) * s), y = (int)((panel.Top + Engine.ClipHeader + 2) * s);
        int w = (int)((cfg.Global.Width - 2 * Engine.Margin - 20 - Engine.ClipIcon) * s), h = (int)((Engine.ClipHeader - 6) * s);
        clipSearch.SetBounds(x, y, Math.Max(20, w), Math.Max(16, h));
        clipSearch.Visible = true;
    }

    // ------------------------------------------------------------ housekeeping
    public void OpenSettings()
    {
        if (settings != null && !settings.IsDisposed) { settings.Show(); settings.WindowState = FormWindowState.Normal; settings.Activate(); return; }
        settings = new SettingsForm(cfg, engine, () => { ApplyAll(); Dirty(); });
        settings.FormClosed += (_, _) => { Save(); TrimMemory(); };
        settings.Show(); settings.TopMost = true; settings.TopMost = false; settings.Activate();
    }

    void OnSession(object s, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; BeginInvoke(RenderFrame); }
    }

    void Dirty() { saveTimer.Stop(); saveTimer.Start(); }
    void Save() { try { ConfigStore.Save(cfg, cfgPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    /// <summary>Collects garbage, then hands unused pages back to Windows. The heap is a few MB, so the collection is a millisecond or two.</summary>
    internal static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        using var me = Process.GetCurrentProcess(); EmptyWorkingSet(me.Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop(); trimTimer.Stop(); saveTimer.Stop(); SystemEvents.SessionSwitch -= OnSession;
        if (barRegistered) RemoveBar();
        Save(); settings?.Close();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { renderer?.Dispose(); engine.Dispose(); timer.Dispose(); saveTimer.Dispose(); trimTimer.Dispose(); menu.Dispose(); clipWatcher?.Dispose(); }
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (memDc != IntPtr.Zero) DeleteDC(memDc);
        if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        base.Dispose(disposing);
    }
}
