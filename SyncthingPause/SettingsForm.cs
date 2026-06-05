using System.Diagnostics;

namespace SyncthingPause;

/// <summary>
/// Dark-themed Settings GUI matching the AHK version layout.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly SyncthingApi _api;
    private readonly Action _onApplied;
    private readonly Action _onSaved;
    private readonly OsdToolTip _osd;
    private bool _disposed;

    // Controls we need to read on Save (assigned in Build* methods)
    private ComboBox _cboDblClick = null!;
    private ComboBox _cboMiddleClick = null!;
    private CheckBox _cbRunOnStartup = null!;
    private CheckBox _cbStartBrowser = null!;
    private CheckBox _cbNetPause = null!;
    private CheckBox _cbAutoUpdates = null!;
    private TextBox _edApiKey = null!;
    private TextBox _edSyncExe = null!;
    private TextBox _edWebUI = null!;
    private NumericUpDown _nudDelay = null!;
    private CheckBox _cbGlobal = null!;
    private CheckBox _cbLocal = null!;
    private CheckBox _cbRelay = null!;
    private Label? _discoveryWarnLabel;
    private System.Windows.Forms.Timer? _discoveryRetryTimer;
    // Bounded-retry counter for the 2 s discovery-probe loop. 30 ticks × 2 s = 60 s.
    // After the cap we stop the timer, dispose it, and update the warning label so
    // a permanently-unreachable Syncthing doesn't keep hitting /rest/config/options
    // for the entire time the dialog is open. Reopening Settings re-arms the loop.
    private const int DiscoveryRetryCapTicks = 30;
    private int _discoveryRetryCount;
    private CheckBox _cbSoundNotify = null!;
    private CheckBox _cbStopOnExit = null!;
    // Theme toggle — two radios in the Discovery section's right column (label
    // on line 1, radios on line 2). RadioButton is a "toggle box" that reads
    // both options at a glance; persisted as `Dark` or `Light` in AppConfig.
    private RadioButton _rbThemeDark = null!;
    private RadioButton _rbThemeLight = null!;

    // Footer action buttons — sized in Load (handle exists → LogicalToDeviceUnits is DPI-accurate)
    // to a generous minimum width so they read as the primary actions and balance the wider
    // links/actions row above them, rather than shrinking to their short labels.
    private Button _btnSave = null!;
    private Button _btnApply = null!;
    private Button _btnCancel = null!;

    // Reveal-glyph button (MDL2); every other control draws from CardLayout's CardFonts.
    private readonly Font _iconFont;

    // Form background (captured after Theme.Initialize); CardLayout reads the rest
    // of the palette directly from Theme.* per render.
    private static readonly Color BgColor = Theme.Bg;

    // 96-DPI design CLIENT width. Above 100% the Load handler sizes the form to
    // DesignClientWidth * (DeviceDpi/96) instead of the AutoSize content width, which
    // under-measures at high DPI (a Fill field's PreferredSize is its 96-DPI literal
    // width). 100% is unaffected — it keeps the natural measured width.
    private const int DesignClientWidth = 450;

    public SettingsForm(AppConfig config, SyncthingApi api, OsdToolTip osd, Action onApplied, Action onSaved)
    {
        _config = config;
        _api = api;
        _osd = osd;
        _onApplied = onApplied;
        _onSaved = onSaved;

        _iconFont = new Font("Segoe MDL2 Assets", 9f);

        Text = $"SyncthingPause v{AppConfig.Version} \u2014 Settings";
        // FixedDialog (not FixedToolWindow) so the close button is the standard
        // full-size Windows X rather than the cramped tool-window variant.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = BgColor;
        ShowInTaskbar = false;
        // Pin the design baseline to 96 DPI BEFORE setting AutoScaleMode so that
        // every literal `new Size(_, _)` / `new Point(_, _)` below is always
        // interpreted as 96-DPI design pixels — regardless of which monitor the
        // form is first realized on. Without this, AutoScaleDimensions defaults
        // to whatever the form's first monitor reports, and on 125%/150% laptops
        // the form gets double-scaled (once by us, once by WinForms) which clips
        // buttons + NumericUpDown.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // Layout: one card per former section, stacked + scrollable. Every control
        // lands in the same field the unchanged Save/probe logic reads — only how
        // the controls are created and positioned changes, never the behaviour.
        var stack = new CardStack(this, scroll: true);
        BuildClickActionsSection(stack);
        BuildGeneralSection(stack);
        BuildPathsSection(stack);
        BuildApiSection(stack);
        BuildDiscoverySection(stack);
        BuildUpdatesSection(stack);
        BuildButtonRow(stack);

        // Fit the window to its content; clamp to the work area so a tall 150% render
        // scrolls inside the AutoScroll host instead of running off-screen. Measured on
        // Load (handle exists + AutoScale has run → ContentSize is device-DPI accurate).
        Load += (_, _) =>
        {
            // Each ThemedComboBox bumped its ItemHeight in OnHandleCreated (AFTER the initial
            // layout pass), growing the closed owner-draw box via CB_SETITEMHEIGHT. ComboBox's
            // preferred height ignores ItemHeight AND it snaps its own height, so the AutoSize
            // cell it sits in stays too short and the box bleeds past the card's bottom border on
            // the last row (Middle-click). The combo exposes how far it overflows; reserve that —
            // plus a few px of breathing room — as extra BOTTOM MARGIN on each combo row (the one
            // lever the combo can't override; the Margin setter also busts the cell's stale
            // preferred-size cache, which a plain PerformLayout did NOT — that was the old, inert fix).
            foreach (var cb in new[] { _cboDblClick, _cboMiddleClick })
            {
                if (cb is ThemedComboBox t)
                {
                    var m = cb.Margin;
                    cb.Margin = new Padding(m.Left, m.Top, m.Right,
                        m.Bottom + t.OverflowBelow + LogicalToDeviceUnits(4));
                }
            }

            // Footer actions: give Save / Apply / Cancel a generous minimum width so they read as
            // substantial primary buttons and visually balance the (wider) links row above, instead
            // of collapsing to their short labels. LogicalToDeviceUnits keeps it crisp at any DPI.
            int minBtnW = LogicalToDeviceUnits(92);
            _btnSave.MinimumSize = new Size(minBtnW, 0);
            _btnApply.MinimumSize = new Size(minBtnW, 0);
            _btnCancel.MinimumSize = new Size(minBtnW, 0);

            // Re-run layout now that the reservations + button widths are set, so ContentSize /
            // footer PreferredSize below measure the finalized geometry.
            PerformLayout();

            // Cards scroll inside the AutoScroll host; the button bar is a docked footer
            // OUTSIDE that host (so it never falls below the scroll fold at 150%). Size the
            // window to: cards content (clamped to the work area) PLUS the footer height.
            var content = stack.ContentSize;                       // cards only — buttons are in the footer
            int footerH = stack.Footer?.PreferredSize.Height ?? 0;
            var wa = Screen.FromControl(this).WorkingArea;
            int availH = wa.Height - LogicalToDeviceUnits(80) - footerH;
            int cardsH = content.Height;
            bool clamp = cardsH > availH;
            if (clamp) cardsH = Math.Max(0, availH);   // floor: a pathologically tall footer can't drive a negative size
            // Content fits: add a few device-px of slack so PreferredSize measurement drift
            // (Load-time prediction runs a hair short of the finalized layout) doesn't trip a
            // spurious AutoScroll scrollbar. Invisible when content genuinely fits.
            else cardsH += LogicalToDeviceUnits(6);

            // Width: AutoSize content under-measures at high DPI — a Fill field's PreferredSize
            // reports its 96-DPI literal width (it only STRETCHES when given room), so the measured
            // stack lands ~1.2x, not 1.5x. Mirror EQSwitch UI/SettingsForm: above 100% take the width
            // from a design baseline * the DPI factor, so the Dock=Top stack + Fill fields stretch to
            // a true 1.5x. At 100% keep the measured width (baseline unchanged).
            double f = DeviceDpi / 96.0;
            int w = content.Width;
            if (f > 1.001) w = Math.Max(w, (int)Math.Round(DesignClientWidth * f));
            if (clamp) w += SystemInformation.VerticalScrollBarWidth;
            w = Math.Min(w, wa.Width);
            // The footer's button rows don't scroll horizontally, so the form must fit them too.
            w = Math.Max(w, stack.Footer?.PreferredSize.Width ?? 0);
            ClientSize = new Size(w, cardsH + footerH);
        };

        // First-run auto-open can land behind a fullscreen app (game, video) since
        // TopMost loses to fullscreen D3D. Force foreground on the first paint so
        // the user actually sees the dialog they're being asked to configure.
        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
        };
    }

    private void BuildClickActionsSection(CardStack stack)
    {
        var card = stack.NewCard("\U0001F5B1", "Tray Click Actions", Theme.AccentBlue);

        _cboDblClick = card.RowFit("Double-click:",
            Fields.Combo(160, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.DblClickAction)));
        _cboDblClick.AccessibleName = "Double-click action";

        _cboMiddleClick = card.RowFit("Middle-click:",
            Fields.Combo(160, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.MiddleClickAction)));
        _cboMiddleClick.AccessibleName = "Middle-click action";
    }

    private void BuildGeneralSection(CardStack stack)
    {
        var card = stack.NewCard("⚙", "General", Theme.AccentBlue);

        _cbRunOnStartup = card.Check(Fields.Check("Run on startup", _config.RunOnStartup));
        if (_config.IsPortable)
        {
            _cbRunOnStartup.Enabled = false;
            card.Hint("(not available in portable mode)");
        }
        _cbStartBrowser = card.Check(Fields.Check("Start browser when Syncthing launches", _config.StartBrowser));
        _cbNetPause = card.Check(Fields.Check("Auto-pause on public networks", _config.NetworkAutoPause));
        _cbSoundNotify = card.Check(Fields.Check("Play sounds on events", _config.SoundNotifications));
        _cbStopOnExit = card.Check(Fields.Check("Stop Syncthing when tray exits", _config.StopOnExit));

        // Windows startup delay — gap between tray launch and Syncthing launch (lets the
        // network stack settle on auto-startup). Spin in 5s steps or type any value [0, 3600].
        // 58px is sized for the realistic value (a 1–2 digit delay, default 20) rather than the
        // bulky 4-digit-max width — yet still clears "3600" + spinner at 100% (~51px content) and
        // at 150% (AutoScaleMode.Dpi scales the 58px by the DPI ratio → ~87px), so the [0, 3600]
        // max that AppConfig's load clamp depends on stays uncapped. Narrower would risk clipping.
        _nudDelay = Fields.Numeric(0, 3600, _config.StartupDelay, width: 58, increment: 5);
        _nudDelay.AccessibleName = "Windows startup delay in seconds";
        card.FlowRow("Windows startup delay:", _nudDelay, Fields.Label("seconds"));
    }

    private void BuildPathsSection(CardStack stack)
    {
        var card = stack.NewCard("\U0001F4C1", "Paths", Theme.AccentBlue);

        var btnBrowse = Fields.Button("...");
        btnBrowse.AccessibleName = "Browse for syncthing.exe";
        btnBrowse.Click += OnBrowseSyncExe;
        _edSyncExe = card.RowWith("Syncthing:", Fields.Text(220, mono: true), btnBrowse);
        _edSyncExe.Text = _config.SyncExe;
        _edSyncExe.AccessibleName = "Syncthing executable path";

        var btnOpenWebUI = Fields.Button("Open");
        btnOpenWebUI.AccessibleName = "Open Web UI in browser";
        btnOpenWebUI.Click += (_, _) =>
        {
            var url = _edWebUI.Text.Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                _osd.ShowMessage("URL is not valid — use http:// or https://", 4000);
                return;
            }
            try
            {
                // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- url is validated as http/https via Uri.TryCreate above; handed to Windows default browser
                using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _osd.ShowMessage("Could not open browser — check Windows default browser", 5000);
                TrayLog.Warn("SettingsForm OpenWebUI failed: " + ex.Message);
            }
        };
        _edWebUI = card.RowWith("Web UI:", Fields.Text(220, mono: true), btnOpenWebUI);
        _edWebUI.Text = _config.WebUI;
        _edWebUI.AccessibleName = "Syncthing Web UI URL";
    }

    private void BuildApiSection(CardStack stack)
    {
        var card = stack.NewCard("\U0001F511", "API", Theme.AccentBlue);

        // "" = Segoe MDL2 RedEye (show); "" = Hide (mask). The glyph is
        // unreadable to assistive tech, hence the explicit AccessibleName.
        var btnReveal = Fields.Button("", _iconFont);
        btnReveal.AccessibleName = "Show or hide API key";

        // A Syncthing API key is ~40 chars; a mono fill field fits it and stretches at any DPI.
        _edApiKey = card.RowWith("API Key:", Fields.Text(216, mono: true), btnReveal);
        _edApiKey.Text = _config.ApiKey;
        _edApiKey.UseSystemPasswordChar = true;
        _edApiKey.AccessibleName = "Syncthing API key";

        btnReveal.Click += (_, _) =>
        {
            _edApiKey.UseSystemPasswordChar = !_edApiKey.UseSystemPasswordChar;
            btnReveal.Text = _edApiKey.UseSystemPasswordChar ? "" : "";
        };
    }

    private void BuildDiscoverySection(CardStack stack)
    {
        var card = stack.NewCard("\U0001F310", "Discovery", Theme.AccentBlue);

        // Two columns: discovery toggles on the left, appearance (Theme) on the right,
        // expressed relationally so they stay aligned at any DPI (the old layout pinned
        // the Theme column to an absolute x=240 and fought overlap at 125%+).
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var checks = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 28, 0),
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        _cbGlobal = Fields.Check("Global Discovery");
        _cbLocal = Fields.Check("Local Discovery");
        _cbRelay = Fields.Check("NAT Traversal (Relaying)");
        foreach (var cb in new[] { _cbGlobal, _cbLocal, _cbRelay })
        {
            cb.Margin = new Padding(0, 3, 0, 3);
            checks.Controls.Add(cb);
        }

        // Theme toggle - appearance grouped with the network toggles per user request.
        // Persists to AppConfig and applies on next launch (Save spawns a replacement
        // process; see Theme.cs for the restart-to-apply / GDI-cache rationale).
        var themeCol = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        themeCol.Controls.Add(new Label
        {
            Text = "Theme:",
            AutoSize = true,
            ForeColor = Theme.Dim,
            Font = CardFonts.Body,
            Margin = new Padding(0, 4, 0, 2),
        });
        bool currentlyDark = string.Equals(_config.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(_config.ThemeMode);
        var radios = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        _rbThemeDark = new RadioButton
        {
            Text = "Dark",
            Font = CardFonts.Body,
            ForeColor = Theme.Fg,
            AutoSize = true,
            Checked = currentlyDark,
            Margin = new Padding(0, 0, 12, 0),
            AccessibleName = "Dark theme",
        };
        _rbThemeLight = new RadioButton
        {
            Text = "Light",
            Font = CardFonts.Body,
            ForeColor = Theme.Fg,
            AutoSize = true,
            Checked = !currentlyDark,
            // Match Dark's vertical margins exactly. In a LeftToRight FlowLayoutPanel each
            // control sits at rowTop + Margin.Top; Dark uses Padding(0,0,12,0) (top 0) while
            // the RadioButton default Margin is Padding(3) (top 3) — leaving Light 3px lower
            // than Dark. Zero top/bottom here puts the two glyphs on the same baseline.
            Margin = Padding.Empty,
            AccessibleName = "Light theme",
        };
        radios.Controls.Add(_rbThemeDark);
        radios.Controls.Add(_rbThemeLight);
        themeCol.Controls.Add(radios);

        grid.Controls.Add(checks, 0, 0);
        grid.Controls.Add(themeCol, 1, 0);
        card.Full(grid);

        // Persistent warn label - toggled by the async probe (SetDiscoveryWarn) instead of
        // the old add-to-Controls / Remove churn (which assumed a flat form, not a card).
        _discoveryWarnLabel = card.Hint(string.Empty);
        _discoveryWarnLabel.Visible = false;

        // Boxes start disabled; the async probe enables them + sets _discoveryReadOk so a
        // Save before the probe lands cannot clobber Syncthing's state with default-false.
        _cbGlobal.Enabled = _cbLocal.Enabled = _cbRelay.Enabled = false;
        _discoveryReadOk = false;

        bool apiKeyEmpty = string.IsNullOrEmpty(_config.ApiKey);
        bool reachable = !apiKeyEmpty && _api.IsReachable();
        if (apiKeyEmpty)
        {
            SetDiscoveryWarn("(set API Key above to manage discovery)");
        }
        else if (!reachable)
        {
            SetDiscoveryWarn("(could not read current state - API unreachable)");
            StartDiscoveryRetryTimer();
        }
        else
        {
            StartDiscoveryRetryTimer();
        }
    }

    /// <summary>Show/replace the Discovery warning text, or hide it when null/empty. Replaces
    /// the pre-rebuild add/remove-from-Controls dance now that the label lives in a card.</summary>
    private void SetDiscoveryWarn(string? text)
    {
        if (_discoveryWarnLabel == null || _discoveryWarnLabel.IsDisposed) return;
        if (string.IsNullOrEmpty(text)) { _discoveryWarnLabel.Visible = false; return; }
        _discoveryWarnLabel.Text = text;
        _discoveryWarnLabel.Visible = true;
    }

    private void StartDiscoveryRetryTimer()
    {
        // Require an API key — without it the read will deterministically fail and
        // spamming /rest on a bad key just adds log noise.
        if (string.IsNullOrEmpty(_config.ApiKey)) return;

        _discoveryRetryCount = 0;
        _discoveryRetryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _discoveryRetryTimer.Tick += (_, _) => OnDiscoveryRetryTick();
        _discoveryRetryTimer.Start();

        // Fire the first probe immediately rather than waiting 2 s for the
        // initial Tick. The probe is fully async (pool thread + BeginInvoke
        // back) so this doesn't reblock the UI; it just compresses the
        // dialog-open-to-boxes-populated latency from ~2 s to ~200-500 ms
        // for the common case where Syncthing is reachable and responsive.
        RunDiscoveryProbeOnce();
    }

    private void OnDiscoveryRetryTick()
    {
        if (_disposed || IsDisposed) { _discoveryRetryTimer?.Stop(); return; }

        if (++_discoveryRetryCount > DiscoveryRetryCapTicks)
        {
            _discoveryRetryTimer?.Stop();
            _discoveryRetryTimer?.Dispose();
            _discoveryRetryTimer = null;
            // Lazy-create the warn label if the dialog opened in the
            // reachable-at-start path (no initial label was created then).
            // After 60 s of failures we owe the user some explanation
            // beyond "boxes are mysteriously disabled."
            SetDiscoveryWarn("(Syncthing unreachable - reopen Settings to retry)");
            return;
        }

        RunDiscoveryProbeOnce();
    }

    /// <summary>
    /// Fires a single discovery probe on a pool thread. On 200, marshals the
    /// parsed values back to the UI under SuspendLayout, enables the boxes,
    /// removes any warn label, and stops the retry timer. On any failure
    /// (TCP probe miss, exception, non-200 status) it returns silently —
    /// the retry timer (or the timeout branch in <see cref="OnDiscoveryRetryTick"/>)
    /// handles surface. Extracted from the old inline Tick lambda so both
    /// the initial probe and subsequent retries share one code path.
    /// </summary>
    private void RunDiscoveryProbeOnce()
    {
        _ = Task.Run(() =>
        {
            if (_disposed || IsDisposed) return;
            if (!_api.IsReachable()) return;

            int status;
            string body;
            try
            {
                (status, body) = _api.Get("/rest/config/options", timeoutMs: 1500);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Discovery probe failed: " + ex.Message);
                return;
            }
            if (status != 200) return;

            bool g = ParseJsonBool(body, "globalAnnounceEnabled", false);
            bool l = ParseJsonBool(body, "localAnnounceEnabled", false);
            bool r = ParseJsonBool(body, "relaysEnabled", false);

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (_disposed || IsDisposed) return;
                    SuspendLayout();
                    try
                    {
                        _cbGlobal.Checked = g;
                        _cbLocal.Checked = l;
                        _cbRelay.Checked = r;
                        _cbGlobal.Enabled = _cbLocal.Enabled = _cbRelay.Enabled = true;
                        _discoveryReadOk = true;

                        SetDiscoveryWarn(null);
                    }
                    finally
                    {
                        ResumeLayout(false);
                    }
                    // Single deferred repaint instead of cascading per-mutation ones.
                    Invalidate(invalidateChildren: true);

                    _discoveryRetryTimer?.Stop();
                    _discoveryRetryTimer?.Dispose();
                    _discoveryRetryTimer = null;
                }));
            }
            catch (ObjectDisposedException) { /* dialog closed between the probe and the marshal */ }
            catch (InvalidOperationException) { /* handle not yet created */ }
        });
    }

    private bool _discoveryReadOk;

    private void BuildUpdatesSection(CardStack stack)
    {
        var card = stack.NewCard("⬆", "Updates", Theme.AccentBlue);

        _cbAutoUpdates = Fields.Check("Check for Syncthing updates (daily)", _config.AutoCheckUpdates);
        var btnCheckNow = Fields.Button("Check Now");
        btnCheckNow.AccessibleName = "Check Now";
        btnCheckNow.Click += async (_, _) =>
        {
            // Double-click guard: the _api.Get HTTP call below is now async
            // off the UI thread, but the click handler still needs the guard
            // because a fast user can queue a second click before the await
            // resumes. Disabling for the duration also prevents stacking two
            // modal SyncthingUpdateDialog instances if both clicks see
            // newer=true.
            if (!btnCheckNow.Enabled) return;
            btnCheckNow.Enabled = false;
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _osd.ShowMessage("API Key required \u2014 set above", 3000);
                btnCheckNow.Enabled = true;
                return;
            }
            _osd.ShowMessage("Checking for updates...", 2000);

            // v3.2.1: move the daemon-poll HTTP off the UI thread. Pre-v3.2.1
            // this ran synchronously on the click handler, freezing the dialog
            // for up to 5 s (the default _api.Get timeout) on a slow daemon
            // or transient network. Same async pattern as the Discovery probe
            // fix in v3.2.0.
            int status;
            string body;
            try
            {
                (status, body) = await System.Threading.Tasks.Task.Run(
                    () => _api.Get("/rest/system/upgrade"));
            }
            catch (Exception ex)
            {
                TrayLog.Warn($"Check Now threw {ex.GetType().Name}: {ex.Message}");
                _osd.ShowMessage("Could not reach Syncthing API", 3000);
                if (!_disposed && !IsDisposed) btnCheckNow.Enabled = true;
                return;
            }

            // The user could have closed the dialog while we were on the pool
            // thread. Standard async-void guard \u2014 without it, the UI work
            // below would mutate disposed controls and ObjectDisposedException
            // escapes async-void as an unobserved task exception (which
            // TaskScheduler.UnobservedTaskException can crash on at GC).
            if (_disposed || IsDisposed) return;

            try
            {
                if (status == 200)
                {
                    bool newer = ParseJsonBool(body, "newer", false);
                    if (newer)
                    {
                        // Use JsonDocument — same standard ParseJsonBool's docstring
                        // already calls out for this file. The prior IndexOf-based
                        // parser would lock onto "latest" inside an unrelated string
                        // value (the same bypass class ParseJsonBool was rewritten
                        // to defeat). 2026-04-25 audit F2.
                        string latest = SyncthingUpdateDialog.UnknownVersionSentinel;
                        string running = SyncthingUpdateDialog.UnknownVersionSentinel;
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("latest", out var lEl) &&
                                lEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                latest = lEl.GetString() ?? SyncthingUpdateDialog.UnknownVersionSentinel;
                            }
                            if (doc.RootElement.TryGetProperty("running", out var rEl) &&
                                rEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                running = rEl.GetString() ?? SyncthingUpdateDialog.UnknownVersionSentinel;
                            }
                        }
                        catch (System.Text.Json.JsonException) { /* keep defaults */ }

                        // Offer the user an explicit upgrade path instead of just an
                        // OSD. The dialog handles POST /rest/system/upgrade + polling
                        // for the daemon to come back. Modal so the OSD doesn't race
                        // with the user clicking again.
                        using var dlg = new SyncthingUpdateDialog(_api, running, latest);
                        dlg.ShowDialog(this);
                    }
                    else
                    {
                        _osd.ShowMessage("Syncthing is up to date", 3000);
                    }
                }
                else
                {
                    _osd.ShowMessage($"Check failed (HTTP {status})", 3000);
                }
            }
            catch (Exception ex)
            {
                // Defensive catch for the post-HTTP block: JsonDocument.Parse
                // has its own local catch (line ~616), but TryGetProperty +
                // ShowDialog could still throw on pathological input or GDI
                // failure. Surfacing to logs preserves the diagnostic value
                // the v3.1.0 catch refactor added (typed errors with exception
                // type names, not silent fallthrough).
                TrayLog.Warn($"Check Now post-HTTP threw {ex.GetType().Name}: {ex.Message}");
                _osd.ShowMessage("Check Now failed — see tray.log", 3000);
            }
            finally
            {
                if (!_disposed && !IsDisposed) btnCheckNow.Enabled = true;
            }
        };
        card.FlowRow(string.Empty, _cbAutoUpdates, btnCheckNow);
    }

    private void BuildButtonRow(CardStack stack)
    {
        // Top row: links + actions (left-aligned). Bottom row: Save / Apply / Cancel hug
        // the right edge. AutoSize buttons grow to their text at any DPI, so the old
        // per-button hand-tuned widths and clip-avoidance comments are gone.
        Button Link(string text, string url)
        {
            var b = Fields.Button(text);
            b.Click += (_, _) =>
            {
                // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- hardcoded GitHub URLs only
                using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            };
            return b;
        }

        var btnGitHub = Link("GitHub", "https://github.com/itsnateai/syncthingpause");

        var btnUpdate = Fields.Button("Update");
        btnUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };

        var btnSyncthing = Link("Syncthing", "https://github.com/syncthing/syncthing");

        var btnHelp = Fields.Button("Help");
        btnHelp.Click += (_, _) =>
        {
            using var hf = new HelpForm(_config.SettingsFilePath, (msg, ms) => _osd.ShowMessage(msg, ms));
            hf.ShowDialog(this);
        };

        var btnCheck = Fields.Button("Check Config");
        btnCheck.AccessibleName = "Check Config";
        btnCheck.Click += OnCheckConfig;

        var top = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Left,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
        };
        foreach (var b in new[] { btnGitHub, btnUpdate, btnSyncthing, btnHelp, btnCheck })
        {
            b.Margin = new Padding(0, 0, 6, 0);
            top.Controls.Add(b);
        }

        var btnSave = _btnSave = Fields.Primary("Save");
        btnSave.Click += OnSave;
        var btnApply = _btnApply = Fields.Button("Apply");
        btnApply.Click += OnApply;
        var btnCancel = _btnCancel = Fields.Button("Cancel");
        btnCancel.DialogResult = DialogResult.Cancel;
        btnCancel.Click += (_, _) => Close();

        // Pin both button rows in a footer DOCKED below the scroll viewport so the primary
        // actions stay visible when the cards scroll at 150%. Pre-fix these were AddFullWidth'd
        // INTO the AutoScroll stack and fell below the fold (Save/Apply/Cancel clipped off-frame).
        var footer = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg,
            Padding = new Padding(8, 4, 8, 6),
            Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(top, 0, 0);
        // Save / Apply / Cancel spread evenly across the row — each centred in an equal third,
        // so the gaps before / between / after are equal (Bars.Split hugged all three to the
        // right edge, leaving the left two-thirds empty).
        footer.Controls.Add(Bars.Distribute(btnSave, btnApply, btnCancel), 0, 1);
        stack.SetFooter(footer);

        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private void OnBrowseSyncExe(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select syncthing.exe",
            Filter = "Executables (*.exe)|*.exe",
            FileName = _edSyncExe.Text,
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            _edSyncExe.Text = ofd.FileName;
    }

    private void OnCheckConfig(object? sender, EventArgs e)
    {
        var results = string.Empty;

        if (File.Exists(_config.SyncExe))
            results += "\u2713 Syncthing exe: Found\r\n";
        else
            results += $"\u2717 Syncthing exe: NOT FOUND at {_config.SyncExe}\r\n";

        if (IsSyncthingRunning())
            results += "\u2713 Process: Running\r\n";
        else
            results += "\u2717 Process: Not running\r\n";

        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            results += "\u2717 API Key: Not set\r\n";
        }
        else
        {
            try
            {
                var (status, _) = _api.Get("/rest/system/status");
                results += status == 200
                    ? "\u2713 API: Connected (HTTP 200)\r\n"
                    : $"\u2717 API: HTTP {status}\r\n";
            }
            catch (Exception ex)
            {
                // Surface to logs even though the user-visible OSD stays terse.
                // Previously a bare catch swallowed every type with no diagnostics,
                // which made it impossible to distinguish "daemon not running" from
                // a real bug in the OSD path.
                TrayLog.Warn($"OnCheckConfig API probe threw {ex.GetType().Name}: {ex.Message}");
                results += "\u2717 API: Unreachable\r\n";
            }

            try
            {
                var (status2, body2) = _api.Get("/rest/config/options");
                if (status2 == 200)
                {
                    var gd = ParseJsonBool(body2, "globalAnnounceEnabled", false) ? "on" : "off";
                    var ld = ParseJsonBool(body2, "localAnnounceEnabled", false) ? "on" : "off";
                    var rl = ParseJsonBool(body2, "relaysEnabled", false) ? "on" : "off";
                    results += $"  Discovery: Global={gd} Local={ld} NAT={rl}\r\n";
                }
            }
            catch (Exception ex)
            {
                // Best-effort: this is the second probe in a diagnostic-only flow,
                // and the first probe's failure already surfaced. Log for parity
                // with the catch above; don't add a second OSD line.
                TrayLog.Warn($"OnCheckConfig options probe threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        _osd.ShowMessage(results.Replace("\r\n", " | ").TrimEnd(' ', '|'), 5000);
    }

    /// <summary>
    /// Four outcomes that callers (OnSave, OnApply) need to route on:
    /// <list type="bullet">
    ///   <item><term>Normal</term><description>Save succeeded; post-save UI work proceeds normally.</description></item>
    ///   <item><term>ThemeRestartFired</term><description>Theme changed; replacement spawned; <c>Application.Exit()</c> queued. Caller MUST NOT touch UI on this dying instance.</description></item>
    ///   <item><term>ThemeRestartFailed</term><description>Theme changed but <c>Process.Start</c> failed (locked exe, AV scan). User-facing OSD "Theme will apply on next launch" already shown. Caller should close dialog quietly (or, on the Apply path, refresh the tray) without firing the "Settings applied" OSD (which would bury the fallback message).</description></item>
    ///   <item><term>SaveFailed</term><description>INI write failed. "Could not save settings…" OSD already shown and in-memory <c>_config</c> mutations were rolled back (at minimum <c>ThemeMode</c>). Caller should close dialog quietly without firing "Settings applied" (which would mislead).</description></item>
    /// </list>
    /// </summary>
    private enum ApplyResult { Normal, ThemeRestartFired, ThemeRestartFailed, SaveFailed }

    /// <summary>
    /// Persists settings and triggers post-save side effects. Returns one of
    /// the four <see cref="ApplyResult"/> outcomes; callers branch on the
    /// result to decide whether to <c>Close()</c>, fire <c>_onApplied()</c>,
    /// or show the "Settings applied" OSD.
    /// </summary>
    private ApplyResult ApplySettings(bool notify)
    {
        // Validate the sync-exe path. ValidateSyncExe rejects UNC paths (NTLM-leak
        // via SMB auth on File.Exists/LaunchSyncthing), null-byte truncation,
        // traversal, wrong filename, missing file. INI Load already enforces this;
        // the missing call-site here is the gap closed by 2026-04-25 audit F1.
        //
        // On rejection we KEEP the previously-saved SyncExe and continue saving
        // OTHER settings — locking the user out of saving "Run on startup" because
        // their syncthing.exe was uninstalled or moved is unfriendly. OSD-warn only
        // when the user actively typed something different (not on stale-from-load
        // case where the textbox just mirrors a now-missing saved path).
        var validatedExe = AppConfig.ValidateSyncExe(_edSyncExe.Text);
        if (validatedExe is null)
        {
            if (!string.IsNullOrWhiteSpace(_edSyncExe.Text) &&
                !string.Equals(_edSyncExe.Text, _config.SyncExe, StringComparison.Ordinal))
            {
                _osd.ShowMessage(
                    "Syncthing path rejected — keeping previous value", 5000);
            }
            // Snap textbox back to what's actually persisted so the user can see
            // their typed (rejected) value didn't take.
            _edSyncExe.Text = _config.SyncExe ?? "";
        }
        else
        {
            _config.SyncExe = validatedExe;
        }

        _config.DblClickAction = AppConfig.ActionIndexToValue(_cboDblClick.SelectedIndex);
        _config.MiddleClickAction = AppConfig.ActionIndexToValue(_cboMiddleClick.SelectedIndex);
        _config.RunOnStartup = _config.IsPortable ? false : _cbRunOnStartup.Checked;
        _config.StartBrowser = _cbStartBrowser.Checked;
        _config.NetworkAutoPause = _cbNetPause.Checked;
        _config.AutoCheckUpdates = _cbAutoUpdates.Checked;
        _config.SoundNotifications = _cbSoundNotify.Checked;
        _config.StopOnExit = _cbStopOnExit.Checked;
        _config.ApiKey = _edApiKey.Text;
        _config.WebUI = AppConfig.ValidateWebUI(_edWebUI.Text);

        // Theme — snapshot the pre-save value so we can decide whether to
        // auto-restart. Compare case-insensitively to match Load's normaliser.
        string priorTheme = _config.ThemeMode;
        string newTheme = _rbThemeLight.Checked ? "Light" : "Dark";
        bool themeChanged = !string.Equals(priorTheme, newTheme,
            StringComparison.OrdinalIgnoreCase);
        _config.ThemeMode = newTheme;

        // NumericUpDown clamps to [Minimum, Maximum] on both spinner and typed input,
        // so no range check or fallback OSD is needed here — the value is always valid.
        _config.StartupDelay = (int)_nudDelay.Value;

        if (!_config.Save())
        {
            // Roll back the ThemeMode mutation we made above \u2014 INI didn't
            // persist, so in-memory _config must match disk. If we left
            // _config.ThemeMode at the user's unsaved pick, a Settings-reopen
            // before they fix the INI lock would pre-check the wrong radio
            // (and the next successful Save without a theme change would
            // silently flip the theme on the next launch \u2014 surprising).
            // Other _config.* fields aren't read by pre-save UI state, so
            // leaving them dirty is harmless; ThemeMode is the only field
            // with a meaningful secondary reader between SaveFailed and
            // the next save attempt.
            _config.ThemeMode = priorTheme;
            _osd.ShowMessage("Could not save settings \u2014 file may be locked", 5000);
            return ApplyResult.SaveFailed;
        }

        // Everything past here used to block the UI thread: a 300 ms IsReachable
        // probe, a synchronous HTTP PATCH (up to 1500 ms), a COM call to
        // WScript.Shell for the startup shortcut (50-200 ms), and the tray-refresh
        // callback which itself kicks 3 more HTTP GETs. Total ~350-1200 ms of
        // frozen UI. Now all four run on a pool thread — Save() returning means
        // the INI is on disk; anything else is best-effort background work.
        // OsdToolTip and the tray callbacks both self-marshal UI updates back.
        //
        // Snapshot every field the background task needs BEFORE leaving the UI
        // thread — the controls can't be read from a pool thread.
        bool globalDiscovery = _cbGlobal.Checked;
        bool localDiscovery = _cbLocal.Checked;
        bool relayEnabled = _cbRelay.Checked;
        bool discoveryReadOk = _discoveryReadOk;
        string apiKey = _config.ApiKey;
        bool isPortable = _config.IsPortable;
        bool runOnStartup = _config.RunOnStartup;
        bool netAutoPause = _config.NetworkAutoPause;
        string? iconPath = isPortable
            ? null
            : Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty,
                "Resources", "sync.ico");
        // On a theme-restart, suppress the tray-rebuild callback — the
        // replacement process re-reads everything from the INI and rebuilds
        // its menu from scratch, so firing LoadFolders on a tray context
        // that's about to dispose is just noise that races the teardown.
        Action? savedCallback = (notify && !themeChanged) ? _onSaved : null;

        if (themeChanged)
        {
            // Theme-restart path: run discovery PATCH + StartupShortcut.Apply
            // SYNCHRONOUSLY here so the dying process completes them BEFORE
            // Application.Exit fires. The pool-thread fan-out below would
            // otherwise outlive Application.Exit, racing the replacement's
            // 5 s mutex wait and writing OSDs into a disposed OsdToolTip.
            // UI thread blocks ~1.5–2 s worst case; user clicked Save
            // expecting some pause, and the replacement's confirmation toast
            // at ~+800 ms covers the perceived gap.
            //
            // Skipped on this path: the WMI probe (a precondition warn — it
            // re-fires on the next non-theme Save) and savedCallback (set to
            // null above; replacement does its own LoadFolders on startup).
            if (discoveryReadOk && !string.IsNullOrEmpty(apiKey) && _api.IsReachable())
            {
                try
                {
                    var g = globalDiscovery ? "true" : "false";
                    var l = localDiscovery ? "true" : "false";
                    var r = relayEnabled ? "true" : "false";
                    var (status, _) = _api.Patch("/rest/config/options",
                        $"{{\"globalAnnounceEnabled\":{g},\"localAnnounceEnabled\":{l},\"relaysEnabled\":{r}}}",
                        timeoutMs: 1500);
                    if (status != 200)
                        TrayLog.Warn($"Discovery PATCH returned HTTP {status} on theme-restart.");
                }
                catch (Exception ex)
                {
                    TrayLog.Warn("Discovery PATCH threw on theme-restart: " + ex.Message);
                }
            }
            if (iconPath is not null)
            {
                try { StartupShortcut.Apply(runOnStartup, iconPath); }
                catch (Exception ex)
                {
                    TrayLog.Warn("StartupShortcut.Apply threw on theme-restart: " + ex.Message);
                }
            }
            // TryAutoRestartForTheme returns true if the replacement process
            // was successfully spawned (Application.Exit has been queued).
            // On false, the fallback OSD ("Theme will apply on next launch")
            // has already been shown by TryAutoRestartForTheme and this
            // instance keeps running — caller should treat as "deferred".
            bool restartFired = TryAutoRestartForTheme();
            return restartFired ? ApplyResult.ThemeRestartFired : ApplyResult.ThemeRestartFailed;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (discoveryReadOk && !string.IsNullOrEmpty(apiKey) && _api.IsReachable())
            {
                try
                {
                    var g = globalDiscovery ? "true" : "false";
                    var l = localDiscovery ? "true" : "false";
                    var r = relayEnabled ? "true" : "false";
                    var (status, _) = _api.Patch("/rest/config/options",
                        $"{{\"globalAnnounceEnabled\":{g},\"localAnnounceEnabled\":{l},\"relaysEnabled\":{r}}}",
                        timeoutMs: 1500);
                    if (status != 200)
                    {
                        _osd.ShowMessage($"Discovery settings not saved to Syncthing (HTTP {status})", 5000);
                        TrayLog.Warn($"Discovery PATCH returned HTTP {status}.");
                    }
                }
                catch (Exception ex)
                {
                    _osd.ShowMessage("Discovery settings not saved to Syncthing", 5000);
                    TrayLog.Warn("Discovery PATCH threw: " + ex.Message);
                }
            }

            if (iconPath is not null)
            {
                bool ok;
                try
                {
                    ok = StartupShortcut.Apply(runOnStartup, iconPath);
                }
                catch (Exception ex)
                {
                    ok = false;
                    TrayLog.Warn("StartupShortcut.Apply threw: " + ex.Message);
                }
                if (!ok)
                {
                    _osd.ShowMessage(
                        runOnStartup
                            ? "Could not create startup shortcut — check Windows permissions"
                            : "Could not remove startup shortcut — it may be locked",
                        5000);
                }
            }

            if (netAutoPause)
            {
                bool found = false;
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "root\\StandardCimv2",
                        "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
                    using var results = searcher.Get();
                    foreach (var obj in results)
                    {
                        using (obj) { found = true; break; }
                    }
                }
                catch { /* handled below */ }
                if (!found)
                    _osd.ShowMessage("Network auto-pause may not work on this system", 5000);
            }

            // Tray refresh — LoadFolders() + the "Settings saved" OSD. The tray's
            // onSaved delegate self-marshals its UI work via the tray's RunOnUi,
            // so calling it from this pool thread is safe.
            savedCallback?.Invoke();
        });
        return ApplyResult.Normal;
    }

    /// <summary>
    /// Spawn a replacement process with the <c>--after-theme-restart</c> flag.
    /// Returns <c>true</c> if the replacement was successfully spawned and
    /// <see cref="Application.Exit"/> was queued (caller should treat as
    /// ThemeRestartFired and stop touching UI). Returns <c>false</c> if any
    /// failure path fired — in that case the user-facing fallback OSD
    /// ("Theme will apply on next launch") has already been shown, and the
    /// caller should treat as ThemeRestartFailed (close dialog quietly,
    /// don't bury the fallback OSD with a "Settings applied" toast).
    /// </summary>
    private bool TryAutoRestartForTheme()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            TrayLog.Warn("Theme auto-restart skipped — Environment.ProcessPath was null/empty.");
            _osd.ShowMessage("Theme will apply on next launch", 4000);
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--after-theme-restart",
                UseShellExecute = true,
            });
            if (p == null)
            {
                TrayLog.Warn("Theme auto-restart — Process.Start returned null; staying open.");
                _osd.ShowMessage("Theme will apply on next launch", 4000);
                return false;
            }
            // Replacement is spawned; let the rest of OnSave/OnApply close the
            // form and let TrayApplicationContext's Dispose clean up. The
            // replacement process retries the single-instance mutex for ~5 s
            // (Program.Main's retry loop) so the dying instance can release it.
            Application.Exit();
            return true;
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"Theme auto-restart failed (err={ex.GetType().Name}: {ex.Message}) — staying open.");
            _osd.ShowMessage("Theme will apply on next launch", 4000);
            return false;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var result = ApplySettings(notify: true);
        switch (result)
        {
            case ApplyResult.ThemeRestartFired:
                // Replacement is up, Application.Exit queued. Don't call
                // Close(); the message pump's shutdown tears the form down.
                return;
            case ApplyResult.ThemeRestartFailed:
                // Auto-restart fell back; non-theme changes were persisted
                // but _onSaved was nulled (because themeChanged), so the
                // tray menu would stay stale until the next poll tick.
                // Fire _onApplied (the lightweight refresh, no HTTP) so the
                // tray's local-settings labels reflect the saved state.
                // Mirrors what OnApply does on this branch.
                _onApplied();
                Close();
                return;
            case ApplyResult.SaveFailed:
                // "Could not save settings…" already shown by ApplySettings.
                // Close dialog to match the pre-refactor behavior (every
                // Save click dismisses regardless of outcome).
                Close();
                return;
            case ApplyResult.Normal:
                Close();
                return;
            default:
                // C# `switch` statements don't enforce exhaustiveness on
                // enums (unlike switch expressions). Future ApplyResult
                // additions hit this branch and surface in the log instead
                // of silently no-op'ing.
                TrayLog.Warn($"OnSave: unhandled ApplyResult {result}");
                Close();
                return;
        }
    }

    private void OnApply(object? sender, EventArgs e)
    {
        // OnApply is "save without close" — fires _onApplied (tray menu
        // rebuild for non-folder local-settings labels like WebUI URL) and
        // a brief "Settings applied" OSD for visual feedback.
        //
        // Skip the _onSaved() callback inside ApplySettings — that rebuilds
        // the tray menu via LoadFolders (another HTTP GET). _onApplied
        // (light-weight, no HTTP) covers the local-settings refresh.
        var result = ApplySettings(notify: false);
        switch (result)
        {
            case ApplyResult.ThemeRestartFired:
                // Tray context being disposed via Application.Exit; touching
                // UI would race teardown and the "Settings applied" OSD would
                // bury the replacement's "Theme applied" toast at +800 ms.
                return;
            case ApplyResult.SaveFailed:
                // "Could not save settings — file may be locked" already
                // shown. Don't fire _onApplied (the tray would refresh
                // against now-stale INI values that didn't persist) and
                // don't show "Settings applied" (which would falsely
                // overwrite the error message).
                return;
            case ApplyResult.ThemeRestartFailed:
                // Auto-restart fallback OSD ("Theme will apply on next
                // launch") already shown. Fire _onApplied so the tray
                // reflects any non-theme local changes the user made in
                // this same save (Click action, sound notifications, etc.)
                // — these were persisted to INI and are now live. Skip
                // "Settings applied" so the fallback OSD isn't overwritten.
                _onApplied();
                return;
            case ApplyResult.Normal:
                _onApplied();
                // OSD timing invariant: "Settings applied" fires SYNCHRONOUSLY
                // here on the UI thread, immediately after ApplySettings
                // returned. The pool task spawned inside ApplySettings (the
                // non-theme branch's discovery PATCH + StartupShortcut.Apply
                // + WMI probe + savedCallback fan-out) marshals its own error
                // OSDs back via OsdToolTip.ShowMessage's BeginInvoke path,
                // which fires 10-1500 ms LATER. Each later OSD overwrites
                // this "Settings applied" message — so the user-visible
                // result is the most-recent (most-relevant) status. DO NOT
                // "fix" this by moving the OSD into the pool task tail or
                // by tracking an error flag — the current timing happens to
                // resolve correctly because the durations match the user's
                // mental model (5000 ms error OSD outlives 3000 ms success).
                // Confirmed by round-3 verifier analysis (verifier flagged
                // it CRITICAL; trace showed the race is benign).
                _osd.ShowMessage("Settings applied", 3000);
                return;
            default:
                // Same future-proofing rationale as OnSave's default arm.
                TrayLog.Warn($"OnApply: unhandled ApplyResult {result}");
                return;
        }
    }


    private static bool IsSyncthingRunning()
    {
        var procs = Process.GetProcessesByName("syncthing");
        bool running = false;
        foreach (var p in procs)
        {
            using (p)
            {
                running = true;
            }
        }
        return running;
    }

    private static bool ParseJsonBool(string json, string key, bool defaultValue)
    {
        // Use JsonDocument — the prior hand-rolled IndexOf approach was bypassable:
        // a body like {"note":"\"key\":true is default","key":false} would lock on to
        // the key-substring inside `note` and return the wrong value, which for a
        // discovery PATCH round-trip could silently re-enable global announce on a
        // privacy-sensitive setup.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return defaultValue;
            if (!doc.RootElement.TryGetProperty(key, out var el))
                return defaultValue;
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => defaultValue,
            };
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Malformed Syncthing response — return the default but surface the
            // signal; a privacy-sensitive discovery toggle silently reverting to
            // `false` is the exact case a field report would need in the log.
            TrayLog.Warn($"ParseJsonBool({key}): {ex.Message}");
            return defaultValue;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _discoveryRetryTimer?.Stop();
                _discoveryRetryTimer?.Dispose();
                _discoveryRetryTimer = null;

                _iconFont.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
