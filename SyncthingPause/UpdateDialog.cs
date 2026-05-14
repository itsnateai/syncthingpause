using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyncthingPause;

/// <summary>
/// Manual update checker — no telemetry, no background requests.
/// User clicks the button, we check GitHub once, download if needed.
/// </summary>
internal sealed class UpdateDialog : Form
{
    private static readonly HttpClient _http = CreateHttpClient();

    private readonly Label _lblStatus;
    private readonly Label _lblDetail;
    private readonly Panel _progressOuter;
    private readonly Panel _progressFill;
    private readonly Button _btnAction;
    private readonly Button _btnCancel;
    private CancellationTokenSource? _cts;

    private string? _remoteVersion;
    private string? _downloadUrl;
    private string? _hashFileUrl;

    private readonly Font _boldFont;
    private readonly Font _italicFont;
    private readonly Font _btnFont;

    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueePos;
    private bool _marqueeForward = true;

    private const string AppName = "SyncthingPause";
    private const string GitHubRepo = "itsnateai/syncthingpause";

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DimColor = Color.FromArgb(0xA0, 0xA0, 0xC0);
    private static readonly Color WarnColor = Color.FromArgb(255, 152, 0);
    private static readonly Color ProgressBg = Color.FromArgb(0x2A, 0x2A, 0x3E);
    private static readonly Color ProgressFg = Color.FromArgb(76, 175, 80);

    public UpdateDialog()
    {
        Text = $"{AppName} — Update";
        // FixedDialog (not FixedToolWindow) + ShowIcon=false matches the chrome
        // used by SettingsForm and HelpForm — a standard full-size Windows X
        // button rather than the cramped tool-window caption. AutoScaleMode=Dpi
        // keeps this dialog's absolute-pixel layout honest at 125/150/200% DPI
        // (it inherited the default `Font` scaling and skewed visually on HiDPI).
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
        // Pin design baseline to 96 DPI BEFORE AutoScaleMode so literal Size/Point
        // values below are always interpreted at 96 DPI regardless of which monitor
        // the dialog is realized on. See SettingsForm.cs for the full rationale.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _italicFont = new Font("Segoe UI", 7.5f, FontStyle.Italic);
        _btnFont = new Font("Segoe UI", 8f);

        _lblStatus = new Label
        {
            Text = "Checking GitHub for new version...",
            Location = new Point(20, 20),
            Size = new Size(370, 24),
            Font = _boldFont,
            ForeColor = FgColor,
            BackColor = BgColor,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblStatus);

        _lblDetail = new Label
        {
            Text = "",
            Location = new Point(20, 48),
            Size = new Size(370, 20),
            ForeColor = DimColor,
            BackColor = BgColor,
            Font = _italicFont,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblDetail);

        _progressOuter = new Panel
        {
            Location = new Point(30, 80),
            Size = new Size(350, 18),
            BackColor = ProgressBg,
            BorderStyle = BorderStyle.None
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 18),
            BackColor = ProgressFg
        };
        _progressOuter.Controls.Add(_progressFill);
        Controls.Add(_progressOuter);

        // Two-button row laid out to match SettingsForm/HelpForm conventions:
        // both buttons 110 px wide, ending at x=406 (16 px right margin on a
        // 420 px form — mirrors the other dialogs' right-aligned 16 px margin).
        // Pre-v2.2.30 the buttons were 110/80 with a 45 px right margin, which
        // read as "a bit off" next to the other two dialogs.
        _btnAction = new Button
        {
            Text = "Upgrade Now",
            Location = new Point(166, 112),
            Size = new Size(110, 32),
            Visible = false,
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _btnFont,
        };
        _btnAction.Click += OnActionClick;
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
            _cts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        // Esc closes the dialog (was never wired — CancelButton is the form-level
        // property WinForms needs for Esc to actually fire Cancel).
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

        Shown += async (_, _) =>
        {
            if (IsWingetManaged())
            {
                ShowWingetNotice();
                return;
            }
            await CheckForUpdateAsync();
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        // Disable auto-redirect so every hop of a GET-chain is re-checked against
        // IsAllowedReleaseAssetUrl. Default HttpClientHandler would transparently
        // follow 3xx from an allowlisted origin to anywhere the Location header
        // points — a tampered release JSON could route the binary or SHA256SUMS
        // fetch to an attacker host via a crafted redirect. SendAllowlistedAsync
        // below validates each hop before issuing the GET.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppName, version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Issue a GET and follow up to 5 redirects manually. Every hop's URL —
    /// including the initial one — is validated via <see cref="IsAllowedReleaseAssetUrl"/>
    /// before the request is sent. Throws if any hop lands off-list or the redirect
    /// chain exceeds the hop limit.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAllowlistedAsync(
        string url, HttpCompletionOption completion, CancellationToken ct)
    {
        const int maxHops = 5;
        for (int hop = 0; hop < maxHops; hop++)
        {
            if (!IsAllowedReleaseAssetUrl(url))
                throw new HttpRequestException($"URL not in allowlist: {url}");

            var response = await _http.GetAsync(url, completion, ct);

            int status = (int)response.StatusCode;
            if (status >= 300 && status < 400 && response.Headers.Location != null)
            {
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(url), response.Headers.Location).ToString();
                response.Dispose();
                url = next;
                continue;
            }

            return response;
        }
        throw new HttpRequestException($"Too many redirects (>{maxHops}) starting from initial URL.");
    }

    // ─── Check GitHub ───────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _marqueeTimer.Start();

        try
        {
            using var response = await SendAllowlistedAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest",
                HttpCompletionOption.ResponseContentRead,
                _cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                    ? vals.FirstOrDefault() : null;
                ShowError(remaining == "0"
                    ? "GitHub API rate limit reached." : "GitHub API access denied (403).",
                    remaining == "0" ? "Try again in a few minutes." : "Check your network connection.");
                return;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ShowError("No releases found on GitHub.", "The repository may not have any published releases.");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var rawTag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            // Strict semver whitelist before rendering. A compromised GitHub release's
            // tag_name is otherwise interpolated raw into a dialog label and the
            // "Downloading ..." status line. Accept only `MAJOR.MINOR.PATCH` with
            // optional `-pre.1` style suffix. Anything else → treat as a bad release
            // and bail out before _remoteVersion reaches any render site.
            if (!IsSafeSemverTag(rawTag))
            {
                ShowError("GitHub release tag looks malformed.",
                    "Refusing to render or download an unverified version string.");
                return;
            }
            _remoteVersion = rawTag;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Equals("SyncthingPause.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        _downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    }
                    if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        _hashFileUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    }
                }
            }

            if (string.IsNullOrEmpty(_downloadUrl))
            {
                ShowError("No update package found in the latest release.", "The release may be incomplete.");
                return;
            }

            ShowVersionComparison();
        }
        catch (TaskCanceledException)
        {
            if (_cts?.IsCancellationRequested != true)
                ShowError("Request timed out.", "Check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            ShowError("Could not reach GitHub.", ex.Message);
        }
        catch (JsonException)
        {
            ShowError("Unexpected response from GitHub.", "The API response format may have changed.");
        }
        catch (Exception ex)
        {
            ShowError("Update check failed.", ex.Message);
        }
    }

    // ─── Compare Versions ───────────────────────────────────────

    private void ShowVersionComparison()
    {
        _marqueeTimer.Stop();
        _progressFill.Size = new Size(0, 18);
        _progressFill.Location = new Point(0, 0);

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var isNewer = Version.TryParse(_remoteVersion, out var remote)
                   && Version.TryParse(localVersion, out var local)
                   && remote > local;

        _lblDetail.Text = $"Current: {localVersion}  →  GitHub: {_remoteVersion}";
        _progressOuter.Visible = false;

        if (isNewer)
        {
            _lblStatus.Text = "A new version is available!";
            _btnAction.Text = "Upgrade Now";
            _btnAction.Visible = true;
            _btnCancel.Text = "Cancel";
        }
        else
        {
            _lblStatus.Text = "You're on the latest version!";
            _btnAction.Visible = false;
            _btnCancel.Text = "OK";
            _btnCancel.Location = new Point(170, 112);
        }
    }

    // ─── Download & Apply ───────────────────────────────────────

    private async void OnActionClick(object? sender, EventArgs e)
    {
        _btnAction.Enabled = false;
        _btnCancel.Text = "Cancel";
        _progressOuter.Visible = true;
        _progressFill.Location = new Point(0, 0);
        _lblStatus.Text = $"Downloading {AppName} {_remoteVersion}...";

        // Origin allowlist: both the binary and its SHA256SUMS must come from the
        // GitHub release endpoints. A tampered release JSON could otherwise point
        // either URL at an attacker host — and a SHA256SUMS swap defeats hash
        // verification end-to-end, so this check applies uniformly to both.
        if (!IsAllowedReleaseAssetUrl(_downloadUrl))
        {
            ShowError("Update failed: download URL is not from the expected source.", _downloadUrl ?? "(null)");
            return;
        }
        if (!IsAllowedReleaseAssetUrl(_hashFileUrl))
        {
            ShowError("Update failed: SHA256SUMS URL is not from the expected source.", _hashFileUrl ?? "(null)");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        var newPath = exePath + ".new";
        var oldPath = exePath + ".old";

        try
        {
            if (!await DownloadFileAsync(_downloadUrl!, newPath))
                return;

            // SHA256 verification is mandatory — releases MUST publish SHA256SUMS
            if (string.IsNullOrEmpty(_hashFileUrl))
            {
                TryDelete(newPath);
                ShowError("Integrity verification unavailable.",
                    "This release does not publish SHA256SUMS. Update aborted.");
                return;
            }

            _lblStatus.Text = "Verifying integrity...";
            string hashContent;
            try
            {
                using var hashResponse = await SendAllowlistedAsync(
                    _hashFileUrl!, HttpCompletionOption.ResponseContentRead, _cts!.Token);
                hashResponse.EnsureSuccessStatusCode();
                hashContent = await hashResponse.Content.ReadAsStringAsync(_cts.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception hashEx)
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "Could not fetch SHA256SUMS: " + hashEx.Message);
                return;
            }

            string? expectedHash = ParseShaSum(hashContent, "SyncthingPause.exe");

            if (string.IsNullOrEmpty(expectedHash))
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "SHA256SUMS has no entry for SyncthingPause.exe.");
                return;
            }

            var actualHash = ComputeFileHash(newPath);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(newPath);
                ShowError("Integrity verification failed.",
                    "Downloaded file does not match the expected SHA256 checksum.");
                return;
            }

            _lblStatus.Text = "Applying update...";
            _progressOuter.Visible = false;

            // Crash sentinel: written BEFORE the file swap. The new version's
            // TrayApplicationContext deletes this file after 30s of stable operation.
            // If the next launch sees this sentinel still present, the previous boot
            // crashed before proving itself stable and the user is told to manually
            // restore the .old backup.
            WriteCrashSentinel();

            TryDelete(oldPath);
            if (File.Exists(exePath))
                File.Move(exePath, oldPath);
            File.Move(newPath, exePath);

            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- exePath is Environment.ProcessPath; the replacement binary was SHA256-verified above against a SHA256SUMS asset from the github.com/itsnateai/ allowlisted origin
            using var _ = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--after-update",
                UseShellExecute = true
            });
            Application.Exit();
        }
        catch (IOException ex)
        {
            TryRollback(exePath, oldPath, newPath, ex);
        }
        catch (TaskCanceledException)
        {
            TryRollback(exePath, oldPath, newPath, null);
            if (!IsDisposed) ShowVersionComparison();
        }
        catch (Exception ex)
        {
            TryRollback(exePath, oldPath, newPath, ex);
        }
    }

    private void TryRollback(string exePath, string oldPath, string newPath, Exception? cause)
    {
        bool rollbackOk = true;
        if (File.Exists(oldPath))
        {
            TryDelete(exePath);
            try
            {
                File.Move(oldPath, exePath);
            }
            catch (Exception rbEx)
            {
                rollbackOk = false;
                if (!IsDisposed)
                    ShowError("Update failed AND rollback failed.",
                        $"Manually rename \"{Path.GetFileName(oldPath)}\" back to \"{Path.GetFileName(exePath)}\". ({rbEx.Message})");
            }
        }
        TryDelete(newPath);
        TryDeleteCrashSentinel();

        if (rollbackOk && !IsDisposed)
        {
            if (cause is IOException ioEx &&
                ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Cannot replace the executable.",
                    "Your antivirus may be locking the file. Try again.");
            }
            else if (cause is not null)
            {
                ShowError("Update failed.", cause.Message);
            }
        }
    }

    // ─── Crash-sentinel helpers (static, callable from Program + TrayContext) ──

    internal static string CrashSentinelPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncthingPause");
            try { Directory.CreateDirectory(dir); } catch { /* fall back below */ }
            return Path.Combine(dir, ".crashguard");
        }
    }

    private static void WriteCrashSentinel()
    {
        try
        {
            File.WriteAllText(CrashSentinelPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort — absence of sentinel just disables auto-rollback detection */ }
    }

    internal static void TryDeleteCrashSentinel()
    {
        try
        {
            var path = CrashSentinelPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    private async Task<bool> DownloadFileAsync(string url, string destPath)
    {
        using var response = await SendAllowlistedAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts!.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            downloaded += read;

            if (totalBytes > 0 && !IsDisposed) BeginInvoke(() =>
            {
                if (IsDisposed) return;
                int pct = (int)(downloaded * 100 / totalBytes);
                _progressFill.Size = new Size(
                    (int)(_progressOuter.Width * downloaded / totalBytes), 18);
                var dlMB = downloaded / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);
                _lblDetail.Text = totalMB < 1
                    ? $"{pct}% ({downloaded / 1024.0:F0} / {totalBytes / 1024.0:F0} KB)"
                    : $"{pct}% ({dlMB:F0} / {totalMB:F0} MB)";
            });
        }

        if (totalBytes > 0 && downloaded != totalBytes)
        {
            TryDelete(destPath);
            ShowError("Download was incomplete.",
                      $"Expected {totalBytes:N0} bytes, got {downloaded:N0}.");
            return false;
        }

        // Minimum size sanity check — reject truncated/empty downloads
        if (downloaded < 100_000)
        {
            TryDelete(destPath);
            ShowError("Downloaded file is too small.",
                      $"Got {downloaded:N0} bytes — expected a valid executable.");
            return false;
        }

        return true;
    }

    // ─── Error ──────────────────────────────────────────────────

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
    }

    // ─── Static Helpers (called from Program.cs) ────────────────

    /// <summary>
    /// Torn-state recovery only: if the update was interrupted between moving
    /// exe→.old and .new→exe, the exe is gone but .old still has the previous
    /// version. Restore it so the tray can launch. Called from Program.Main
    /// before any tray UI.
    /// </summary>
    internal static void RecoverFromTornUpdate()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || File.Exists(exePath)) return;

        var oldPath = exePath + ".old";
        if (File.Exists(oldPath))
        {
            try { File.Move(oldPath, exePath); } catch { }
        }
    }

    /// <summary>
    /// Proactive cleanup of stale .old/.new files. Safe to call ONLY after the
    /// current version has proven itself stable (see TrayApplicationContext's
    /// stability timer) — otherwise a post-update crash leaves the user with
    /// no backup to roll back to.
    /// </summary>
    internal static void CleanupStaleUpdateArtifacts()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

        foreach (var suffix in new[] { ".old", ".new" })
        {
            var path = exePath + suffix;
            if (!File.Exists(path)) continue;
            try { File.Delete(path); } catch { /* locked; try again next stable boot */ }
        }
    }

    /// <summary>Show a brief floating toast near the system tray after a successful update.</summary>
    internal static void ShowUpdateToast()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        // Two-stage timing. Stage 1: wait 1.5s after app start so the toast
        // lands AFTER the tray icon has promoted. Stage 2: ToastWindow shows
        // itself and self-disposes via FormClosed → Dispose chain, which
        // cleans the inner dismiss timer and font even if the user Alt-F4s
        // it or Application.Exit fires mid-flight.
        var delay = new System.Windows.Forms.Timer { Interval = 1500 };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            delay.Dispose();
            var toast = new ToastWindow($"\u2705 {AppName} updated to v{version}!");
            toast.Show();
        };
        delay.Start();
    }

    /// <summary>
    /// Owns the toast <see cref="Form"/>, its dismiss timer, and its font as a
    /// single disposable unit. Prior inline implementation leaked the outer
    /// timer, the form, and the dismiss timer on external close (Alt-F4 or
    /// <see cref="Application.Exit"/>). WinForms routes form close → Dispose,
    /// so overriding <see cref="Dispose(bool)"/> guarantees teardown.
    /// </summary>
    private sealed class ToastWindow : Form
    {
        private readonly System.Windows.Forms.Timer _dismiss;
        private readonly Font _font;

        public ToastWindow(string message)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = BgColor;
            ForeColor = FgColor;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12, 8, 12, 8);
            // Pin design baseline to 96 DPI BEFORE AutoScaleMode so the hardcoded
            // 20px screen-corner offset (set in Load handler below) and the
            // Padding(12, 8, 12, 8) literals scale correctly at non-100% DPI.
            // Matches the pattern in SettingsForm/HelpForm/UpdateDialog/OsdToolTip.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            _font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var lbl = new Label
            {
                Text = message,
                AutoSize = true,
                Font = _font,
                ForeColor = FgColor,
                BackColor = BgColor,
            };
            Controls.Add(lbl);

            var screen = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            Load += (_, _) => Location = new Point(
                screen.Right - Width - 20, screen.Bottom - Height - 20);

            _dismiss = new System.Windows.Forms.Timer { Interval = 5000 };
            _dismiss.Tick += (_, _) =>
            {
                _dismiss.Stop();
                Close();
            };
            _dismiss.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dismiss.Stop();
                _dismiss.Dispose();
                _font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ─── Winget Detection ──────────────────────────────────────

    private static bool IsWingetManaged() =>
        (Environment.ProcessPath ?? "").Contains(@"Microsoft\WinGet\Packages", StringComparison.OrdinalIgnoreCase);

    private void ShowWingetNotice()
    {
        _marqueeTimer.Stop();
        _progressOuter.Visible = false;
        _lblStatus.Text = "This installation is managed by winget.";
        _lblStatus.ForeColor = WarnColor;
        _lblDetail.Text = "Use:  winget upgrade itsnateai.SyncthingPause";
        _btnAction.Visible = false;
        _btnCancel.Text = "OK";
        _btnCancel.Location = new Point(170, 112);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Host-based allowlist validated at every redirect hop via
    /// <see cref="SendAllowlistedAsync"/>. Suffix-matches *.githubusercontent.com
    /// so future GitHub-controlled release-asset CDN hosts (beyond the already-seen
    /// `objects.githubusercontent.com` and `release-assets.githubusercontent.com`)
    /// keep working without another CVE-shaped code change. Repo scoping on
    /// github.com/api.github.com prevents path traversal to unrelated repos.
    /// </summary>
    internal static bool IsAllowedReleaseAssetUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        string host = uri.Host;
        if (host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.StartsWith("/repos/itsnateai/syncthingpause/", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.StartsWith("/repos/itsnateai/synctray/", StringComparison.OrdinalIgnoreCase))) return true;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.StartsWith("/itsnateai/syncthingpause/", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.StartsWith("/itsnateai/synctray/", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    // Compiled once: strict semver whitelist for GitHub release `tag_name`.
    // Accepts `1.2.3` and `1.2.3-pre.1` / `1.2.3-rc2` etc. The leading `v` is
    // already stripped by the caller. Rejects anything with whitespace, control
    // chars, format specifiers, or other renderable surprises.
    private static readonly Regex _safeSemver = new(
        @"^\d{1,5}\.\d{1,5}\.\d{1,5}(-[a-z0-9][a-z0-9.]{0,31})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool IsSafeSemverTag(string? tag)
        => !string.IsNullOrEmpty(tag) && _safeSemver.IsMatch(tag);

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Parse a GNU-style SHA256SUMS file body and return the hex digest for the
    /// named file, or null if not found. Accepts "hash  name" and "hash *name"
    /// formats, is case-insensitive on the filename, and ignores blank/comment lines.
    /// </summary>
    internal static string? ParseShaSum(string content, string fileName)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(fileName)) return null;
        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var name = parts[1].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].Trim();
        }
        return null;
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
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
        }
        base.Dispose(disposing);
    }
}
