namespace Glassy.Core;

public enum ClipHit { None, Row, Pin, Open, Delete, Search }

/// <summary>One row's icon x-positions and top y, in DIPs. Shared by the renderer (drawing) and hit-testing, so
/// they can never drift apart. Icons are anchored to the row's top, not its centre, since rows are variable height.
/// The icon strip only occupies the row's first ClipIcon pixels, so only the header line (type + age, drawn on
/// that same strip) needs to stop short of the icons at HeaderRight - the body below can use the full TextRight.</summary>
public readonly struct ClipRowGeom
{
    public readonly float Top, IconY, TypeIconX, TextLeft, TextRight, HeaderRight, OpenX, PinX, DeleteX;
    public ClipRowGeom(float top, float iconY, float typeIconX, float textLeft, float textRight, float headerRight, float openX, float pinX, float deleteX)
    { Top = top; IconY = iconY; TypeIconX = typeIconX; TextLeft = textLeft; TextRight = textRight; HeaderRight = headerRight; OpenX = openX; PinX = pinX; DeleteX = deleteX; }
}

/// <summary>
/// Pure layout math for the Clipboard panel: row geometry, variable row heights, scroll clamping, and mouse
/// hit-testing. No drawing and no Win32 here, so every case (including exact icon boundaries and the walk over
/// variable-height rows) is unit-testable without a window. Row heights themselves are measured by the renderer
/// (wrapped text needs real font metrics) and passed in as a plain array - this module only does the geometry.
/// </summary>
public static class ClipboardLayout
{
    /// <summary>Height of a row showing up to `lines` of wrapped text or a file list, clamped to at least one line.</summary>
    public static float RowHeightForLines(int lines) => Engine.ClipBodyTop + Math.Max(1, lines) * Engine.ClipLineH + Engine.ClipBodyBottomPad;
    /// <summary>Height of a row showing an image thumbnail of the given pixel height.</summary>
    public static float RowHeightForImage(float imageHeight) => Engine.ClipBodyTop + Math.Max(Engine.ClipLineH, imageHeight) + Engine.ClipBodyBottomPad;
    /// <summary>The tallest a row is allowed to grow, for the given per-panel line cap.</summary>
    public static float MaxRowHeight(int maxLines) => RowHeightForLines(maxLines);
    /// <summary>Where a row's body (wrapped text, file list, or image) starts, relative to the row's top.</summary>
    public static float BodyTop(float rowTop) => rowTop + Engine.ClipBodyTop;

    /// <summary>The row body's usable text/image width for a widget of the given total width - the same number
    /// drawing and hit-testing/scrolling must measure text and images against.</summary>
    public static float BodyWidth(float widgetWidth)
    {
        float left = Engine.Margin + 10, right = widgetWidth - Engine.Margin - 10;
        var g = Row(left, right, 0);
        return g.TextRight - g.TextLeft;
    }

    public static ClipRowGeom Row(float panelLeft, float panelRight, float rowTop)
    {
        const float gap = 4;
        float del = panelRight - Engine.ClipIcon;
        float pin = del - Engine.ClipIcon - gap;
        float open = pin - Engine.ClipIcon - gap;
        float iconY = rowTop + Engine.ClipIconTopPad;
        return new ClipRowGeom(rowTop, iconY, panelLeft, panelLeft + Engine.ClipIcon + 6, panelRight, open - 6, open, pin, del);
    }

    static float ListAreaHeight(float panelHeight, bool searchVisible) => panelHeight - Engine.ClipHeader - (searchVisible ? Engine.ClipHeader : 0);

    public static float TotalContentHeight(IReadOnlyList<float> rowHeights)
    {
        float t = 0; for (int i = 0; i < rowHeights.Count; i++) t += rowHeights[i]; return t;
    }

    public static float MaxScroll(float panelHeight, bool searchVisible, IReadOnlyList<float> rowHeights) =>
        Math.Max(0, TotalContentHeight(rowHeights) - ListAreaHeight(panelHeight, searchVisible));

    public static float ClampScroll(float scroll, float panelHeight, bool searchVisible, IReadOnlyList<float> rowHeights) =>
        Math.Clamp(scroll, 0, MaxScroll(panelHeight, searchVisible, rowHeights));

    /// <summary>The minimal scroll adjustment so row `index` is fully visible in the list area - aligns to the top
    /// edge if it's above the view, the bottom edge if below, unchanged if already visible. Used for scroll-wheel
    /// driven selection: the newly selected row must always be on screen without a separate drag-to-scroll step.</summary>
    public static float ScrollToShow(int index, float panelHeight, bool searchVisible, IReadOnlyList<float> rowHeights, float scroll)
    {
        if (index < 0 || index >= rowHeights.Count) return ClampScroll(scroll, panelHeight, searchVisible, rowHeights);
        float top = 0; for (int i = 0; i < index; i++) top += rowHeights[i];
        float bottom = top + rowHeights[index];
        float areaHeight = ListAreaHeight(panelHeight, searchVisible);
        float s = scroll;
        if (top < s) s = top;
        else if (bottom > s + areaHeight) s = bottom - areaHeight;
        return ClampScroll(s, panelHeight, searchVisible, rowHeights);
    }

    /// <summary>Scrollbar thumb geometry (top offset from the list's top, and height), in DIPs. Height 0 means
    /// hidden - everything already fits, so a thumb spanning the whole track would tell the user nothing.</summary>
    public static (float top, float height) ScrollThumb(float panelHeight, bool searchVisible, IReadOnlyList<float> rowHeights, float scroll)
    {
        float trackHeight = ListAreaHeight(panelHeight, searchVisible);
        float total = TotalContentHeight(rowHeights);
        if (total <= trackHeight || trackHeight <= 0) return (0, 0);
        float thumbHeight = Math.Max(18, trackHeight * trackHeight / total);
        float max = MaxScroll(panelHeight, searchVisible, rowHeights);
        float frac = max > 0 ? Math.Clamp(scroll, 0, max) / max : 0;
        return ((trackHeight - thumbHeight) * frac, thumbHeight);
    }

    /// <summary>mouseX/mouseY are DIPs relative to the same origin as PanelRuntime.Top. rowHeights.Count must equal the item count.</summary>
    public static (int index, ClipHit hit) HitTest(float panelTop, float panelHeight, float panelWidth, bool searchVisible, IReadOnlyList<float> rowHeights, float scrollOffset, float mouseX, float mouseY)
    {
        float left = Engine.Margin + 10, right = panelWidth - Engine.Margin - 10;
        float headerBottom = panelTop + Engine.ClipHeader;
        if (mouseY < panelTop || mouseY >= panelTop + panelHeight) return (-1, ClipHit.None);
        if (mouseY < headerBottom)
            return mouseX >= right - Engine.ClipIcon ? (-1, ClipHit.Search) : (-1, ClipHit.None);

        float listTop = headerBottom + (searchVisible ? Engine.ClipHeader : 0);
        if (mouseY < listTop) return (-1, ClipHit.None);   // inside the search row itself; the real textbox handles clicks there
        float yInList = mouseY - listTop + scrollOffset;

        float acc = 0; int idx = -1;
        for (int i = 0; i < rowHeights.Count; i++)
        {
            if (yInList < acc + rowHeights[i]) { idx = i; break; }
            acc += rowHeights[i];
        }
        if (idx < 0) return (-1, ClipHit.None);

        float rowTop = listTop + acc - scrollOffset;
        var g = Row(left, right, rowTop);
        if (mouseY >= g.IconY && mouseY < g.IconY + Engine.ClipIcon)   // the icon strip at the top of the row; below it is the text/image body
        {
            if (mouseX >= g.DeleteX && mouseX < g.DeleteX + Engine.ClipIcon) return (idx, ClipHit.Delete);
            if (mouseX >= g.PinX && mouseX < g.PinX + Engine.ClipIcon) return (idx, ClipHit.Pin);
            if (mouseX >= g.OpenX && mouseX < g.OpenX + Engine.ClipIcon) return (idx, ClipHit.Open);
        }
        return (idx, ClipHit.Row);
    }
}
