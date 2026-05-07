namespace SyncthingTray;

/// <summary>
/// Custom ToolStripRenderer that applies the dark theme (matching Settings/Help forms)
/// to the tray context menu. Brushes and pens are cached as static fields to avoid
/// GDI object churn on every paint call (important for 24/7 operation).
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color MenuBg = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color MenuFg = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color HighlightBg = Color.FromArgb(0x35, 0x35, 0x50);
    private static readonly Color SeparatorColor = Color.FromArgb(0x40, 0x40, 0x50);

    // Cached GDI objects — colors are fixed, so these live for the process lifetime
    private static readonly SolidBrush BgBrush = new(MenuBg);
    private static readonly SolidBrush HighlightBrush = new(HighlightBg);
    private static readonly Pen SeparatorPen = new(SeparatorColor);
    private static readonly Pen BorderPen = new(SeparatorColor);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

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
        e.TextColor = e.Item.Enabled ? MenuFg : Color.FromArgb(0x60, 0x60, 0x70);
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
        // Suppress default image margin rendering (white strip on left)
        e.Graphics.FillRectangle(BgBrush, e.AffectedBounds);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
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
