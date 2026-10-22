using Glassy.Core;

namespace Glassy.App;

/// <summary>Notification-area icon with the widget's own right-click menu, so the gadget can be reached (Settings, Reset
/// position, Restart, Exit) even when the widget itself can't be seen. Its picture is a scrolling CPU line redrawn every
/// tick: if it stops moving, the widget has hung.</summary>
sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon icon;
    readonly float[] hist = Enumerable.Repeat(float.NaN, 16).ToArray();
    Icon current;

    public TrayIcon(ContextMenuStrip menu, Action doubleClick)
    {
        icon = new NotifyIcon { ContextMenuStrip = menu, Text = "Glassy System Gadget" };
        icon.DoubleClick += (_, _) => doubleClick();
    }

    public bool Visible
    {
        get => icon.Visible;
        set { if (value && current == null) Update(float.NaN); icon.Visible = value; }
    }

    /// <summary>cpu: total CPU percent, or NaN when no CPU panel is sampling - then a steady pulse still shows it's alive.</summary>
    public void Update(float cpu)
    {
        bool real = !float.IsNaN(cpu);
        Array.Copy(hist, 1, hist, 0, hist.Length - 1);
        hist[^1] = real ? cpu : 50 + 40 * MathF.Sin(Environment.TickCount64 / 300f);
        using var bmp = TrayArt.Draw(SystemInformation.SmallIconSize.Width, hist);
        using var ms = new MemoryStream(TrayArt.Ico(bmp));
        var next = new Icon(ms);
        icon.Icon = next; current?.Dispose(); current = next;
        icon.Text = $"Glassy System Gadget {Updater.CurrentText}" + (real ? $"\nCPU {cpu:0}%" : "");
    }

    public void Dispose() { icon.Visible = false; icon.Dispose(); current?.Dispose(); }   // hide first, or a dead icon lingers until hovered
}
