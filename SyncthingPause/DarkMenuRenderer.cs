namespace SyncthingPause;

/// <summary>
/// Custom ToolStripRenderer that applies the active theme (Dark or Light, per
/// the user's <c>ThemeMode</c> setting) to the tray context menu. Brushes and
/// pens are cached as static fields to avoid GDI object churn on every paint
/// call (important for 24/7 operation).
///
/// Restart-to-apply: these <c>static readonly</c> fields capture <see cref="Theme"/>
/// values at first class load. The <c>Theme.Initialize()</c> call in
/// <c>TrayApplicationContext</c>'s constructor body must precede the first
/// construction of this renderer — see <c>Theme.cs</c> docstring.
///
/// Class name kept as "DarkMenuRenderer" through the dual-theme migration to
/// avoid sprawling renames across the codebase; behaviour is theme-aware.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color MenuBg = Theme.Bg;
    private static readonly Color MenuFg = Theme.Fg;
    private static readonly Color MenuFgDisabled = Theme.FgDisabled;
    private static readonly Color HighlightBg = Theme.HighlightBg;
    private static readonly Color SeparatorColor = Theme.Divider;

    // Cached GDI objects — colors are fixed for the process lifetime once
    // Theme.Initialize has run before this class first loads.
    private static readonly SolidBrush BgBrush = new(MenuBg);
    private static readonly SolidBrush HighlightBrush = new(HighlightBg);
    private static readonly Pen SeparatorPen = new(SeparatorColor);
    private static readonly Pen BorderPen = new(SeparatorColor);

    public DarkMenuRenderer() : base(new ThemedColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        var brush = e.Item.Selected && e.Item.Enabled ? HighlightBrush : BgBrush;
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // v2.3.1: Tag = Color opts into a custom text color. Critical detail:
        // ToolStripRenderer.OnRenderItemText branches on item.Enabled and calls
        // ControlPaint.DrawStringDisabled for disabled items — that path IGNORES
        // e.TextColor and renders the system default embossed-grey, so simply
        // setting e.TextColor doesn't survive the disabled-state hand-off. We
        // draw the text ourselves with TextRenderer.DrawText (same primitive the
        // base uses for enabled items) and skip the base call when a Tag color
        // is requested. This is what makes the Synced Folders device-name
        // headers (which are Enabled=false) actually render green/red.
        if (e.Item.Tag is Color tagColor)
        {
            if (!string.IsNullOrEmpty(e.Text))
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, tagColor, e.TextFormat);
            return;
        }
        e.TextColor = e.Item.Enabled ? MenuFg : MenuFgDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        int y = bounds.Height / 2;
        e.Graphics.DrawLine(SeparatorPen, bounds.Left + 4, y, bounds.Right - 4, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.FillRectangle(BgBrush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(BorderPen, rect);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Suppress default image margin rendering (white strip on left in light
        // Windows themes; visible faint band in dark themes too).
        e.Graphics.FillRectangle(BgBrush, e.AffectedBounds);
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => SeparatorColor;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => HighlightBg;
        public override Color MenuStripGradientBegin => MenuBg;
        public override Color MenuStripGradientEnd => MenuBg;
        public override Color MenuItemSelectedGradientBegin => HighlightBg;
        public override Color MenuItemSelectedGradientEnd => HighlightBg;
        public override Color MenuItemPressedGradientBegin => HighlightBg;
        public override Color MenuItemPressedGradientEnd => HighlightBg;
        public override Color ImageMarginGradientBegin => MenuBg;
        public override Color ImageMarginGradientMiddle => MenuBg;
        public override Color ImageMarginGradientEnd => MenuBg;
        public override Color ToolStripDropDownBackground => MenuBg;
        public override Color SeparatorDark => SeparatorColor;
        public override Color SeparatorLight => SeparatorColor;
    }
}
