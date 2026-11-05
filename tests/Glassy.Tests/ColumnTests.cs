using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class ColumnTests
{
    // Uptime stays in the main column, Clipboard asks for Right 1, File shares for Left 2.
    static Engine Columns(DockEdge dock)
    {
        var c = AppConfig.CreateDefault();
        c.Global.Dock = dock;
        var clip = AppConfig.ClipboardPanel(true); clip.Column = 1;
        var fs = AppConfig.FileSharePanel(); fs.Column = -2;
        c.Panels = new List<PanelConfig> { new() { Kind = PanelKind.Uptime }, clip, fs };
        var fake = new FakeFileShareProvider { Value = new FileShareStatus { Running = true } };
        return new Engine(c, clipboard: new ClipboardStore(Path.Combine(Path.GetTempPath(), "glassy-col-" + Guid.NewGuid().ToString("N"))), shares: fake);
    }

    static float LeftOf(Engine e, PanelKind k) => e.Panels.First(p => p.Cfg.Kind == k).Left;

    [Theory]
    // undocked: Left 2 | Main | Right 1, with no gap where Left 1 would be
    [InlineData(DockEdge.None, 0, 1, 2, 1)]
    // docked right: Right 1 becomes Left 1, so Left 2 | Left 1 | Main against the edge
    [InlineData(DockEdge.Right, 0, 2, 1, 2)]
    // docked left: Left 2 becomes Right 2, so Main | Right 1 | Right 2
    [InlineData(DockEdge.Left, 2, 0, 1, 0)]
    public void Columns_sit_side_by_side_and_never_on_the_docked_side(DockEdge dock, int share, int uptime, int clip, int main)
    {
        using var e = Columns(dock);
        float w = e.Config.Global.Width;
        Assert.Equal(share * w, LeftOf(e, PanelKind.FileShare));
        Assert.Equal(uptime * w, LeftOf(e, PanelKind.Uptime));
        Assert.Equal(clip * w, LeftOf(e, PanelKind.Clipboard));
        Assert.Equal(main * w, e.MainLeft);
        Assert.Equal(3 * w, e.TotalWidth);
        Assert.All(e.Panels, p => Assert.Equal(Engine.Margin, p.Top));               // each column starts at the top
        Assert.Equal(e.Panels.Max(p => p.Top + p.Height) + Engine.Margin, e.TotalHeight);   // as tall as the tallest column
    }

    [Fact]
    public void One_column_is_the_old_layout()
    {
        var c = AppConfig.CreateDefault(); using var e = new Engine(c);
        Assert.Equal(c.Global.Width, e.TotalWidth);
        Assert.Equal(0, e.MainLeft);
        Assert.All(e.Panels, p => Assert.Equal(0, p.Left));
    }
}
