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

    private readonly Font _boldFont;
    private readonly Font _normalFont;
    private readonly Font _sectionFont;
    private readonly Font _monoFont;
    private readonly Font _btnFont;
    private readonly Font _subFont;
    private readonly Font _iconFont;

    // Theme-aware static caches — SettingsForm class loads when the user opens
    // Settings (well after Theme.Initialize ran in TrayApplicationContext's ctor),
    // so these capture the active palette correctly.
    private static readonly Color BgColor = Theme.Bg;
    private static readonly Color FgColor = Theme.Fg;
    private static readonly Color DimColor = Theme.Dim;
    private static readonly Color DividerColor = Theme.Divider;
    private static readonly Color EditBgColor = Theme.EditBg;
    private static readonly Color ComboSelectedBgColor = Theme.ComboSelectedBg;
    private static readonly Color WarnLabelColor = Theme.FgDisabled;

    // CLAUDE.md: cache GDI in paint paths — combo item draw fires per item per paint.
    private static readonly SolidBrush ComboBgBrush = new(EditBgColor);
    private static readonly SolidBrush ComboSelectedBrush = new(ComboSelectedBgColor);

    public SettingsForm(AppConfig config, SyncthingApi api, OsdToolTip osd, Action onApplied, Action onSaved)
    {
        _config = config;
        _api = api;
        _osd = osd;
        _onApplied = onApplied;
        _onSaved = onSaved;

        _boldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        _normalFont = new Font("Segoe UI", 9f);
        _sectionFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        _monoFont = new Font("Consolas", 8f);
        _btnFont = new Font("Segoe UI", 8f);
        _subFont = new Font("Segoe UI", 8f);
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

        int sw = 410;
        int y = 10;

        BuildClickActionsSection(ref y, sw);
        BuildGeneralSection(ref y, sw);
        BuildPathsSection(ref y, sw);
        BuildApiSection(ref y, sw);
        BuildDiscoverySection(ref y, sw);
        BuildUpdatesSection(ref y, sw);
        BuildButtonRow(ref y, sw);

        y += 40;
        ClientSize = new Size(sw, y);

        // First-run auto-open can land behind a fullscreen app (game, video) since
        // TopMost loses to fullscreen D3D. Force foreground on the first paint so
        // the user actually sees the dialog they're being asked to configure.
        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
        };
    }

    private void BuildClickActionsSection(ref int y, int sw)
    {
        AddSectionHeader("Tray Click Actions", 16, ref y, sw);

        // Combo width sized to content: longest option "Pause/Resume" + chevron +
        // padding fits comfortably in 160px. Matches professional-settings-dialog
        // convention of proportional-to-content sizing rather than row-filling.
        AddLabel("Double-click:", 16, y + 2, 0, _normalFont, DimColor);
        _cboDblClick = AddComboBox(112, y, 160, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.DblClickAction),
            accessibleName: "Double-click action");
        y += 30;

        AddLabel("Middle-click:", 16, y + 2, 0, _normalFont, DimColor);
        _cboMiddleClick = AddComboBox(112, y, 160, AppConfig.ClickActions, AppConfig.ActionValueToIndex(_config.MiddleClickAction),
            accessibleName: "Middle-click action");
        y += 30;
    }

    private void BuildGeneralSection(ref int y, int sw)
    {
        AddSectionHeader("General", 16, ref y, sw);

        _cbRunOnStartup = AddCheckBox("Run on startup", 16, y, _config.RunOnStartup);
        if (_config.IsPortable)
        {
            _cbRunOnStartup.Enabled = false;
            AddLabel("(not available in portable mode)", 36, y + 18, 300, _subFont, WarnLabelColor);
            y += 16;
        }
        y += 26;

        _cbStartBrowser = AddCheckBox("Start browser when Syncthing launches", 16, y, _config.StartBrowser);
        y += 26;

        _cbNetPause = AddCheckBox("Auto-pause on public networks", 16, y, _config.NetworkAutoPause);
        y += 26;

        _cbSoundNotify = AddCheckBox("Play sounds on events", 16, y, _config.SoundNotifications);
        y += 26;

        _cbStopOnExit = AddCheckBox("Stop Syncthing when tray exits", 16, y, _config.StopOnExit);
        y += 26;

        // Windows startup delay — gap between tray launch and Syncthing launch.
        // Primary use case is tray on Windows auto-startup: waiting a few seconds
        // for the network stack and other boot services to settle before firing
        // Syncthing. NumericUpDown lets the user spin in 5-second steps or type
        // any value in [0, 3600] directly.
        AddLabel("Windows startup delay:", 16, y, 0, _normalFont, DimColor);
        _nudDelay = new NumericUpDown
        {
            Location = new Point(160, y - 2),
            // Width=60 worked at 100% DPI but clipped digits at 125% — NumericUpDown's
            // spinner band scales independently of the parent at non-100% scale (well-
            // known WinForms quirk), eating ~25px and leaving no room for 4-digit values.
            // MinimumSize is the floor AutoScaleMode.Dpi won't shrink past.
            Width = 80,
            MinimumSize = new Size(80, 26),
            Minimum = 0,
            Maximum = 3600,
            Increment = 5,
            Value = Math.Clamp(_config.StartupDelay, 0, 3600),
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Left,
            AccessibleName = "Windows startup delay in seconds",
        };
        Controls.Add(_nudDelay);
        AddLabel("seconds", 248, y, 0, _normalFont, DimColor);
        y += 30;
    }

    private void BuildPathsSection(ref int y, int sw)
    {
        AddSectionHeader("Paths", 16, ref y, sw);

        AddLabel("Syncthing:", 16, y, 0, _normalFont, DimColor);
        _edSyncExe = AddTextBox(90, y - 2, 220, _config.SyncExe, true, accessibleName: "Syncthing executable path");
        var btnBrowse = new Button
        {
            Text = "...",
            Font = _btnFont,
            Location = new Point(314, y - 3),
            Size = new Size(50, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            AccessibleName = "Browse for syncthing.exe",
        };
        btnBrowse.Click += OnBrowseSyncExe;
        Controls.Add(btnBrowse);
        y += 28;

        AddLabel("Web UI:", 16, y, 0, _normalFont, DimColor);
        _edWebUI = AddTextBox(90, y - 2, 220, _config.WebUI, true, accessibleName: "Syncthing Web UI URL");
        var btnOpenWebUI = new Button
        {
            Text = "Open",
            Font = _btnFont,
            Location = new Point(314, y - 3),
            Size = new Size(50, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            AccessibleName = "Open Web UI in browser",
        };
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
                // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- url is validated as http/https via Uri.TryCreate on line 220 above; handed to Windows default browser
                using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _osd.ShowMessage("Could not open browser — check Windows default browser", 5000);
                TrayLog.Warn("SettingsForm OpenWebUI failed: " + ex.Message);
            }
        };
        Controls.Add(btnOpenWebUI);
        y += 30;
    }

    private void BuildApiSection(ref int y, int sw)
    {
        AddSectionHeader("API", 16, ref y, sw);

        AddLabel("API Key:", 16, y, 0, _normalFont, DimColor);
        // Textbox is narrower than the old 272px to make room for the reveal toggle.
        // A Syncthing API key is ~40 chars; 216px @ Consolas-8 fits the key comfortably.
        _edApiKey = AddTextBox(90, y - 2, 216, _config.ApiKey, true, accessibleName: "Syncthing API key");
        _edApiKey.UseSystemPasswordChar = true;

        var btnReveal = new Button
        {
            // "\uE7B3" = Segoe MDL2 RedEye ("show"); "\uE7B4" = Hide ("mask").
            // These render inside the password-toggle glyph set used across Win10/11.
            Text = "\uE7B3",
            Font = _iconFont,
            Location = new Point(310, y - 2),
            Size = new Size(52, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            // TabStop = true + explicit AccessibleName so keyboard-only and
            // screen-reader users can discover the reveal affordance. The glyph
            // itself is unreadable to assistive tech — Segoe MDL2 "\uE7B3" has
            // no semantic text.
            TabStop = true,
            AccessibleName = "Show or hide API key",
        };
        btnReveal.FlatAppearance.BorderColor = DividerColor;
        btnReveal.Click += (_, _) =>
        {
            _edApiKey.UseSystemPasswordChar = !_edApiKey.UseSystemPasswordChar;
            btnReveal.Text = _edApiKey.UseSystemPasswordChar ? "\uE7B3" : "\uE7B4";
        };
        Controls.Add(btnReveal);

        y += 30;
    }

    private void BuildDiscoverySection(ref int y, int sw)
    {
        AddSectionHeader("Discovery", 16, ref y, sw);

        // The HTTP probe used to run synchronously here — up to 1500 ms on the
        // UI thread when Syncthing was up but slow, on top of the 300 ms-per-
        // address IsReachable TCP probe. Total perceived lag on Settings open
        // ranged from ~100 ms (Syncthing snappy) to ~2 s (slow API + IPv6 race
        // on a hostname that resolved to [::1, 127.0.0.1] with v4-only listen).
        // Now the dialog appears immediately with the checkboxes disabled; the
        // probe runs on a pool thread via StartDiscoveryRetryTimer below and
        // populates values within ~200-500 ms when Syncthing is responsive.
        // Synchronous IsReachable is still acceptable (300 ms × N addresses,
        // typically <50 ms when Syncthing is up) and lets us pick the right
        // initial warn message without waiting for the HTTP call.
        bool apiKeyEmpty = string.IsNullOrEmpty(_config.ApiKey);
        bool reachable = !apiKeyEmpty && _api.IsReachable();

        // Capture the y-coordinates of the first two Discovery rows so the
        // Theme: label + radio pair line up exactly with them on the right.
        // The Theme control is hosted in the Discovery section per user request
        // — visually grouped with other "appearance vs network" toggles, two
        // lines tall on the right side of the section.
        int themeLabelY = y;
        _cbGlobal = AddCheckBox("Global Discovery", 16, y, false);
        _cbGlobal.Width = 200;
        y += 24;
        int themeRadioY = y;
        _cbLocal = AddCheckBox("Local Discovery", 16, y, false);
        _cbLocal.Width = 200;
        y += 24;
        _cbRelay = AddCheckBox("NAT Traversal (Relaying)", 16, y, false);
        y += 24;

        // ── Theme toggle (right column, two lines) ─────────────────────────
        // Line 1 (themeLabelY): "Theme:" header. Line 2 (themeRadioY): two
        // radio buttons "Dark" / "Light" side by side. Persists to AppConfig
        // and applies on next launch — the SettingsForm Save path spawns a
        // replacement process so this is seamless. See Theme.cs for the
        // restart-to-apply rationale (GDI brush/pen caches captured at first
        // class load can't be invalidated without a process restart).
        const int ThemeColX = 240;
        AddLabel("Theme:", ThemeColX, themeLabelY + 2, 0, _normalFont, DimColor);

        bool currentlyDark = string.Equals(_config.ThemeMode, "Dark",
            StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_config.ThemeMode);
        _rbThemeDark = new RadioButton
        {
            Text = "Dark",
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            Location = new Point(ThemeColX, themeRadioY),
            Width = 64,
            Checked = currentlyDark,
            AccessibleName = "Dark theme",
        };
        _rbThemeLight = new RadioButton
        {
            Text = "Light",
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            Location = new Point(ThemeColX + 64, themeRadioY),
            Width = 64,
            Checked = !currentlyDark,
            AccessibleName = "Light theme",
        };
        Controls.Add(_rbThemeDark);
        Controls.Add(_rbThemeLight);

        // The three discovery boxes start DISABLED with `_discoveryReadOk = false`.
        // ApplySettings gates the discovery PATCH on _discoveryReadOk so a Save
        // before the async probe lands won't silently clobber Syncthing's existing
        // state with default-false. The probe enables the boxes + sets the flag
        // atomically (SuspendLayout / ResumeLayout) on success.
        _cbGlobal.Enabled = _cbLocal.Enabled = _cbRelay.Enabled = false;
        _discoveryReadOk = false;

        if (apiKeyEmpty)
        {
            // No key → can't probe and can't recover by retry. Static label.
            _discoveryWarnLabel = AddLabel("(set API Key above to manage discovery)", 36, y, 320, _subFont, WarnLabelColor);
            y += 18;
        }
        else if (!reachable)
        {
            // Syncthing not listening on the WebUI port. Show the warn and arm
            // the retry timer — when it comes up, the probe will swap the label
            // out and enable the boxes.
            _discoveryWarnLabel = AddLabel("(could not read current state — API unreachable)", 36, y, 320, _subFont, WarnLabelColor);
            y += 18;
            StartDiscoveryRetryTimer();
        }
        else
        {
            // Reachable. Kick off the async probe — the dialog is already
            // visible by the time it lands. No warn label initially; the retry
            // timer's timeout path (60 s with no successful probe) will
            // surface a recovery hint via the warn label it lazily creates.
            StartDiscoveryRetryTimer();
        }
        y += 6;
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
            if (_discoveryWarnLabel == null || _discoveryWarnLabel.IsDisposed)
            {
                _discoveryWarnLabel = AddLabel(
                    "(Syncthing unreachable — reopen Settings to retry)",
                    36, _cbRelay.Bottom + 4, 320, _subFont, WarnLabelColor);
            }
            else
            {
                _discoveryWarnLabel.Text = "(Syncthing unreachable — reopen Settings to retry)";
            }
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

                        if (_discoveryWarnLabel != null)
                        {
                            Controls.Remove(_discoveryWarnLabel);
                            _discoveryWarnLabel.Dispose();
                            _discoveryWarnLabel = null;
                        }
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

    private void BuildUpdatesSection(ref int y, int sw)
    {
        AddSectionHeader("Updates", 16, ref y, sw);

        _cbAutoUpdates = AddCheckBox("Check for Syncthing updates (daily)", 16, y, _config.AutoCheckUpdates);
        _cbAutoUpdates.Width = 220;
        var btnCheckNow = new Button
        {
            Text = "Check Now",
            Font = _btnFont,
            Location = new Point(240, y - 2),
            Size = new Size(90, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnCheckNow.Click += (_, _) =>
        {
            // Double-click guard: synchronous _api.Get freezes the UI while the
            // 5s timeout runs, but the click handler is short enough that a fast
            // user can queue a second click after we return. Disabling for the
            // duration also prevents stacking two modal SyncthingUpdateDialog
            // instances if both clicks see newer=true.
            if (!btnCheckNow.Enabled) return;
            btnCheckNow.Enabled = false;
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _osd.ShowMessage("API Key required \u2014 set above", 3000);
                btnCheckNow.Enabled = true;
                return;
            }
            _osd.ShowMessage("Checking for updates...", 2000);
            try
            {
                var (status, body) = _api.Get("/rest/system/upgrade");
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
                // Surface to logs — previously a bare-catch swallowed every type
                // (OperationCanceledException, OutOfMemoryException, real bugs)
                // and reduced them all to one identical OSD with no diagnostics.
                TrayLog.Warn($"Check Now threw {ex.GetType().Name}: {ex.Message}");
                _osd.ShowMessage("Could not reach Syncthing API", 3000);
            }
            finally
            {
                btnCheckNow.Enabled = true;
            }
        };
        Controls.Add(btnCheckNow);
        y += 30;

        AddDivider(0, y, sw);
        y += 8;
    }

    private void BuildButtonRow(ref int y, int sw)
    {
        // Both rows are laid out to start at x=16 (left margin) and end at x=394
        // (right margin 16 = left margin, form width 410). Previously the top row
        // ended at 384 and the bottom row ended at 370, giving asymmetric
        // whitespace on the right and misalignment between the two rows.
        //
        // Top row: GitHub(68) | Update(58) | Syncthing(68) | Help(58) | Check Config(100)
        //   = 352 px of buttons, 4 gaps, flows to end at 394.
        // Bottom row: Save(114) | Apply(114) | Cancel(114)
        //   = 342 px of buttons, 2 × 18 px gaps, flows to end at 394.

        AddLinkButton("GitHub", 16, y, 68, "https://github.com/itsnateai/syncthingpause");

        var btnUpdate = new Button
        {
            Text = "Update",
            Font = _btnFont,
            Location = new Point(90, y),
            Size = new Size(58, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };
        Controls.Add(btnUpdate);

        AddLinkButton("Syncthing", 154, y, 68, "https://github.com/syncthing/syncthing");

        var btnHelp = new Button
        {
            Text = "Help",
            Font = _btnFont,
            Location = new Point(228, y),
            Size = new Size(58, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnHelp.Click += (_, _) =>
        {
            using var hf = new HelpForm(_config.SettingsFilePath, (msg, ms) => _osd.ShowMessage(msg, ms));
            hf.ShowDialog(this);
        };
        Controls.Add(btnHelp);

        var btnCheck = new Button
        {
            Text = "Check Config",
            Font = _btnFont,
            Location = new Point(294, y),
            Size = new Size(100, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            AccessibleName = "Check Config",
        };
        btnCheck.Click += OnCheckConfig;
        Controls.Add(btnCheck);
        y += 34;

        var btnSave = new Button
        {
            Text = "Save",
            Font = _normalFont,
            Location = new Point(16, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnSave.Click += OnSave;
        Controls.Add(btnSave);

        var btnApply = new Button
        {
            Text = "Apply",
            Font = _normalFont,
            Location = new Point(148, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnApply.Click += OnApply;
        Controls.Add(btnApply);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Font = _normalFont,
            Location = new Point(280, y),
            Size = new Size(114, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            DialogResult = DialogResult.Cancel,
        };
        btnCancel.Click += (_, _) => Close();
        Controls.Add(btnCancel);

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
                _osd.ShowMessage("Settings applied", 3000);
                return;
            default:
                // Same future-proofing rationale as OnSave's default arm.
                TrayLog.Warn($"OnApply: unhandled ApplyResult {result}");
                return;
        }
    }

    // --- UI Helpers ---

    private Label AddLabel(string text, int x, int y, int w, Font font, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = BgColor,
            Location = new Point(x, y),
            AutoSize = w <= 0,
        };
        if (w > 0) lbl.Width = w;
        Controls.Add(lbl);
        return lbl;
    }

    private Label AddDivider(int x, int y, int w)
    {
        var lbl = new Label
        {
            Location = new Point(x, y),
            Size = new Size(w, 1),
            BackColor = DividerColor,
        };
        Controls.Add(lbl);
        return lbl;
    }

    private CheckBox AddCheckBox(string text, int x, int y, bool isChecked)
    {
        var cb = new CheckBox
        {
            Text = text,
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            Location = new Point(x, y),
            Width = 320,
            Checked = isChecked,
            // WinForms CheckBox already exposes Text as AccessibleName by default
            // for screen readers, but setting it explicitly keeps the contract
            // uniform across every control in this form.
            AccessibleName = text,
        };
        Controls.Add(cb);
        return cb;
    }

    private TextBox AddTextBox(int x, int y, int w, string text, bool useMono = false, string? accessibleName = null)
    {
        var tb = new TextBox
        {
            Text = text,
            Font = useMono ? _monoFont : _normalFont,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            Location = new Point(x, y),
            Width = w,
            // Note: TextBox auto-sizes its Height to fit Font when Multiline=false
            // (per MS AutoScaleMode docs, TextBox/Label use Font scaling regardless
            // of AutoScaleMode). The Height = 24 literal is treated as a minimum
            // intent for layout consistency with the NUD at MinimumSize.Height=26
            // and surrounding buttons at 26 — the actual rendered height tracks
            // Font.Height + chrome at the current monitor's DPI.
            Height = 24,
            BorderStyle = BorderStyle.FixedSingle,
        };
        if (accessibleName is not null) tb.AccessibleName = accessibleName;
        Controls.Add(tb);
        return tb;
    }

    private ComboBox AddComboBox(int x, int y, int w, string[] items, int selectedIndex, string? accessibleName = null)
    {
        var cb = new ComboBox
        {
            Font = _normalFont,
            ForeColor = FgColor,
            BackColor = EditBgColor,
            Location = new Point(x, y),
            Width = w,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            // ItemHeight set below AFTER Controls.Add so cb.DeviceDpi reports the
            // form's actual current monitor DPI. AutoScaleMode.Dpi does NOT scale
            // ItemHeight (it's an int property, not a Size, so the auto-scale walk
            // skips it). Font.Height alone is computed against the screen DC at
            // 96 DPI design metrics — stays ~15px regardless of monitor, so
            // ItemHeight would clip at 150%+ scale where the rendered 9pt Segoe UI
            // is ~23px tall. Font.GetHeight(deviceDpi) returns the actual physical
            // pixel height at this monitor's DPI; LogicalToDeviceUnits scales the
            // 4px chrome padding to match. Together they produce a row that fits
            // glyphs at every DPI from 100% to 250%.
        };
        if (accessibleName is not null) cb.AccessibleName = accessibleName;
        cb.DrawItem += OnDrawComboItem;
        cb.Items.AddRange(items);
        cb.SelectedIndex = selectedIndex;
        Controls.Add(cb);
        cb.ItemHeight = (int)Math.Ceiling(cb.Font.GetHeight(cb.DeviceDpi)) + cb.LogicalToDeviceUnits(4);
        return cb;
    }

    private static void OnDrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || sender is not ComboBox cb) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(selected ? ComboSelectedBrush : ComboBgBrush, e.Bounds);

        var text = cb.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, cb.Font, e.Bounds, FgColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void AddSectionHeader(string text, int x, ref int y, int sw)
    {
        y += 4;
        var lbl = AddLabel(text, x, y, 0, _sectionFont, DimColor); // AutoSize
        int labelWidth = TextRenderer.MeasureText(text, _sectionFont).Width;
        int labelEnd = x + labelWidth + 4;
        AddDivider(labelEnd, y + 7, sw - labelEnd - 10);
        y += 20;
    }

    private void AddLinkButton(string text, int x, int y, int w, string url)
    {
        var btn = new Button
        {
            Text = text,
            Font = _btnFont,
            Location = new Point(x, y),
            Size = new Size(w, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            AccessibleName = text,
        };
        btn.Click += (_, _) =>
        {
            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- AddLinkButton is only called with hardcoded URLs (github.com/itsnateai/syncthingpause, github.com/syncthing/syncthing)
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
        Controls.Add(btn);
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

                _boldFont.Dispose();
                _normalFont.Dispose();
                _sectionFont.Dispose();
                _monoFont.Dispose();
                _btnFont.Dispose();
                _subFont.Dispose();
                _iconFont.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
