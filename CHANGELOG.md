# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingTray are documented here.

## v2.2.41 — 2026-05-07

### Bug fixes (post-v2.2.40 verifier round)
- **`pause.dat` write is now fully atomic, even under AV-locks.** v2.2.40's `File.Replace` was called with `destinationBackupFileName: null` — if antivirus held the destination open mid-Replace, the original file could be deleted before the rename completed, losing the snapshot. Now passes a `.bak` backup path so a failed Replace leaves a recoverable copy of the prior pause.dat. The backup is cleaned on success; an error-path log line names the .bak so support can recover by hand if needed.
- **Auto-pause no longer traps users into the empty-snapshot unwedge path.** When auto-pause fired in the rare case where every folder/device was already paused-in-config (e.g., user paused everything via the Syncthing web UI before walking onto public wifi), v2.2.40 still set `_paused=true` and `_autoPaused=true` with an empty snapshot. A subsequent manual "Resume Syncing" click would then hit the empty-snapshot fallback ("unpause everything currently paused") and silently un-pause folders the user had intentionally paused before any of this. Auto-pause now skips the state mutation when nothing was actually flipped — leaves `_paused=false`, doesn't claim `_autoPaused`, and logs an info line.

## v2.2.40 — 2026-05-07

### Bug fixes (post-v2.2.39 multi-agent audit)
- **Zero-device users no longer loop on `_pauseNeedsReapply` forever.** If you run Syncthing locally with no remote devices (local-only folders), v2.2.39's reapply path was gated behind `if (deviceCount > 0)` and skipped on every poll — `_pauseNeedsReapply` was set in `RestorePauseStateOnStartup` but never cleared, leaving the in-memory flag stuck. Hoisted the reapply branch outside the deviceCount gate so the PUT-and-self-clear path runs regardless of remote-device presence. The external-resume detection (which inherently needs per-device pause state) stays gated.
- **`pause.dat` writes are now atomic.** Replaced `File.WriteAllText` with write-temp-then-`File.Replace` so a crash, BSOD, or power loss mid-write can't truncate the snapshot file. A truncated `pause.dat` would otherwise be deleted by `RestorePauseStateOnStartup`'s malformed-file handler on next launch, losing the snapshot and forcing the unwedge fallback.
- **Auto-resume on network change no longer silently un-pauses everything when the snapshot is empty.** v2.2.39's network-auto-resume path inherited the same "flip every paused entry" fallback as `MenuResume` — which is correct for a user-initiated unwedge but inappropriate for a silent automatic action that could unpause folders the user intentionally paused before NetworkAutoPause kicked in. Auto-resume now bails (info-log, no OSD) when the snapshot is empty and lets the user manually resume.

### Hardening
- **`pause.dat` reader trims each ID** before populating the snapshot lists — defends against a `\r` trailing the last ID on a line if a user hand-edited the file in Notepad (CRLF endings would otherwise leave one folder permanently paused on resume because the lookup hash wouldn't match).
- Confirm-handshake (`if (allPaused) clear flag`) in the connections poll is now explicitly documented as a fallback for the rare case where `ReapplyInheritedPause` doesn't self-clear via PUT response (e.g., HTTP timeout); v2.2.39 CHANGELOG drift fixed.

### Known limitations (unchanged from v2.2.39, surfacing for transparency)
- `PUT /rest/config` is not optimistically-concurrent. If you edit Syncthing config in the web UI at the exact moment the tray is applying a pause/resume, your edits can be silently overwritten. The window is small (sub-second on localhost). Optimistic concurrency via Syncthing's config `version` field is on the v2.2.41+ roadmap.

## v2.2.39 — 2026-05-07

### Bug fixes
- **Pause/Resume now correctly handle folder-level pause state.** On Syncthing v2, folders carry a persisted `paused` flag in `/rest/config/folders` independent of the device-level pause toggled by `POST /rest/system/pause`. If anything in your setup put folders into config-level pause (e.g. clicking "Pause" on a folder card in the Syncthing web UI), SyncthingTray's "Resume Syncing" would call `POST /rest/system/resume`, get HTTP 200 back, show the "Syncing resumed" OSD, and leave the web UI showing every folder still paused — because that endpoint only resumes devices, not folders. The asymmetry effectively wedged sync. SyncthingTray now applies pause/resume by reading `/rest/config`, mutating the `paused` field on every folder and device, and `PUT`ing the config back atomically — one round-trip, both axes covered, version-independent (works on Syncthing v1 and v2). The legacy `/rest/system/pause` and `/rest/system/resume` endpoints are no longer called.
- **User-intentional folder pauses are preserved across tray pause/resume cycles.** If you have a folder paused on purpose (kept off until later) and then click "Pause Syncing" in the tray, the new pause flow snapshots which folders/devices the tray itself flipped to paused. On resume, only that snapshot is restored — folders you had already paused stay paused. The snapshot persists in `pause.dat` (schema v3) so it survives tray restart, daemon restart, and reboot.
- **Wedged-on-upgrade users self-heal on first Resume click.** If you were already in the wedged state (folders paused-in-config left over from any earlier session), the first time you click "Resume Syncing" after upgrading the tray detects the missing snapshot and falls back to "unpause every paused folder and device" — your config is reconciled in one click. Subsequent pause/resume cycles use the new snapshot path.

### Internals
- `ApplyConfigPause` helper centralises the GET-mutate-PUT flip; `MenuPause`, `MenuResume`, `ReapplyInheritedPause`, and the network auto-pause/auto-resume paths all route through it. Skipping the PUT when no field would change avoids unnecessary config-reload churn.
- `pause.dat` schema bumped to v3 with appended snapshot lines (folder-IDs and device-IDs, pipe-separated). Older `pause.dat` files (v2 schema) load fine — empty snapshot triggers the unwedge fallback path described above.
- The post-PUT response is treated as authoritative confirmation (`PUT /rest/config` returns 200 only after Syncthing's config save completes), so `_pauseNeedsReapply` clears immediately on success rather than waiting for a second-poll handshake.

## v2.2.38 — 2026-05-01

### Reliability
- **Manual pauses now survive a Syncthing daemon restart that happens while SyncthingTray stays running.** If the Syncthing process restarted mid-session — auto-update, crash, sleep/wake handoff — its in-memory pause state was lost; the next connections poll reported the daemon as no longer paused, and SyncthingTray treated the discrepancy as an external resume, silently clearing your pause-until time and deleting `pause.dat`. The pause-until UI looked normal but the daemon was already syncing again. SyncthingTray now tracks the daemon's reported start time on each poll and re-arms the pause-reapply handshake when the start time changes while a manual pause is held — same mechanism that already restores the pause across a tray-app restart, just no longer one-shot. The external-resume transition is now also INFO-logged (it was previously silent), so a "sync kicked back on" report is grep-able in `tray.log`.

## v2.2.36 — 2026-04-25

### Security
- **Settings dialog now rejects UNC and forward-slash UNC paths for the syncthing.exe location.** Typing `\\attacker\share\syncthing.exe`, `//attacker/share/syncthing.exe`, or any mixed-slash variant into the path textbox previously persisted the value verbatim. The next "Restart Syncthing" call would `File.Exists()` the UNC, triggering an SMB/NTLM negotiation against the attacker's host and leaking your hash to their SMB responder. The path is now validated up-front through the same `ValidateSyncExe` gate that protects the INI-load path; rejection keeps your previous saved value and warns via OSD instead of silently saving the bad path. The fix uses the workspace's char-pair UNC predicate that was already deployed for `OpenFolder` in v2.2.33-v2.2.35 — one more boundary now consistently enforces the same defense.

### Bug fixes
- **Saving an unrelated setting no longer fails if your Syncthing path went missing.** If your `syncthing.exe` was uninstalled, moved, or renamed between sessions, opening Settings and clicking Save (to change "Run on startup", say) used to fail outright with "Syncthing path rejected — must be a local path to syncthing.exe". Now your previous valid path is kept, the rest of your settings save normally, and an OSD only warns if you actively typed a different rejected value.

### Internals
- **Update-check JSON parsing replaced with `JsonDocument`.** The "Check Now" button in Settings used a hand-rolled `IndexOf`-based parser to extract the latest version from Syncthing's `/rest/system/upgrade` response. The same anti-pattern was already called out as bypassable in another part of this file's docstring — now consistent with the rest of the codebase.

## v2.2.35 — 2026-04-23

### Security
- **Self-updater now validates every redirect hop against an explicit allowlist, and the allowlist is future-proof to additional GitHub CDN hosts.** The prior updater validated only the initial download URL, then relied on `HttpClient`'s default transparent redirect follow — which would cheerfully 302 an allowlisted `github.com/itsnateai/synctray/...` hand-off to anywhere the `Location` header pointed. A tampered release JSON or a compromised upstream redirect could have steered either the binary or the `SHA256SUMS` fetch to an attacker host, defeating the integrity check end-to-end. The updater now disables auto-redirect and walks each hop manually through `SendAllowlistedAsync`, re-checking the host against the allowlist before issuing the GET. Host matching moved from prefix-string equality on two hardcoded CDN hosts to suffix-match on `*.githubusercontent.com` with repo-scoped exact-match on `api.github.com` / `github.com`, so future GitHub-controlled release-asset CDN rollouts (like the `release-assets.githubusercontent.com` host that appeared alongside `objects.githubusercontent.com` earlier this month) don't silently break the in-app updater.

## v2.2.34 — 2026-04-18

### Privacy
- **Diagnostic logging now actually ships off by default.** The README, CHANGELOG, HelpForm, and in-tree `TrayLog` docstring all advertised `DiagnosticLogging` as opt-in via `DiagnosticLogging=1`, but the code default was `true` — so a fresh install with no INI quietly wrote to `%LOCALAPPDATA%\SyncthingTray\tray.log` on first run. The default is now `false`, matching every user-facing surface. Users who already opened Settings at least once have the key persisted explicitly in their INI, so their preference is preserved across upgrade.
- **Settings-path no longer leaks the username into the log.** The `AppConfig.Load` failure branches (file locked, file corrupt) logged the full `%USERPROFILE%\…\SyncthingTray.ini` path. Now only the file name is logged — the load-failure reason is the useful signal, not the install location.

### Security
- **Update integrity: SHA256SUMS URL now goes through the same origin allowlist as the binary URL.** v2.2.33 validated the download URL against the `github.com/itsnateai/` + `objects.githubusercontent.com` allowlist, but the parallel `SHA256SUMS` fetch ran with no origin check — a tampered release JSON could redirect the hash file to an attacker host, defeating the integrity check end-to-end. Both URLs are now gated by the same helper.
- **Remote version tag is strict-semver-validated before it touches the UI.** The `tag_name` field from GitHub's release JSON was interpolated raw into the Update dialog's status label and the "Downloading SyncthingTray v… " string — a compromised repo could push any renderable string there, including control characters, format specifiers, or a phishing hint. The tag is now parsed through a strict `\d+\.\d+\.\d+(-[a-z0-9.]+)?` whitelist before it reaches any render site; anything else short-circuits to an error.
- **Pause-state file has a 256 KB read cap and bounds-checks its UTC ticks.** A tampered or runaway `pause.dat` could previously balloon past Int64 limits: `File.ReadAllLines` would OOM on a multi-GB file, and `new DateTime(ticks)` throws on out-of-range input. Either would brick the tray at startup with no OSD. The read is now capped and the tick value validated against `DateTime.MaxValue.Ticks` before the conversion.
- **Folder labels and device names pass through a sanitiser before the menu renders them.** A hostile peer config could emit a folder label containing `&` (which WinForms treats as an accelerator-key prefix, stealing keyboard focus on menu open), CR/LF (which broke `TrayLog` interpolation into multi-line entries), or a multi-KB payload (menu layout crash). The new `MenuTextSanitizer` escapes `&`, strips C0/C1 control characters, and caps at 120 chars with a trailing ellipsis on truncation.

### Race-conditions
- **User-initiated pause no longer gets silently converted into an auto-pause.** A user clicking "Pause" between the poll-tick's `!_paused` read and the post-HTTP UI marshal would have their manual pause state overwritten with `_autoPaused = true`, causing the next network-category transition to auto-resume over their intent. The marshal now re-checks the state on the UI thread and bails if the user flipped it mid-flight. Matching guard added to the auto-resume branch.
- **`_paused` and `_autoPaused` are now `volatile`.** Publication-ordering fix on the pool-thread reads — without it, the race-guard above could see stale cached values instead of the UI thread's most recent write.

### Resource hygiene
- **Update dialog's `CheckForUpdateAsync` disposes the prior CTS before overwriting.** Consistency fix — the sibling pattern at the download path already disposed the previous `CancellationTokenSource` before creating a new one; the check path didn't. A user who cancelled, then re-triggered "Check for updates" would leak one native event handle per round-trip.
- **Update-toast now self-disposes on any close path.** The post-update toast was built from a one-shot outer timer + a `Form` + a dismiss timer + a font, each tracked manually. An external close (Alt-F4, `Application.Exit`) would skip the dismiss timer's disposal branch and orphan the font. The toast is now a `ToastWindow : Form` that owns all three resources and routes its teardown through the standard `Form.Dispose(bool)` chain.

## v2.2.33 — 2026-04-18

### Security
- **Open-Folder UNC guard now covers forward-slash and mixed-slash variants.** v2.2.32 introduced a UNC check to stop `Process.Start` from handing `\\attacker\share` to the Windows shell, but the check was a backslash-prefix match. .NET 8 and the Windows shell both treat `/` and `\` as interchangeable separators, so `//attacker/share`, `\/attacker\share`, and `/\attacker\share` all still flowed through to `Directory.Exists` (a 20-30 s SMB timeout if the peer is unreachable) and then to `Process.Start(UseShellExecute=true)` — reopening the same NTLM-hash-leak-via-SMB threat the v2.2.32 fix was meant to close. The detection now predicates on character-class membership at positions [0] and [1], closing all four slash permutations at once.

## v2.2.32 — 2026-04-18

### Reliability
- **Tray startup no longer freezes while Syncthing cold-starts.** The post-launch sequence ran Syncthing's status poll and folder fetch synchronously on the UI thread, so a Syncthing that took its time to come up meant the tray icon appeared but the menu was unresponsive for up to ~1.8 seconds (a ~300 ms reachability probe + up to 1.5 s for `/rest/system/status` + `/rest/config/folders` + `/rest/config/devices`). Both calls now run on the thread pool, matching the steady-state poll tick and power-resume paths that were already pool-threaded.
- **"Refresh List" menu item no longer freezes the menu.** The per-folder submenu's "Refresh List" entry was refetching folders synchronously on the UI thread, which held the context menu open-but-frozen for the duration of the HTTP call. Now runs on the thread pool — menu dismisses immediately, the list repopulates when the response arrives.
- **Settings-Save no longer freezes on slow Syncthing.** Clicking "Save" in Settings refetched the folder list synchronously on the UI thread, so a Syncthing taking its time to respond (the 300 ms reachability probe + up to 1.5 s REST fetch) visibly stuttered the Save-and-close animation. The refresh now runs on the thread pool and the "Settings saved" OSD marshals back via the UI-thread dispatcher.
- **Double-clicking Resume is debounced.** Every other click-handler in the tray menu was protected by an 800 ms overclick guard, but Resume wasn't — an impatient double-click could fire two `/rest/system/resume` POSTs back-to-back. Rare in practice (Syncthing handles it idempotently), but the guard now matches Pause and the other click-paths.
- **Settings' "probe Syncthing until it appears" loop stops after 60 s.** The 2-second poll that auto-refreshes the discovery checkboxes while Settings is open had no retry cap — a permanently-unreachable Syncthing (wrong path, wrong API key, Syncthing uninstalled) meant the dialog kept hitting `/rest/config/options` every 2 seconds for the entire time the user left Settings sitting open. After 30 ticks the loop now stops, disposes the timer, and updates the warning label to prompt the user to reopen Settings.

### Security
- **Open-Folder menu items can't escape to arbitrary protocol handlers.** The per-folder "Open folder" menu entry called `Process.Start` on whatever `path` Syncthing's REST config advertised. Local paths worked fine; a hostile or corrupted config that smuggled in `ms-settings:…`, `shell:appsfolder\…`, or a UNC `\\attacker\share` would hand the shell a protocol invocation. The path is now validated — UNC, any URI-shaped colon outside drive-letter position, and non-fully-qualified paths are refused with an OSD before the shell ever sees them.

### Resource hygiene
- **Update dialog's GitHub response is disposed on every path.** `await _http.GetAsync(...)` assigned to a plain `var`; the early-return branches for HTTP 403 (rate-limit) and 404 (no releases) left the `HttpResponseMessage` to the finalizer. It's now a `using var` — disposal happens regardless of which branch exits first.

### Accessibility
- **Every button in Settings speaks its own name.** The six link buttons that open the Help / WebUI / log pages and the "Check Config" button were the last Settings controls without explicit `AccessibleName`. Screen readers now announce each one by its visible text instead of generic "button".

## v2.2.31 — 2026-04-18

### Docs
- **Help window describes how folders actually group.** The in-app Help text was still describing the pre-v2.2.16 prefix-heuristic — "labels sharing a prefix cluster under a device header" with `s24_*` / `tablet_*` examples — but v2.2.16 replaced that with structured grouping from Syncthing's own folder-to-device config. Text now reflects reality: folders group under the remote devices they're shared with; unshared folders fall under "Local only".
- **Stale comment on `_activePauseMinutes` corrected.** Field doc claimed it was used to set `.Checked` marks on pause-duration submenu items, but the menu rebuilds from scratch on every state change and no such marks are wired. Comment now describes actual uses: pause.dat round-trip, OSD text, and the "Resuming in N min" tooltip.

## v2.2.30 — 2026-04-18

### Update dialog polish
- **Chrome now matches Settings and Help.** The Update dialog was using `FormBorderStyle.FixedToolWindow` and leaving `ShowIcon` at the default, which gave it a cramped tool-window caption bar visibly different from the other two dialogs. Switched to `FixedDialog` + `ShowIcon=false` to match.
- **Scales correctly at 125/150/200% DPI.** The Update dialog inherited the WinForms default `AutoScaleMode.Font`; its absolute-pixel controls skewed visibly on HiDPI displays. Now `AutoScaleMode.Dpi`, matching SettingsForm and HelpForm.
- **Esc closes the dialog.** `_btnCancel` had its own `Click` handler but `CancelButton = _btnCancel` was never set on the form — so pressing Esc did nothing. Wired now.
- **Buttons are the same width and right-align symmetrically.** The two-button row was `Upgrade Now` (110 px) + `Cancel` (80 px) with a 45 px right margin — next to Settings' and Help's symmetric 16 px margins this read as "a bit off". Now both buttons are 110 px, ending at x=406 on a 420 px form.

## v2.2.29 — 2026-04-18

### Accessibility
- **Screen readers and keyboard-only users can use Settings properly.** Every focusable control in the Settings dialog — the two click-action combo boxes, the API key field, Syncthing path, Web UI URL, startup-delay spinner, the "..." browse button, the Web UI Open button, and the API-key reveal eye-toggle — now has an explicit `AccessibleName`. WinForms doesn't auto-associate Labels with adjacent controls the way HTML `<label for>` does, so screen readers were reading "edit", "combo box", "spin button", "button" with no context. The eye-toggle also picks up `TabStop = true` — its Segoe MDL2 glyph has no readable text, so keyboard users previously couldn't discover the reveal affordance at all.

## v2.2.28 — 2026-04-18

### Start-up
- **Browser-on-launch honors late toggles.** The one-shot "Start browser when Syncthing launches" latch was set at tray-startup if the setting was on, then fired as soon as the first poll confirmed Syncthing was reachable. But a user who opened Settings and turned the setting off during Syncthing's cold-start window (before it was reachable) would still get the browser popped at them when Syncthing finally came up. The latch now re-reads `StartBrowser` at fire time, so if it's off by then nothing opens.

## v2.2.27 — 2026-04-18

### Reliability
- **Native handles drop on fast-exit.** Three one-shot WinForms Timers (startup-delay, first-run-settings-open, 30-second-stability-proof) were created as local `var`s that only self-disposed inside their own Tick handlers. If the tray exited before the Tick fired (fast shutdown under 30 seconds — restart loop, Windows logoff, killed from Task Manager), the timers leaked their native timer handles until the finalizer thread eventually ran. They're now tracked in a `_oneShotTimers` field; Tick removes each one on fire, and `Dispose(bool)` stops + disposes whatever's left.
- **Syncthing launch no longer leaks a Process handle on a rare race.** `_launchedPid = p.Id` followed by a separate `p.Dispose()` left the handle leaked if `.Id` threw (process exited between `Process.Start` and the Id read — observed on slow disks with a corrupt `syncthing.exe`). The `Process` now lives in a `using` so disposal runs regardless; the PID is still captured to the int field before the scope ends.

## v2.2.26 — 2026-04-18

### Reliability
- **Inherited pause now survives a stale post-reapply snapshot.** When the tray restarts with a pause from the previous session, it re-POSTs `/rest/system/pause` to Syncthing on the first successful poll. The old shape of that code cleared the `_pauseNeedsReapply` flag as soon as the POST returned 200 — but the *next* poll could still see `allPaused == false` if its snapshot was fetched before Syncthing finished applying the pause, or if Syncthing silently rejected the request, or if an admin resumed concurrently. When that happened, the external-resume branch silently dropped the inherited pause the user hadn't touched. The flag now stays set until a subsequent poll actually observes `allPaused == true`; any unconfirmed tick just re-POSTs (idempotent server-side). Also marked the flag `volatile` — it's written from both the UI thread (Restore/MenuPause/ClearPauseState) and the poll thread (ReapplyInheritedPause) — matching the pattern already used on `_foldersLoadedSuccessfully` for cross-thread publication ordering.

## v2.2.25 — 2026-04-17

### Reliability
- **Tray no longer crashes on boot from a hand-edited INI.** `AppConfig.Load` used `int.TryParse` with no bounds on `StartupDelay`, then the constructor did `new Timer { Interval = _config.StartupDelay * 1000 }`. A value like `180000` (user typo, or muscle memory from milliseconds) overflows after `× 1000` to a negative `int`, and WinForms `Timer.Interval` throws `ArgumentOutOfRangeException` on anything `≤ 0` — silent first-line boot crash, no OSD, no log, no sign of what happened. The load path now clamps to `[0, 3600]`, matching the Settings-dialog NumericUpDown invariant.

## v2.2.24 — 2026-04-17

### Reliability
- **Tray menu no longer freezes when Syncthing is slow or dead.** Every menu click that talked to Syncthing — Pause, Resume, Rescan All, Rescan Folder, Check for Update, Upgrade Syncthing — was calling the REST API synchronously on the UI thread. With `HttpClient.Timeout = 5 s`, a dead or flaky Syncthing meant the menu stayed dismissed-but-frozen and the tray icon's tooltip stuck at "not responding" until the timeout popped. Worst offender: the pause auto-resume timer's deadline fire (user never clicked anything, yet their tray froze for up to 5 s). All seven HTTP sites now run on the thread pool via `Task.Run`; state mutations + icon/menu updates marshal back to UI via `RunOnUi`.
- **Pause always sends the POST.** Previously, switching an active 5-min pause to a 30-min pause re-used the local `_paused=true` state and skipped the REST POST — which meant if Syncthing had been resumed externally through its Web UI, the tray kept showing "paused" until the next 10-second poll reconciled it. Duration changes now always re-post; `/rest/system/pause` is idempotent server-side so this is purely an accuracy fix, not a behavior change for the happy path.

## v2.2.23 — 2026-04-17

### Reliability
- **Pause state machine is now thread-safe.** Three background-thread code paths were mutating UI-affine state directly on the thread pool: the auto-pause block (when the machine joins a public wifi), the auto-resume block (when returning to a private wifi), and the external-resume detector (when someone hits Resume in the Syncthing Web UI while the tray thinks sync is paused). Each called `_pauseTimer.Stop()` + `BuildMenu()` + `UpdateTrayIcon()` directly. WinForms `Timer.Stop()` off the UI thread is undefined — 99 runs of 100 the call silently succeeded; the 100th would throw or corrupt the timer queue, leaving a dangling auto-resume that never fires. Plus the `OnPowerModeChanged` wake handler read `_paused` and `_pauseResumeAtUtc` on the SystemEvents background thread before marshaling, racing with user-initiated MenuResume/ClearPauseState. All four sites now wrap the read/state/timer/menu writes in `RunOnUi(() => …)` so every pause-state mutation happens on the UI thread atomically.

## v2.2.22 — 2026-04-17

### Settings
- **No more jolt on Save.** The Save button used to run up to ~1200 ms of synchronous work on the UI thread: a 300 ms socket probe + up to 1500 ms discovery PATCH + 50-200 ms WScript.Shell COM call for the startup shortcut + the tray-refresh callback which itself fires three more HTTP GETs. Now `_config.Save()` (the actual INI write) runs inline — so the file is guaranteed on disk before the dialog closes — and everything after it runs on a pool thread. The dialog dismisses instantly; OSDs for any async failure still surface correctly via the existing self-marshaling path.

## v2.2.21 — 2026-04-17

### Settings
- **Button rows right-aligned with the form.** Both the top action row (GitHub / Update / Syncthing / Help / Check Config) and the bottom row (Save / Apply / Cancel) now end at x=394, giving a symmetric 16 px margin on both sides and visual alignment between the two rows. Previously the top row ended at x=384 (26 px margin) and the bottom row at x=370 (40 px margin) — enough asymmetry to read as "a bit off" on the bottom-right corner.

## v2.2.20 — 2026-04-17

### Help window
- **Backup Settings button.** Added between "Syncthing Docs" and "Close" in the Help window footer. Copies `SyncthingTray.ini` (containing the API key and every user-visible setting) to a timestamped sibling file — `SyncthingTray.ini.backup-YYYYMMDD-HHMMSS` — in the same directory. The confirmation OSD shows the new filename. This is the user-facing complement to the existing `.corrupt.bak` rotation that kicks in only on detected corruption — now users can snapshot pre-emptively before a Windows update, disk migration, or fresh-install test. All three footer buttons resized to 110 px wide with symmetric 19 px gaps.

## v2.2.19 — 2026-04-17

### Settings
- **Windows startup delay — text left-aligned.** Matches the convention every other input box in the dialog already follows (paths, Web UI, API key). NumericUpDown defaults to right-align which made it the only odd one out.

## v2.2.18 — 2026-04-17

Settings-dialog polish: clearer startup-delay control, no more jerk during auto-populate.

### Settings
- **Startup Delay → NumericUpDown.** Replaced the free-form text box with a spin-to-5 numeric control. Users can still type any value directly; the control clamps to `[0, 3600]` on both spinner and keyboard entry, so the separate range-check + error OSD in the save path is gone (value can't be invalid by construction).
- **Label clarified: "Windows startup delay".** The setting's primary purpose is the delay on Windows auto-start before SyncTray launches Syncthing — the new label makes that plain.
- **No more jerk when discovery auto-populates.** When Settings was open during a fresh Syncthing cold-start, the 2 s retry timer fired the 300 ms socket probe + up to 1500 ms HTTP GET synchronously on the UI thread — freezing the dialog at exactly the worst moment (right as Syncthing came alive). The HTTP work now runs on a pool thread via `Task.Run`; the UI update marshals back with `BeginInvoke` and runs inside `SuspendLayout` / `ResumeLayout` so the three checkboxes, their Enabled flips, and the warning-label removal repaint once at the end instead of cascading through five separate layout passes.

## v2.2.17 — 2026-04-17

No more mouse-spinner when opening a synced folder.

### Synced Folders
- **Open Folder hands off to a pool thread.** `Process.Start` was running on the UI thread, so a cold Explorer launch (100-500 ms native process fork + shell init) kept the tray's context menu and mouse cursor locked in "busy" state for ~1.5 s per click. Now the shell call runs on `Task.Run` and the menu dismisses instantly; the Explorer window still renders at whatever pace Windows manages it, but the tray stops spinning. Errors (bad path, permissions) surface as OSDs via UI-thread marshaling.
- **Uses `UseShellExecute=true` instead of spawning `explorer.exe` directly.** The shell reuses an already-running Explorer process where possible, meaningfully faster than forking a new one every click.
- **UNC paths skip the pre-check.** `Directory.Exists` on an unreachable network share could block the UI thread for the full SMB timeout (~20-30 s). Local paths still get the friendly "Folder not found" OSD; UNC paths delegate the existence check to the shell.

## v2.2.16 — 2026-04-17

Synced Folders now group by remote device (structured data), not by folder-label prefix.

### Synced Folders
- **Grouped by Remote Device.** Syncthing's `/rest/config/folders` includes a `devices` array per folder; the tray now uses that plus `/rest/config/devices` for human-readable names and `/rest/system/status` for the local myID (to filter self out). Each folder appears under every remote device it's shared with — a folder shared with 5 devices appears under all 5 device headers, which is correct and deliberate. The previous label-prefix heuristic (split on space/underscore, ≥2 folders) was fragile: it silently gave up on any naming convention that used hyphens or no separators at all.
- **"Local only" bucket.** Folders that aren't shared with any remote device fall under a "Local only" header at the bottom instead of disappearing into an unlabeled group.
- **Graceful degradation.** If the device roster fetch fails, device headers fall back to the short base32 handle (first hyphen-separated chunk of the device ID). If the myID fetch fails, the local device leaks as its own group until the next successful poll — ugly, not broken.
- **Dups are safe.** Each duplicated folder entry is an independent `ToolStripMenuItem` with its own click-handler captures; disposal cascades correctly from the parent menu on every rebuild. No shared refs, no leak, no double-fire.

## v2.2.15 — 2026-04-17

Synced Folders — singletons now render after the device groups, not before.

### Synced Folders
- **"Other" bucket sorts last.** The unlabeled singletons group (folders whose prefix doesn't appear on ≥2 folders) was rendering at the top of the Synced Folders submenu instead of at the bottom. Cause: the previous sort-sentinel trick used `\uFFFE` as a placeholder key intended to sort last, but `StringComparer.CurrentCultureIgnoreCase` is a linguistic comparer that treats reserved/non-character code points as ignorable — so the sentinel compared as empty string and pushed singletons above `s20`/`s24`/`tablet`. Replaced the sentinel trick with explicit ordering: named device groups first (alphabetical by prefix), singletons last.

## v2.2.14 — 2026-04-17

Snappier close when "Stop Syncthing when tray exits" is on.

### Exit
- **Close lag cut from ~10s to ~4s worst case.** The tray used to hold the UI frozen while `StopSyncthing` ran synchronously with a 5 s HttpClient timeout + 5 s polling wait. An in-flight poll-tick could queue a GET on the single localhost keep-alive connection that blocked the shutdown POST behind it — visible as a multi-second freeze between clicking Exit and the tray icon disappearing. Now timers are stopped first (no new polls can race), the shutdown POST is capped at 2 s, and the post-shutdown wait is capped at 2 s. If Syncthing hasn't exited by then the force-kill fallback takes over immediately.

## v2.2.13 — 2026-04-17

Network-adaptive restore for inherited auto-pauses.

### Pause
- **Auto-pause flag persisted.** `pause.dat` now stores whether the pause was triggered by network auto-pause (line 3: `0` = manual, `1` = auto). Legacy 2-line files from v2.2.12 are still accepted and default to manual.
- **Reboot-on-private-after-public-auto-pause adapts.** If the tray inherits an auto-pause from the previous session but the current network is private (or domain), the stale pause is dropped on startup instead of being re-applied to a freshly-launched Syncthing. Syncing resumes on boot automatically rather than requiring a manual Resume click. Manual pauses always re-apply regardless of network, preserving explicit user intent.

## v2.2.12 — 2026-04-17

Pause submenu polish + reboot-survival, plus two settings-flow fixes.

### Pause
- **"Until resumed" promoted to the top** of the submenu with a separator underneath, framing it as the primary action and the timed options as secondary choices.
- **Survives reboots.** SyncthingTray re-applies the inherited pause to Syncthing on the first successful poll after startup. Syncthing's own `/rest/system/pause` is runtime-only, so without the re-apply a reboot would have silently dropped the pause. Expired deadlines (pause.dat older than its timer) are skipped rather than re-applied just to auto-resume a moment later. User changes made in the Syncthing Web UI while the tray is offline still take precedence — the poll reconciliation catches that.

### Settings
- **"Start browser when Syncthing launches" now works on every tray start.** Previously the browser only popped when Syncthing itself cold-started, so closing and relaunching the tray while Syncthing kept running silently skipped the browser-open. The tray now owns browser-opening — passes `--no-browser` to Syncthing and fires OpenWebUI once per tray session on the first reachable poll.
- **Discovery section auto-refreshes.** When the dialog opens during a fresh cold start (Syncthing still binding its REST port), the three Discovery checkboxes used to be stuck disabled with "(could not read current state)" until the user closed and reopened the window. The dialog now retries `/rest/config/options` every 2 s in the background; once the read succeeds, the checkboxes populate and the warning label vanishes on its own.

## v2.2.11 — 2026-04-17

Timed pause + slimmer help text.

### Pause (MWBToggle-style submenu)
- **5 min / 30 min / Until resumed.** Right-click → Pause Syncing opens a submenu with three durations. Timed pauses auto-resume at the deadline and show the remaining time on the Resume item. "Until resumed" stays paused until you click Resume.
- **Survives sleep.** Deadlines are stored as absolute UTC time, so a 30 min pause through a 2 hr sleep resumes at wake rather than 30 min after wake.
- **Survives tray restart.** Active pause state persists to `pause.dat` in the app dir. Closing and reopening the tray mid-pause preserves the countdown.
- **External resume detected.** Hitting Resume in the Syncthing Web UI clears the local timer on the next poll, so a stale deadline can't double-fire the resume path.
- Double-click / middle-click still pause untimed — the click-path behavior from earlier versions is preserved.

### Help window
- **Content trimmed.** The verbose prose paragraphs are replaced with tight bulleted sections that still cover every tray interaction, menu item, settings group, and troubleshooting path — just faster to scan.

## v2.2.10 — 2026-04-17

Settings UI polish — tighter Tray Click Actions row.

### Settings
- **Click-action dropdowns sized to content.** Double-click and Middle-click combos were 250 px wide — wider than the path and API key fields in the same window. Shrunk to 160 px so each control's width matches its expected content, matching the convention used by Windows 11 Settings, VS Code, and JetBrains dialogs.

## v2.2.9 — 2026-04-17

Folders-by-device, proper API key masking, fuller help window, normal close button.

### Synced Folders
- **Grouped by device.** Labels that share a prefix (`s20_*`, `s24_*`, `tablet_*`) now cluster under a dimmed device header, with the prefix stripped from the child labels so the submenu reads as a true two-level list. Folders with unique names drop into an unnamed group at the bottom. Alphabetical-only sorting from v2.2.8 is replaced.

### Settings
- **API Key is masked.** The field starts hidden, matching how other apps present secrets. A Segoe MDL2 eye toggle next to the field reveals the key while pasting or verifying.
- **Normal close button.** The Settings window's tool-window chrome gave a cramped, oddly-placed X in the top right. Settings now uses the standard fixed-dialog chrome — full-size X in the usual spot.

### Help window
- **Proper help content.** The old terse bullet list is replaced with structured prose covering tray interactions, every context menu item, every Settings section, the API key workflow, troubleshooting the most common OSDs, and the diagnostic log location.
- **Section headers.** Content renders in a RichTextBox with blue section headers and wrapped body paragraphs, matching MicMute's help style but tuned for SyncTray's narrower window.

## v2.2.8 — 2026-04-17

Scannable Synced Folders menu — big readability win for users with many folders.

### Synced Folders
- **Alphabetical order.** The submenu followed Syncthing's config order, which was effectively random. Folders are now sorted case-insensitively, so device clusters like `s20_*`, `s24_*`, and `tablet_*` line up in natural groups instead of shuffling through the list.
- **Group separators.** A thin divider is drawn between letter-groups when either side has three or more folders, giving the eye a rest point on long lists while keeping isolated names grouped at the top.

## v2.2.7 — 2026-04-17

Help window fixes — no functional changes elsewhere.

### Help window
- **Status section no longer clipped.** The help text below "Status:" was painted past the window edge with no way to scroll. The help body is now inside a scrollable panel, so every line is reachable regardless of DPI.
- **Divider above buttons.** A subtle horizontal line now separates the help text from the **Syncthing Docs** and **Close** buttons, matching the divider under the title.

## v2.2.6 — 2026-04-17

A steadier tray, especially when Syncthing isn't running. No breaking changes.

### Smoother tray
- **No more stall when Syncthing is off.** Clicking the tray icon or opening Settings while Syncthing was stopped could hang the menu for 5–6 seconds on every click. Now the tray checks the API with a fast probe (about 1.5 seconds) and opens immediately either way.
- **Settings window actually comes to the front.** On first open, the window occasionally appeared behind other apps and you'd have to hunt for it in the taskbar. It now activates reliably.
- **Wake-from-sleep catches up fast.** Resuming from sleep, hibernate, or Win+L now triggers an immediate status refresh instead of waiting up to a full poll cycle for the tray to notice the network came back.
- **Apply vs Save.** Clicking **Apply** in Settings no longer re-runs the full folder reload — the folder list no longer flickers when you tweak one setting at a time. **Save** continues to run the full path as before.

### Clearer errors
- **"API key rejected" is its own message.** A wrong or stale API key in Settings now surfaces as a distinct error instead of folding into a generic "could not reach Syncthing" message, so you know exactly what to fix.
- **Fewer duplicate log lines.** When multiple different warnings fire in the same minute, each type is now logged once per minute instead of every occurrence piling up in `tray.log`.

### Display & paths
- **Crisper on high-DPI displays.** The app now declares Per-Monitor V2 DPI awareness, so text and icons render sharp on 4K displays and when dragging between monitors at different scales.
- **Long paths supported.** Paths over 260 characters in folder selection now work on Windows 10/11 with long paths enabled, instead of being silently truncated.

### Safer install paths
- **Network paths rejected for `syncthing.exe`.** Pointing the tray at `\\server\share\syncthing.exe` or similar UNC paths is now refused with a clear error, rather than launching an executable across the network.

### Packaging
- **Single-file release is smaller.** Publish compression is on and native runtime libraries are embedded inside the .exe — the download drops by roughly a third and nothing loose lands next to the binary.

## v2.2.5 — 2026-04-16

A safer self-update and a lot less guessing. No breaking changes — your existing settings keep working.

### Safer updates
- **Verified downloads.** Every update is checked against a published checksum before it lands on disk. If the check can't be done for any reason, the update is aborted — no more silently installing something unverified.
- **Clear recovery when an update fails** — you get a concrete next step instead of a broken install with no explanation.
- **Crash-during-update detection.** If a new version crashes within 30 seconds of launching, the next time you start SyncthingTray you're told what happened and pointed at the `.exe.old` backup so you can roll back.

### Honest status, no more lies
- **Auto-pause tells the truth.** When you move to a public Wi-Fi, SyncthingTray only claims "paused" if Syncthing actually paused. Previously the tray could flip to "paused" locally while syncing continued in the background.
- **Discovery settings reflect reality.** If the tray can't read Syncthing's current discovery state, those checkboxes are disabled with an explanation — you can't accidentally overwrite Syncthing's real config with the wrong values.
- **Save failures are reported** for Discovery, "Run on startup", the "Open Web UI" buttons, and the startup-delay field — no more clicking Save, getting nothing back, and wondering if it took.
- **Settings file recovery.** If `SyncthingTray.ini` is corrupt or locked, the tray says so, uses safe defaults, and preserves your original file as a `.corrupt.bak` before overwriting.
- **Fewer fake alerts.** The "file error detected" notification no longer cries wolf during a brief Syncthing hiccup, and the "Syncthing stopped unexpectedly" alert is rate-limited so a crash-restart loop doesn't spam you.

### Smoother tray
- **Tray stays responsive during Syncthing slowdowns.** If Syncthing's API hangs for a few seconds, the menu, tooltip, and right-click still open immediately.
- **First-run wizard only runs once.** Closing it without saving no longer triggers it again on the next launch.
- **Device connect/disconnect notifications** now tell you when the background polling that drives them has stopped working, so you're never silently left without them.
- **Multi-user machines.** On shared PCs, launching SyncthingTray no longer silently fails when another user also has it running.

### Diagnostics
- **New log file** at `%LOCALAPPDATA%\SyncthingTray\tray.log` — attach this to any bug report. 1 MB cap, rotates once. Disable with `DiagnosticLogging=0` in `SyncthingTray.ini` if you prefer.

## v2.2.4 — 2026-04-15

- **Marked as a Long-Term Release.** The in-app Update button is the recommended way to stay current.

## v2.2.3 — 2026-04-15

- **Update integrity check.** The in-app Update button now checks the downloaded file against a checksum published with each release.

## v2.2.2 — 2026-04-15

- **Works with WinGet.** If you installed through `winget install itsnateai.SyncthingTray`, the in-app Update button sends you back to `winget upgrade` instead of trying to overwrite the managed install.
- **Release build is a true single file** — no loose `.dll` files next to the .exe anymore.

## v2.2.1 — 2026-04-15

- **Fixed a broken build pipeline** that was producing releases with missing pieces.

## v2.2.0 — 2026-03-25

### New Features
- **One-click self-update** from the Settings window — download, verify, and install the latest release without leaving the tray.
- **Auto-discover `syncthing.exe`** — if it isn't sitting next to SyncthingTray, the tray checks your PATH and the usual install locations automatically.
- **Discovery settings default to off** when the settings file is missing them, so a fresh install never quietly announces you to the network.

### Performance
- **Faster status checks** — the tray spends less time scanning running processes when Syncthing is already running.
- **Lower Syncthing CPU footprint** — Syncthing is launched with a reduced thread budget so it's gentler on the rest of your machine.

### Bug Fixes
- **No more tray crash under heavy memory pressure** — the tray keeps running and recovers on the next check instead of dying.
- **Clearer Auto-update label** in Settings — it's for Syncthing updates, not SyncthingTray updates.

## v2.1.0 — 2026-03-18

### New Features
- **Rescan Now** — trigger a rescan for all folders or an individual folder right from the tray menu.
- **Configurable double-click and middle-click actions** — Web UI, Rescan, Pause/Resume, or Do Nothing.
- **Dark-themed dropdowns** in Settings.

### Bug Fixes
- **No more rare crash when exiting during the startup delay.**
- **Pause/Resume no longer gets stuck reporting the wrong state** — the tray now reflects the real Syncthing state after you pause or resume.
- **Conflict detection works for all your folders**, not just the one named "default".
- **Tray icon recovers after Explorer restarts** — the icon comes back automatically instead of vanishing until relaunch.
- **Stopping Syncthing only stops the instance SyncthingTray launched** — no longer kills other `syncthing` processes you may be running separately.
- **Config save errors are reported** instead of silently failing.
- **Menu no longer freezes while Syncthing is shutting down.**
- **No resource leak during startup shortcut handling.**
- **Menu opens faster when Syncthing's API is unreachable** — the tray fails fast (about 5 seconds) instead of waiting through a long timeout chain (~25 seconds).
- **Rescan is skipped when Syncthing isn't running** instead of throwing a cryptic error.

### Robustness
- **Faster update and status checks** with connection pooling.
- **More robust API parsing.**
- **Startup delay no longer freezes the tray** — you can still open menus while SyncthingTray is waiting to launch Syncthing.
- **Save / Apply / Cancel** in Settings, like standard Windows dialogs.
- **OSD notifications** replace intrusive pop-up dialogs for informational messages.
- **Smoother dark menu rendering.**

## v2.0.0 — 2026-03-17

### New Features
- **Full rewrite from AutoHotkey v2 to C# .NET 8 WinForms.**
- All v1.6.0 features preserved in the C# port.
- **Dark-themed context menu.**
- **No overlapping status polls** when Syncthing's API is slow — tray stays responsive.
- **Lower idle overhead and snappier tooltips** — the tray uses less CPU and updates only when something actually changes.
- **Embedded icons** — tray icons are built into the .exe, no external `.ico` files needed at runtime.

## v1.6.0 — 2026-03-16

### New Features
- Middle-click tray icon toggles pause/resume (configurable in Settings)
- Overclick safeguard — shared cooldown prevents rapid Start/Stop/Restart/Pause desync

## v1.5.0 — 2026-03-15

### New Features
- Middle-click tray icon toggles pause/resume
- Device counter in tooltip (e.g. "2/3 devices")
- Synced Folders submenu — open any synced folder in Explorer, rescan individual folders
- Configurable Syncthing exe path and Web UI URL
- First-run wizard — auto-opens Settings when no config exists
- Startup delay setting (seconds before launching Syncthing)
- Portable mode — auto-detected on removable drives (disables startup shortcut)
- Discovery toggles (Global, Local, NAT Traversal) in Settings
- Config Check utility (validates exe, process, API, discovery)
- Network auto-pause on public networks (WMI-based)
- Auto-update check for Syncthing (daily, rate-limited)
- Help window with usage guide
- GitHub and Syncthing buttons in Settings
- ToolTip cleanup (auto-dismiss pattern)

## v1.4.0 — 2026-03-14

### New Features
- Device connect/disconnect notifications
- File conflict (pull error) detection
- Pause/Resume syncing via tray menu

## v1.3.0

### New Features
- Start Browser setting — optionally open Web UI when Syncthing launches

## v1.2.0

### New Features
- Initial release
- Launches Syncthing hidden (no console window)
- Tray icon with sync/pause state icons
- Start, stop, restart Syncthing from tray menu
- Open Syncthing Web UI on double-click
- Graceful shutdown via REST API with process kill fallback
- Crash detection with audible alert
- Run at Windows startup (Startup folder shortcut)
- Single-instance enforcement
- Tray icon recovery after Explorer restarts
