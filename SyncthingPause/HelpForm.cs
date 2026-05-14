using System.Diagnostics;
using System.Text;

namespace SyncthingPause;

/// <summary>
/// Dark-themed Help window. Renders structured help text via a RichTextBox with
/// section headers and body paragraphs — same pattern as MicMute's help, adapted
/// for SyncthingPause's narrower (400px) fixed window.
/// </summary>
internal sealed class HelpForm : Form
{
    private readonly List<Font> _fonts = new();
    private bool _disposed;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DividerColor = Color.FromArgb(0x40, 0x40, 0x50);
    // Catppuccin Mocha "blue" — reads clearly as an accent on the 0x1E1E2E base.
    private static readonly Color HeaderColor = Color.FromArgb(0x89, 0xB4, 0xFA);

    private static readonly string s_helpText = @"Syncthing, hidden in the tray. One-click pause, rescan, and folder access without opening a browser.

Sync icon = running. Pause icon = paused or stopped.

——— TRAY ——————————————————————————

• Right-click — full menu.
• Double-click / Middle-click — configurable in Settings (Open Web UI, Rescan Now, Pause/Resume, or Do Nothing).
• Tooltip shows sync state, progress %, and connected/total devices.

——— MENU ——————————————————————————

Open Web UI — opens the Syncthing dashboard.

Synced Folders — every folder Syncthing is configured for. Each has Open Folder + Rescan. Folders group under the remote devices they're shared with (read from Syncthing's own config); a folder shared with two devices appears under both. Folders you haven't shared yet fall under ""Local only"".

Force Rescan Now — rescans every folder.

Pause Syncing — submenu: 5 minutes, 30 minutes, Until resumed. Timed pauses auto-resume at the deadline and survive sleep and tray restarts.

Start / Stop / Restart Syncthing — full process control.

——— SETTINGS —————————————————————

Tray Click Actions — behavior for Double-click and Middle-click.

General — Run on startup, auto-open browser, auto-pause on public Wi-Fi, sound on events, stop Syncthing with tray, Startup Delay.

Paths — Syncthing exe (auto-discovered) and Web UI URL (localhost only).

API Key — required for status, pause/resume, discovery. Get it from the Web UI: Actions → Settings → API Key. Field is masked; click the eye to reveal.

Discovery — Global (public tracker) and Local (LAN).

Updates — daily poll for new Syncthing releases.

——— TROUBLESHOOTING ———————————————

""Stopped unexpectedly"" — Syncthing crashed. Use Start Syncthing. Rate-limited to 1 alert per 5 min.

""API key rejected"" — key doesn't match Syncthing. Re-copy from Web UI.

Sluggish menu — tray probes with a 1.5s TCP check when Syncthing is off.

Log: %LOCALAPPDATA%\SyncthingPause\tray.log (off by default — opt in via DiagnosticLogging=1).

——— LINKS ————————————————————————

docs.syncthing.net
github.com/itsnateai/syncthingpause";

    private readonly string _iniPath;
    private readonly Action<string, int> _showOsd;

    /// <param name="iniPath">Absolute path to SyncthingPause.ini — used by the Backup button.</param>
    /// <param name="showOsd">Callback to display an OSD message (text, duration ms) — reuses the tray's OSD rather than MessageBox per the project's notification convention.</param>
    public HelpForm(string iniPath, Action<string, int> showOsd)
    {
        _iniPath = iniPath;
        _showOsd = showOsd;

        Text = "SyncthingPause Help";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(400, 380);
        BackColor = BgColor;
        ShowInTaskbar = false;
        // Pin design baseline to 96 DPI BEFORE AutoScaleMode so literal Size/Point
        // values below are always interpreted at 96 DPI regardless of which monitor
        // the form is realized on. See SettingsForm.cs for the full rationale.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var labelFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        _fonts.Add(labelFont);
        var btnFont = new Font("Segoe UI", 8f);
        _fonts.Add(btnFont);

        var lblTitle = new Label
        {
            Text = $"SyncthingPause v{AppConfig.Version}",
            Font = labelFont,
            ForeColor = FgColor,
            Location = new Point(16, 14),
            AutoSize = true,
        };
        Controls.Add(lblTitle);

        var topDivider = new Label
        {
            Location = new Point(0, 36),
            Size = new Size(400, 1),
            BackColor = DividerColor,
        };
        Controls.Add(topDivider);

        var textBox = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = BgColor,
            ForeColor = FgColor,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            WordWrap = true,
            TabStop = false,
            Location = new Point(16, 46),
            Size = new Size(368, 270),
        };
        Controls.Add(textBox);
        RenderHelp(textBox);

        // Kill default "all text selected when shown" behavior.
        Shown += (_, _) =>
        {
            textBox.SelectionStart = 0;
            textBox.SelectionLength = 0;
            textBox.DeselectAll();
            ActiveControl = null;
        };

        var btnDivider = new Label
        {
            Location = new Point(0, 326),
            Size = new Size(400, 1),
            BackColor = DividerColor,
        };
        Controls.Add(btnDivider);

        // Three-button row: Docs | Backup Settings | Close.
        // 16 .. 126 | 145 .. 255 | 274 .. 384 — 19 px gaps, symmetric inside the
        // 400 px content width.
        var btnDocs = new Button
        {
            Text = "Syncthing Docs",
            Font = btnFont,
            Location = new Point(16, 340),
            Size = new Size(110, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnDocs.FlatAppearance.BorderColor = DividerColor;
        btnDocs.Click += (_, _) =>
        {
            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- URL is a string literal, zero user input
            using var p = Process.Start(new ProcessStartInfo("https://docs.syncthing.net") { UseShellExecute = true });
        };
        Controls.Add(btnDocs);

        var btnBackup = new Button
        {
            Text = "Backup Settings",
            Font = btnFont,
            Location = new Point(145, 340),
            Size = new Size(110, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnBackup.FlatAppearance.BorderColor = DividerColor;
        btnBackup.Click += (_, _) => BackupSettings();
        Controls.Add(btnBackup);

        var btnClose = new Button
        {
            Text = "Close",
            Font = btnFont,
            Location = new Point(274, 340),
            Size = new Size(110, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            DialogResult = DialogResult.Cancel,
        };
        btnClose.FlatAppearance.BorderColor = DividerColor;
        btnClose.Click += (_, _) => Close();
        Controls.Add(btnClose);

        CancelButton = btnClose;
        AcceptButton = btnClose;
    }

    private void RenderHelp(RichTextBox textBox)
    {
        // Track fonts immediately — if any ctor throws (OOM / GDI exhaustion) the
        // already-constructed ones would otherwise leak their native handles until
        // the GC finalizer eventually runs.
        var headerFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        _fonts.Add(headerFont);
        var bodyFont = new Font("Segoe UI", 9f);
        _fonts.Add(bodyFont);

        textBox.Clear();

        var body = new StringBuilder();

        void FlushBody()
        {
            if (body.Length == 0) return;
            // Collapse leading blank lines so sections don't stack extra gaps.
            var text = body.ToString().TrimStart('\r', '\n');
            body.Clear();
            if (text.Length == 0) return;
            textBox.SelectionFont = bodyFont;
            textBox.SelectionColor = FgColor;
            textBox.AppendText(text);
        }

        var lines = s_helpText.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            if (raw.StartsWith("\u2014\u2014\u2014") || raw.StartsWith("---"))
            {
                FlushBody();
                var title = raw.Trim().Trim('\u2014', '-', ' ');
                if (title.Length == 0) continue;
                textBox.AppendText("\n");
                textBox.SelectionFont = headerFont;
                textBox.SelectionColor = HeaderColor;
                textBox.AppendText(title + "\n\n");
                continue;
            }

            body.AppendLine(raw);
        }
        FlushBody();

        textBox.SelectionStart = 0;
        textBox.SelectionLength = 0;
    }

    /// <summary>
    /// Copies SyncthingPause.ini to a timestamped sibling file. The INI holds every
    /// user-visible setting including the Syncthing API key, so a pre-emptive
    /// backup is the user's only recovery path if the file gets clobbered (failed
    /// update, disk issue, accidental edit). Destination path is shown in an OSD
    /// on success. Failures surface the exception message.
    /// </summary>
    private void BackupSettings()
    {
        try
        {
            if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath))
            {
                _showOsd("No settings file to back up yet (first-run state).", 4000);
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = _iniPath + ".backup-" + timestamp;

            // Don't overwrite an existing backup with the same name — the timestamp
            // is to the second so a genuine collision means the user clicked twice
            // within one second; either copy is fine but we refuse silently to
            // surface that (rather than clobber).
            if (File.Exists(backupPath))
            {
                _showOsd("Backup already exists for this second — try again.", 4000);
                return;
            }

            File.Copy(_iniPath, backupPath, overwrite: false);
            _showOsd("Settings backed up: " + Path.GetFileName(backupPath), 5000);
        }
        catch (Exception ex)
        {
            _showOsd("Backup failed: " + ex.Message, 5000);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                foreach (var f in _fonts) f.Dispose();
                _fonts.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
