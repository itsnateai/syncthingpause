using System.Text;

namespace SyncthingPause;

/// <summary>
/// Manages INI-based settings for SyncthingPause.
/// </summary>
internal sealed class AppConfig
{
    public static readonly string Version = typeof(AppConfig).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Click action values: "webui", "rescan", "pause", "none"
    public static readonly string[] ClickActions = ["Open Web UI", "Rescan Now", "Pause/Resume", "Do Nothing"];
    public static readonly string[] ClickActionValues = ["webui", "rescan", "pause", "none"];

    // Settings
    public string DblClickAction { get; set; } = "webui";
    public string MiddleClickAction { get; set; } = "pause";
    public bool RunOnStartup { get; set; }
    public bool StartBrowser { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string SyncExe { get; set; } = string.Empty;
    public string WebUI { get; set; } = "http://localhost:8384";
    public int StartupDelay { get; set; } = 20;
    public bool NetworkAutoPause { get; set; }
    public bool AutoCheckUpdates { get; set; }
    public bool SoundNotifications { get; set; }
    public bool StopOnExit { get; set; }

    /// <summary>
    /// Opt-in: every other user-facing surface (README, CHANGELOG, HelpForm,
    /// CLAUDE.md, TrayLog docstring) advertises this as opt-in via
    /// `DiagnosticLogging=1`. Shipping a default of `true` contradicted the
    /// consent model; v2.2.34 flipped this to `false` so the promise matches
    /// the behaviour. Existing users who opened Settings at least once already
    /// have the key persisted in their INI, so their preference is preserved.
    /// </summary>
    public bool DiagnosticLogging { get; set; }

    /// <summary>
    /// Window-chrome theme — "Dark" (default, Catppuccin Mocha) or "Light"
    /// (v2.1.x classic — pure white BG, near-black text, brand-blue
    /// #2255AA headers, cornsilk #FFF8DC focus tint). Restart-to-apply by
    /// design — the GDI brush/pen caches in DarkMenuRenderer / OsdToolTip /
    /// the dialog forms capture <see cref="Theme"/>'s values at first class
    /// load and can't be invalidated without a process restart. The Save path
    /// in SettingsForm spawns a replacement process to make this seamless.
    ///
    /// Tray icons follow the OS theme regardless of this pin — the user pin
    /// only affects window chrome.
    /// </summary>
    public string ThemeMode { get; set; } = "Dark";

    public string SettingsFilePath { get; }
    public bool IsPortable { get; }
    public bool IsFirstRun { get; }

    /// <summary>
    /// Load-error state surfaced to the user after the tray is up.
    /// - None: load succeeded or no file to load (first run)
    /// - Locked: IO/permission error reading the file
    /// - Corrupt: file existed but parsing produced no settings
    /// </summary>
    public AppConfigLoadResult LoadResult { get; private set; } = AppConfigLoadResult.None;

    /// <summary>
    /// Tracks which keys were explicitly present in the INI file.
    /// Only these keys are written back on Save(), so new defaults
    /// in future versions aren't overridden by stale saved values.
    /// </summary>
    private readonly HashSet<string> _configuredKeys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public AppConfig(string appDirectory)
    {
        SettingsFilePath = Path.Combine(appDirectory, "SyncthingPause.ini");
        SyncExe = Path.Combine(appDirectory, "syncthing.exe");
        if (!File.Exists(SyncExe))
            SyncExe = ValidateSyncExe(DiscoverSyncExe()) ?? SyncExe;

        // Portable mode detection
        try
        {
            var root = Path.GetPathRoot(appDirectory);
            if (root is not null)
            {
                var driveInfo = new DriveInfo(root);
                IsPortable = driveInfo.DriveType == DriveType.Removable;
            }
        }
        catch { /* ignore drive detection failures */ }

        // v3.0.0 — rename predecessor (SyncthingTray) one-shot copy. If we
        // have no SyncthingPause.ini yet but a co-located SyncthingTray.ini
        // exists (portable installs that drop the binary in the same dir),
        // copy it across so the user's settings carry forward. Best-effort:
        // failure leaves IsFirstRun=true and the legacy file is untouched
        // for manual recovery. Save() always writes to the new path; the
        // legacy file is preserved as a rollback affordance.
        //
        // Verifier round (2026-05-08): added size cap + symlink/junction
        // refusal. A symlinked SyncthingTray.ini pointing at a system file
        // (hosts, SAM, an attacker-controlled UNC share, etc.) would
        // otherwise be silently copied into our state and parsed; a giant
        // SyncthingTray.ini (Syncthing-syncs-the-install-dir bug, malicious
        // padding) would OOM Load() since File.ReadAllLines has no size
        // bound. 256 KB matches the pause.dat cap and is ~10× any real INI.
        var legacySettingsPath = Path.Combine(appDirectory, "SyncthingTray.ini");
        const long LegacyIniMaxBytes = 256 * 1024;
        if (!File.Exists(SettingsFilePath) && File.Exists(legacySettingsPath))
        {
            try
            {
                // Refuse symlinks / junctions / mount points — File.Copy follows
                // reparse points by default and would read attacker-controlled
                // bytes from anywhere on disk.
                var attrs = File.GetAttributes(legacySettingsPath);
                if ((attrs & FileAttributes.ReparsePoint) == 0)
                {
                    var legacyInfo = new FileInfo(legacySettingsPath);
                    if (legacyInfo.Length > 0 && legacyInfo.Length <= LegacyIniMaxBytes)
                    {
                        File.Copy(legacySettingsPath, SettingsFilePath, overwrite: false);
                    }
                    else if (legacyInfo.Length > LegacyIniMaxBytes)
                    {
                        // Verifier round 2: oversized legacy was being skipped
                        // silently. Log so the user has a breadcrumb when
                        // they wonder why their old settings didn't carry —
                        // 256 KB+ INIs typically mean the file is corrupt or
                        // the install dir got Syncthing'd into a sync loop.
                        TrayLog.Warn($"AppConfig: legacy SyncthingTray.ini is {legacyInfo.Length} bytes (cap {LegacyIniMaxBytes}); refusing migration. File left intact for manual review.");
                    }
                    /* 0-byte legacy: skip silently — empty file = nothing to migrate */
                }
                else
                {
                    TrayLog.Warn("AppConfig: legacy SyncthingTray.ini is a reparse point (symlink / junction); refusing migration. File left intact for manual review.");
                }
            }
            catch { /* legacy left intact for manual recovery */ }
        }

        IsFirstRun = !File.Exists(SettingsFilePath);
        if (!IsFirstRun)
            Load();
    }

    public void Load()
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(SettingsFilePath, Utf8NoBom);
        }
        catch (IOException)
        {
            LoadResult = AppConfigLoadResult.Locked;
            return;
        }
        catch (UnauthorizedAccessException)
        {
            LoadResult = AppConfigLoadResult.Locked;
            return;
        }
        catch (Exception)
        {
            LoadResult = AppConfigLoadResult.Locked;
            return;
        }

        Dictionary<string, string> settings;
        try
        {
            settings = ParseIni(lines);
        }
        catch
        {
            LoadResult = AppConfigLoadResult.Corrupt;
            return;
        }

        // File existed but parsed to nothing — either empty or structurally broken.
        // Treat as corrupt so we refuse to clobber it on Save until the user confirms.
        bool looksEmpty = settings.Count == 0 && lines.Length > 0
            && lines.Any(l => l.Trim().Length > 0 && l.Trim()[0] != ';' && l.Trim()[0] != '[');
        if (looksEmpty)
        {
            LoadResult = AppConfigLoadResult.Corrupt;
            return;
        }

        _configuredKeys.Clear();
        foreach (var key in settings.Keys)
            _configuredKeys.Add(key);

        // New action-based settings (v2.1+)
        DblClickAction = GetString(settings, "DblClickAction", string.Empty);
        MiddleClickAction = GetString(settings, "MiddleClickAction", string.Empty);

        // Backward compat: migrate old boolean settings if new keys absent
        if (string.IsNullOrEmpty(DblClickAction))
            DblClickAction = GetBool(settings, "DblClickOpen", true) ? "webui" : "none";
        if (string.IsNullOrEmpty(MiddleClickAction))
            MiddleClickAction = GetBool(settings, "MiddleClickEnabled", true) ? "pause" : "none";

        RunOnStartup = GetBool(settings, "RunOnStartup", false);
        StartBrowser = GetBool(settings, "StartBrowser", false);
        ApiKey = GetString(settings, "ApiKey", string.Empty);
        NetworkAutoPause = GetBool(settings, "NetworkAutoPause", false);
        AutoCheckUpdates = GetBool(settings, "AutoCheckUpdates", false);
        SoundNotifications = GetBool(settings, "SoundNotifications", false);
        StopOnExit = GetBool(settings, "StopOnExit", false);
        DiagnosticLogging = GetBool(settings, "DiagnosticLogging", false);

        // Theme — case-insensitive value with canonical-case storage so a
        // hand-edited "themeMode=dark" works. Unknown values keep the current
        // default rather than silently clobbering to a sentinel.
        var themeRaw = GetString(settings, "ThemeMode", string.Empty);
        if (string.Equals(themeRaw, "Dark", StringComparison.OrdinalIgnoreCase))
            ThemeMode = "Dark";
        else if (string.Equals(themeRaw, "Light", StringComparison.OrdinalIgnoreCase))
            ThemeMode = "Light";
        else if (!string.IsNullOrEmpty(themeRaw))
        {
            TrayLog.Warn($"AppConfig: unknown ThemeMode value '{themeRaw}' — keeping '{ThemeMode}'.");
        }

        var exe = GetString(settings, "SyncExe", string.Empty);
        if (!string.IsNullOrEmpty(exe))
            SyncExe = ValidateSyncExe(exe) ?? SyncExe;

        var webUi = GetString(settings, "WebUI", string.Empty);
        if (!string.IsNullOrEmpty(webUi))
            WebUI = ValidateWebUI(webUi);

        // Clamp to [0, 3600] at the load boundary. The Settings UI NumericUpDown
        // already clamps on input, but a user hand-editing SyncthingPause.ini
        // (typo, muscle memory from StartupDelay=180000 meaning-ms) could land a
        // value that overflows when the constructor multiplies by 1000 → WinForms
        // Timer.Interval setter throws on <=0, crashing the tray on boot with no
        // OSD, no log, no sign of what happened. 3600 s = 1 hour upper bound
        // matches the UI maximum so load and save agree on the same invariant.
        if (int.TryParse(GetString(settings, "StartupDelay", "0"), out int delay))
            StartupDelay = Math.Clamp(delay, 0, 3600);
    }

    /// <summary>
    /// Mark a key as explicitly configured (called from UI save).
    /// </summary>
    public void MarkConfigured(string key) => _configuredKeys.Add(key);

    /// <summary>
    /// Mark all current settings as configured (user saved via Settings dialog).
    /// </summary>
    public void MarkAllConfigured()
    {
        string[] allKeys = ["DblClickAction", "MiddleClickAction", "RunOnStartup", "StartBrowser", "ApiKey",
            "SyncExe", "WebUI", "StartupDelay", "NetworkAutoPause", "AutoCheckUpdates", "SoundNotifications", "StopOnExit", "DiagnosticLogging", "ThemeMode"];
        foreach (var key in allKeys)
            _configuredKeys.Add(key);
    }

    public bool Save()
    {
        // When saving from the UI, all keys are explicitly configured
        MarkAllConfigured();

        var sb = new StringBuilder();
        sb.AppendLine("[Settings]");
        sb.AppendLine($"DblClickAction={DblClickAction}");
        sb.AppendLine($"MiddleClickAction={MiddleClickAction}");
        sb.AppendLine($"RunOnStartup={BoolToStr(RunOnStartup)}");
        sb.AppendLine($"StartBrowser={BoolToStr(StartBrowser)}");
        sb.AppendLine($"ApiKey={ApiKey}");
        sb.AppendLine($"SyncExe={SyncExe}");
        sb.AppendLine($"WebUI={WebUI}");
        sb.AppendLine($"StartupDelay={StartupDelay}");
        sb.AppendLine($"NetworkAutoPause={BoolToStr(NetworkAutoPause)}");
        sb.AppendLine($"AutoCheckUpdates={BoolToStr(AutoCheckUpdates)}");
        sb.AppendLine($"SoundNotifications={BoolToStr(SoundNotifications)}");
        sb.AppendLine($"StopOnExit={BoolToStr(StopOnExit)}");
        sb.AppendLine($"DiagnosticLogging={BoolToStr(DiagnosticLogging)}");
        sb.AppendLine($"ThemeMode={ThemeMode}");

        try
        {
            // If the existing file was detected as corrupt, preserve it as a backup
            // so the user can recover any hand-edited values that failed to parse.
            if (LoadResult == AppConfigLoadResult.Corrupt && File.Exists(SettingsFilePath))
            {
                var backup = SettingsFilePath + "." +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".corrupt.bak";
                try { File.Copy(SettingsFilePath, backup, overwrite: false); }
                catch { /* backup is best-effort; don't block the save */ }
                LoadResult = AppConfigLoadResult.None;
            }

            var tmpPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, sb.ToString(), Utf8NoBom);
            File.Move(tmpPath, SettingsFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Write a minimal stub INI so future launches don't re-trigger the first-run
    /// wizard when the user cancels it. Only called when IsFirstRun is true.
    /// </summary>
    public void SeedFirstRunStub()
    {
        try
        {
            if (File.Exists(SettingsFilePath)) return;
            File.WriteAllText(SettingsFilePath,
                "[Settings]\n; First-run stub — open Settings to configure SyncthingPause.\n",
                Utf8NoBom);
        }
        catch
        {
            // Portable mode on an ejected drive, or read-only location; caller will
            // see IsFirstRun=true on next launch, which is a recoverable state.
        }
    }
    /// <summary>
    /// Validate WebUI is a safe localhost URL (SSRF prevention).
    /// </summary>
    internal static string ValidateWebUI(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "http://localhost:8384";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "http://localhost:8384";
        if (uri.Scheme != "http" && uri.Scheme != "https") return "http://localhost:8384";
        if (uri.Host != "localhost" && uri.Host != "127.0.0.1" && uri.Host != "::1")
            return "http://localhost:8384";
        return url;
    }

    /// <summary>
    /// Validate SyncExe points to an actual syncthing binary.
    /// </summary>
    internal static string? ValidateSyncExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Reject path traversal
        if (path.Contains("..")) return null;
        // Reject null-byte truncation attempts
        if (path.Contains('\0')) return null;
        // Reject UNC / remote-share paths — a tampered INI pointing at
        // \\attacker\share\syncthing.exe would trigger an NTLM handshake against
        // the attacker on the local network, leaking the user's hash even before
        // File.Exists returns.
        //
        // Char-pair predicate catches all 4 mixed-slash UNC forms — \\srv, //srv,
        // \/srv, /\srv. On Windows, File.Exists("//srv/share/...") normalizes the
        // forward slashes to backslashes and triggers the same SMB authentication
        // as a plain UNC path. Uri.IsUnc and StartsWith(@"\\") only catch the
        // backslash variant. Same predicate as OpenFolder uses (v2.2.33-2.2.35).
        if (path.Length >= 2 &&
            (path[0] == '\\' || path[0] == '/') &&
            (path[1] == '\\' || path[1] == '/'))
            return null;
        // Also catch URI-form UNC (file://server/share/) — Uri parses these distinctly
        // from the leading-double-slash forms above.
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc) return null;
        }
        catch { /* treat as invalid */ return null; }
        // Must be named syncthing.exe (case-insensitive)
        var fileName = Path.GetFileName(path);
        if (!fileName.Equals("syncthing.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path)) return null;
        return path;
    }

    private static Dictionary<string, string> ParseIni(string[] lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '[')
                continue;
            int eq = trimmed.IndexOf('=');
            if (eq > 0)
                dict[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }
        return dict;
    }

    private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        => d.TryGetValue(key, out var v) ? v == "1" : def;

    private static string GetString(Dictionary<string, string> d, string key, string def)
        => d.TryGetValue(key, out var v) ? v : def;

    private static string BoolToStr(bool b) => b ? "1" : "0";

    /// <summary>
    /// Searches common locations for syncthing.exe when it's not next to the tray app.
    /// </summary>
    private static string? DiscoverSyncExe()
    {
        var candidates = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // PATH lookup
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, "syncthing.exe"));
        }

        // Common install locations
        candidates.Add(Path.Combine(localAppData, "Syncthing", "syncthing.exe"));
        candidates.Add(Path.Combine(programFiles, "Syncthing", "syncthing.exe"));

        // Chocolatey
        var chocoDir = Environment.GetEnvironmentVariable("ChocolateyInstall") ?? @"C:\ProgramData\chocolatey";
        candidates.Add(Path.Combine(chocoDir, "bin", "syncthing.exe"));

        // Search user profile for syncthing* folders (depth-limited to 3 levels)
        try
        {
            foreach (var file in Directory.EnumerateFiles(userProfile, "syncthing.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
            }))
            {
                candidates.Add(file);
            }
        }
        catch { /* ACL or IO errors — skip */ }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static int ActionValueToIndex(string value)
    {
        int idx = Array.IndexOf(ClickActionValues, value);
        return idx >= 0 ? idx : 0;
    }

    public static string ActionIndexToValue(int index)
        => index >= 0 && index < ClickActionValues.Length ? ClickActionValues[index] : "none";
}

internal enum AppConfigLoadResult
{
    None,
    Locked,
    Corrupt,
}
