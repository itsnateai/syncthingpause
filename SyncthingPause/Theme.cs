namespace SyncthingPause;

/// <summary>
/// Window-chrome palette for SyncthingPause. Two palettes — Catppuccin Mocha
/// (Dark) and v2.1.x classic (Light, pure white BG + cornsilk focus tint +
/// brand-blue #2255AA headers) — selected once at startup via
/// <see cref="Initialize"/> based on the user's saved preference,
/// resolved through <see cref="ResolveIsDark"/>. All chrome surfaces read from
/// the static colour properties on this class.
///
/// Tray icons are NOT driven by this class — they follow the OS theme directly,
/// independent of the user's window-chrome pin. (Sync and pause icons are
/// colour-coded and read fine on either taskbar background.)
///
/// Why static state with restart-to-apply: the GDI brush/pen caches in
/// <see cref="DarkMenuRenderer"/>, <see cref="OsdToolTip"/>, and the various
/// dialog forms are <c>static readonly</c> field initializers that capture
/// <c>Theme.*</c> at first class load. They are write-once per process.
/// <see cref="Initialize"/> MUST be called before any of those classes is
/// first touched (currently: in <c>TrayApplicationContext</c>'s constructor
/// body, immediately after <c>_config = new AppConfig(appDir)</c> and before
/// any subsequent line). Runtime theme swap is intentionally NOT supported —
/// restart-to-apply keeps the GDI caches honest.
/// </summary>
internal static class Theme
{
    private static bool _isDark = true;
    private static bool _initialized;

    public static bool IsDark => _isDark;

    public static void Initialize(bool isDark)
    {
        // Idempotent guard: a second call CAN'T take effect because the static
        // GDI brush/pen caches in DarkMenuRenderer / OsdToolTip / dialog forms
        // already captured Theme.* at first class load. Log loudly (rather than
        // silently returning) so a future maintainer who tries to add
        // live-theme-swap gets a Trace entry pointing at the constraint.
        if (_initialized)
        {
            System.Diagnostics.Trace.WriteLine(
                $"SyncthingPause: Theme.Initialize called twice (was isDark={_isDark}, requested {isDark}) — ignored. " +
                "Theme is restart-to-apply by design (static GDI caches captured at first class load).");
            return;
        }
        _isDark = isDark;
        _initialized = true;
    }

    /// <summary>
    /// Resolves the user's saved ThemeMode value into a concrete is-dark decision.
    /// Recognises "Dark" and "Light" case-insensitively; anything else (including
    /// the future "System" sentinel) falls back to the OS theme.
    /// </summary>
    public static bool ResolveIsDark(string? configValue)
    {
        if (string.Equals(configValue, "Dark", System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(configValue, "Light", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return !IsSystemLightTheme();
    }

    /// <summary>
    /// Reads <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme</c>.
    /// Returns <c>false</c> on any failure (locked key, missing value, registry
    /// exception). Logs the failure so it's diagnosable, not silently dark.
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? val = key?.GetValue("SystemUsesLightTheme");
            return val is int i && i == 1;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"SyncthingPause: Theme.IsSystemLightTheme registry read failed " +
                $"(err={ex.GetType().Name}: {ex.Message}) — assuming dark theme");
            return false;
        }
    }

    // ── Active palette accessors — every chrome surface reads from these. ──
    public static System.Drawing.Color Bg                => _isDark ? Dark.Bg                : Light.Bg;
    public static System.Drawing.Color Fg                => _isDark ? Dark.Fg                : Light.Fg;
    public static System.Drawing.Color FgDisabled        => _isDark ? Dark.FgDisabled        : Light.FgDisabled;
    public static System.Drawing.Color Dim               => _isDark ? Dark.Dim               : Light.Dim;
    public static System.Drawing.Color HighlightBg       => _isDark ? Dark.HighlightBg       : Light.HighlightBg;
    public static System.Drawing.Color EditBg            => _isDark ? Dark.EditBg            : Light.EditBg;
    public static System.Drawing.Color Divider           => _isDark ? Dark.Divider           : Light.Divider;
    public static System.Drawing.Color AccentBlue        => _isDark ? Dark.AccentBlue        : Light.AccentBlue;
    public static System.Drawing.Color AccentGreen       => _isDark ? Dark.AccentGreen       : Light.AccentGreen;
    public static System.Drawing.Color AccentRed         => _isDark ? Dark.AccentRed         : Light.AccentRed;
    public static System.Drawing.Color AccentWarn        => _isDark ? Dark.AccentWarn        : Light.AccentWarn;
    public static System.Drawing.Color OsdBorder         => _isDark ? Dark.OsdBorder         : Light.OsdBorder;
    public static System.Drawing.Color ComboSelectedBg   => _isDark ? Dark.ComboSelectedBg   : Light.ComboSelectedBg;

    // ── Dark — Catppuccin Mocha (existing palette, untouched). ──
    private static class Dark
    {
        public static readonly System.Drawing.Color Bg              = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x2E);
        public static readonly System.Drawing.Color Fg              = System.Drawing.Color.FromArgb(0xCD, 0xD6, 0xF3);
        public static readonly System.Drawing.Color FgDisabled      = System.Drawing.Color.FromArgb(0x60, 0x60, 0x70);
        public static readonly System.Drawing.Color Dim             = System.Drawing.Color.FromArgb(0xA0, 0xA0, 0xC0);
        public static readonly System.Drawing.Color HighlightBg     = System.Drawing.Color.FromArgb(0x35, 0x35, 0x50);
        public static readonly System.Drawing.Color EditBg          = System.Drawing.Color.FromArgb(0x2A, 0x2A, 0x3E);
        public static readonly System.Drawing.Color Divider         = System.Drawing.Color.FromArgb(0x40, 0x40, 0x50);
        public static readonly System.Drawing.Color AccentBlue      = System.Drawing.Color.FromArgb(0x89, 0xB4, 0xFA);
        public static readonly System.Drawing.Color AccentGreen     = System.Drawing.Color.FromArgb(0xA6, 0xE3, 0xA1);
        public static readonly System.Drawing.Color AccentRed       = System.Drawing.Color.FromArgb(0xF3, 0x8B, 0xA8);
        public static readonly System.Drawing.Color AccentWarn      = System.Drawing.Color.FromArgb(255, 152, 0);
        public static readonly System.Drawing.Color OsdBorder       = System.Drawing.Color.FromArgb(0x44, 0x44, 0x5A);
        public static readonly System.Drawing.Color ComboSelectedBg = System.Drawing.Color.FromArgb(0x35, 0x35, 0x50);
    }

    // ── Light — v2.1.x classic (pure white + cornsilk). ──
    // Selected over Catppuccin Latte (the template's Variant A) because the
    // first-look on the Latte port read as "cool tint, hurts eyes" at dialog
    // scale. v2.1.x classic mirrors the pixel-for-pixel feel of the original
    // MWBToggle/sibling pre-v2.x light theme: pure white BG, near-black text,
    // brand-blue accent #2255AA, cornsilk #FFF8DC for the focus tint.
    //
    // Slot-clash watchout (per canonical template): HighlightBg (cornsilk
    // warm yellow) is used for menu/button hover AND OSD pill bg. EditBg
    // (faint cool off-white) is used for ComboBox bg AND button-pressed
    // state. Hover-warm-pressed-cool is unusual but acceptable — the pressed
    // state is brief and the hue mismatch goes unanalysed in practice.
    private static class Light
    {
        public static readonly System.Drawing.Color Bg              = System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xFF); // pure white
        public static readonly System.Drawing.Color Fg              = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E); // near-black text
        public static readonly System.Drawing.Color FgDisabled      = System.Drawing.Color.FromArgb(0x99, 0x99, 0x99); // muted grey
        public static readonly System.Drawing.Color Dim             = System.Drawing.Color.FromArgb(0x55, 0x55, 0x55); // secondary text (~7.5:1 on white — comfortably WCAG AAA)
        public static readonly System.Drawing.Color HighlightBg     = System.Drawing.Color.FromArgb(0xFF, 0xF8, 0xDC); // cornsilk — warm focus tint
        public static readonly System.Drawing.Color EditBg          = System.Drawing.Color.FromArgb(0xF8, 0xF8, 0xF8); // faint off-white — input inset
        public static readonly System.Drawing.Color Divider         = System.Drawing.Color.FromArgb(0xC8, 0xC8, 0xC8); // light grey divider
        public static readonly System.Drawing.Color AccentBlue      = System.Drawing.Color.FromArgb(0x22, 0x55, 0xAA); // brand blue — section headers
        public static readonly System.Drawing.Color AccentGreen     = System.Drawing.Color.FromArgb(0x2E, 0x7D, 0x32); // forest green — online device header
        public static readonly System.Drawing.Color AccentRed       = System.Drawing.Color.FromArgb(0xC6, 0x28, 0x28); // deep red — offline device header
        public static readonly System.Drawing.Color AccentWarn      = System.Drawing.Color.FromArgb(0xB4, 0x53, 0x09); // deep amber — warning text (distinct from offline-red so "warn" reads as caution, "offline" as error)
        public static readonly System.Drawing.Color OsdBorder       = System.Drawing.Color.FromArgb(0xC8, 0xC8, 0xC8); // matches divider
        public static readonly System.Drawing.Color ComboSelectedBg = System.Drawing.Color.FromArgb(0xFF, 0xF8, 0xDC); // cornsilk — matches hover tint
    }
}
