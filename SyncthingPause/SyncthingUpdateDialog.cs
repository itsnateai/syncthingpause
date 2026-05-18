using System.Text.Json;

namespace SyncthingPause;

/// <summary>
/// Modal dialog for upgrading the Syncthing daemon. Shown by SettingsForm's
/// "Check Now" button when GET /rest/system/upgrade reports a newer version.
///
/// Flow:
///   1. Display "Update available: vRUNNING -> vLATEST" with [Upgrade Now] [Cancel].
///   2. On Upgrade Now: POST /rest/system/upgrade (Syncthing downloads + restarts itself).
///   3. Poll GET /rest/system/version every 1s for up to 60s.
///   4. When `version` flips away from the pre-upgrade running value -> success,
///      label flips to "Upgraded to vNEW!", button changes to Close, fire toast.
///   5. On timeout -> warn: daemon hasn't come back, ask user to check tray.
///
/// What this dialog does NOT do (and why):
///   - No GitHub call: Syncthing's own /rest/system/upgrade is the upgrade trigger.
///   - No hash verification: Syncthing's upgrade-fetcher does its own signature check.
///   - No file swap / .old / .new dance: the daemon swaps its own binary atomically.
///   - No crash sentinel: failure surfaces as the next poll-tick reporting timeout,
///     not as a torn binary for SyncthingPause to recover.
///
/// The visual layout (FixedDialog, dark palette, marquee progress, two-button row)
/// matches UpdateDialog so users see one upgrade UX across "SyncthingPause updates
/// itself" and "Syncthing daemon updates itself".
/// </summary>
internal sealed class SyncthingUpdateDialog : Form
{
    private readonly SyncthingApi _api;
    private readonly string _runningBefore;
    private readonly string _latest;

    private readonly Label _lblStatus;
    private readonly Label _lblDetail;
    private readonly Panel _progressOuter;
    private readonly Panel _progressFill;
    private readonly Button _btnAction;
    private readonly Button _btnCancel;

    private readonly Font _boldFont;
    private readonly Font _italicFont;
    private readonly Font _btnFont;

    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueePos;
    private bool _marqueeForward = true;
    private CancellationTokenSource? _pollCts;

    // Double-click guard: WM_COMMAND can queue a second click before Enabled=false
    // takes effect across the message pump. _busy is the synchronous source of truth.
    private bool _busy;

    // Tracks the last HTTP status returned by the /rest/system/version probe inside
    // PollForVersionFlipAsync. Surfaced in the timeout-warn log so "didn't come
    // back after 60s" includes a hint (e.g. "last status=401" = bad API key, "last
    // status=-1" = couldn't connect for the whole window) instead of being opaque.
    // Initialized to -1 to match SyncthingApi's "unreachable" sentinel: if every
    // poll iteration threw before reaching the assignment at line ~350, the log
    // reads "Last status=-1" (consistent with "couldn't connect"), not "Last
    // status=0" (which would be ambiguous — HTTP has no 0 status code, but readers
    // might assume "no data" vs "never assigned").
    private int _lastPollStatus = -1;

    // True when _runningBefore is a real version string. False when the upstream
    // response was missing the "running" field and we got the "unknown" sentinel.
    // Drives the poll's flip-detection mode: a known-running value lets us accept
    // "any change is progress"; an unknown one forces exact-match against _latest
    // so we don't false-positive on the first real version we see.
    private readonly bool _runningKnown;

    // Theme-aware caches — captured at first class load (the user opening
    // Check Now → "Update available" dialog is well after Theme.Initialize).
    private static readonly Color BgColor = Theme.Bg;
    private static readonly Color FgColor = Theme.Fg;
    private static readonly Color DimColor = Theme.Dim;
    private static readonly Color WarnColor = Theme.AccentWarn;
    private static readonly Color OkColor = Theme.AccentGreen;
    private static readonly Color ProgressBg = Theme.EditBg;
    private static readonly Color ProgressFg = Theme.AccentGreen;

    // 60s ceiling on the post-POST poll. Syncthing's self-restart usually finishes
    // in ~5-15s; 60s tolerates a slow download on a constrained connection. After
    // that we surface the "didn't come back" warning rather than spin forever.
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // Shared sentinel for "JSON didn't carry the running field; we don't actually
    // know what's running". SettingsForm references this when building the dialog
    // ctor args so the magic string isn't duplicated across files.
    internal const string UnknownVersionSentinel = "unknown";

    public SyncthingUpdateDialog(SyncthingApi api, string runningBefore, string latest)
    {
        _api = api;
        _runningBefore = runningBefore ?? string.Empty;
        _latest = latest ?? string.Empty;
        // SettingsForm passes UnknownVersionSentinel when JSON parsing failed to
        // find the running field. Treat that (and empty/blank) as not-real so the
        // poll switches to exact-match mode against _latest.
        _runningKnown = !string.IsNullOrWhiteSpace(_runningBefore)
            && !string.Equals(_runningBefore, UnknownVersionSentinel, StringComparison.OrdinalIgnoreCase);

        Text = "Syncthing — Update";
        // Same chrome conventions as UpdateDialog / SettingsForm / HelpForm:
        // FixedDialog (full X button, not the cramped tool-window caption),
        // ShowIcon off, no min/max. AutoScaleMode=Dpi with a 96-DPI baseline
        // keeps the literal Point/Size below honest at 125/150/200% DPI.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowIcon = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(420, 180);
        BackColor = BgColor;
        ForeColor = FgColor;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _italicFont = new Font("Segoe UI", 7.5f, FontStyle.Italic);
        _btnFont = new Font("Segoe UI", 8f);

        _lblStatus = new Label
        {
            Text = "A new Syncthing version is available!",
            Location = new Point(20, 20),
            Size = new Size(370, 24),
            Font = _boldFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_lblStatus);

        _lblDetail = new Label
        {
            Text = FormatVersionLine(_runningBefore, _latest),
            Location = new Point(20, 48),
            Size = new Size(370, 20),
            ForeColor = DimColor,
            BackColor = BgColor,
            Font = _italicFont,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_lblDetail);

        _progressOuter = new Panel
        {
            Location = new Point(30, 80),
            Size = new Size(350, 18),
            BackColor = ProgressBg,
            BorderStyle = BorderStyle.None,
            Visible = false,
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 18),
            BackColor = ProgressFg,
        };
        _progressOuter.Controls.Add(_progressFill);
        Controls.Add(_progressOuter);

        // Two-button row matching UpdateDialog's geometry: 110-wide buttons ending
        // at x=406 (16px right margin on a 420-px form). Action is left, Cancel
        // right — same eye-path as the rest of the app.
        _btnAction = new Button
        {
            Text = "Upgrade Now",
            Location = new Point(166, 112),
            Size = new Size(110, 32),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _btnFont,
        };
        _btnAction.Click += OnUpgradeClick;
        Controls.Add(_btnAction);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(296, 112),
            Size = new Size(110, 32),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _btnFont,
            DialogResult = DialogResult.Cancel,
        };
        _btnCancel.Click += (_, _) =>
        {
            // Cancel here is UI-only. If the daemon has already accepted the POST,
            // the upgrade proceeds regardless — closing the dialog just stops us
            // from polling. That's intentional: there's no Syncthing-side cancel.
            _pollCts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        // Esc → Cancel button. WinForms needs CancelButton wired explicitly;
        // setting DialogResult on the button alone doesn't bind Esc.
        CancelButton = _btnCancel;

        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _marqueeTimer.Tick += (_, _) =>
        {
            const int step = 4, barW = 80;
            if (_marqueeForward) _marqueePos += step; else _marqueePos -= step;
            if (_marqueePos + barW >= _progressOuter.Width) _marqueeForward = false;
            if (_marqueePos <= 0) _marqueeForward = true;
            _progressFill.Location = new Point(_marqueePos, 0);
            _progressFill.Size = new Size(barW, 18);
        };
    }

    private static string FormatVersionLine(string running, string latest)
    {
        var r = string.IsNullOrEmpty(running) ? "?" : running;
        var l = string.IsNullOrEmpty(latest) ? "?" : latest;
        return $"Current: {r}  →  Latest: {l}";
    }

    private async void OnUpgradeClick(object? sender, EventArgs e)
    {
        // Sync double-click guard — Enabled=false alone is racy because WM_COMMAND
        // can queue a second click before WinForms processes the property change.
        // _busy flips before any await so the second click short-circuits.
        if (_busy) return;
        _busy = true;
        _btnAction.Enabled = false;
        _btnAction.Visible = false;
        _lblStatus.Text = "Requesting upgrade...";
        _progressOuter.Visible = true;
        _marqueeTimer.Start();
        TrayLog.Info($"Syncthing upgrade requested: {_runningBefore} -> {_latest}");

        // POST runs off the UI thread — the SyncthingApi client is synchronous and
        // we don't want to freeze the dialog during the 5s timeout. Task.Run is
        // fine here: no shared mutable state captured, return type is value-typed.
        int postStatus;
        try
        {
            postStatus = await Task.Run(() => _api.Post("/rest/system/upgrade").StatusCode);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Syncthing upgrade POST threw: " + ex.Message);
            if (IsDisposed) return;
            ShowError("Upgrade request failed.", ex.Message);
            return;
        }

        // IsDisposed gate after every await: the user may have hit Cancel or the X
        // button while we were on the pool thread. Without this, the continuation
        // mutates disposed controls and ObjectDisposedException escapes async-void
        // as an unobserved exception — TaskScheduler.UnobservedTaskException can
        // crash the process on tear-down.
        if (IsDisposed) return;

        if (postStatus != 200)
        {
            // SyncthingApi.DoRequest returns -1 for connection-failure (timeout,
            // connection refused, transient HttpRequestException without a status
            // code). That's "couldn't reach the daemon", not "daemon rejected the
            // request" — distinct user-facing failure modes, distinct messages.
            if (postStatus == -1)
            {
                TrayLog.Warn("Syncthing upgrade POST: could not reach daemon.");
                ShowError("Could not reach Syncthing daemon.",
                    "The daemon may have stopped. Check the tray icon.");
            }
            else
            {
                TrayLog.Warn($"Syncthing upgrade POST returned HTTP {postStatus}.");
                ShowError($"Upgrade rejected (HTTP {postStatus}).",
                    "Syncthing rejected the upgrade. Check the daemon log.");
            }
            return;
        }

        _lblStatus.Text = "Restarting Syncthing...";
        _lblDetail.Text = $"Waiting for v{StripV(_latest)} to come back online...";

        bool flipped = await PollForVersionFlipAsync();
        if (IsDisposed) return;

        _marqueeTimer.Stop();
        _progressFill.Location = new Point(0, 0);
        _progressFill.Size = new Size(0, 18);
        _progressOuter.Visible = false;

        if (flipped)
        {
            _lblStatus.Text = "Upgrade complete!";
            _lblStatus.ForeColor = OkColor;
            _lblDetail.Text = $"Syncthing is now running {_latest}.";
            _btnCancel.Text = "Close";
            _btnCancel.Location = new Point(170, 112);
            TrayLog.Info($"Syncthing upgrade confirmed: now running {_latest}.");
            UpdateDialog.ShowToast($"✅ Syncthing updated to {_latest}!");
        }
        else
        {
            // The POST was accepted but we never saw the version flip. Most likely
            // the daemon is still downloading / restarting and just exceeded our
            // 60s budget. Don't claim failure — the upgrade may still be in flight.
            _lblStatus.Text = "Upgrade started — daemon hasn't come back yet.";
            _lblStatus.ForeColor = WarnColor;
            _lblDetail.Text = "Check the tray icon shortly to confirm.";
            _btnCancel.Text = "OK";
            _btnCancel.Location = new Point(170, 112);
            TrayLog.Warn($"Syncthing upgrade: {PollTimeout.TotalSeconds:F0}s poll timeout, version flip not observed. Last status={_lastPollStatus}.");
        }
        // Symmetric clear with ShowError: dialog has reached its terminal state
        // regardless of outcome, so the busy guard no longer applies.
        _busy = false;
    }

    /// <summary>
    /// Poll <c>/rest/system/version</c> until the returned <c>version</c> field
    /// indicates the daemon has restarted on the new version, or until
    /// <see cref="PollTimeout"/> elapses. Returns true on success, false on
    /// timeout or any unexpected error.
    ///
    /// Two detection modes:
    /// - <see cref="_runningKnown"/> true → "any flip" (version != _runningBefore).
    ///   Tolerates Syncthing pulling a slightly-newer-than-expected patch release
    ///   between our check and the POST.
    /// - <see cref="_runningKnown"/> false → "exact match _latest". Safer when we
    ///   don't have a baseline: a daemon that's been running unmodified would
    ///   otherwise false-positive on the first real version string we see.
    /// </summary>
    private async Task<bool> PollForVersionFlipAsync()
    {
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource(PollTimeout);
        var ct = _pollCts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1500ms per-call timeout: short enough that a hung daemon
                    // doesn't eat half our budget, long enough that a busy daemon
                    // still answers. The pool-thread Get() is safe to call here.
                    var (status, body) = await Task.Run(
                        () => _api.Get("/rest/system/version", timeoutMs: 1500),
                        ct);

                    _lastPollStatus = status;
                    if (status == 200 && !string.IsNullOrEmpty(body))
                    {
                        string nowRunning = ExtractVersion(body);
                        // Empty parse → treat as not-flipped, keep polling. Don't
                        // false-positive a parse failure as a successful upgrade.
                        if (nowRunning.Length > 0 && IsUpgradeObserved(nowRunning))
                        {
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception)
                {
                    // Connection refused / read-timeout during the restart window
                    // is expected — daemon is briefly down. Explicit Exception
                    // (not bare catch) still tolerates the same transient set but
                    // signals intent and won't accidentally swallow a hypothetical
                    // non-Exception throw like a typed pre-.NET-Core flow. The
                    // SyncthingApi.Get contract already swallows HttpRequestException
                    // and returns (-1, ""), so we rarely actually land in here.
                }

                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }
        catch (OperationCanceledException) { /* timeout — fall through */ }
        return false;
    }

    /// <summary>
    /// Decide whether a freshly-polled <paramref name="nowRunning"/> version string
    /// counts as the upgrade having landed. See PollForVersionFlipAsync's docstring
    /// for the two modes.
    /// </summary>
    private bool IsUpgradeObserved(string nowRunning)
    {
        if (_runningKnown)
        {
            return !string.Equals(nowRunning, _runningBefore, StringComparison.Ordinal);
        }
        // Exact-match mode: compare on extracted MAJOR.MINOR.PATCH only. Syncthing's
        // upgrade-check `latest` field typically returns clean tags ("v2.1.0"), but
        // the running daemon's /rest/system/version may carry pre-release suffixes
        // ("v2.1.0-rc.1") or build metadata ("v2.1.0+gabc123") that an ordinal-equal
        // would miss. MMP comparison treats those as the same release.
        var nowMMP = ExtractMajorMinorPatch(nowRunning);
        var latestMMP = ExtractMajorMinorPatch(_latest);
        // If either side fails to yield a usable triple, fall back to a normalized
        // ordinal compare rather than silently match — better a false timeout than
        // a false success.
        if (nowMMP.Length == 0 || latestMMP.Length == 0)
        {
            return string.Equals(
                NormalizeVersion(nowRunning),
                NormalizeVersion(_latest),
                StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(nowMMP, latestMMP, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract a "major.minor.patch" string from a Syncthing version like
    /// "v2.1.0", "v2.1.0-rc.1", or "v2.1.0+12-gabc123". Returns empty if the
    /// input doesn't parse to at least major.minor.patch.
    ///
    /// Accept-shape rules (intentional):
    /// - 2 segments ("v2.1") → empty. Reject: incomplete, can't safely compare.
    /// - 3 segments ("v2.1.0") → "2.1.0". Standard case.
    /// - 4+ segments ("v2.1.0.123") → "2.1.0". Tolerate trailing extras; Syncthing
    ///   doesn't ship 4-segment versions today, but other tools have, and the
    ///   first three segments are the canonical release identity.
    /// - Empty / non-numeric segment / leading/trailing dots → empty.
    /// </summary>
    private static string ExtractMajorMinorPatch(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return string.Empty;
        var s = NormalizeVersion(v);
        // Cut at the first separator that introduces pre-release / build metadata.
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        // Validate shape: at least three numeric segments separated by dots.
        // `< 3` not `!= 3` — see docstring "Accept-shape rules".
        var parts = s.Split('.');
        if (parts.Length < 3) return string.Empty;
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0) return string.Empty;
            foreach (var c in parts[i])
                if (c < '0' || c > '9') return string.Empty;
        }
        return $"{parts[0]}.{parts[1]}.{parts[2]}";
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        var s = v.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        return s;
    }

    private static string ExtractVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { /* fall through to empty */ }
        return string.Empty;
    }

    private static string StripV(string version)
        => !string.IsNullOrEmpty(version) && version[0] == 'v' ? version[1..] : version;

    /// <summary>
    /// Switch the dialog to an error-display state: stop the marquee, replace the
    /// status/detail text, hide the action button, and recenter the Cancel button
    /// as a dismiss-only "OK". Also clears <see cref="_busy"/>: the dialog is no
    /// longer mid-upgrade, so a future code path that re-shows _btnAction (today
    /// none does) wouldn't inherit a stuck busy flag.
    /// </summary>
    private void ShowError(string message, string detail)
    {
        _marqueeTimer.Stop();
        _progressOuter.Visible = false;
        _lblStatus.Text = message;
        _lblStatus.ForeColor = WarnColor;
        _lblDetail.Text = detail;
        _btnAction.Visible = false;
        _btnCancel.Text = "OK";
        _btnCancel.Location = new Point(170, 112);
        _busy = false;
    }

    /// <summary>
    /// Cancel any active poll BEFORE the form-close completes. The Cancel button
    /// already does this in its Click handler — but the X-button (FixedDialog
    /// shows one) and Alt-F4 bypass that handler entirely, leaving the poll loop
    /// running against a soon-to-be-disposed form. OnFormClosing is the choke
    /// point every close path funnels through.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _pollCts?.Cancel(); } catch (ObjectDisposedException) { }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont.Dispose();
            _italicFont.Dispose();
            _btnFont.Dispose();
            _marqueeTimer.Stop();
            _marqueeTimer.Dispose();
            try { _pollCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _pollCts?.Dispose(); } catch (ObjectDisposedException) { }
        }
        base.Dispose(disposing);
    }
}
