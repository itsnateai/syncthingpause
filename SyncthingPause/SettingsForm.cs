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

    private readonly Font _boldFont;
    private readonly Font _normalFont;
    private readonly Font _sectionFont;
    private readonly Font _monoFont;
    private readonly Font _btnFont;
    private readonly Font _subFont;
    private readonly Font _iconFont;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DimColor = Color.FromArgb(0xA0, 0xA0, 0xC0);
    private static readonly Color DividerColor = Color.FromArgb(0x40, 0x40, 0x50);
    private static readonly Color EditBgColor = Color.FromArgb(0x2A, 0x2A, 0x3E);
    private static readonly Color ComboSelectedBgColor = Color.FromArgb(0x35, 0x35, 0x50);

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
            AddLabel("(not available in portable mode)", 36, y + 18, 300, _subFont, Color.FromArgb(0x80, 0x80, 0x90));
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

        bool curGlobal = false, curLocal = false, curRelay = false;
        bool discoveryRead = false;
        int discoveryStatus = -1;
        // Fast probe first — a full HTTP GET against a dead port burns the HttpClient
        // timeout (5s) on the UI thread, which is why this dialog used to take 6s to
        // appear when Syncthing was off.
        if (!string.IsNullOrEmpty(_config.ApiKey) && _api.IsReachable())
        {
            try
            {
                var (status, body) = _api.Get("/rest/config/options", timeoutMs: 1500);
                discoveryStatus = status;
                if (status == 200)
                {
                    curGlobal = ParseJsonBool(body, "globalAnnounceEnabled", false);
                    curLocal = ParseJsonBool(body, "localAnnounceEnabled", false);
                    curRelay = ParseJsonBool(body, "relaysEnabled", false);
                    discoveryRead = true;
                }
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Discovery read failed: " + ex.Message);
            }
        }

        _cbGlobal = AddCheckBox("Global Discovery", 16, y, curGlobal);
        y += 24;
        _cbLocal = AddCheckBox("Local Discovery", 16, y, curLocal);
        y += 24;
        _cbRelay = AddCheckBox("NAT Traversal (Relaying)", 16, y, curRelay);
        y += 24;

        // If the API read failed, the three boxes above do NOT reflect Syncthing's
        // real state. Disable + warn so Save doesn't silently destroy the user's
        // existing discovery config by PATCHing a default-false payload.
        _discoveryReadOk = discoveryRead;
        if (!discoveryRead)
        {
            _cbGlobal.Enabled = _cbLocal.Enabled = _cbRelay.Enabled = false;
            string message;
            if (string.IsNullOrEmpty(_config.ApiKey))
                message = "(set API Key above to manage discovery)";
            else if (discoveryStatus == 401 || discoveryStatus == 403)
                message = "(API Key rejected — check the key above)";
            else
                message = "(could not read current state — API unreachable)";
            _discoveryWarnLabel = AddLabel(message, 36, y, 320, _subFont, Color.FromArgb(0x80, 0x80, 0x90));
            y += 18;

            // Auto-refresh loop — polls /rest/config/options every 2 s while the dialog
            // is open. Covers the fresh-cold-start window where Syncthing takes 5-15 s
            // to bind its REST port after the tray launched it. Without this, the user
            // has to manually close + reopen Settings to pick up discovery values.
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
        _discoveryRetryTimer.Tick += (_, _) =>
        {
            if (_disposed || IsDisposed) { _discoveryRetryTimer?.Stop(); return; }

            if (++_discoveryRetryCount > DiscoveryRetryCapTicks)
            {
                _discoveryRetryTimer?.Stop();
                _discoveryRetryTimer?.Dispose();
                _discoveryRetryTimer = null;
                if (_discoveryWarnLabel != null && !_discoveryWarnLabel.IsDisposed)
                {
                    _discoveryWarnLabel.Text = "(Syncthing unreachable — reopen Settings to retry)";
                }
                return;
            }

            // Offload the probe + HTTP call to a pool thread. Running them on the
            // UI thread (300 ms IsReachable + up to 1500 ms HTTP) froze the dialog
            // at exactly the worst moment: when Syncthing was transitioning from
            // down to up, right as the user was looking at Settings. The UI mutation
            // marshals back via BeginInvoke and runs under SuspendLayout so the
            // three checkboxes + Enabled flips + warning-label removal repaint once
            // instead of cascading.
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
                    TrayLog.Warn("Discovery auto-refresh probe failed: " + ex.Message);
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
                        // Single deferred repaint instead of five mid-mutation ones.
                        Invalidate(invalidateChildren: true);

                        _discoveryRetryTimer?.Stop();
                        _discoveryRetryTimer?.Dispose();
                        _discoveryRetryTimer = null;
                    }));
                }
                catch (ObjectDisposedException) { /* dialog closed between the probe and the marshal */ }
                catch (InvalidOperationException) { /* handle not yet created */ }
            });
        };
        _discoveryRetryTimer.Start();
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
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _osd.ShowMessage("API Key required \u2014 set above", 3000);
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
                        string ver = "unknown";
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("latest", out var el) &&
                                el.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                ver = el.GetString() ?? "unknown";
                            }
                        }
                        catch (System.Text.Json.JsonException) { /* keep ver = "unknown" */ }
                        _osd.ShowMessage($"Update available: {ver}", 5000);
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
            catch
            {
                _osd.ShowMessage("Could not reach Syncthing API", 3000);
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
            catch
            {
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
            catch { /* best-effort */ }
        }

        _osd.ShowMessage(results.Replace("\r\n", " | ").TrimEnd(' ', '|'), 5000);
    }

    private void ApplySettings(bool notify)
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

        // NumericUpDown clamps to [Minimum, Maximum] on both spinner and typed input,
        // so no range check or fallback OSD is needed here — the value is always valid.
        _config.StartupDelay = (int)_nudDelay.Value;

        if (!_config.Save())
        {
            _osd.ShowMessage("Could not save settings \u2014 file may be locked", 5000);
            return;
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
        Action? savedCallback = notify ? _onSaved : null;

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
    }

    private void OnSave(object? sender, EventArgs e)
    {
        ApplySettings(notify: true);
        Close();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        // Skip the _onSaved() callback — that rebuilds the tray menu via LoadFolders
        // (another HTTP GET). Apply persists local settings (WebUI label, ApiKey,
        // etc.) and PATCHes discovery; the folder-list refetch is what Save (or the
        // next 10s poll tick) is for. A separate rebuild runs below to keep the
        // menu's local-settings labels fresh (e.g., WebUI URL shown in the menu item).
        ApplySettings(notify: false);
        _onApplied();
        _osd.ShowMessage("Settings applied", 3000);
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
