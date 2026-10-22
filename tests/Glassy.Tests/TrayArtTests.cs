using System.Drawing;
using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class TrayArtTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void Ico_round_trips_through_System_Drawing_pixel_for_pixel(int size)
    {
        using var bmp = TrayArt.Draw(size, new float[] { 5, 40, 90, float.NaN, 20, 60 });
        using var ms = new MemoryStream(TrayArt.Ico(bmp));
        using var icon = new Icon(ms);
        Assert.Equal(size, icon.Width); Assert.Equal(size, icon.Height);
        using var back = icon.ToBitmap();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Assert.Equal(bmp.GetPixel(x, y).ToArgb(), back.GetPixel(x, y).ToArgb());   // rows not flipped, channels not swapped
    }

    [Fact]
    public void Idle_noise_is_scaled_up_to_a_visible_wiggle()
    {
        using var b = TrayArt.Draw(16, new float[] { 1, 3, 1, 3, 2, 1, 3, 1 });   // an idle machine: 1-3% CPU
        int rows = Enumerable.Range(0, 16).Count(y => Enumerable.Range(0, 16).Any(x => { var c = b.GetPixel(x, y); return c.R > 200 && c.G < 180 && c.B > 180; }));
        Assert.True(rows >= 5, $"line spans only {rows} rows");               // on a fixed 0-100 scale it would be a flat line along the bottom
    }
}
