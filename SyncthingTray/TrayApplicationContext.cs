using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncthingTray;

/// <summary>
/// Main application context — manages the system tray icon, context menu,
/// polling timer, and all syncthing lifecycle operations.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncthingApi _api;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _iconTimer;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly OsdToolTip _osd;
    private readonly Icon _syncIcon;
    private readonly Icon _pauseIcon;

    // Hidden form to receive WndProc messages (TaskbarCreated, middle-click)
    private readonly MessageWindow _messageWindow;

    // State
    private bool _intentionalStop;
    private string _syncStatus = "unknown";
    private string _syncDetail = string.Empty;
    private readonly Dictionary<string, bool> _knownDevices = new();
    // v2.3.0: per-device paused state, populated free-of-charge from each
    // /rest/system/connections poll. Used by the Devices submenu and the Synced
    // Folders header coloring. Updated under the same UI/poll thread contract as
    // _knownDevices (UI thread reads in BuildMenu; pool thread writes on poll-tick).
    private readonly Dictionary<string, bool> _devicePaused = new();
    private bool _devicesPollSeeded;
    private int _lastConflictCount;
    private bool _deviceApiFailureNotified;
    // Accessed from both the UI thread (LoadFolders via Settings Save) and the poll
    // thread (PollSyncStatusCore). Concurrent dict removes the enumerate-during-remove
    // race that would throw InvalidOperationException on a plain Dictionary.
    private readonly ConcurrentDictionary<string, int> _folderPullErrors = new();
    // Serializes LoadFolders so a poll-tick lazy reload can't race with a Settings-Save
    // reload. Both threads would otherwise fire duplicate HTTP GETs and compete on
    // the _folders assignment + _folderPullErrors prune.
    private readonly System.Threading.SemaphoreSlim _loadFoldersGate = new(1, 1);
    // volatile: publication ordering with `_folders` — readers that see the latch
    // set must also see the latest `_folders` assignment done immediately before it.
    private volatile bool _foldersLoadedSuccessfully;
    // volatile: read by the poll-tick on a thread-pool thread to decide whether
    // to fire auto-pause / auto-resume HTTP, written by the UI thread from
    // MenuPause / MenuResume / ClearPauseState. The race guard on the UI-marshal
    // paths relies on the read seeing the UI thread's latest write so that a
    // user-initiated MenuPause landing mid-auto-pause doesn't get silently
    // converted into an _autoPaused state by the pool-thread marshal.
    private volatile bool _paused;

    // Absolute UTC time when a timed pause should auto-resume. null = no timed pause
    // active (either not paused at all, or an "Until resumed" pause with no deadline).
    // Using absolute wall-clock time (rather than trusting WinForms Timer.Interval)
    // makes the pause survive sleep/hibernate correctly — MWB pattern.
    private DateTime? _pauseResumeAtUtc;

    // Original duration of the active pause: 0 = untimed, 5 or 30 = minutes.
    // Only meaningful when _paused is true. Used for pause.dat round-trip, OSD
    // text on pause/resume events, and the "Resuming in <N min>" tooltip during
    // a timed pause. (An earlier revision of the pause submenu used this to set
    // .Checked marks; the current menu rebuilds from scratch on every state
    // change so no checkmarks are wired today.)
    private int _activePauseMinutes;

    // Set during RestorePauseStateOnStartup when we inherit an active pause from
    // a prior session. Syncthing's /rest/system/pause is runtime-only state, so
    // after its process restart the daemon has forgotten it was paused. The next
    // poll-tick re-POSTs pause to reconcile Syncthing with our sidecar. The flag
    // is NOT cleared on the POST's 200 response — we wait for a subsequent poll
    // that actually observes `allPaused == true` in /rest/system/connections
    // before dropping the flag. Otherwise a stale pre-reapply snapshot could
    // trip the external-resume branch and silently drop the inherited pause.
    //
    // Volatile: written from both UI thread (MenuPause/ClearPauseState/Restore)
    // and the poll thread (ReapplyInheritedPause). `_foldersLoadedSuccessfully`
    // already uses this same pattern for publication ordering across threads.
    private volatile bool _pauseNeedsReapply;

    // One-shot latch for the "Start browser when Syncthing launches" setting.
    // Set in StartAfterDelay if the checkbox is on; cleared as soon as the first
    // poll confirms Syncthing is reachable and OpenWebUI is fired. Lives outside
    // LaunchSyncthing because the browser should also pop when the tray restarts
    // against an already-running Syncthing (previously the setting silently
    // no-op'd that case).
    private bool _pendingOpenWebUI;

    private readonly System.Windows.Forms.Timer _pauseTimer;
    // Tracks one-shot Timers (startup, first-run, stability) so Dispose can stop +
    // release them if the tray exits before their Tick fires — otherwise fast-exit
    // (<30 s) leaves the WinForms timer queue holding native handles that don't
    // drop until the finalizer runs.
    private readonly List<System.Windows.Forms.Timer> _oneShotTimers = new();
    private string _pauseStatePath = string.Empty;
    private int _connectedCount;
    private int _totalDevices;
    // volatile: same concurrency contract as `_paused` — UI-thread write, pool-thread
    // read in the auto-resume branch. A user-initiated ClearPauseState flipping this
    // to false mid-auto-resume must win the race.
    private volatile bool _autoPaused;
    private int _lastNetworkCategory = -1;

    // Tracks Syncthing's startTime as reported by /rest/system/status. If it
    // changes between consecutive polls while we hold a manual pause, the daemon
    // was restarted (auto-update, crash, sleep/wake) and forgot its runtime pause
    // — re-arm _pauseNeedsReapply so the connections-poll's external-resume
    // branch doesn't silently drop our pause. Empty before the first observation;
    // never reset to empty on transient HTTP failures (so a flaky poll doesn't
    // wipe the baseline and miss a real restart on the tick after).
    private string _lastSyncthingStartTime = string.Empty;

    // Snapshot of folder/device IDs that the tray flipped paused=true. v2.2.39
    // introduces a config-level flip (PUT /rest/config) so resume can restore
    // exactly the IDs we paused — preserves user-intentional folder pauses that
    // existed before MenuPause. Persisted to pause.dat as v3 schema (lines 5-6).
    // Empty list with _paused=true (legacy v2 pause.dat from <= v2.2.38) means
    // "we don't remember what we paused — fall back to flipping every paused
    // entry to unpaused on resume", which is also the unwedge path for users
    // who upgraded from v2.2.38 with config-paused folders.
    //
    // Lock: _pauseSnapshotLock — both UI thread (MenuPause/Resume) and pool
    // thread (ReapplyInheritedPause/auto-pause) read+write the lists.
    private readonly object _pauseSnapshotLock = new();
    private List<string> _trayPausedFolderIds = new();
    private List<string> _trayPausedDeviceIds = new();

    private long _lastUpdateCheck;
    private string _updateAvailable = string.Empty;
    private string _updateRunning = string.Empty;
    private long _lastActionTick;

    // Cached state for change detection
    private bool _lastRunningState;
    private bool _lastPausedState;
    private bool _firstIconPoll = true;
    private long _lastUnexpectedStopAlertTick;

    // Menu rebuild tracking — skip if state unchanged
    private bool _lastMenuRunning;
    private bool _lastMenuPaused;
    private int _lastMenuFolderCount;
    private string _lastMenuUpdate = string.Empty;
    private string _lastMenuStatus = string.Empty;
    private string _lastMenuDetail = string.Empty;
    private int _lastMenuConnected = -1;
    private int _lastMenuTotal = -1;
    private bool _menuBuilt;

    // Cached per-cycle process check (avoid repeated Process.GetProcessesByName allocations)
    private bool _cachedRunning;
    private long _cachedRunningTick;

    // Cached WMI network category (60s TTL — WMI is slow, network category rarely changes)
    private int _cachedNetworkCategory = -1;
    private long _cachedNetworkCategoryTick;
    private bool _wmiFailureNotified;

    // Pre-computed constant strings (avoid repeated interpolation)
    private static readonly string TitleString = $"SyncthingTray v{AppConfig.Version}";

    // v2.3.0 menu coloring (Catppuccin-Mocha-aligned, readable on the dark bg).
    // Applied via Item.Tag = Color, honored by DarkMenuRenderer.OnRenderItemText.
    private static readonly Color HeaderOnlineColor = Color.FromArgb(0xA6, 0xE3, 0xA1);
    private static readonly Color HeaderOfflineColor = Color.FromArgb(0xF3, 0x8B, 0xA8);
    private static readonly Color PausedDimColor = Color.FromArgb(0x80, 0x80, 0x90);

    // Cached tooltip state — only rebuild string when components change
    private string _lastTipStatus = string.Empty;
    private string _lastTipDetail = string.Empty;
    private int _lastTipConnected = -1;
    private int _lastTipTotal = -1;

    // Folder cache
    private FolderInfo[] _folders = [];

    // Maps Syncthing deviceID -> human-readable name. Populated from /rest/config/devices
    // on each LoadFolders call. Cached across calls so a transient roster-fetch failure
    // doesn't suddenly flip device headers back to shortened IDs; only a later successful
    // fetch replaces the cache.
    private Dictionary<string, string> _deviceRoster = new(StringComparer.Ordinal);

    // Syncthing's own device ID for this machine. Used to exclude "self" from the
    // folder.devices lists when grouping, so folders don't all appear under a
    // local-device header. Fetched once from /rest/system/status — immutable for the
    // process lifetime of a given Syncthing instance.
    private string _myDeviceId = string.Empty;

    private bool _disposed;
    private bool _stopping;

    // Cached sync detail to avoid string allocation when percentage unchanged
    private double _lastPct = -1;

    // PID of the syncthing process we launched (for targeted kill)
    private int _launchedPid;

    public TrayApplicationContext()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? AppContext.BaseDirectory;
        _config = new AppConfig(appDir);
        TrayLog.Enable(_config.DiagnosticLogging);
        TrayLog.Info($"SyncthingTray v{AppConfig.Version} starting. Portable={_config.IsPortable}, FirstRun={_config.IsFirstRun}.");
        _api = new SyncthingApi(_config);

        // Load icons from embedded resources
        _syncIcon = LoadEmbeddedIcon("sync.ico");
        _pauseIcon = LoadEmbeddedIcon("pause.ico");

        _osd = new OsdToolTip();

        _trayIcon = new NotifyIcon
        {
            Icon = _syncIcon,
            Text = TitleString,
            Visible = true,
        };
        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _trayIcon.MouseUp += OnTrayMouseUp;

        _messageWindow = new MessageWindow(this);

        // Build initial menu (always, even during startup delay)
        BuildMenu();

        // Timers
        _iconTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _iconTimer.Tick += (_, _) => UpdateTrayIcon();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 10000 };
        _pollTimer.Tick += OnPollTick;

        // Pause auto-resume timer. Interval is re-armed each tick against the
        // absolute UTC deadline so wake-from-sleep doesn't drift the resume time.
        _pauseTimer = new System.Windows.Forms.Timer();
        _pauseTimer.Tick += OnPauseTimerTick;
        _pauseStatePath = Path.Combine(appDir, "pause.dat");
        RestorePauseStateOnStartup();

        // Startup delay — use a timer so the message pump stays alive
        if (_config.StartupDelay > 0)
        {
            var startupTimer = new System.Windows.Forms.Timer { Interval = _config.StartupDelay * 1000 };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                _oneShotTimers.Remove(startupTimer);
                startupTimer.Dispose();
                StartAfterDelay();
            };
            _oneShotTimers.Add(startupTimer);
            startupTimer.Start();
        }
        else
        {
            StartAfterDelay();
        }

        // First-run: open settings. Also seed a stub INI so cancelling the dialog
        // doesn't re-trigger first-run on the next launch forever.
        if (_config.IsFirstRun)
        {
            _config.SeedFirstRunStub();
            var firstRunTimer = new System.Windows.Forms.Timer { Interval = 500 };
            firstRunTimer.Tick += (_, _) =>
            {
                firstRunTimer.Stop();
                _oneShotTimers.Remove(firstRunTimer);
                firstRunTimer.Dispose();
                if (_disposed) return;
                OpenSettings();
            };
            _oneShotTimers.Add(firstRunTimer);
            firstRunTimer.Start();
        }

        // Notify if we replaced a previous instance
        if (Program.KilledPreviousInstance)
            ShowOsd("Replaced previous SyncthingTray instance", 3000);

        // Surface config-load failures that happened before the OSD existed.
        switch (_config.LoadResult)
        {
            case AppConfigLoadResult.Locked:
                ShowOsd("Settings file could not be read — using last session defaults", 6000);
                TrayLog.Warn($"AppConfig.Load: file locked or unreadable: {Path.GetFileName(_config.SettingsFilePath)}");
                break;
            case AppConfigLoadResult.Corrupt:
                ShowOsd("SyncthingTray.ini appears corrupt — defaults loaded; original will be backed up on Save", 8000);
                TrayLog.Warn($"AppConfig.Load: corrupt file detected: {Path.GetFileName(_config.SettingsFilePath)}");
                break;
        }

        // Crash sentinel persisted from the previous run — likely a bad update.
        if (Program.CrashSentinelPersisted)
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            ShowOsd(
                $"Previous version may have crashed. Backup available at {Path.GetFileName(exePath)}.old if you need to roll back.",
                12000);
            TrayLog.Warn("Crash sentinel persisted across launches — previous run did not reach stable uptime.");
        }

        // Stability proof: if we reach 30s without crashing, clear the crash sentinel
        // AND proactively clean up stale .old/.new update artifacts. The artifact
        // cleanup is deliberately deferred — doing it in Program.Main would defeat
        // the sentinel feature on post-update crashes (we'd delete the .old backup
        // before we knew whether the new version was stable).
        var stabilityTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        stabilityTimer.Tick += (_, _) =>
        {
            stabilityTimer.Stop();
            _oneShotTimers.Remove(stabilityTimer);
            stabilityTimer.Dispose();
            if (_disposed) return;
            UpdateDialog.TryDeleteCrashSentinel();
            UpdateDialog.CleanupStaleUpdateArtifacts();
            TrayLog.Info("30s stability reached; crash sentinel + .old/.new cleaned.");
        };
        _oneShotTimers.Add(stabilityTimer);
        stabilityTimer.Start();

        // Sleep/wake: on Resume, the Syncthing process state and the network
        // category (for auto-pause) may both be stale. Drop caches and force an
        // immediate poll so the tray reflects reality before the next 10s tick.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        TrayLog.Info("PowerMode: Resume — invalidating caches + polling.");
        InvalidateRunningCache();

        // SystemEvents.PowerModeChanged fires on a CLR-pooled background thread.
        // Marshal everything to UI — the pause-state reads below MUST NOT race
        // with UI-thread MenuResume/ClearPauseState mutations.
        RunOnUi(() =>
        {
            if (_disposed) return;

            // A 30-min pause through a 2-hr sleep must resume at wake, not 30 min
            // after wake. The poll-tick picks up Syncthing's state eventually, but
            // firing an explicit deadline check here avoids a 10s-window stale menu.
            if (_paused && _pauseResumeAtUtc is DateTime due)
            {
                if (DateTime.UtcNow >= due)
                    MenuResume();
                else
                    StartOrRearmPauseTimer();
            }

            _pollTimer.Stop();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { PollSyncStatusCore(); }
                catch (Exception ex) { TrayLog.Warn("PowerMode Resume poll faulted: " + ex.Message); }
                finally { if (!_disposed) RunOnUi(() => { if (!_disposed) _pollTimer.Start(); }); }
            });
        });
    }

    private void StartAfterDelay()
    {
        if (_disposed) return;

        if (!IsSyncthingRunning())
            LaunchSyncthing();

        // Latch a browser-open on first reachable poll, regardless of whether we
        // cold-launched Syncthing or it was already up. Cleared in the poll-tick.
        if (_config.StartBrowser)
            _pendingOpenWebUI = true;

        _iconTimer.Start();
        _pollTimer.Start();

        // Both calls below do synchronous HTTP (IsReachable probe + /rest/system/status
        // + /rest/config/folders + /rest/config/devices). Running them on the UI thread
        // meant the tray icon appeared but the menu was unresponsive for up to ~1.8 s
        // while Syncthing cold-started. Both functions are already written to be
        // pool-safe (BuildMenu/UpdateTooltip/UpdateTrayIcon all self-marshal via
        // RunOnUi), matching the poll-tick and PowerMode-resume call sites.
        _ = Task.Run(() =>
        {
            try { PollSyncStatusCore(); }
            catch (Exception ex) { TrayLog.Warn("StartAfterDelay PollSyncStatus faulted: " + ex.Message); }
            try { LoadFolders(); }
            catch (Exception ex) { TrayLog.Warn("StartAfterDelay LoadFolders faulted: " + ex.Message); }
        });
    }

    // --- Tray Icon Events ---

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        ExecuteClickAction(_config.DblClickAction);
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
            ExecuteClickAction(_config.MiddleClickAction);
    }

    private void ExecuteClickAction(string action)
    {
        switch (action)
        {
            case "webui":
                OpenWebUI();
                break;
            case "rescan":
                MenuRescanAll();
                break;
            case "pause":
                TogglePause();
                break;
            // "none" or unknown — do nothing
        }
    }

    // --- Icon & Tooltip ---

    private void UpdateTrayIcon()
    {
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(UpdateTrayIcon);
            return;
        }

        bool running = IsSyncthingRunning();

        if (_firstIconPoll || running != _lastRunningState || _paused != _lastPausedState)
        {
            // Unexpected stop detection. Rate-limit so a crash-restart flap
            // during a Syncthing upgrade or reboot doesn't spam OSDs+beeps.
            if (!_firstIconPoll && !running && !_intentionalStop)
            {
                long now = Environment.TickCount64;
                const long AlertCooldownMs = 5 * 60_000; // 5 min between alerts
                if (_lastUnexpectedStopAlertTick == 0 || now - _lastUnexpectedStopAlertTick > AlertCooldownMs)
                {
                    _lastUnexpectedStopAlertTick = now;
                    ShowOsd("Syncthing has stopped unexpectedly!", 5000);
                    if (_config.SoundNotifications)
                        PlaySound(System.Media.SystemSounds.Hand);
                    else
                    {
                        _ = Task.Run(() =>
                        {
                            try { NativeMethods.Beep(300, 300); } catch { /* no speaker */ }
                        });
                    }
                    TrayLog.Warn("Syncthing stopped unexpectedly.");
                }
            }
            else if (running && _lastUnexpectedStopAlertTick != 0)
            {
                _lastUnexpectedStopAlertTick = 0; // reset on recovery
            }

            _intentionalStop = false;
            _lastRunningState = running;
            _lastPausedState = _paused;
            _firstIconPoll = false;

            _trayIcon.Icon = (running && !_paused) ? _syncIcon : _pauseIcon;
            BuildMenu();
        }
    }

    private void UpdateTooltip()
    {
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(UpdateTooltip);
            return;
        }

        // Early exit: skip all string work if none of the tooltip components changed
        if (_syncStatus == _lastTipStatus
            && _syncDetail == _lastTipDetail
            && _connectedCount == _lastTipConnected
            && _totalDevices == _lastTipTotal)
        {
            return;
        }

        _lastTipStatus = _syncStatus;
        _lastTipDetail = _syncDetail;
        _lastTipConnected = _connectedCount;
        _lastTipTotal = _totalDevices;

        var statusSuffix = _syncStatus switch
        {
            "paused" => " \u2014 Paused",
            "idle" => " \u2014 Online",
            "syncing" => " \u2014 Syncing",
            "error" => " \u2014 Error",
            "stopped" => " \u2014 Stopped",
            _ => string.Empty,
        };

        var tip = _syncDetail.Length > 0
            ? string.Concat(TitleString, statusSuffix, " (", _syncDetail, ")")
            : string.Concat(TitleString, statusSuffix);

        // NotifyIcon.Text max is 127 chars
        _trayIcon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // --- Menu Building ---

    private static readonly DarkMenuRenderer DarkRenderer = new();

    private void BuildMenu()
    {
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(BuildMenu);
            return;
        }

        bool running = IsSyncthingRunning();

        // Skip rebuild if nothing that affects menu items has changed
        if (_menuBuilt
            && running == _lastMenuRunning
            && _paused == _lastMenuPaused
            && _folders.Length == _lastMenuFolderCount
            && _updateAvailable == _lastMenuUpdate
            && _syncStatus == _lastMenuStatus
            && _syncDetail == _lastMenuDetail
            && _connectedCount == _lastMenuConnected
            && _totalDevices == _lastMenuTotal)
        {
            return;
        }
        _menuBuilt = true;
        _lastMenuRunning = running;
        _lastMenuPaused = _paused;
        _lastMenuFolderCount = _folders.Length;
        _lastMenuUpdate = _updateAvailable;
        _lastMenuStatus = _syncStatus;
        _lastMenuDetail = _syncDetail;
        _lastMenuConnected = _connectedCount;
        _lastMenuTotal = _totalDevices;

        var oldMenu = _trayIcon.ContextMenuStrip;
        var menu = new ContextMenuStrip { Renderer = DarkRenderer };

        // Title
        var titleItem = menu.Items.Add(TitleString);
        titleItem.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        // Status + WebUI link
        var statusText = running
            ? _syncStatus switch
            {
                "paused" => "Paused",
                "idle" => _syncDetail.Length > 0 ? $"Online — {_syncDetail}" : "Online",
                "syncing" => _syncDetail.Length > 0 ? $"Syncing — {_syncDetail}" : "Syncing...",
                "error" => _syncDetail.Length > 0 ? $"Error — {_syncDetail}" : "Error",
                _ => "Running",
            }
            : "Stopped";
        var statusItem = menu.Items.Add($"Syncthing: {statusText}");
        statusItem.Enabled = false;
        menu.Items.Add(_config.WebUI, null, (_, _) => OpenWebUI());

        // Synced Folders submenu — grouped by device prefix. Any label prefix (first
        // word before a space or underscore) shared by 2+ folders becomes a disabled
        // header with stripped child labels; singletons drop into an unnamed bucket
        // at the bottom. Heuristic matches how users name device-scoped folders
        // (s20_Dcim, s24_Pictures, tablet_Downloads) — no device-roster API call needed.
        if (_folders.Length > 0)
        {
            var folderItem = new ToolStripMenuItem("Synced Folders");
            BuildSyncedFoldersMenu(folderItem);
            menu.Items.Add(folderItem);
        }

        // v2.3.0: per-device pause/resume submenu. Self device is excluded — there's
        // no semantic for "pause the local machine in its own roster". Skipped entirely
        // when the device roster is empty (shows up before first /rest/config/devices
        // success).
        if (_deviceRoster.Count > 0)
        {
            var devItem = new ToolStripMenuItem("Devices");
            BuildDevicesMenu(devItem);
            if (devItem.DropDownItems.Count > 0)
                menu.Items.Add(devItem);
        }

        // Rescan Now
        if (running && !string.IsNullOrEmpty(_config.ApiKey))
            menu.Items.Add("Force Rescan Now", null, (_, _) => MenuRescanAll());

        menu.Items.Add(new ToolStripSeparator());

        // Settings
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Pause/Resume. When paused, the direct Resume item stays top-level for a
        // single-click resume; when not paused, the Pause submenu offers 5 min /
        // 30 min / Until resumed. MWB pattern, adapted so that the common case
        // (click to resume) remains one click rather than a submenu dive.
        if (running)
        {
            if (_paused)
            {
                string resumeLabel = "Resume Syncing";
                if (_pauseResumeAtUtc is DateTime due)
                {
                    var remaining = due - DateTime.UtcNow;
                    if (remaining.TotalSeconds > 0)
                        resumeLabel = $"Resume Syncing (auto in {FormatPauseRemaining(remaining)})";
                }
                // Overclick guard is on the click-path only; direct MenuResume callers
                // (OnPauseTimerTick auto-resume, OnPowerModeChanged, and TogglePause's
                // own guarded path) must not consume the 800 ms budget or the user's
                // very next click after an automatic resume would get "Please wait…".
                menu.Items.Add(resumeLabel, null, (_, _) =>
                {
                    if (IsOverclickGuarded(800)) return;
                    MenuResume();
                });
            }
            else
            {
                // "Until resumed" sits at the top as the primary/default action;
                // timed options live below a separator as secondary choices.
                var pauseItem = new ToolStripMenuItem("Pause Syncing");
                pauseItem.DropDownItems.Add("Until resumed", null, (_, _) => MenuPause(0));
                pauseItem.DropDownItems.Add(new ToolStripSeparator());
                pauseItem.DropDownItems.Add("5 minutes", null, (_, _) => MenuPause(5));
                pauseItem.DropDownItems.Add("30 minutes", null, (_, _) => MenuPause(30));
                menu.Items.Add(pauseItem);
            }
            menu.Items.Add(new ToolStripSeparator());
        }

        // Process controls
        menu.Items.Add("Restart Syncthing", null, (_, _) => MenuRestart());
        if (running)
            menu.Items.Add("Stop Syncthing", null, (_, _) => MenuStop());
        else
            menu.Items.Add("Start Syncthing", null, (_, _) => MenuStart());

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;

        // Dispose old menu — Dispose() cascades to all owned items and submenus
        oldMenu?.Dispose();
    }

    // Short, human-readable handle for a device ID when no friendly name is known.
    // Syncthing device IDs are base32 with hyphen groups — the first group reads
    // cleanly as a stable handle (e.g. "ABCDEFG" for ID "ABCDEFG-HIJKLMN-...").
    private static string ShortenDeviceId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "unknown";
        int dash = id.IndexOf('-');
        if (dash > 0) return id[..dash];
        return id.Length > 7 ? id[..7] : id;
    }

    /// <summary>
    /// Pulls the device roster from /rest/config/devices and this Syncthing instance's
    /// own myID from /rest/system/status. Both are used by BuildSyncedFoldersMenu to
    /// group folders under human-readable device headers. Failures leave the prior
    /// cached values in place — better to show stale names than to drop back to
    /// shortened IDs on a transient blip.
    /// </summary>
    private void LoadDeviceRosterAndMyId()
    {
        // myID only needs fetching once per process — Syncthing's device identity
        // is immutable for a given install (tied to a self-signed cert).
        if (string.IsNullOrEmpty(_myDeviceId))
        {
            try
            {
                var (s, body) = _api.Get("/rest/system/status", timeoutMs: 1500);
                if (s == 200)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("myID", out var idEl))
                        _myDeviceId = idEl.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                TrayLog.Warn("LoadDeviceRosterAndMyId(myID): " + ex.Message);
            }
        }

        try
        {
            var (s, body) = _api.Get("/rest/config/devices", timeoutMs: 1500);
            if (s != 200) return;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                string id = d.TryGetProperty("deviceID", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                string name = d.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0) continue;
                if (name.Length == 0) name = ShortenDeviceId(id);
                next[id] = name;
            }
            _deviceRoster = next;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("LoadDeviceRosterAndMyId(devices): " + ex.Message);
        }
    }

    /// <summary>
    /// Builds the Synced Folders submenu grouped by remote device, using structured
    /// data from Syncthing's config (folder.devices + device roster) rather than
    /// parsing folder labels. A folder shared with N remote devices appears under N
    /// device headers — this is by design so every header's contents are complete.
    /// Folders with no remote devices fall into a "Local only" bucket at the bottom.
    /// </summary>
    private void BuildSyncedFoldersMenu(ToolStripMenuItem folderItem)
    {
        // deviceName -> folders shared with that device (dups across headers are
        // expected for multi-device folders).
        var byDevice = new Dictionary<string, List<FolderInfo>>(StringComparer.Ordinal);
        // v2.3.0: deviceName -> deviceId so the header can look up connection
        // status from _knownDevices for green/red coloring. First-seen-wins on
        // pathological name collisions (Syncthing usually keeps device names
        // unique — collisions are user-induced).
        var nameToId = new Dictionary<string, string>(StringComparer.Ordinal);
        var localOnly = new List<FolderInfo>();

        foreach (var f in _folders)
        {
            // Filter self out. If _myDeviceId isn't known yet (first load failed the
            // system/status fetch), the local device leaks in as its own group — ugly
            // but not broken; resolves itself on the next successful LoadFolders.
            var remotes = f.DeviceIds.Where(id => id.Length > 0 && id != _myDeviceId).ToArray();
            if (remotes.Length == 0)
            {
                localOnly.Add(f);
                continue;
            }
            foreach (var id in remotes)
            {
                var name = _deviceRoster.TryGetValue(id, out var n) && n.Length > 0
                    ? n
                    : ShortenDeviceId(id);
                if (!byDevice.TryGetValue(name, out var list))
                {
                    byDevice[name] = list = new List<FolderInfo>();
                    nameToId[name] = id;
                }
                list.Add(f);
            }
        }

        var orderedDeviceNames = byDevice.Keys
            .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        void EmitFolder(FolderInfo f)
        {
            var path = f.Path;
            var folderId = f.Id;
            var paused = f.Paused;
            var subItem = new ToolStripMenuItem(MenuTextSanitizer.Sanitize(f.Label));
            // v2.3.0: dim paused folder labels so the user can spot at a glance which
            // folders aren't syncing without expanding each submenu.
            if (paused) subItem.Tag = PausedDimColor;
            subItem.DropDownItems.Add("Open Folder", null, (_, _) => OpenFolder(path));
            subItem.DropDownItems.Add("Rescan", null, (_, _) => MenuRescanFolder(folderId));
            // v2.3.3: show BOTH Resume and Pause always, grey the action that's a no-op
            // for the current state. Mirrors the Devices submenu UX from v2.3.2 — the
            // pause state is visible at a glance via which item is enabled.
            var resumeFI = new ToolStripMenuItem("Resume Folder", null,
                (_, _) => TogglePauseFolder(folderId, false));
            resumeFI.Enabled = paused;
            subItem.DropDownItems.Add(resumeFI);
            var pauseFI = new ToolStripMenuItem("Pause Folder", null,
                (_, _) => TogglePauseFolder(folderId, true));
            pauseFI.Enabled = !paused;
            subItem.DropDownItems.Add(pauseFI);
            folderItem.DropDownItems.Add(subItem);
        }

        bool firstGroup = true;
        foreach (var deviceName in orderedDeviceNames)
        {
            if (!firstGroup) folderItem.DropDownItems.Add(new ToolStripSeparator());
            firstGroup = false;
            // v2.3.0: color the device-name header based on connection state.
            // Green when /rest/system/connections reports connected=true; red
            // otherwise. The header is still Enabled=false (no click action).
            var headerColor = HeaderOfflineColor;
            if (nameToId.TryGetValue(deviceName, out var deviceId)
                && _knownDevices.TryGetValue(deviceId, out var connected)
                && connected)
            {
                headerColor = HeaderOnlineColor;
            }
            folderItem.DropDownItems.Add(new ToolStripMenuItem(MenuTextSanitizer.Sanitize(deviceName))
            {
                Enabled = false,
                Tag = headerColor,
            });
            foreach (var f in byDevice[deviceName].OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase))
                EmitFolder(f);
        }

        if (localOnly.Count > 0)
        {
            if (!firstGroup) folderItem.DropDownItems.Add(new ToolStripSeparator());
            firstGroup = false;
            folderItem.DropDownItems.Add(new ToolStripMenuItem("Local only") { Enabled = false });
            foreach (var f in localOnly.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase))
                EmitFolder(f);
        }

        folderItem.DropDownItems.Add(new ToolStripSeparator());
        folderItem.DropDownItems.Add("Refresh List", null, (_, _) =>
        {
            // Pool-thread hop — LoadFolders does synchronous HTTP (up to ~1.8 s on a
            // slow Syncthing). Running on the UI thread froze the menu between click
            // and dismiss. BuildMenu inside LoadFolders self-marshals back to the UI.
            _ = Task.Run(() =>
            {
                try { LoadFolders(); }
                catch (Exception ex) { TrayLog.Warn("Refresh List LoadFolders faulted: " + ex.Message); }
            });
        });
    }

    /// <summary>
    /// v2.3.0 — toggle a single folder's config-level paused state. PATCHes
    /// /rest/config/folders/&lt;id&gt; with {"paused":bool}. Persistent across daemon
    /// restart and reboot (lives in Syncthing's config). Idempotent server-side.
    /// Updates _folders cache + rebuilds menu on success so the label flips
    /// immediately without waiting for the next poll. Failure surfaces an OSD.
    ///
    /// Independent from the global pause snapshot — does NOT touch _paused,
    /// _pauseNeedsReapply, or pause.dat. Per-folder pauses survive reboots
    /// because Syncthing persists `paused` in config (no tray-side bookkeeping).
    /// </summary>
    private void TogglePauseFolder(string folderId, bool newPaused)
    {
        if (string.IsNullOrEmpty(folderId)) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                string body = newPaused ? "{\"paused\":true}" : "{\"paused\":false}";
                status = _api.Patch($"/rest/config/folders/{Uri.EscapeDataString(folderId)}", body).StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn($"TogglePauseFolder {folderId}: {ex.Message}");
                ShowOsd("Folder pause/resume failed", 3000);
                return;
            }
            if (status != 200)
            {
                ShowOsd($"Folder pause/resume failed (HTTP {status})", 3000);
                return;
            }
            // Update cache + rebuild menu on UI thread so the next open shows the
            // flipped Pause/Resume label without waiting for the 10s poll. The
            // _folders array is replaced rather than mutated in place — same
            // publication contract as LoadFolders' assignment.
            RunOnUi(() =>
            {
                if (_disposed) return;
                _folders = _folders
                    .Select(f => f.Id == folderId ? f with { Paused = newPaused } : f)
                    .ToArray();
                // v2.3.3: BuildMenu's cache key doesn't include per-folder paused
                // state, so a folder-pause flip alone doesn't trip the change
                // detector. Invalidate explicitly so the rebuild actually happens
                // and the user sees the flipped label / dim / Resume-enabled state.
                _menuBuilt = false;
                BuildMenu();
                var folder = _folders.FirstOrDefault(f => f.Id == folderId);
                var label = folder?.Label ?? folderId;
                ShowOsd($"{label}: {(newPaused ? "paused" : "resumed")}", 2500);
                TrayLog.Info($"TogglePauseFolder: {folderId} -> paused={newPaused}.");
            });
        });
    }

    /// <summary>
    /// v2.3.0 — toggle a single device's config-level paused state. PATCHes
    /// /rest/config/devices/&lt;id&gt; with {"paused":bool}. Same contract as
    /// TogglePauseFolder. The visible label refresh comes from the next poll's
    /// /rest/system/connections (which carries per-device paused) — no manual
    /// cache update needed; just BuildMenu and OSD here.
    /// </summary>
    private void TogglePauseDevice(string deviceId, bool newPaused)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                string body = newPaused ? "{\"paused\":true}" : "{\"paused\":false}";
                status = _api.Patch($"/rest/config/devices/{Uri.EscapeDataString(deviceId)}", body).StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn($"TogglePauseDevice {deviceId}: {ex.Message}");
                ShowOsd("Device pause/resume failed", 3000);
                return;
            }
            if (status != 200)
            {
                ShowOsd($"Device pause/resume failed (HTTP {status})", 3000);
                return;
            }
            RunOnUi(() =>
            {
                if (_disposed) return;
                // Optimistically update _devicePaused so the menu label flips on
                // next open. The 10s poll will reconcile if Syncthing's persisted
                // state diverges (it shouldn't — PATCH 200 means cfg.Save() done).
                _devicePaused[deviceId] = newPaused;
                // v2.3.3: see TogglePauseFolder for why this is needed — BuildMenu's
                // cache check doesn't track per-device pause state.
                _menuBuilt = false;
                BuildMenu();
                var name = _deviceRoster.TryGetValue(deviceId, out var n) && n.Length > 0 ? n : ShortenDeviceId(deviceId);
                ShowOsd($"{name}: {(newPaused ? "paused" : "resumed")}", 2500);
                TrayLog.Info($"TogglePauseDevice: {deviceId} -> paused={newPaused}.");
            });
        });
    }

    /// <summary>
    /// v2.3.0 — builds the Devices submenu. Each remote device shows as a sub-submenu
    /// with a state-aware Pause/Resume entry. Header text is colored by connection
    /// status (green=connected, red=disconnected) and dimmed when paused. Self device
    /// is excluded — Syncthing has no semantic for pausing the local machine.
    /// </summary>
    private void BuildDevicesMenu(ToolStripMenuItem devItem)
    {
        var entries = _deviceRoster
            .Where(kv => kv.Key.Length > 0 && kv.Key != _myDeviceId)
            .Select(kv => new
            {
                Id = kv.Key,
                Name = kv.Value.Length > 0 ? kv.Value : ShortenDeviceId(kv.Key),
                Connected = _knownDevices.TryGetValue(kv.Key, out var c) && c,
                Paused = _devicePaused.TryGetValue(kv.Key, out var p) && p,
            })
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var d in entries)
        {
            var deviceId = d.Id;
            var paused = d.Paused;
            var sub = new ToolStripMenuItem(MenuTextSanitizer.Sanitize(d.Name));
            // Color: paused devices use the dim grey (intent over reachability — if
            // the user paused it, that's the load-bearing state to surface). Otherwise
            // green/red by connection. Mirrors Synced Folders header convention.
            sub.Tag = paused ? PausedDimColor : (d.Connected ? HeaderOnlineColor : HeaderOfflineColor);
            // v2.3.2: show BOTH Resume and Pause always, with the action that matches
            // the current state greyed out (no-op anyway). Lets you see device state
            // at a glance without opening the web UI; the available action is the
            // enabled one. Resume above Pause per the typical "exit-state-first" idiom
            // (the "way out" of the current state is visually closer to the user's eye).
            var resumeItem = new ToolStripMenuItem("Resume Device", null,
                (_, _) => TogglePauseDevice(deviceId, false));
            resumeItem.Enabled = paused;
            sub.DropDownItems.Add(resumeItem);
            var pauseItem = new ToolStripMenuItem("Pause Device", null,
                (_, _) => TogglePauseDevice(deviceId, true));
            pauseItem.Enabled = !paused;
            sub.DropDownItems.Add(pauseItem);
            devItem.DropDownItems.Add(sub);
        }
    }

    // --- Polling ---

    private async void OnPollTick(object? sender, EventArgs e)
    {
        // Timer tick fires on the UI thread. Stop it so long polls can't stack,
        // then run the actual HTTP work on the thread pool so a slow Syncthing
        // response (cascading 5s timeouts) can't freeze the tray menu/tooltip.
        _pollTimer.Stop();
        try
        {
            await Task.Run(PollSyncStatusCore);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Poll task faulted: " + ex.Message);
        }
        finally
        {
            if (!_disposed)
                _pollTimer.Start();
        }
    }

    /// <summary>
    /// Initial synchronous poll called from StartAfterDelay — populates tooltip and
    /// menu state before the first timer tick fires.
    /// </summary>
    private void PollSyncStatus() => PollSyncStatusCore();

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread when called from a
    /// background thread. No-op on the UI thread.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_messageWindow.InvokeRequired)
        {
            try { _messageWindow.BeginInvoke(action); }
            catch (ObjectDisposedException) { /* handle already destroyed — tray exiting */ }
            catch (InvalidOperationException) { /* handle not yet created — same exit state */ }
        }
        else
        {
            action();
        }
    }

    private void PollSyncStatusCore()
    {
        bool running = IsSyncthingRunning();
        if (!running || string.IsNullOrEmpty(_config.ApiKey))
        {
            _syncStatus = running ? "unknown" : "stopped";
            _syncDetail = string.Empty;
            UpdateTooltip();
            return;
        }

        // One-shot: once Syncthing is running + reachable, honor the "Start browser
        // when Syncthing launches" setting regardless of whether we cold-launched
        // Syncthing or joined an existing process. Fires at most once per tray session.
        // Re-check _config.StartBrowser at fire time so a user who toggled the setting
        // off in Settings during the cold-start window (before Syncthing became
        // reachable) doesn't get a browser popped at them anyway.
        if (_pendingOpenWebUI && _api.IsReachable())
        {
            _pendingOpenWebUI = false;
            if (_config.StartBrowser)
                RunOnUi(OpenWebUI);
        }

        // 1. Sync completion
        bool apiReachable = true;
        try
        {
            var (status, body) = _api.Get("/rest/db/completion");
            if (status < 0) { apiReachable = false; }
            if (status == 200)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("completion", out var compEl) && compEl.TryGetDouble(out double pct))
                {
                    pct = Math.Round(pct, 1);
                    if (pct >= 100)
                    {
                        _syncStatus = _paused ? "paused" : "idle";
                        _syncDetail = _paused ? "Paused" : "";
                    }
                    else
                    {
                        _syncStatus = "syncing";
                        if (pct != _lastPct)
                        {
                            _lastPct = pct;
                            _syncDetail = string.Concat(pct.ToString(System.Globalization.CultureInfo.InvariantCulture), "% complete");
                        }
                    }
                }
                else
                {
                    _syncStatus = _paused ? "paused" : "idle";
                    _syncDetail = _paused ? "Paused" : "Connected";
                }
            }
            else
            {
                _syncStatus = "error";
                _syncDetail = $"API HTTP {status}";
            }
        }
        catch
        {
            _syncStatus = "error";
            _syncDetail = "API unreachable";
            apiReachable = false;
        }

        // Fast-fail: skip remaining API calls if connection refused
        if (!apiReachable)
        {
            UpdateTooltip();
            return;
        }

        // 1.5. Daemon-restart detection.
        //
        // /rest/system/pause is runtime-only — if the Syncthing daemon restarts
        // (auto-update, crash, sleep/wake handoff) while we hold _paused=true,
        // it forgets the pause. Without this guard, the next /rest/system/connections
        // poll would report allPaused=false and the external-resume branch below
        // would silently call ClearPauseState() + delete pause.dat. By comparing
        // startTime across polls and re-arming _pauseNeedsReapply on change, we
        // re-use the same handshake RestorePauseStateOnStartup already relies on.
        try
        {
            var (sts, sbody) = _api.Get("/rest/system/status", timeoutMs: 1500);
            if (sts == 200)
            {
                using var sdoc = JsonDocument.Parse(sbody);
                if (sdoc.RootElement.TryGetProperty("startTime", out var stEl))
                {
                    string startTime = stEl.GetString() ?? string.Empty;
                    if (startTime.Length > 0)
                    {
                        if (_lastSyncthingStartTime.Length > 0
                            && startTime != _lastSyncthingStartTime
                            && _paused
                            && !_pauseNeedsReapply)
                        {
                            TrayLog.Info($"Syncthing daemon restart detected (startTime {_lastSyncthingStartTime} -> {startTime}); re-arming pause reapply.");
                            _pauseNeedsReapply = true;
                        }
                        _lastSyncthingStartTime = startTime;
                    }
                }
            }
            // Non-200 / parse miss: leave _lastSyncthingStartTime intact so a single
            // flaky tick doesn't wipe the baseline and mask a real restart next tick.
        }
        catch
        {
            // Best-effort detection — never let this break the rest of the poll.
        }

        // Lazy folder reload — when the tray starts while Syncthing is off, LoadFolders
        // bails on the probe and leaves _folders empty. Once Syncthing comes up and a
        // poll succeeds, refetch so the "Synced Folders" submenu appears without the
        // user having to open+save Settings or hit Refresh List.
        // Using a success latch (not `_folders.Length == 0`) so legit zero-folder
        // users don't re-fetch every 10s forever.
        if (!_foldersLoadedSuccessfully)
            LoadFolders();

        // 2. Device tracking
        try
        {
            var (status2, body2) = _api.Get("/rest/system/connections");
            if (status2 == 200)
            {
                if (_deviceApiFailureNotified)
                {
                    TrayLog.Info("Device connections endpoint recovered.");
                    _deviceApiFailureNotified = false;
                }

                using var doc2 = JsonDocument.Parse(body2);
                if (doc2.RootElement.TryGetProperty("connections", out var connections))
                {
                    bool allPaused = true;
                    int deviceCount = 0;
                    int connCount = 0;

                    foreach (var prop in connections.EnumerateObject())
                    {
                        string deviceId = prop.Name;
                        var dev = prop.Value;

                        bool connected = dev.TryGetProperty("connected", out var cEl) && cEl.GetBoolean();
                        bool paused = dev.TryGetProperty("paused", out var pEl) && pEl.GetBoolean();

                        if (!paused)
                            allPaused = false;

                        deviceCount++;
                        if (connected)
                            connCount++;

                        if (_devicesPollSeeded && _knownDevices.TryGetValue(deviceId, out bool wasConnected))
                        {
                            if (connected && !wasConnected)
                            {
                                ShowOsd("Device connected", 3000);
                                PlaySound(System.Media.SystemSounds.Asterisk);
                            }
                            else if (!connected && wasConnected)
                            {
                                ShowOsd("Device disconnected", 3000);
                                PlaySound(System.Media.SystemSounds.Exclamation);
                            }
                        }
                        _knownDevices[deviceId] = connected;
                        // v2.3.0: track per-device paused state for the Devices submenu's
                        // Pause/Resume label. Free piggyback on this poll — no extra HTTP.
                        _devicePaused[deviceId] = paused;
                    }
                    _devicesPollSeeded = true;
                    _connectedCount = connCount;
                    _totalDevices = deviceCount;

                    // Inherited-pause reapply runs regardless of deviceCount (zero-device
                    // users — local-only Syncthing — would otherwise loop on
                    // _pauseNeedsReapply forever; v2.2.40 hoist).
                    //
                    // v2.2.40: ApplyConfigPause's PUT response is authoritative (Syncthing's
                    // queue model: 200 fires only after cfg.Save() completes). ReapplyInheritedPause
                    // self-clears the flag on PUT 200. The `if (allPaused) clear flag` confirm-
                    // handshake below remains as a fallback for the rare case where Syncthing's
                    // /rest/system/connections snapshot races our PUT and the flag wasn't cleared
                    // by ReapplyInheritedPause's own success path (e.g., HTTP timeout).
                    if (_pauseNeedsReapply)
                    {
                        if (deviceCount > 0 && allPaused)
                        {
                            // Fallback path: ReapplyInheritedPause didn't clear the flag (PUT
                            // failure or transport error). The connections snapshot now confirms
                            // Syncthing is paused; safe to drop the flag.
                            _pauseNeedsReapply = false;
                            TrayLog.Info("Inherited pause confirmed by Syncthing; reapply flag cleared (handshake fallback).");
                        }
                        else
                        {
                            // Fire ReapplyInheritedPause. Idempotent — ApplyConfigPause skips the
                            // PUT when no field would change. Self-clears the flag on success.
                            // Runs even with deviceCount==0 because the PUT covers folder pause too.
                            ReapplyInheritedPause();
                        }
                    }
                    else if (deviceCount > 0)
                    {
                        // External-resume detection still requires per-device pause data from
                        // /rest/system/connections, so it stays gated on deviceCount > 0.
                        //
                        // _paused read-modify-write + ClearPauseState (which touches
                        // _pauseTimer, a WinForms UI-affine timer) must happen on
                        // the UI thread — this branch runs from PollSyncStatusCore
                        // via `await Task.Run(...)` which drops the sync context.
                        bool pausedNow = allPaused;
                        RunOnUi(() =>
                        {
                            if (_disposed) return;
                            bool wasPaused = _paused;
                            _paused = pausedNow;
                            // External resume (user hit Resume in Web UI, or our deadline
                            // already fired and Syncthing reflects it): drop our timer +
                            // sidecar so a stale deadline doesn't double-fire MenuResume().
                            // Logged at INFO so a silent state transition is grep-able in
                            // tray.log when investigating "sync kicked back on" reports.
                            if (wasPaused && !_paused)
                            {
                                TrayLog.Info("External resume detected (Syncthing reports allPaused=false while we held a pause) — clearing local pause state.");
                                ClearPauseState();
                            }
                        });
                    }

                    // Prune stale device entries — if a device was removed from
                    // Syncthing config, it won't reappear in this response; drop it
                    // from _knownDevices to prevent unbounded growth.
                    var seenDevices = new HashSet<string>(connections.EnumerateObject().Select(p => p.Name));
                    foreach (var id in _knownDevices.Keys.Where(k => !seenDevices.Contains(k)).ToList())
                        _knownDevices.Remove(id);
                    foreach (var id in _devicePaused.Keys.Where(k => !seenDevices.Contains(k)).ToList())
                        _devicePaused.Remove(id);
                }
            }
            else
            {
                // HTTP non-200 — one-shot OSD so the user knows device events
                // (connect/disconnect notifications) have silently stopped.
                if (!_deviceApiFailureNotified)
                {
                    _deviceApiFailureNotified = true;
                    ShowOsd($"Device polling failed (HTTP {status2}) — check API key", 5000);
                    TrayLog.Warn($"Device connections endpoint returned HTTP {status2}.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_deviceApiFailureNotified)
            {
                _deviceApiFailureNotified = true;
                ShowOsd("Device polling failed — check Syncthing status", 5000);
                TrayLog.Warn("Device connections threw: " + ex.Message);
            }
        }

        // 3. Conflict detection (check all known folders)
        // Track per-folder error counts so a transient failure on one folder
        // doesn't reset totalErrors and then spuriously re-fire the "N new errors"
        // OSD when that folder recovers.
        try
        {
            foreach (var folder in _folders)
            {
                try
                {
                    var (fs, fb) = _api.Get($"/rest/db/status?folder={Uri.EscapeDataString(folder.Id)}");
                    if (fs == 200)
                    {
                        using var fd = JsonDocument.Parse(fb);
                        if (fd.RootElement.TryGetProperty("pullErrors", out var peEl) && peEl.TryGetInt32(out int ec))
                            _folderPullErrors[folder.Id] = ec;
                    }
                    // Non-200 or missing pullErrors — keep the prior count rather
                    // than defaulting to 0 (which would fake a recovery).
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"Folder status read failed for {folder.Id}: {ex.Message}");
                }
            }

            int totalErrors = 0;
            foreach (var kv in _folderPullErrors) totalErrors += kv.Value;

            if (totalErrors > _lastConflictCount && _lastConflictCount >= 0)
            {
                int newErrs = totalErrors - _lastConflictCount;
                ShowOsd($"{newErrs} file error(s) detected \u2014 check Web UI", 5000);
                PlaySound(System.Media.SystemSounds.Hand);
            }
            _lastConflictCount = totalErrors;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Conflict detection outer catch: " + ex.Message);
        }

        // 4. Network auto-pause
        if (_config.NetworkAutoPause)
        {
            try
            {
                int cat = GetNetworkCategory();
                if (cat == -1)
                {
                    // WMI query failed — one-shot warn so user knows auto-pause is asleep
                    if (!_wmiFailureNotified)
                    {
                        _wmiFailureNotified = true;
                        ShowOsd("Network auto-pause: WMI unavailable — still syncing", 5000);
                        TrayLog.Warn("Network auto-pause disabled: WMI returned no NetworkCategory.");
                    }
                }
                else if (cat != _lastNetworkCategory && _lastNetworkCategory != -1)
                {
                    // Auto-pause / auto-resume: HTTP on this (pool) thread, then
                    // marshal the UI-affine state mutations (_pauseTimer.Stop,
                    // UpdateTrayIcon, BuildMenu) onto the UI thread as a single atom.
                    // WinForms Timer.Stop() off-UI is undefined behavior — historically
                    // this block's 99/100 silent success is exactly the symptom.
                    if (cat == 0 && !_paused)
                    {
                        // v2.2.39: config-level pause via PUT /rest/config (same as MenuPause).
                        // Snapshot what we flip so auto-resume restores exactly that set.
                        var (flippedFolders, flippedDevices, ok) = ApplyConfigPause(targetPaused: true);
                        // v2.2.40: If everything was already config-paused (e.g., user paused
                        // all folders via Web UI before network changed), nothing was flipped —
                        // we have no snapshot to restore on auto-resume. Stamping
                        // _autoPaused/_paused with an empty snapshot would let a subsequent
                        // user-initiated MenuResume hit its empty-snapshot fallback ("unpause
                        // everything") and silently unpause the user's intentional pauses.
                        // Treat zero-flip as "nothing to do" and skip the state mutation.
                        bool nothingToFlip = flippedFolders.Count == 0 && flippedDevices.Count == 0;
                        if (ok && !nothingToFlip)
                        {
                            RunOnUi(() =>
                            {
                                if (_disposed) return;
                                // CAS guard: if the user flipped _paused true
                                // (MenuPause) between the pool-thread read above
                                // and this marshal, their action wins. Stamping
                                // _autoPaused on their manual pause would cause
                                // the next network transition to auto-resume over
                                // their intent.
                                if (_paused) return;
                                _paused = true;
                                _autoPaused = true;
                                lock (_pauseSnapshotLock)
                                {
                                    _trayPausedFolderIds = flippedFolders;
                                    _trayPausedDeviceIds = flippedDevices;
                                }
                                // Auto-pause has no deadline — it resumes on the next network
                                // category transition, not on a clock. Unify the state so the
                                // menu renders "Resume Syncing" (no countdown).
                                _activePauseMinutes = 0;
                                _pauseResumeAtUtc = null;
                                _pauseTimer.Stop();
                                PersistPauseState();
                                UpdateTrayIcon();
                                BuildMenu();
                                ShowOsd("Auto-paused: public network detected", 3000);
                            });
                        }
                        else if (ok)
                        {
                            // Nothing was active to pause — already config-paused. Don't claim
                            // _autoPaused state (would trap user into the empty-snapshot
                            // unwedge path on manual Resume).
                            TrayLog.Info("Auto-pause: nothing to flip (everything already config-paused); skipping state mutation.");
                        }
                        else
                        {
                            ShowOsd("Auto-pause FAILED — still syncing on public network", 6000);
                            TrayLog.Warn("Auto-pause: ApplyConfigPause failed; _paused not flipped.");
                        }
                    }
                    else if (cat != 0 && _autoPaused)
                    {
                        // v2.2.39: restore exactly the IDs we flipped on auto-pause.
                        // v2.2.40: bail if snapshot is empty. Manual MenuResume's "flip everything
                        // currently paused" fallback is appropriate as a user-initiated unwedge —
                        // but auto-resume firing the same fallback silently would unpause folders
                        // the user had intentionally paused before NetworkAutoPause kicked in. Auto
                        // is silent + automatic, so it must not over-reach. User can manually
                        // Resume Syncing if they want to clear inherited pause-state.
                        HashSet<string>? folderFilter = null;
                        HashSet<string>? deviceFilter = null;
                        bool haveSnapshot;
                        lock (_pauseSnapshotLock)
                        {
                            haveSnapshot = _trayPausedFolderIds.Count > 0 || _trayPausedDeviceIds.Count > 0;
                            if (haveSnapshot)
                            {
                                folderFilter = new HashSet<string>(_trayPausedFolderIds, StringComparer.Ordinal);
                                deviceFilter = new HashSet<string>(_trayPausedDeviceIds, StringComparer.Ordinal);
                            }
                        }
                        // No snapshot to restore — leave _autoPaused in place so a future
                        // public-network transition won't try to re-pause on top, and let
                        // the user click Resume Syncing to explicitly unwedge. Fall through
                        // to update _lastNetworkCategory below; ok stays false so we won't
                        // touch state.
                        bool ok;
                        if (!haveSnapshot)
                        {
                            TrayLog.Info("Auto-resume: snapshot empty; skipping silent unpause-everything (user must manually resume).");
                            ok = false;
                        }
                        else
                        {
                            (_, _, ok) = ApplyConfigPause(targetPaused: false, folderIdFilter: folderFilter, deviceIdFilter: deviceFilter);
                        }
                        if (ok)
                        {
                            RunOnUi(() =>
                            {
                                if (_disposed) return;
                                // CAS guard: if the user flipped the state (either
                                // ClearPauseState dropped _autoPaused or MenuResume
                                // cleared _paused) between the pool-thread read and
                                // this marshal, there's nothing left to auto-resume.
                                if (!_autoPaused || !_paused) return;
                                ClearPauseState();
                                UpdateTrayIcon();
                                BuildMenu();
                                ShowOsd("Auto-resumed: private network detected", 3000);
                            });
                        }
                        else if (haveSnapshot)
                        {
                            // Genuine ApplyConfigPause failure (haveSnapshot==true means we
                            // intended to flip but the PUT or GET errored). Differentiated
                            // from the empty-snapshot skip above (no OSD, just info-log).
                            ShowOsd("Auto-resume FAILED — still paused", 5000);
                            TrayLog.Warn("Auto-resume: ApplyConfigPause failed.");
                        }
                    }
                }
                _lastNetworkCategory = cat;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Auto-pause error: " + ex.Message);
            }
        }

        // 5. Auto-update check (24h rate limit)
        if (_config.AutoCheckUpdates && !string.IsNullOrEmpty(_config.ApiKey) && running)
        {
            long elapsed = Environment.TickCount64 - _lastUpdateCheck;
            if (_lastUpdateCheck == 0 || elapsed > 86400000)
                CheckForUpdate();
        }

        UpdateTooltip();
    }

    // --- Update Check ---

    /// <summary>
    /// Called from both the poll-tick (pool thread) and MenuCheckUpdate (now also
    /// pool thread via Task.Run). HTTP runs here; _updateAvailable/_updateRunning
    /// writes + the BuildMenu marshal back to UI.
    /// </summary>
    private void CheckForUpdate()
    {
        _lastUpdateCheck = Environment.TickCount64;
        try
        {
            var (status, body) = _api.Get("/rest/system/upgrade");
            if (status != 200) return;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool newer = root.TryGetProperty("newer", out var nEl) && nEl.GetBoolean();
            string latest = root.TryGetProperty("latest", out var lEl) ? lEl.GetString() ?? string.Empty : string.Empty;
            string running = root.TryGetProperty("running", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;

            RunOnUi(() =>
            {
                if (_disposed) return;
                _updateRunning = running;
                if (newer && latest.Length > 0)
                {
                    _updateAvailable = latest;
                    BuildMenu();
                    ShowOsd($"Syncthing update available: {latest} (current: {running})", 5000);
                }
                else
                {
                    _updateAvailable = string.Empty;
                    BuildMenu();
                }
            });
        }
        catch { /* best-effort */ }
    }

    private void MenuCheckUpdate()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required \u2014 set in Settings", 3000);
            return;
        }

        if (_updateAvailable.Length > 0)
        {
            DoUpdate();
            return;
        }

        ShowOsd("Checking for updates...", 2000);
        // CheckForUpdate fires its own HTTP; hop to pool so the click doesn't
        // freeze the menu. Follow-up OSD reads _updateAvailable on UI thread.
        _ = Task.Run(() =>
        {
            CheckForUpdate();
            RunOnUi(() =>
            {
                if (_disposed) return;
                if (_updateAvailable.Length == 0)
                {
                    var msg = "Syncthing is up to date";
                    if (_updateRunning.Length > 0)
                        msg += $" ({_updateRunning})";
                    ShowOsd(msg, 3000);
                }
            });
        });
    }

    private void DoUpdate()
    {
        if (_updateAvailable.Length == 0) return;
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                status = _api.Post("/rest/system/upgrade").StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("DoUpdate POST threw: " + ex.Message);
                ShowOsd("Upgrade request failed", 5000);
                return;
            }
            if (status == 200)
            {
                RunOnUi(() =>
                {
                    if (_disposed) return;
                    ShowOsd($"Syncthing upgrading to {_updateAvailable}...", 5000);
                    _updateAvailable = string.Empty;
                    BuildMenu();
                });
            }
            else
            {
                ShowOsd($"Upgrade failed (HTTP {status})", 5000);
            }
        });
    }

    // --- Folder Loading ---

    private void LoadFolders()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _folders = [];
            BuildMenu();
            return;
        }

        // Non-blocking gate — if another thread is already inside LoadFolders (UI
        // Settings-Save vs poll-tick lazy reload), skip. The in-flight call will
        // publish results via `_folders = ...`. No need to duplicate the HTTP GET.
        if (!_loadFoldersGate.Wait(0))
        {
            TrayLog.Info("LoadFolders: skipped — concurrent call in flight.");
            return;
        }
        try
        {
            // Fast probe — skip the 5s HttpClient timeout on the UI thread when Syncthing
            // isn't listening. Called from the Save-settings callback, so a 5s freeze here
            // showed up to the user as "Save button lags then closes".
            if (!_api.IsReachable())
            {
                // Drop the stale list — menu shouldn't show ghost folders from before
                // Syncthing died. The poll-tick lazy-reload will refill it when
                // Syncthing comes back.
                _folders = [];
                _foldersLoadedSuccessfully = false;
                BuildMenu();
                return;
            }

            try
            {
                var (status, body) = _api.Get("/rest/config/folders", timeoutMs: 1500);
                if (status == 200)
                {
                    using var doc = JsonDocument.Parse(body);
                    var list = new List<FolderInfo>();
                    foreach (var folder in doc.RootElement.EnumerateArray())
                    {
                        string fId = folder.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        string fLabel = folder.TryGetProperty("label", out var lblEl) ? lblEl.GetString() ?? string.Empty : string.Empty;
                        string fPath = folder.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

                        // Parse the devices[] array — each entry is {deviceID, introducedBy, ...}.
                        // We keep only the IDs; names resolve via the device roster fetched below.
                        var deviceIds = new List<string>();
                        if (folder.TryGetProperty("devices", out var devsEl) && devsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var dev in devsEl.EnumerateArray())
                            {
                                if (dev.TryGetProperty("deviceID", out var didEl))
                                {
                                    var did = didEl.GetString();
                                    if (!string.IsNullOrEmpty(did)) deviceIds.Add(did);
                                }
                            }
                        }

                        // v2.3.0: read folder.paused so the per-folder Pause/Resume submenu
                        // entry can render the right state-aware label without an extra GET.
                        bool fPaused = folder.TryGetProperty("paused", out var pEl) && pEl.ValueKind == JsonValueKind.True;

                        string displayName = fLabel.Length > 0 ? fLabel : fId;
                        if (fPath.Length > 0)
                            list.Add(new FolderInfo(fId, displayName, fPath, deviceIds.ToArray(), fPaused));
                    }
                    _folders = list.ToArray();

                    // Refresh the device roster + myID alongside the folder list so the
                    // menu can group by human-readable device name. Failures don't abort
                    // the folder load — degraded mode is grouping by shortened device ID.
                    LoadDeviceRosterAndMyId();

                    // Success latch — even an empty folder list counts. Prevents the poll
                    // tick from re-firing LoadFolders every 10s for zero-folder users.
                    _foldersLoadedSuccessfully = true;
                    TrayLog.Info($"LoadFolders: HTTP 200, {_folders.Length} folder(s) loaded, {_deviceRoster.Count} device(s) in roster.");

                    // Prune per-folder error tracking for folders no longer configured —
                    // otherwise _folderPullErrors grows unbounded over months of churn.
                    var live = new HashSet<string>(_folders.Select(f => f.Id));
                    foreach (var id in _folderPullErrors.Keys.Where(k => !live.Contains(k)).ToList())
                        _folderPullErrors.TryRemove(id, out _);
                }
                else
                {
                    // Non-200 (401 after token rotate, 500 during Syncthing restart).
                    // Reset latch so the next poll tick retries — otherwise a single
                    // successful load + a later auth change strands us with ghost folders
                    // forever.
                    _foldersLoadedSuccessfully = false;
                    TrayLog.Warn($"LoadFolders: HTTP {status}; will retry on next poll.");
                }
            }
            catch (Exception ex)
            {
                _foldersLoadedSuccessfully = false;
                TrayLog.Warn("LoadFolders threw: " + ex.Message);
            }

            BuildMenu();
        }
        finally
        {
            // Swallow ObjectDisposedException on tray-exit race: if Dispose fires
            // while a pool thread is still inside LoadFolders, _loadFoldersGate can
            // be disposed before we reach here. The exception would be caught by
            // the outer Task.Run catch but emit a misleading "LoadFolders faulted"
            // log during normal clean exit.
            try { _loadFoldersGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    // --- Menu Actions ---

    private void OpenWebUI()
    {
        var url = _config.WebUI;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowOsd("Web UI URL is invalid — check Settings", 4000);
            return;
        }
        try
        {
            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- url is validated as http/https via Uri.TryCreate on line 1408 above; handed to Windows default browser
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowOsd("Could not open browser — check Windows default browser", 5000);
            TrayLog.Warn("OpenWebUI Process.Start failed: " + ex.Message);
        }
    }

    // Windows path APIs treat '\' and '/' as interchangeable separators, so
    // `//srv/s`, `\/srv\s`, `/\srv\s` all normalise to UNC alongside `\\srv\s`.
    // Prefix-string matching (StartsWith(@"\\")) only catches 1 of the 4 forms;
    // predicate on character-class membership at positions [0] and [1] instead.
    internal static bool IsUncPath(string path)
    {
        return path.Length >= 2
            && (path[0] == '\\' || path[0] == '/')
            && (path[1] == '\\' || path[1] == '/');
    }

    private void OpenFolder(string path)
    {
        // Reject UNC, URI schemes (ms-settings:, shell:appsfolder\..., etc.),
        // and non-fully-qualified paths before handing anything to the shell.
        // Syncthing's /rest/config/folders is the source of truth for `path`,
        // but defense-in-depth: a hostile peer-shared folder definition or a
        // corrupted config could smuggle a protocol handler path through.
        // UNC is refused (not just skipped) because Directory.Exists is bypassed
        // on UNC for timeout reasons — we don't want Process.Start racing the
        // shell against an unreachable share either.
        int colonIdx = path.IndexOf(':');
        bool isUnc = IsUncPath(path);
        bool hasUriColon = colonIdx >= 0 && colonIdx != 1;
        if (isUnc || hasUriColon || !Path.IsPathFullyQualified(path))
        {
            string safe = path.Length > 80 ? path.Substring(0, 80) + "..." : path;
            ShowOsd($"Folder path rejected: {safe}", 3500);
            TrayLog.Warn($"OpenFolder rejected suspicious path: {path}");
            return;
        }
        if (!Directory.Exists(path))
        {
            ShowOsd($"Folder not found: {path}", 3000);
            return;
        }

        // Fire on a pool thread so the context menu dismisses immediately.
        // UseShellExecute=true lets the Windows shell reuse any already-running
        // Explorer process — meaningfully faster than spawning explorer.exe via
        // a fresh Process.Start(fileName: "explorer.exe") which always forks a
        // new process (100-500 ms on a cold cache).
        _ = Task.Run(() =>
        {
            try
            {
                // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- path is defense-in-depth validated at lines 1445-1453: rejects UNC, URI schemes (ms-settings:, shell:appsfolder:...), and non-fully-qualified paths before reaching the shell
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RunOnUi(() => ShowOsd($"Failed to open folder: {ex.Message}", 4000));
                TrayLog.Warn($"OpenFolder({path}) threw: {ex.Message}");
            }
        });
    }

    private void OpenSettings()
    {
        // Single instance of settings
        foreach (Form f in Application.OpenForms)
        {
            if (f is SettingsForm sf)
            {
                sf.Activate();
                return;
            }
        }

        using var form = new SettingsForm(
            _config, _api, _osd,
            // Apply: lightweight — rebuild the menu so local-settings labels (WebUI
            // URL etc.) reflect what the user just saved. No HTTP refetch.
            onApplied: BuildMenu,
            // Save: full path — refetch folder list + OSD confirmation.
            // Hop to a pool thread so Save-click doesn't block the dialog close
            // on LoadFolders's HTTP round-trip (up to 1.5 s + IsReachable probe
            // ~300 ms). Matches the threading contract at the other LoadFolders
            // call sites (poll-tick Task.Run(PollSyncStatusCore)).
            onSaved: () =>
            {
                _ = Task.Run(() =>
                {
                    LoadFolders();
                    RunOnUi(() => ShowOsd("Settings saved", 3000));
                });
            });
        form.ShowDialog();
    }

    private bool IsOverclickGuarded(int cooldownMs = 1500)
    {
        if (_stopping) return true;
        long now = Environment.TickCount64;
        if (now - _lastActionTick < cooldownMs)
        {
            ShowOsd("Please wait...", 1500);
            return true;
        }
        _lastActionTick = now;
        return false;
    }

    // Click-path (double-click / middle-click) default is untimed pause — preserves
    // the muscle memory from pre-timed-pause versions where a single click toggled
    // indefinite pause/resume.
    private void TogglePause()
    {
        if (IsOverclickGuarded(800)) return;
        if (_paused)
            MenuResume();
        else
            MenuPause(0);
    }

    /// <summary>
    /// Enter (or re-arm) a pause. minutes = 0 means "Until resumed" (no deadline).
    /// Click-handler entry point — runs HTTP on a pool thread so a 5-second HttpClient
    /// timeout on a dead Syncthing can't freeze the tray menu. State mutations + UI
    /// updates marshal back via RunOnUi.
    /// </summary>
    private void MenuPause(int minutes)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for pause \u2014 set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            // v2.2.39: atomic config flip via PUT /rest/config — captures which
            // folders/devices were not-paused and flips them. The legacy
            // /rest/system/pause endpoint only mutates DEVICES (per Syncthing v2
            // source); using the asymmetric pair (system/pause + system/resume)
            // wedged users when folders ended up config-paused some other way
            // (e.g. web UI Actions). PUT the whole config so we own both axes.
            // Returned lists become the snapshot for resume so user-intentional
            // folder pauses outside our flip set are preserved.
            var (flippedFolders, flippedDevices, ok) = ApplyConfigPause(targetPaused: true);
            if (!ok)
            {
                ShowOsd("Failed to pause syncing", 3000);
                return;
            }
            // v2.2.42: same empty-snapshot guard as auto-pause. If everything is
            // already config-paused (e.g. user manually paused all folders via
            // Syncthing web UI), MenuPause has nothing to flip — claiming _paused
            // with an empty snapshot would trap the next MenuResume click into the
            // "unpause everything paused" upgrade-unwedge fallback, silently
            // un-pausing folders the user had intentionally paused. Show a clear
            // OSD instead and leave state untouched.
            if (flippedFolders.Count == 0 && flippedDevices.Count == 0)
            {
                ShowOsd("Already paused — nothing to do", 3000);
                TrayLog.Info("MenuPause: nothing to flip (everything already config-paused); _paused not stamped to avoid empty-snapshot resume trap.");
                return;
            }
            RunOnUi(() =>
            {
                if (_disposed) return;
                _paused = true;
                _activePauseMinutes = minutes;
                lock (_pauseSnapshotLock)
                {
                    _trayPausedFolderIds = flippedFolders;
                    _trayPausedDeviceIds = flippedDevices;
                }
                _pauseTimer.Stop();
                if (minutes > 0)
                {
                    _pauseResumeAtUtc = DateTime.UtcNow.AddMinutes(minutes);
                    StartOrRearmPauseTimer();
                }
                else
                {
                    _pauseResumeAtUtc = null;
                }
                PersistPauseState();
                UpdateTrayIcon();
                BuildMenu();
                ShowOsd($"Syncing paused ({FormatPauseDuration(minutes)})", 3000);
            });
        });
    }

    /// <summary>
    /// Resume syncing. Click-handler entry point — HTTP on pool thread, state + UI
    /// on UI thread. Also called from OnPauseTimerTick and OnPowerModeChanged (both
    /// UI-thread callers — the Task.Run hop there is redundant but safe).
    /// </summary>
    private void MenuResume()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for resume \u2014 set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            // v2.2.39: restore exactly the IDs MenuPause snapshotted — preserves
            // user-intentional folder/device pauses that were already paused
            // before our flip. Empty snapshot = legacy v2 pause.dat from older
            // versions OR fresh state with config-pause-wedge: fall back to
            // "unpause everything currently paused" so upgraders get unwedged
            // on first Resume click.
            HashSet<string>? folderFilter = null;
            HashSet<string>? deviceFilter = null;
            lock (_pauseSnapshotLock)
            {
                if (_trayPausedFolderIds.Count > 0 || _trayPausedDeviceIds.Count > 0)
                {
                    folderFilter = new HashSet<string>(_trayPausedFolderIds, StringComparer.Ordinal);
                    deviceFilter = new HashSet<string>(_trayPausedDeviceIds, StringComparer.Ordinal);
                }
            }

            var (_, _, ok) = ApplyConfigPause(
                targetPaused: false,
                folderIdFilter: folderFilter,
                deviceIdFilter: deviceFilter);

            if (!ok)
            {
                ShowOsd("Failed to resume syncing", 3000);
                return;
            }

            RunOnUi(() =>
            {
                if (_disposed) return;
                ClearPauseState();
                UpdateTrayIcon();
                BuildMenu();
                ShowOsd("Syncing resumed", 3000);
            });
        });
    }

    /// <summary>
    /// Reset all pause-related state. Called after a successful resume (manual
    /// or auto) and when the poll-tick detects Syncthing was resumed externally.
    /// </summary>
    private void ClearPauseState()
    {
        _paused = false;
        _autoPaused = false;
        _activePauseMinutes = 0;
        _pauseResumeAtUtc = null;
        _pauseNeedsReapply = false;
        _pauseTimer.Stop();
        lock (_pauseSnapshotLock)
        {
            _trayPausedFolderIds = new List<string>();
            _trayPausedDeviceIds = new List<string>();
        }
        DeletePauseStateFile();
    }

    private void StartOrRearmPauseTimer()
    {
        if (_pauseResumeAtUtc is not DateTime due) return;
        var msUntil = (long)(due - DateTime.UtcNow).TotalMilliseconds;
        // WinForms Timer.Interval must be ≥ 1. Clamp short deadlines to 1s so the
        // tick handler runs soon and fires the auto-resume path.
        _pauseTimer.Interval = (int)Math.Max(1000, Math.Min(int.MaxValue, msUntil));
        _pauseTimer.Start();
    }

    private void OnPauseTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _pauseTimer.Stop();
        if (_pauseResumeAtUtc is not DateTime due)
            return;

        if (DateTime.UtcNow >= due)
        {
            TrayLog.Info("Pause timer elapsed — auto-resuming.");
            MenuResume();
        }
        else
        {
            // Early tick (shouldn't happen, but guard — a Timer with extreme Interval
            // values can fire prematurely). Re-arm for the remaining slice.
            StartOrRearmPauseTimer();
        }
    }

    private static string FormatPauseDuration(int minutes) => minutes switch
    {
        0 => "until resumed",
        1 => "1 minute",
        _ => $"{minutes} minutes",
    };

    private static string FormatPauseRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 2)
            return $"{(int)Math.Ceiling(remaining.TotalMinutes)} min";
        if (remaining.TotalSeconds >= 60)
            return "1 min";
        var secs = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{secs} sec";
    }

    // pause.dat schema v3 marker on line 4. Lines 5/6 carry the tray-paused
    // folder/device ID lists (pipe-separated). Older v2 readers (lines 1-3 only)
    // safely ignore the extra lines; v3 readers detect the marker and load the
    // snapshot. Pipe is safe because Syncthing folder IDs are 5+5 lowercase
    // hex (e.g. "agwci-hd37s") and device IDs are dash-separated base32 — neither
    // can contain '|'.
    private const string PauseStateSchemaV3 = "v3";

    private void PersistPauseState()
    {
        try
        {
            if (!_paused)
            {
                DeletePauseStateFile();
                return;
            }
            // Line 1: minutes (0 = untimed). Line 2: resume-at ticks (empty for untimed).
            // Line 3: auto-pause flag (1 if this pause was triggered by network auto-pause,
            // 0 if user-initiated). The auto flag lets RestorePauseStateOnStartup drop an
            // inherited auto-pause when the network is no longer public — otherwise a
            // reboot mid-auto-pause would leave sync stuck paused even after returning
            // to a private network.
            // Line 4: schema marker "v3" (introduced in SyncthingTray v2.2.39).
            // Line 5: pipe-separated folder IDs the tray flipped paused=true.
            // Line 6: pipe-separated device IDs the tray flipped paused=true.
            var ticksLine = _pauseResumeAtUtc is DateTime due ? due.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            var autoLine = _autoPaused ? "1" : "0";
            string folderLine, deviceLine;
            lock (_pauseSnapshotLock)
            {
                folderLine = string.Join('|', _trayPausedFolderIds);
                deviceLine = string.Join('|', _trayPausedDeviceIds);
            }
            var body = $"{_activePauseMinutes}\n{ticksLine}\n{autoLine}\n{PauseStateSchemaV3}\n{folderLine}\n{deviceLine}";
            // v2.2.40: atomic write — write to .tmp, then File.Replace with a `.bak`
            // backup so AV-lock or in-place-replace failures still leave a recoverable
            // copy of the prior pause.dat. Without the backup name, a Replace failure
            // mid-operation could leave the destination deleted (the very scenario the
            // atomic write was meant to prevent — Replace deletes-before-renames).
            var tempPath = _pauseStatePath + ".tmp";
            var backupPath = _pauseStatePath + ".bak";
            File.WriteAllText(tempPath, body);
            if (File.Exists(_pauseStatePath))
                File.Replace(tempPath, _pauseStatePath, destinationBackupFileName: backupPath);
            else
                File.Move(tempPath, _pauseStatePath);
            // Best-effort cleanup of the backup on success — keeping it around forever
            // would let stale state stick after several pause/resume cycles. The .bak
            // is only useful between the failed-Replace moment and the next successful
            // PersistPauseState, so dropping it after success is safe.
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"PersistPauseState: {ex.Message}");
            // Best-effort cleanup of orphan tmp on any failure path.
            try { var t = _pauseStatePath + ".tmp"; if (File.Exists(t)) File.Delete(t); } catch { }
            // Recovery hint: a .bak file may exist from a prior successful write whose
            // post-Replace cleanup Delete failed (e.g., AV held it open). It does NOT
            // contain pre-write state for THIS failure (.NET's File.Replace doesn't
            // create the backup until the rename half completes). Log it so support
            // can decide whether the .bak is worth recovering, but don't auto-restore.
            try { var b = _pauseStatePath + ".bak"; if (File.Exists(b)) TrayLog.Warn("PersistPauseState: stale .bak file present at " + b + " (from a prior cycle whose cleanup failed; predates this failure)."); } catch { }
        }
    }

    private void DeletePauseStateFile()
    {
        try
        {
            if (File.Exists(_pauseStatePath)) File.Delete(_pauseStatePath);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"DeletePauseStateFile: {ex.Message}");
        }
    }

    /// <summary>
    /// Called once from the ctor. If pause.dat exists, re-attach the timer and
    /// mark that Syncthing needs to be re-paused on the first successful poll
    /// (Syncthing loses its runtime pause state across its own restart / a
    /// reboot). Expired timed pauses are dropped so we don't re-pause a deadline
    /// that's already in the past.
    /// </summary>
    // pause.dat has a fixed 3-line schema (minutes + ticks + auto flag). The real
    // file is ~20 bytes. A tampered / runaway writer could balloon it to GB — we
    // cap the read at 256 KB so a boot-time OOM can't brick the tray.
    private const long PauseStateMaxBytes = 256 * 1024;

    private void RestorePauseStateOnStartup()
    {
        try
        {
            if (!File.Exists(_pauseStatePath)) return;
            var info = new FileInfo(_pauseStatePath);
            if (info.Length > PauseStateMaxBytes)
            {
                TrayLog.Warn($"RestorePauseStateOnStartup: pause.dat is {info.Length} bytes (cap {PauseStateMaxBytes}) — deleting.");
                DeletePauseStateFile();
                return;
            }
            var lines = File.ReadAllLines(_pauseStatePath);
            if (lines.Length < 1 || !int.TryParse(lines[0], out int minutes))
            {
                TrayLog.Warn("RestorePauseStateOnStartup: malformed pause.dat — deleting.");
                DeletePauseStateFile();
                return;
            }

            // Parse the auto-paused flag (line 3). Missing = legacy 2-line format from
            // earlier v2.2.12 builds — default to false (treat as manual pause).
            bool wasAutoPaused = lines.Length >= 3 && lines[2].Trim() == "1";

            // Network-adaptive restore: if this pause was triggered by auto-pause on
            // public wifi but we're now on a private/domain network, the pause reason
            // is already gone. Drop the inherited pause so sync resumes automatically
            // on boot rather than leaving the user manually clicking Resume. WMI
            // unavailable (cat == -1) means we can't confirm network state — conservative
            // default is to keep the pause and let the user decide.
            if (wasAutoPaused && _config.NetworkAutoPause)
            {
                int cat = GetNetworkCategory();
                // NetworkCategory: 0 = Public, 1 = Private, 2 = DomainAuthenticated.
                if (cat > 0)
                {
                    TrayLog.Info($"RestorePauseStateOnStartup: inherited auto-pause but network is now private (cat={cat}) — dropping stale pause.");
                    DeletePauseStateFile();
                    return;
                }
                // cat == 0 (still public) or cat == -1 (WMI down): fall through, restore pause.
            }

            if (minutes > 0)
            {
                if (lines.Length < 2 || !long.TryParse(lines[1], out long ticks))
                {
                    TrayLog.Warn("RestorePauseStateOnStartup: timed pause missing ticks — deleting.");
                    DeletePauseStateFile();
                    return;
                }
                // Bounds-check: `new DateTime(ticks)` throws on negative or
                // > DateTime.MaxValue.Ticks. Untrusted input (hand-edit, tampered
                // pause.dat) must not OOM or crash the tray at boot.
                if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
                {
                    TrayLog.Warn($"RestorePauseStateOnStartup: ticks out of range ({ticks}) — deleting.");
                    DeletePauseStateFile();
                    return;
                }
                var due = new DateTime(ticks, DateTimeKind.Utc);
                if (due - DateTime.UtcNow <= TimeSpan.FromSeconds(5))
                {
                    TrayLog.Info("RestorePauseStateOnStartup: deadline already passed — dropping stale pause.");
                    DeletePauseStateFile();
                    return;
                }
                _activePauseMinutes = minutes;
                _pauseResumeAtUtc = due;
                StartOrRearmPauseTimer();
            }
            else
            {
                _activePauseMinutes = 0;
                _pauseResumeAtUtc = null;
            }

            // v3 schema: lines 4-6 carry the snapshot. Missing schema marker = legacy v2
            // pause.dat (pre-v2.2.39) — leave snapshots empty; ReapplyInheritedPause +
            // MenuResume will fall back to "flip everything" which auto-unwedges users
            // upgrading from v2.2.38 with config-paused folders.
            //
            // v2.2.40 hardening: Trim() on each parsed ID strips a trailing \r if a user
            // hand-edited pause.dat in Notepad (CRLF line endings) — without this, the
            // last id on each line would have \r appended and never match in folderIdFilter
            // lookups, silently leaving that folder paused on resume.
            int snapshotFolders = 0, snapshotDevices = 0;
            if (lines.Length >= 4 && lines[3].Trim() == PauseStateSchemaV3)
            {
                if (lines.Length >= 5 && lines[4].Length > 0)
                {
                    var ids = lines[4].Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    lock (_pauseSnapshotLock) { _trayPausedFolderIds = ids; }
                    snapshotFolders = ids.Count;
                }
                if (lines.Length >= 6 && lines[5].Length > 0)
                {
                    var ids = lines[5].Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    lock (_pauseSnapshotLock) { _trayPausedDeviceIds = ids; }
                    snapshotDevices = ids.Count;
                }
            }

            _paused = true;
            _autoPaused = wasAutoPaused;
            _pauseNeedsReapply = true;
            TrayLog.Info($"RestorePauseStateOnStartup: inherited pause ({FormatPauseDuration(minutes)}, auto={wasAutoPaused}, snapshot {snapshotFolders}f/{snapshotDevices}d); Syncthing will be re-paused on first successful poll.");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"RestorePauseStateOnStartup: {ex.Message}");
            DeletePauseStateFile();
        }
    }

    /// <summary>
    /// Atomically flips folder/device pause state in Syncthing's config.
    /// GET /rest/config -> mutate JSON paused fields -> PUT /rest/config.
    /// One round-trip = one Syncthing config-reload, regardless of how many
    /// folders/devices change.
    ///
    /// Why bulk-PUT instead of N PATCH /rest/config/folders/&lt;id&gt; calls:
    /// rapid sequential PATCHes against Syncthing v2 fail (HTTP 000 / no
    /// response) because each PATCH triggers a config-reload that briefly
    /// closes the REST listener — subsequent PATCHes hit the closed window.
    /// PUT-the-whole-config is a single reload.
    ///
    /// IDs filter: pass null to flip every folder/device. Pass a HashSet to
    /// flip only matching IDs (used by MenuResume to restore exactly what
    /// MenuPause flipped, preserving user-intentional folder pauses).
    ///
    /// Returns the lists of IDs that *actually changed* (were not already in
    /// the target state). MenuPause uses this to snapshot what to restore on
    /// MenuResume. If nothing would change, the PUT is skipped to avoid the
    /// config-reload churn for a no-op.
    ///
    /// Caller threading: pool thread only (HTTP synchronous). State writes
    /// must marshal to UI thread.
    /// </summary>
    private (List<string> FlippedFolders, List<string> FlippedDevices, bool Ok) ApplyConfigPause(
        bool targetPaused,
        HashSet<string>? folderIdFilter = null,
        HashSet<string>? deviceIdFilter = null)
    {
        var flippedFolders = new List<string>();
        var flippedDevices = new List<string>();
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            TrayLog.Warn("ApplyConfigPause: no API key configured.");
            return (flippedFolders, flippedDevices, false);
        }
        try
        {
            // 8s GET — slow disk + large config still under HttpClient's 5s default
            // could spuriously fail on some users, so override.
            var (gs, body) = _api.Get("/rest/config", timeoutMs: 8000);
            if (gs != 200 || string.IsNullOrEmpty(body))
            {
                TrayLog.Warn($"ApplyConfigPause: GET /rest/config returned HTTP {gs}.");
                return (flippedFolders, flippedDevices, false);
            }

            JsonNode? root = JsonNode.Parse(body);
            if (root is null)
            {
                TrayLog.Warn("ApplyConfigPause: failed to parse /rest/config body.");
                return (flippedFolders, flippedDevices, false);
            }

            if (root["folders"] is JsonArray folders)
            {
                foreach (JsonNode? f in folders)
                {
                    if (f is null) continue;
                    string id = f["id"]?.GetValue<string>() ?? string.Empty;
                    if (id.Length == 0) continue;
                    if (folderIdFilter is not null && !folderIdFilter.Contains(id)) continue;
                    bool current = (f["paused"] as JsonValue)?.GetValue<bool>() ?? false;
                    if (current == targetPaused) continue;
                    f["paused"] = targetPaused;
                    flippedFolders.Add(id);
                }
            }

            if (root["devices"] is JsonArray devices)
            {
                foreach (JsonNode? d in devices)
                {
                    if (d is null) continue;
                    string id = d["deviceID"]?.GetValue<string>() ?? string.Empty;
                    if (id.Length == 0) continue;
                    if (deviceIdFilter is not null && !deviceIdFilter.Contains(id)) continue;
                    bool current = (d["paused"] as JsonValue)?.GetValue<bool>() ?? false;
                    if (current == targetPaused) continue;
                    d["paused"] = targetPaused;
                    flippedDevices.Add(id);
                }
            }

            if (flippedFolders.Count == 0 && flippedDevices.Count == 0)
            {
                // No-op: config is already in the target state. Skip the PUT to avoid
                // unnecessary config-reload churn (and the brief REST-listener close).
                TrayLog.Info($"ApplyConfigPause: already at targetPaused={targetPaused}, no PUT needed.");
                return (flippedFolders, flippedDevices, true);
            }

            string newBody = root.ToJsonString();
            // 30s ceiling for PUT — Syncthing reapplies config + may rebuild folder
            // ignore caches; on machines with many folders this can take seconds.
            var (ps, _) = _api.Put("/rest/config", newBody, timeoutMs: 30_000);
            if (ps != 200)
            {
                TrayLog.Warn($"ApplyConfigPause: PUT /rest/config returned HTTP {ps}.");
                return (flippedFolders, flippedDevices, false);
            }
            TrayLog.Info($"ApplyConfigPause: targetPaused={targetPaused}; flipped {flippedFolders.Count} folder(s), {flippedDevices.Count} device(s).");
            return (flippedFolders, flippedDevices, true);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"ApplyConfigPause: {ex.Message}");
            return (flippedFolders, flippedDevices, false);
        }
    }

    /// <summary>
    /// Called from the poll-tick once Syncthing is confirmed reachable. Re-applies
    /// our pause via ApplyConfigPause (config-level), so it survives daemon restart
    /// even on Syncthing v2 where config is the source of truth for paused state.
    ///
    /// Uses the snapshot of IDs the tray paused (so we don't unpause user-intentional
    /// pauses across reapply cycles). Empty snapshot = legacy v2 pause.dat (pre-v2.2.39)
    /// or fresh state — fall back to "pause every currently-active folder/device" so
    /// the user's intent ("pause everything that was syncing when I clicked Pause")
    /// is preserved.
    ///
    /// On success, this clears _pauseNeedsReapply immediately — the PUT response means
    /// the config IS applied (no need for a second poll to confirm).
    /// </summary>
    private void ReapplyInheritedPause()
    {
        if (!_pauseNeedsReapply) return;
        // Race guard (v2.2.39): if a user-initiated MenuResume already cleared
        // _paused while a daemon-restart-driven re-arm was in flight from poll
        // Section 1.5, honor the user's intent and bail. Without this, the
        // legacy-fallback path (empty snapshot + null filter) would re-pause
        // every just-resumed folder and device.
        if (!_paused)
        {
            _pauseNeedsReapply = false;
            TrayLog.Info("ReapplyInheritedPause: skipped, _paused=false (concurrent resume); flag cleared.");
            return;
        }
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            // Can't act without an API key. Leave the flag set so a later key
            // entry + poll cycle can still reconcile.
            return;
        }

        HashSet<string>? folderFilter = null;
        HashSet<string>? deviceFilter = null;
        bool haveSnapshot;
        lock (_pauseSnapshotLock)
        {
            haveSnapshot = _trayPausedFolderIds.Count > 0 || _trayPausedDeviceIds.Count > 0;
            if (haveSnapshot)
            {
                folderFilter = new HashSet<string>(_trayPausedFolderIds, StringComparer.Ordinal);
                deviceFilter = new HashSet<string>(_trayPausedDeviceIds, StringComparer.Ordinal);
            }
        }

        var (flippedFolders, flippedDevices, ok) = ApplyConfigPause(
            targetPaused: true,
            folderIdFilter: folderFilter,
            deviceIdFilter: deviceFilter);

        if (!ok)
        {
            TrayLog.Warn("ReapplyInheritedPause: ApplyConfigPause failed; will retry next poll.");
            return;
        }

        if (!haveSnapshot && (flippedFolders.Count > 0 || flippedDevices.Count > 0))
        {
            // Legacy fallback path took effect — record what we just flipped so the
            // resume path can restore exactly that set, and persist the v3 schema.
            lock (_pauseSnapshotLock)
            {
                _trayPausedFolderIds = flippedFolders;
                _trayPausedDeviceIds = flippedDevices;
            }
            // Persist the upgraded snapshot (best-effort — UI-thread file IO).
            RunOnUi(() => { if (!_disposed) PersistPauseState(); });
        }

        _pauseNeedsReapply = false;
        TrayLog.Info("ReapplyInheritedPause: pause re-applied via PUT /rest/config; flag cleared.");
    }

    private void MenuStart()
    {
        if (IsOverclickGuarded()) return;
        if (!LaunchSyncthing())
        {
            ShowOsd("Could not launch syncthing.exe \u2014 check path in Settings", 5000);
            return;
        }
        UpdateTrayIcon();
        BuildMenu();
        ShowOsd("Syncthing started", 3000);
    }

    private void MenuStop()
    {
        if (IsOverclickGuarded()) return;
        if (IsSyncthingRunning())
        {
            _intentionalStop = true;
            StopSyncthing();
            UpdateTrayIcon();
            BuildMenu();
            ShowOsd("Syncthing stopped", 3000);
        }
        else
        {
            ShowOsd("Syncthing is not running", 3000);
        }
    }

    private void MenuRescanAll()
    {
        if (IsOverclickGuarded(800)) return;
        if (!IsSyncthingRunning())
        {
            ShowOsd("Syncthing is not running", 3000);
            return;
        }
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        // HTTP on pool thread — a dead Syncthing would otherwise freeze the tray
        // menu for HttpClient.Timeout (5 s). OSD self-marshals to UI.
        _ = Task.Run(() =>
        {
            try
            {
                var (status, _) = _api.Post("/rest/db/scan");
                ShowOsd(status == 200 ? "Rescanning all folders..." : $"Rescan failed (HTTP {status})", 3000);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuRescanAll POST threw: " + ex.Message);
                ShowOsd("Rescan request failed", 3000);
            }
        });
    }

    private void MenuRescanFolder(string folderId)
    {
        if (IsOverclickGuarded(800)) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var (status, _) = _api.Post($"/rest/db/scan?folder={Uri.EscapeDataString(folderId)}");
                ShowOsd(status == 200 ? $"Rescanning {folderId}..." : $"Rescan failed (HTTP {status})", 3000);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuRescanFolder POST threw: " + ex.Message);
                ShowOsd("Rescan request failed", 3000);
            }
        });
    }

    private void MenuRestart()
    {
        if (IsOverclickGuarded()) return;
        if (IsSyncthingRunning())
        {
            _intentionalStop = true;
            StopSyncthing();
        }
        LaunchSyncthing();
        UpdateTrayIcon();
        BuildMenu();
        ShowOsd("Syncthing restarted", 3000);
    }

    // --- Syncthing Process Management ---

    private bool LaunchSyncthing()
    {
        if (!File.Exists(_config.SyncExe))
        {
            ShowOsd($"syncthing.exe not found: {_config.SyncExe}", 5000);
            return false;
        }

        try
        {
            // Tray owns browser-opening (see _pendingOpenWebUI). Always pass
            // --no-browser so Syncthing doesn't race with us, and so restart-the-
            // tray-while-Syncthing-is-running still honors the setting.
            const string args = "--no-browser";
            var dir = Path.GetDirectoryName(_config.SyncExe) ?? string.Empty;
            var psi = new ProcessStartInfo
            {
                FileName = _config.SyncExe,
                Arguments = args,
                WorkingDirectory = dir,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Limit Syncthing's Go runtime to 2 OS threads — reduces memory/CPU
            // footprint on a machine already running Claude, Docker, Semgrep, etc.
            psi.Environment["GOMAXPROCS"] = "2";
            // `using` so a throw on .Id (process exited between Start and the Id
            // read — rare, but observed on slow-disk + corrupt-exe combinations)
            // still releases the Process handle; otherwise it leaked until the
            // finalizer ran. Process.Id is only valid until Dispose, so capture
            // the int inside the using scope before releasing.
            // nosemgrep: gitlab.security_code_scan.SCS0001-1 -- psi.FileName is _config.SyncExe (Syncthing binary path from the user's own local config file); local write access to the config would already imply arbitrary code exec via easier paths
            using var p = Process.Start(psi);
            if (p is null)
            {
                ShowOsd("Failed to start syncthing process", 5000);
                return false;
            }
            _launchedPid = p.Id;
            InvalidateRunningCache();
            return true;
        }
        catch (Exception ex)
        {
            ShowOsd($"Launch failed: {ex.Message}", 5000);
            return false;
        }
    }

    private void StopSyncthing()
    {
        _stopping = true;
        _iconTimer.Stop();
        try
        {
            StopSyncthingCore();
        }
        finally
        {
            _stopping = false;
            if (!_disposed)
                _iconTimer.Start();
        }
    }

    private void StopSyncthingCore()
    {
        // Graceful: REST API shutdown. Syncthing acks the shutdown POST as soon as
        // it queues the shutdown internally — it doesn't wait for full process
        // teardown before responding — so a 2s timeout is plenty. The 5s default
        // is what used to make the tray close feel frozen when a poll-tick held
        // the keep-alive connection.
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                _api.Post("/rest/system/shutdown", timeoutMs: 2000);
                // Short wait for the process to actually exit. 20×100ms = 2s cap;
                // if Syncthing's still around after that we fall through to kill.
                for (int i = 0; i < 20; i++)
                {
                    InvalidateRunningCache();
                    if (!IsSyncthingRunning()) return;
                    Thread.Sleep(100);
                    Application.DoEvents();
                }
            }
            catch { /* fall through to force kill */ }
        }

        // Force kill fallback — target our launched PID first.
        // v2.3.0: Kill(entireProcessTree:true) is required for Syncthing v2's
        // supervisor+daemon fork model. Killing the supervisor alone leaves the
        // daemon orphaned; killing the daemon alone lets the supervisor restart
        // it. .NET 5+'s entireProcessTree variant terminates the parent and ALL
        // descendants atomically (Win32 Job Object semantics under the hood).
        if (_launchedPid != 0)
        {
            try
            {
                using var p = Process.GetProcessById(_launchedPid);
                p.Kill(entireProcessTree: true);
            }
            catch { /* already gone or wrong PID */ }
            _launchedPid = 0;
        }
        else
        {
            // No tracked PID — fall back to killing by name. Take the process tree
            // for each match: any supervisor we hit will drop its child too.
            foreach (var p in Process.GetProcessesByName("syncthing"))
            {
                using (p)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }
            }
        }

        for (int i = 0; i < 30; i++)
        {
            InvalidateRunningCache();
            if (!IsSyncthingRunning()) break;
            Thread.Sleep(100);
            Application.DoEvents();
        }
        InvalidateRunningCache();
    }

    // --- Network Category Detection (WMI, cached 60s) ---

    private int GetNetworkCategory()
    {
        long now = Environment.TickCount64;
        if (now - _cachedNetworkCategoryTick < 60000)
            return _cachedNetworkCategory;

        _cachedNetworkCategoryTick = now;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\StandardCimv2",
                "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
            using var results = searcher.Get();
            foreach (ManagementObject profile in results)
            {
                using (profile)
                {
                    var cat = profile["NetworkCategory"];
                    if (cat is not null)
                    {
                        _cachedNetworkCategory = Convert.ToInt32(cat);
                        return _cachedNetworkCategory;
                    }
                }
            }
        }
        catch { /* WMI unavailable */ }
        _cachedNetworkCategory = -1;
        return -1;
    }

    // --- Helpers ---

    /// <summary>
    /// Checks if syncthing.exe is running. Result is cached for 2 seconds to avoid
    /// repeated lookups within the same poll cycle (UpdateTrayIcon, PollSyncStatus,
    /// and BuildMenu can all call this in sequence).
    /// When we launched syncthing ourselves (_launchedPid != 0), uses O(1)
    /// GetProcessById instead of enumerating the entire process table.
    /// </summary>
    private bool IsSyncthingRunning()
    {
        long now = Environment.TickCount64;
        if (now - _cachedRunningTick < 2000)
            return _cachedRunning;

        try
        {
            bool running;
            if (_launchedPid != 0)
            {
                // Fast path: O(1) kernel handle lookup by PID
                try
                {
                    using var p = Process.GetProcessById(_launchedPid);
                    running = !p.HasExited;
                }
                catch (ArgumentException)
                {
                    // PID no longer exists
                    running = false;
                    _launchedPid = 0;
                }
            }
            else
            {
                // Slow path: only when syncthing was already running before we started
                var procs = Process.GetProcessesByName("syncthing");
                running = false;
                foreach (var p in procs)
                {
                    using (p)
                    {
                        running = true;
                    }
                }
            }
            _cachedRunning = running;
            _cachedRunningTick = now;
        }
        catch (OutOfMemoryException)
        {
            // Under extreme memory pressure, process enumeration can fail.
            // Return last known state rather than crashing the tray app.
            _cachedRunningTick = now;
        }
        return _cachedRunning;
    }

    /// <summary>
    /// Invalidates the cached process check so the next call does a fresh lookup.
    /// Call after starting or stopping syncthing so state is immediately correct.
    /// </summary>
    private void InvalidateRunningCache()
    {
        _cachedRunningTick = 0;
    }

    private void PlaySound(System.Media.SystemSound sound)
    {
        if (_config.SoundNotifications)
            sound.Play();
    }

    private void ShowOsd(string text, int durationMs)
    {
        if (_disposed) return;
        if (_osd.InvokeRequired)
        {
            // BeginInvoke — fire-and-forget by design. A synchronous Invoke from a
            // pool thread can race with the UI thread's Dispose path during shutdown.
            try { _osd.BeginInvoke(() => { if (!_disposed) _osd.ShowMessage(text, durationMs); }); }
            catch (ObjectDisposedException) { /* tray is exiting */ }
            catch (InvalidOperationException) { /* handle not yet created */ }
        }
        else
        {
            _osd.ShowMessage(text, durationMs);
        }
    }

    private static Icon LoadEmbeddedIcon(string name)
    {
        var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(name);
        if (stream is not null)
        {
            using (stream)
                return new Icon(stream);
        }
        // Clone the shared system icon so we can safely dispose it later
        return (Icon)SystemIcons.Application.Clone();
    }

    private void ExitApplication()
    {
        _intentionalStop = true;
        // Intentional shutdown is by definition stable — clear the sentinel so the
        // next launch doesn't falsely warn about a crash that never happened.
        UpdateDialog.TryDeleteCrashSentinel();

        // Stop timers BEFORE the shutdown call. Otherwise a poll-tick that fires
        // mid-shutdown can queue a GET on the HttpClient's single keep-alive
        // connection to localhost:8384, and the /rest/system/shutdown POST waits
        // behind it (HTTP/1.1 serializes per-connection). That's what turns a
        // normally-snappy close into a several-second freeze.
        try { _pollTimer.Stop(); } catch { /* already disposed */ }
        try { _iconTimer.Stop(); } catch { /* already disposed */ }
        try { _pauseTimer.Stop(); } catch { /* already disposed */ }

        if (_config.StopOnExit)
            StopSyncthing();
        Dispose();
        Application.Exit();
    }

    // --- Cleanup ---

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                // Unsubscribe first — SystemEvents holds a strong reference that
                // otherwise keeps this context alive past tray exit and fires
                // PowerModeChanged on a disposed state.
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

                _iconTimer.Stop();
                _iconTimer.Dispose();
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pauseTimer.Stop();
                _pauseTimer.Dispose();

                // One-shot timers (startup/first-run/stability) that haven't
                // ticked yet — stop + dispose so their native timer handles drop
                // now rather than on the finalizer thread. The Tick handlers
                // themselves Remove from this list on fire, so by exit this is
                // only non-empty when we exited before the timer elapsed.
                foreach (var t in _oneShotTimers)
                {
                    try { t.Stop(); t.Dispose(); } catch { /* already disposed */ }
                }
                _oneShotTimers.Clear();

                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();

                _osd.Dispose();
                _messageWindow.Dispose();

                _api.Dispose();
                _syncIcon.Dispose();
                _pauseIcon.Dispose();
                _loadFoldersGate.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    // --- MessageWindow: hidden form for WndProc (TaskbarCreated recovery) ---

    private sealed class MessageWindow : Form
    {
        private readonly TrayApplicationContext _owner;
        private readonly uint _wmTaskbarCreated;

        public MessageWindow(TrayApplicationContext owner)
        {
            _owner = owner;
            _wmTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Size = Size.Empty;
            // Create the handle without showing
            _ = Handle;
        }

        protected override void WndProc(ref Message m)
        {
            if (_wmTaskbarCreated != 0 && (uint)m.Msg == _wmTaskbarCreated)
            {
                // Explorer restarted — re-show tray icon and force icon re-assignment
                _owner._trayIcon.Visible = true;
                _owner._firstIconPoll = true;
                _owner.UpdateTrayIcon();
            }
            base.WndProc(ref m);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    // --- Data types ---

    private sealed record FolderInfo(string Id, string Label, string Path, string[] DeviceIds, bool Paused);
}
