// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Runtime.InteropServices;

namespace SyncthingPause;

/// <summary>
/// DPI-correct-by-construction layout primitives. The replacement for the
/// absolute-pixel <c>new Point(x, y)</c> / <c>new Size(w, h)</c> + <c>ref int y</c>
/// cursor model that clips at 125%/150% (point-fonts grow, fixed Sizes don't, and
/// the form's <see cref="AutoScaleMode.Dpi"/> walk misses every literal the layout
/// is pinned to). Here NOTHING has a literal pixel position or height: rows live in
/// a <see cref="TableLayoutPanel"/> (label column AutoSize, field column Fill), cards
/// AutoSize to their rows, and the stack sizes to the fonts. At any scale the layout
/// is <i>relational</i>, so 100% and 150% are proportionally identical with zero pixel
/// literals to mis-scale.
///
/// Ported from EQSwitch v3.24.33 (UI/CardLayout.cs). SELF-CONTAINED on purpose:
/// SyncthingPause's <see cref="Theme"/> is colours-only (no fonts, no control
/// factories), so this file bundles its own <see cref="CardFonts"/>, button factory,
/// themed combo, and <c>Lighten</c> — it reads only <i>colours</i> from
/// <see cref="Theme"/>. That keeps Theme thin and makes the primitive reusable across
/// any sibling tray app. Only field WIDTHS remain as literals; a width never clips
/// text vertically, and a Fill field has no literal at all.
/// </summary>
internal static class CardFonts
{
    // App-lifetime shared fonts — NEVER dispose from a form's Dispose path
    // (released at process exit). Forms that use CardLayout keep their own
    // instance fonts only for control kinds CardLayout doesn't build.
    public static readonly Font Title   = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Section = new("Segoe UI Semibold", 9f);
    public static readonly Font Body    = new("Segoe UI", 9f);
    public static readonly Font Button  = new("Segoe UI", 8f);
    public static readonly Font Hint    = new("Segoe UI", 7.5f, FontStyle.Italic);
    public static readonly Font Mono    = new("Consolas", 8.5f);

    public static Color Lighten(Color c, int amount) => Color.FromArgb(
        c.A,
        Math.Clamp(c.R + amount, 0, 255),
        Math.Clamp(c.G + amount, 0, 255),
        Math.Clamp(c.B + amount, 0, 255));
}

/// <summary>
/// Dark/Light-themed, scroll-guarded, owner-drawn ComboBox with DPI-correct
/// <c>ItemHeight</c>. Mirrors SyncthingPause's existing owner-draw combo look
/// (dark dropdown items) without depending on a host form's brushes. The wheel
/// guard forwards the scroll to the parent when closed so spinning a scrollable
/// card doesn't silently change the selected value.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_MOUSEWHEEL = 0x020A;

    // Captured once at first construction — Theme.Initialize has already run in
    // TrayApplicationContext's ctor by the time any settings combo is built, and
    // the theme is restart-to-apply, so a static capture is correct (matches the
    // SettingsForm ComboBgBrush convention — cache GDI in the per-item paint path).
    private static readonly SolidBrush BgBrush  = new(Theme.EditBg);
    private static readonly SolidBrush SelBrush = new(Theme.ComboSelectedBg);

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        Font = CardFonts.Body;
        ForeColor = Theme.Fg;
        BackColor = Theme.EditBg;
    }

    /// <summary>
    /// Extra bottom space a layout must reserve below this combo, ON TOP OF its
    /// font-derived preferred height, so the grown owner-draw box doesn't bleed past
    /// its cell. See <see cref="OnHandleCreated"/> for why the box grows and why the
    /// reservation has to live in the cell margin rather than the combo's own size.
    /// 0 until the handle exists (and <see cref="ItemHeight"/> is bumped); read it
    /// AFTER the handle is created (e.g. on Form.Load).
    /// </summary>
    public int OverflowBelow { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // AutoScaleMode.Dpi does NOT walk ItemHeight (an int, not a Size). Derive
        // it from the font at the device DPI so rows fit glyphs at every scale.
        ItemHeight = (int)Math.Ceiling(Font.GetHeight(DeviceDpi)) + LogicalToDeviceUnits(4);

        // CB_SETITEMHEIGHT (above) grows the closed owner-draw box, but ComboBox's
        // preferred height is font-derived and IGNORES ItemHeight — and ComboBox snaps
        // its own height, ignoring MinimumSize / GetPreferredSize overrides, so the cell a
        // TableLayoutPanel reserves stays too short and the box bleeds past the card border
        // on the last row (measured: realized Height 26 vs PreferredHeight 23 at 96 DPI).
        // The combo can't be made to report the bigger size, so instead we EXPOSE how much
        // it overflows; the host reserves that much extra as bottom Margin on the row (the
        // one lever the combo can't override — margin is added to the cell unconditionally).
        OverflowBelow = Math.Max(0, Height - base.GetPreferredSize(Size.Empty).Height);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) { base.OnDrawItem(e); return; }
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(selected ? SelBrush : BgBrush, e.Bounds);
        string text = Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL && !DroppedDown)
        {
            if (Parent != null)
                SendMessage(Parent.Handle, m.Msg, m.WParam, m.LParam);
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Vertical stack of cards. Wraps the card column in an AutoScroll host so grown
/// content scrolls instead of clipping when the form can't grow further at 150%.
/// </summary>
internal sealed class CardStack
{
    /// <summary>Scroll host — Dock=Fill on the form; scrolls if content outgrows it.</summary>
    public Panel Host { get; }

    private readonly TableLayoutPanel _stack;

    /// <param name="scroll">true (default) wraps the stack in an AutoScroll panel.
    /// false adds the stack directly (Dock=Top, AutoSize) so a parent that AutoSizes
    /// hosts it raw — used for the isolated pilot render.</param>
    public CardStack(Control parent, bool scroll = true)
    {
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
            Padding = new Padding(8, 6, 8, 6),
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        if (scroll)
        {
            Host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            Host.Controls.Add(_stack);
            parent.Controls.Add(Host);
        }
        else
        {
            Host = _stack;
            parent.Controls.Add(_stack);
        }
    }

    /// <summary>The natural (un-scrolled) size of the whole card column — use to size
    /// the host form to its content, then clamp to the work area (AutoScroll the rest).</summary>
    public Size ContentSize => _stack.PreferredSize;

    /// <summary>A control pinned below the scroll viewport via <see cref="SetFooter"/>, or null.
    /// Forms read its size when laying out so the scroll area is the client minus the footer.</summary>
    public Control? Footer { get; private set; }

    /// <summary>Pin a control to the bottom of the form, OUTSIDE the AutoScroll viewport, so the
    /// primary actions (Save/Cancel) stay visible when the cards scroll at high DPI. Re-parents the
    /// scroll Host + the footer into a 2-row root TableLayoutPanel (host fills row 0, footer hugs
    /// row 1) — the same deterministic row ordering HelpForm uses, with no Dock z-order ambiguity
    /// (sibling Dock=Fill + Dock=Bottom is order-dependent and easy to get backwards). No-op in raw
    /// (non-scroll) mode, where Host == the stack and there is no separate viewport to pin against.</summary>
    public void SetFooter(Control footer)
    {
        var parent = Host.Parent;
        if (parent == null || ReferenceEquals(Host, _stack) || Footer != null) return;
        parent.Controls.Remove(Host);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // scroll host fills the space above
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // footer hugs its button rows

        Host.Dock = DockStyle.Fill;
        root.Controls.Add(Host, 0, 0);
        footer.Dock = DockStyle.Fill;
        root.Controls.Add(footer, 0, 1);

        parent.Controls.Add(root);
        Footer = footer;
    }

    /// <summary>Add a styled card (emoji + coloured title + accent bar + hover border).
    /// Empty title ⇒ header-less card.</summary>
    public Card NewCard(string emoji, string title, Color titleColor)
    {
        var card = new Card(emoji, title, titleColor);
        AddRowControl(card.Panel);
        return card;
    }

    /// <summary>Add a raw full-width control as its own stack row (e.g. a button bar between cards).</summary>
    public void AddFullWidth(Control control)
    {
        control.Margin = new Padding(0, 0, 0, 8);
        AddRowControl(control);
    }

    private void AddRowControl(Control control)
    {
        int row = _stack.RowCount;
        _stack.RowCount = row + 1;
        _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        _stack.Controls.Add(control, 0, row);
    }
}

/// <summary>
/// One card: a styled panel whose body is a 2-column <see cref="TableLayoutPanel"/>
/// (label col AutoSize, field col Fill 100%). Every Add* call appends an AutoSize row,
/// so the card grows with its content at any DPI. Build through <see cref="CardStack.NewCard"/>.
/// </summary>
internal sealed class Card
{
    public Panel Panel { get; }
    private readonly TableLayoutPanel _body;

    internal Card(string emoji, string title, Color titleColor)
    {
        Panel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.PanelBg,
            Padding = new Padding(13, 6, 11, 6), // left clears the painted accent bar
            Margin = new Padding(0, 0, 0, 6),
        };
        WireCardPaint(Panel, titleColor);

        _body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // labels hug their text
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // fields take the rest
        Panel.Controls.Add(_body);

        if (!string.IsNullOrEmpty(title))
        {
            var lbl = new Label
            {
                Text = string.IsNullOrEmpty(emoji) ? title : $"{emoji}  {title}",
                AutoSize = true,
                ForeColor = titleColor,
                Font = CardFonts.Title,
                Margin = new Padding(0, 0, 0, 4),
            };
            AddSpanning(lbl);
        }
    }

    // ─── Row builders ───────────────────────────────────────────────

    /// <summary>label : field, field STRETCHES to fill the field column (textboxes, full-width combos).</summary>
    public T Row<T>(string label, T field) where T : Control
    {
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(0, 2, 0, 2);
        AddPair(MakeRowLabel(label), field);
        return field;
    }

    /// <summary>label : field, field keeps its NATURAL width, left-aligned (numerics, small combos).</summary>
    public T RowFit<T>(string label, T field) where T : Control
    {
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 2, 0, 2);
        AddPair(MakeRowLabel(label), field);
        return field;
    }

    /// <summary>label : [fill-field][trailing…] — the field STRETCHES and trailing controls
    /// (Browse button, unit label, reveal glyph) hug to its right. The "path box + Browse" row
    /// that has to stay aligned at any DPI.</summary>
    public T RowWith<T>(string label, T fillField, params Control[] trailing) where T : Control
    {
        var sub = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 + trailing.Length,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        sub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        sub.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fillField.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        fillField.Margin = new Padding(0, 2, 6, 2);
        sub.Controls.Add(fillField, 0, 0);
        for (int i = 0; i < trailing.Length; i++)
        {
            sub.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            trailing[i].Anchor = AnchorStyles.Left;
            trailing[i].Margin = new Padding(0, 2, i < trailing.Length - 1 ? 6 : 0, 2);
            sub.Controls.Add(trailing[i], 1 + i, 0);
        }
        AddPair(MakeRowLabel(label), sub);
        return fillField;
    }

    /// <summary>A field row whose field cell holds several controls flowing left-to-right.
    /// Pass label "" for no leading label (the row spans both columns).</summary>
    public FlowLayoutPanel FlowRow(string label, params Control[] controls)
    {
        var flow = BuildFlow(controls);
        if (string.IsNullOrEmpty(label)) AddSpanning(flow);
        else AddPair(MakeRowLabel(label), flow);
        return flow;
    }

    private static FlowLayoutPanel BuildFlow(Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 1, 0, 1),
            Padding = Padding.Empty,
            Anchor = AnchorStyles.Left,
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 2, 8, 2);
            c.Anchor = AnchorStyles.Left;
            flow.Controls.Add(c);
        }
        return flow;
    }

    /// <summary>A checkbox spanning both columns (optionally with a trailing dim hint).</summary>
    public CheckBox Check(CheckBox box, string? hint = null)
    {
        box.AutoSize = true;
        box.Margin = new Padding(0, 3, 0, 3);
        if (hint == null)
        {
            AddSpanning(box);
        }
        else
        {
            var flow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Left,
            };
            box.Margin = new Padding(0, 3, 10, 3);
            flow.Controls.Add(box);
            flow.Controls.Add(new Label
            {
                Text = hint, AutoSize = true, ForeColor = Theme.FgDisabled,
                Font = CardFonts.Hint, Margin = new Padding(0, 6, 0, 0),
            });
            AddSpanning(flow);
        }
        return box;
    }

    /// <summary>A full-width control spanning both columns (sub-grid, multiline, summary panel).</summary>
    public T Full<T>(T control) where T : Control
    {
        control.Margin = new Padding(0, 2, 0, 2);
        control.Dock = DockStyle.Top;
        AddSpanning(control);
        return control;
    }

    /// <summary>A dim hint/description spanning both columns. Returns the label so callers can
    /// toggle its Visible/Text (e.g. the Discovery "API unreachable" warning).</summary>
    public Label Hint(string text)
    {
        var lbl = new Label
        {
            Text = text, AutoSize = true, ForeColor = Theme.FgDisabled,
            Font = CardFonts.Hint, Margin = new Padding(0, 4, 0, 2),
        };
        AddSpanning(lbl);
        return lbl;
    }

    /// <summary>A bold sub-section header spanning both columns.</summary>
    public Label Section(string text)
    {
        var lbl = new Label
        {
            Text = text, AutoSize = true, ForeColor = Theme.Fg,
            Font = CardFonts.Section, Margin = new Padding(0, 8, 0, 2),
        };
        AddSpanning(lbl);
        return lbl;
    }

    /// <summary>Expose the body table for the rare custom card that drops in a hand-built sub-grid.</summary>
    public TableLayoutPanel Body => _body;

    // ─── internals ──────────────────────────────────────────────────

    private static Label MakeRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Dim,
        Font = CardFonts.Body,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 4, 10, 1), // top:4 vertically centres against ~font-height fields; right:10 gap to field
    };

    private void AddPair(Control label, Control field)
    {
        int row = _body.RowCount;
        _body.RowCount = row + 1;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Controls.Add(label, 0, row);
        _body.Controls.Add(field, 1, row);
    }

    private void AddSpanning(Control control)
    {
        int row = _body.RowCount;
        _body.RowCount = row + 1;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Controls.Add(control, 0, row);
        _body.SetColumnSpan(control, 2);
    }

    private static void WireCardPaint(Panel panel, Color titleColor)
    {
        bool hovered = false;
        panel.MouseEnter += (_, _) => { hovered = true; panel.Invalidate(); };
        panel.MouseLeave += (_, _) =>
        {
            var pos = panel.PointToClient(Cursor.Position);
            if (!panel.ClientRectangle.Contains(pos)) { hovered = false; panel.Invalidate(); }
        };
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var border = hovered ? CardFonts.Lighten(titleColor, -40) : Theme.Divider;
            using var pen = new Pen(border, 1);
            g.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            using var accent = new SolidBrush(titleColor);
            g.FillRectangle(accent, 0, 0, panel.LogicalToDeviceUnits(3), panel.Height); // accent bar (paint geom not auto-scaled)
        };
    }
}

/// <summary>
/// Height-free, DPI-correct, UNPARENTED field factories — the dark-themed control
/// palette without the fixed <c>Size(w, 26)</c> the old absolute helpers baked in.
/// Heights come from the font (single-line inputs auto-size their height at any DPI);
/// only WIDTHS are literal, and width never clips text vertically.
/// </summary>
internal static class Fields
{
    /// <summary>A themed Label for FlowRow units / inline text.</summary>
    public static Label Label(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Dim, Font = CardFonts.Body,
        Margin = new Padding(0, 6, 8, 0),
    };

    /// <summary>A dark TextBox. <paramref name="mono"/> uses the Consolas face for paths / keys.</summary>
    public static TextBox Text(int width = 200, bool mono = false, int maxLength = 0)
    {
        var tb = new TextBox
        {
            Width = width,
            Font = mono ? CardFonts.Mono : CardFonts.Body,
            BackColor = Theme.EditBg,
            ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.FixedSingle,
        };
        if (maxLength > 0) tb.MaxLength = maxLength;
        return tb;
    }

    /// <summary>A dark drop-down-list ComboBox (owner-drawn, scroll-guarded, font-height).</summary>
    public static ThemedComboBox Combo(int width, string[] items, int selectedIndex = 0)
    {
        var cb = new ThemedComboBox { Width = width };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0)
            cb.SelectedIndex = Math.Clamp(selectedIndex, 0, cb.Items.Count - 1);
        return cb;
    }

    /// <summary>A dark NumericUpDown (fixed width, font-height — never the clipped Size(w,22)).</summary>
    public static NumericUpDown Numeric(decimal min, decimal max, decimal val, int width = 80, int increment = 1) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(val, min, max),
        Increment = increment,
        Width = width,
        Font = CardFonts.Body,
        BackColor = Theme.EditBg,
        ForeColor = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Left,
    };

    /// <summary>An AutoSize dark CheckBox (grows with its text — never clips the label).</summary>
    public static CheckBox Check(string text, bool isChecked = false) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = isChecked,
        ForeColor = Theme.Fg,
        Font = CardFonts.Body,
        AccessibleName = text,
    };

    /// <summary>An AutoSize flat button matching SyncthingPause's chrome.</summary>
    public static Button Button(string text, Font? font = null)
    {
        var b = new Button
        {
            Text = text,
            Font = font ?? CardFonts.Button,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Fg,
            BackColor = Theme.EditBg,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = new Padding(8, 3, 8, 3),
            AccessibleName = text,
        };
        b.FlatAppearance.BorderColor = Theme.Divider;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    /// <summary>An AutoSize primary button (subtle green accent border — the default action).</summary>
    public static Button Primary(string text)
    {
        var b = Button(text);
        b.FlatAppearance.BorderColor = Theme.AccentGreen;
        b.ForeColor = Theme.AccentGreen;
        return b;
    }
}

/// <summary>
/// Button-row layouts: a SPLIT row (left group hugs the left edge, right group hugs the
/// right, flexible spacer between) and a SPREAD row (N controls distributed evenly, first
/// hugging left, last hugging right). Replaces the absolute-X right-alignment / spacing that
/// the layout-container rebuild flattens to left-aligned.
/// </summary>
internal static class Bars
{
    public static TableLayoutPanel Split(Control[] left, Control[] right)
    {
        var g = NewBar(2);
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // left group + spacer
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // right group hugs right
        g.Controls.Add(Group(left, AnchorStyles.Left), 0, 0);
        g.Controls.Add(Group(right, AnchorStyles.Right), 1, 0);
        return g;
    }

    public static TableLayoutPanel Spread(params Control[] items)
    {
        int n = items.Length;
        var g = NewBar(n);
        for (int i = 0; i < n; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
        for (int i = 0; i < n; i++)
        {
            items[i].Anchor = i == 0 ? AnchorStyles.Left : i == n - 1 ? AnchorStyles.Right : AnchorStyles.None;
            items[i].Margin = Padding.Empty;
            g.Controls.Add(items[i], i, 0);
        }
        return g;
    }

    /// <summary>N controls each CENTRED in an equal-width column — even spacing with
    /// padding on BOTH outer sides. Differs from <see cref="Spread"/>, which hugs the
    /// first item hard against the left edge and the last against the right (no outer
    /// padding). This is the Save / Apply / Cancel footer row: the buttons read as evenly
    /// distributed across the full width, with equal gaps before, between, and after, at
    /// any DPI (each button auto-sizes; the equal columns + centre anchor do the spacing).</summary>
    public static TableLayoutPanel Distribute(params Control[] items)
    {
        int n = items.Length;
        var g = NewBar(n);
        for (int i = 0; i < n; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
        for (int i = 0; i < n; i++)
        {
            items[i].Anchor = AnchorStyles.None;   // centred in its column → equal padding all around
            items[i].Margin = Padding.Empty;
            g.Controls.Add(items[i], i, 0);
        }
        return g;
    }

    private static TableLayoutPanel NewBar(int cols) => new()
    {
        ColumnCount = cols,
        RowCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Margin = new Padding(0, 4, 0, 2),
        Padding = Padding.Empty,
        BackColor = Color.Transparent,
        RowStyles = { new RowStyle(SizeType.AutoSize) },
    };

    private static FlowLayoutPanel Group(Control[] items, AnchorStyles anchor)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Anchor = anchor,
            BackColor = Color.Transparent,
        };
        for (int i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Padding(0, 0, i < items.Length - 1 ? 8 : 0, 0);
            f.Controls.Add(items[i]);
        }
        return f;
    }
}
