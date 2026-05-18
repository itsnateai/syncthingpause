# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingPause (formerly SyncthingTray, renamed at v3.0.0) are documented here.

## v3.2.5 — 2026-05-18

### Fix: UpdateDialog single-button-mode centering at 125%+ DPI + marquee scaling

Verifier round on v3.2.4 surfaced two CRITICAL findings (convergence between Sonnet and Opus gap-audit pairs on the same issue — load-bearing signal). v3.2.4 added a `CenterX(int controlWidth)` helper that correctly centers controls in the **ctor** (where ClientSize.Width and the literal widths are both design-px) but breaks in the three **post-Show** handlers (`ShowVersionComparison`, `ShowError`, `ShowWingetNotice`) — after the AutoScale walk runs at handle creation, `ClientSize.Width` returns physical-px (e.g. 450 at 125%) while `_btnW = 100` is still a design-px literal. The mixed-units math `(450 - 100) / 2 = 175` lands the button 13 px right of true center against its post-Show physical Width of 125. `_btnRowY = 108` design-px assigned post-Show as physical (where _btnAction.Top is actually 135 physical) put the single-Cancel button 27 px above its row. Net effect at 125 % DPI: the OK / Cancel button on the "You're on the latest version" / error / winget screens drifted up-and-right of the layout grid.

Second finding: the marquee progress timer's `barW = 80` design-px literal was compared against the already-physical `_progressOuter.Width`, so the marquee bar rendered as 80 physical-px (≈ 20 % of a 405-physical-px container at 125 %) instead of the intended 25 % proportional width.

### What's underneath

- **`UpdateDialog.cs · ShowVersionComparison / ShowError / ShowWingetNotice`** — replaced `new Point(CenterX(_btnW), _btnRowY)` with `new Point((ClientSize.Width - _btnCancel.Width) / 2, _btnAction.Top)`. Both operands are post-Show physical-px, math is unit-consistent.
- **`UpdateDialog.cs · CenterX docstring`** — clarified that the helper is for **ctor-time use only**; documents the post-Show divergence and the alternative pattern used in the handlers. Removed the stale `"_ = 0;"` reference that didn't correspond to any code.
- **`UpdateDialog.cs · marquee Tick`** — `step` and `barW` literals (4 / 80 design-px) now wrapped in `LogicalToDeviceUnits` so the bar's screen size scales proportionally with the container. `_progressFill.Size.Height` now reads `_progressOuter.Height` (post-Show physical) instead of a raw 16-px literal — bar always fills its container's vertical extent at every DPI.
- **`UpdateDialog.cs · ShowVersionComparison + DownloadFileAsync`** — same `_progressOuter.Height` pull replaces two more raw 16-px height literals in `_progressFill.Size` assignments.
- **`SyncthingPause.csproj`** — 3.2.4 → 3.2.5.

### Known limitations (not fixed in v3.2.5)

- **`SettingsForm.AddSectionHeader` DeviceDpi pre-handle:** verifier T2 flagged that `DeviceDpi` returns the primary monitor's DPI before the form's HWND exists. On a multi-monitor setup where the primary is 96 DPI and Settings opens on a secondary 125 % monitor, the divider math reverts to a no-op. Not a regression vs pre-v3.2.3 and the fix still works for single-monitor 125 % users (Suzy's reported case). Tracked for a future fix that re-runs the divider math on `OnDpiChanged`.
- **Allowlist host-suffix scoping:** verifier T3 noted `*.githubusercontent.com` accepts cross-repo asset routing. SHA256SUMS verification catches tampered binaries, so this is defense-in-depth rather than a current vulnerability. Tighter path scoping is a future hardening play.
- **Static GDI brush class-load ordering:** `ComboBgBrush` / `ComboSelectedBrush` in `SettingsForm.cs` capture the active palette at first class load. The v3.2.0 theme-restart design assumes `Theme.Initialize` ran first, which is true under normal startup but not enforced by the type system.

### Verifier coverage

Build clean (0 warnings, 0 errors). 92/92 tests pass. Self-contained single-file publish deployed to `C:\Users\nate\proggy\Tools\syncthingpause\SyncthingPause.exe` and propagated to Suzy via the `ToSuzy` Syncthing share.

## v3.2.4 — 2026-05-18

### Fix: UpdateDialog centering + padding

Reported on Suzy's 125% display: the **Update** dialog (opened from Settings → Update) was *quite large and full of unused padding*, and the status/detail text wasn't centered on her screen even though it was on a 100% display. Root cause was a familiar one for this codebase — every horizontally-positioned control in the dialog used a hand-tuned `Location.X` literal, and those literals had drifted off-center as the form size and button widths evolved over the v2.x/v3.x history. The status label sat at `Location(20, 20)` with `Size(370, 24)` inside `ClientSize(420, 180)` — left margin 20, right margin 30, so the label was off-center by 10 design-px (≈ 12 px at 125% DPI, visible to the eye). The two-button row at `(166, 112)` + `(296, 112)` ended up 11 px right of form center; the single-Cancel mode at `(170, 112)` was 15 px right. And the dialog had ≈ 86 px of dead vertical padding around 94 px of content (~48% empty space) — the "unused padding" complaint.

### What's underneath

- **`UpdateDialog.cs · ctor`** — every Location.X is now derived from a new `CenterX(int controlWidth)` helper that computes `(ClientSize.Width - controlWidth) / 2` in design-px. Labels, progress bar, and the button row all center automatically off the form's current size, so future ClientSize tweaks don't need to recompute each Location manually.
- **`UpdateDialog.cs · CenterX`** — new helper method, instance scope. Math runs in design-px (ClientSize.Width is the design-baseline value set before any Controls.Add); AutoScaleMode.Dpi scales the returned Point at realization.
- **`UpdateDialog.cs · _btnW / _btnRowY`** — two new class-level constants (100 design-px, 108 design-px) so the three single-button-mode handlers (`ShowVersionComparison`, `ShowError`, `ShowWingetNotice`) can re-center `_btnCancel` via `CenterX(_btnW)` instead of duplicating literal Point values. Pre-v3.2.4 each handler had a hardcoded `new Point(170, 112)` — a stale mid-point for the old 420-wide form's 110-wide button, off-center by 15 px and frozen against future geometry changes.
- **`UpdateDialog.cs · ClientSize`** — tightened from `(420, 180)` to `(360, 154)`. Vertical content (status 24 + detail 20 + progress 16 + button row 30) now sits inside ~60 px of padding (16 top, 16 bottom, 6/12/20 between sections) instead of the old ~86 px (~37% of the dialog was empty space).
- **`UpdateDialog.cs`** — button widths tightened 110 → 100; "Upgrade Now" measures ~80 px text, leaves 20 px chrome margin which fits comfortably at 200% DPI. Progress bar height tightened 18 → 16 with matching updates in the marquee timer tick, the DownloadFileAsync progress writer, and ShowVersionComparison's progress-reset.
- **`SyncthingPause.csproj`** — 3.2.3 → 3.2.4.

### Verifier coverage

Build clean (0 warnings, 0 errors). 92/92 tests pass. Self-contained single-file publish deployed to `C:\Users\nate\proggy\Tools\syncthingpause\` (relaunched, PID 45084 at deploy time), and dropped into the `ToSuzy` Syncthing share for propagation to the `sendmorepickles` device. Visual smoke at 100% pending on Asus; 125% smoke pending on Suzy once the sync lands.

## v3.2.3 — 2026-05-18

### Fix: AddSectionHeader divider mixed-DPI math + cleanups

Verifier pass on v3.2.2 surfaced one residual mixed-DPI bug: `AddSectionHeader` measures the section-header label width with `TextRenderer.MeasureText` (returns device-px at the form's current DPI), adds it to the design-px `x` parameter and a `+ 4` design-px gap, then passes the result to `AddDivider`, whose `Location`/`Size` autoscale on `Controls.Add`. The device-px width gets double-counted by the autoscale ratio — at 125% DPI the divider lands ~25% of the label width further right than intended, producing visibly misaligned section dividers under the "General", "Paths", "API", "Discovery", "Updates" headers. Same mixed-DPI anti-pattern v3.2.2 claimed to eliminate; v3.2.2's scope was the buttons + NUD + checkbox widths, not the section-header layout — so this one survived. Fix converts the MeasureText result back to design-px via `*(96.0/DeviceDpi)` before mixing with the design-px constants.

### Cleanups

Verifier also surfaced two minor inconsistencies introduced by the v3.2.2 helper refactor:

- `Controls.Add(btnCheckNow)` at the end of `BuildUpdatesSection` was redundant — `AddSizedButton` already calls `Controls.Add` internally. WinForms silently no-ops a duplicate add to the same parent, so the bug was harmless, but the call was inconsistent with the other five `AddSizedButton` callers (which correctly omit the second add).
- `AddLabel("seconds", 248, y, ...)` in `BuildGeneralSection` passed `x=248` as a leftover from the pre-anchor pattern; the real `Location` is assigned on the next line from `_nudDelay.Right + LogicalToDeviceUnits(6)`. Replaced `248` with `0` to match the placeholder convention used for `btnSyncthing` in `BuildButtonRow`.

### Known limitations (not fixed in v3.2.3)

Verifier flagged three concerns that are structural / scope-creep rather than v3.2.x regressions, captured here for the record:

1. **PerMonitorV2 cross-monitor drag.** The csproj declares `ApplicationHighDpiMode=PerMonitorV2` but `SettingsForm` has no `OnDpiChanged` override. Every post-`Controls.Add` `Location` assignment (every button in this dialog, plus the NUD + reveal eye + Theme radios) holds physical-px values captured at the source monitor's DPI; if the form is dragged to a monitor at a different DPI mid-session, WinForms scales the form chrome but the manually-set Locations don't re-derive. Will need a separate fix that re-runs the position-chaining math on `OnDpiChanged`. Not a regression vs pre-v3.2.2 — the existing form had the same blind spot.
2. **150-200% DPI scale untested empirically.** The fix was validated on 100% (Asus) and reported against 125% (Suzy). The hand-tuned button widths chose 25-40% chrome margin around the measured text, which should fit at 200% with the linearity assumption of `AutoScaleMode.Dpi`, but Segoe UI hinting can produce non-linear glyph widening at certain scale jumps. No 175%/200% smoke yet.
3. **Localization.** All Button `Text` values are hardcoded English. Long localized strings (German "Verbindung wird geprüft" for "Check Now", "Konfiguration prüfen" for "Check Config") would clip in their fixed-width buttons. Out of scope for v3.2.x — the app is English-only.

### What's underneath

- **`SettingsForm.cs · AddSectionHeader`** — divider math fixed; `TextRenderer.MeasureText(text, _sectionFont).Width` is now divided by `DeviceDpi/96.0` before being added to design-px `x` and the `+ 4` gap.
- **`SettingsForm.cs · BuildUpdatesSection`** — redundant `Controls.Add(btnCheckNow)` removed.
- **`SettingsForm.cs · BuildGeneralSection`** — `AddLabel("seconds", 248, ...)` → `AddLabel("seconds", 0, ...)`.
- **`SyncthingPause.csproj`** — 3.2.2 → 3.2.3.

### Verifier coverage

92/92 tests pass. Build clean (0 warnings, 0 errors). Self-contained single-file publish deployed to `C:\Users\nate\proggy\Tools\syncthingpause\SyncthingPause.exe` (v3.2.0 backed up as `.v320.bak`). Visual smoke at 100% on Asus confirmed; the section-header divider fix is most visible at 125%+ where pre-v3.2.3 the dividers under "General"/"Paths"/etc. drifted to the right.

## v3.2.2 — 2026-05-18

### Fix: Settings dialog clipping on 125%+ DPI displays

Reported on a 125% display-scale laptop (Suzy's screen): the **Web UI → Open** button rendered as **"Ope"**; the **GitHub / Update / Syncthing / Help / Check Config** row got smushed against the form's bottom edge with descenders on `g` and `p` truncated; **"Syncthing"** showed up as **"Syncthi"**; **"Check Config"** lost the trailing `ig`; the **Windows startup delay** `NumericUpDown` appeared visually underneath the trailing word "delay" of its own label rather than to the right of it. Root cause was a mix of (a) button widths that fit the text at 100% scale with no margin, so DPI auto-scaling at 125%+ didn't leave enough chrome for the slightly-wider glyph rendering at higher DPI, and (b) mixed-DPI math in several spots — e.g. `Width = 200` overwriting an already-autoscaled checkbox width, or `_cbRelay.Bottom + 4` mixing physical-px with design-px. v3.2.2 hand-tunes button widths with a 24-32 design-px chrome margin around the measured text (uniform visual rhythm at 100%, comfortable headroom at 125-200%) and converts every post-Add design-literal override via `LogicalToDeviceUnits` so design-px and device-px never get mixed in the same expression.

### Tried first, then reverted: AutoSize

The first v3.2.2 draft used `AutoSize = true, AutoSizeMode = GrowAndShrink, Padding = new Padding(8, 4, 8, 4)` to let each button grow to fit its text at any DPI. In practice this produced visibly oversized buttons at 100% scale — Button.AutoSize stacks its measured-text width with the custom Padding *and* with the internal chrome margin Button adds for click-target ergonomics. Result: the `...` browse button rendered as a ~64×40 square, "Open" landed below the row baseline because its AutoSize height pushed it past the textbox row, the reveal-eye glyph was huge, and "Check Now" extended past the section divider. Reverted in favor of hand-tuned widths (and the rest of the DPI-math fixes), kept the position-chaining pattern off the prior sibling's autoscaled `Right` edge with `LogicalToDeviceUnits` gaps.

### What's underneath

- **`SettingsForm.cs · AddLinkButton`** — was `void`, now returns `Button` so callers in `BuildButtonRow` can chain Location off the autoscaled `Right`. Width is now a required parameter (it was implicit in the old fixed-size signature too).
- **`SettingsForm.cs · AddSizedButton`** — new helper for non-link buttons (Update, Help, Check Config, Check Now, Browse `...`, Open). Same fixed-Size pattern as `AddLinkButton` minus the link click handler. Caller supplies design-px width.
- **`SettingsForm.cs · BuildButtonRow`** — top row widths hand-tuned: GitHub 68→72, Update 58→62, Syncthing 68→**82** (was clipping to "Syncthi" at 125%), Help 58→52, Check Config 100→98 (with the chrome margin redistributed; the prior 100 design-px left only ~22 px of chrome inside the button, not enough to absorb DPI-scale glyph widening). Positions chain off the prior button's autoscaled `Right + LogicalToDeviceUnits(5)`. Bottom row (Save / Apply / Cancel) kept at fixed `Size(114, 30)` — 6-char "Cancel" at 9pt Segoe UI fits comfortably even at 200% DPI and the trio's symmetry depends on uniform width.
- **`SettingsForm.cs · BuildPathsSection`** — Browse `...` width 50→**32** (three dots only need ~24 chrome; the prior 50 looked disproportionately wide), Open width 50→**54** (was clipping to "Ope" at 125%). Anchored off `_edSyncExe.Right` / `_edWebUI.Right` with a 4-design-px gap via `LogicalToDeviceUnits`.
- **`SettingsForm.cs · BuildApiSection`** — reveal-eye width 52→**40** (single Segoe MDL2 glyph doesn't need 52 px; tighter button looks more icon-like). Anchored off `_edApiKey.Right`.
- **`SettingsForm.cs · BuildUpdatesSection`** — Check Now width 90→**92** (small bump for chrome margin at 125%). Anchored off `_cbAutoUpdates.Right + LogicalToDeviceUnits(10)`.
- **`SettingsForm.cs · BuildGeneralSection`** — `_nudDelay` now positioned off `lblDelay.Right + LogicalToDeviceUnits(8)` instead of a fixed `x=160`. The "seconds" label trails off `_nudDelay.Right + LogicalToDeviceUnits(6)`. At 125% DPI the label "Windows startup delay:" measured ~169 physical-px starting at x=20 — under the previous fixed-x=200 NUD position by ~11 px, so the label's "delay" word was visually overlapping the NUD's spinner band.
- **`SettingsForm.cs · BuildDiscoverySection`** — `_cbGlobal.Width = 200;` and `_cbLocal.Width = 200;` raw-literal overrides (each one assigning a design-px value to an already-autoscaled control, leaving the width at 200 physical instead of the intended 250 at 125% DPI) now wrapped in `LogicalToDeviceUnits(200)`. Same fix for `_cbAutoUpdates.Width = 220` in `BuildUpdatesSection`.
- **`SettingsForm.cs · OnDiscoveryRetryTick`** — warn label position fixed: was `AddLabel(..., 36, _cbRelay.Bottom + 4, ...)` which mixes `_cbRelay.Bottom` (physical-px) with the 36 x-literal and `+ 4` (design-px), then double-scales the y because AddLabel's `(x, y)` args autoscale on Controls.Add. Now passes `(0, 0)` placeholders to AddLabel and reassigns `Location` to `(LogicalToDeviceUnits(36), _cbRelay.Bottom + LogicalToDeviceUnits(4))` post-Add.
- **`SettingsForm.cs · ctor`** — bottom padding bumped `y += 40` → `y += 48`. At 125% the previous 40 design-px (50 physical) was close enough to the bottom-row buttons that descenders on `g` and `p` rendered against the form's chrome.
- **`SyncthingPause.csproj`** — 3.2.1 → 3.2.2.

### Verifier coverage

Build clean (0 warnings, 0 errors). 92/92 tests pass against the pre-revert DLL — the AutoSize→hand-tuned-widths revert touches only helper signatures and width-literal values, no logic that the existing unit tests cover. Visual smoke pending against the running tray on Asus 100% scale and Suzy's 125% scale.

## v3.2.1 — 2026-05-18

### Fix: "Check Now" upgrade probe no longer freezes Settings

Pre-v3.2.1, clicking **Settings → Updates → Check Now** ran `_api.Get("/rest/system/upgrade")` synchronously on the UI thread. On a slow daemon or transient network this would freeze the dialog for up to 5 s (the default API timeout) before either showing the upgrade dialog or the "Syncthing is up to date" OSD. v3.2.1 moves the HTTP call onto a thread-pool task — same async pattern v3.2.0 applied to the Discovery probe at SettingsForm open. The double-click guard, modal upgrade dialog, OSD outcomes, and exception handling are unchanged in behavior; the only user-visible difference is the dialog no longer locks up while the request is in flight.

### Defensive: OnApply "Settings applied" OSD timing invariant documented

Round-3 verifier review flagged the OSD ordering between the synchronous `"Settings applied"` toast and the pool task's `"Discovery settings not saved…"` failure OSDs as a CRITICAL race. Skeptical re-trace showed the race resolves correctly — the success OSD fires first (UI thread, ~0 ms), pool errors fire later (10-1500 ms via `BeginInvoke`) and correctly overwrite the success message so the user sees the most-relevant outcome. v3.2.1 adds a comment block above the OSD call explaining the invariant so a future maintainer doesn't "fix" it by moving the OSD into the pool task tail (which would reverse the ordering and bury error states). Pure documentation — no behavior change.

### What's underneath

- **`SettingsForm.cs`** — Check Now click handler converted from synchronous lambda to `async (_, _) =>`. HTTP call wrapped in `Task.Run`; result marshaled back to UI thread implicitly by `await`. `IsDisposed`/`_disposed` guard added after the await so a dialog-close mid-poll doesn't mutate disposed controls. Same pattern as the v3.2.0 Discovery probe fix. Final `btnCheckNow.Enabled = true` gated on `!_disposed && !IsDisposed` to skip the touch when the form is gone. Catch block on the post-HTTP body narrowed and re-described — the original "Could not reach Syncthing API" message is wrong for that path (the HTTP itself is now caught earlier); replaced with "Check Now failed — see tray.log" so the typed-exception name in the log is the diagnostic.
- **`SettingsForm.cs`** — 14-line comment block in `OnApply`'s `ApplyResult.Normal` case explaining why the synchronous "Settings applied" OSD intentionally fires before pool errors, with a "DO NOT fix this" footnote citing the round-3 verifier trace.
- **`SyncthingPause.csproj`** — 3.2.0 → 3.2.1.

### Verifier coverage

92/92 tests pass. Build clean. No new tests — both changes are surgical (one async refactor on a path that mirrors the v3.2.0 Discovery probe pattern, one comment block).

## v3.2.0 — 2026-05-17

### Feature: user-selectable Dark / Light theme

SyncthingPause shipped dark-only since the v2.0.0 AHK→C# port — a Catppuccin Mocha palette baked into every dialog, menu, and OSD. v3.2.0 adds a Light palette (v2.1.x classic — pure white BG, near-black text, brand-blue `#2255AA` headers, cornsilk `#FFF8DC` focus tint) and a toggle to pick between them in Settings → Discovery. The toggle lives on two lines in the right column of that section: a "Theme:" label above two "Dark" / "Light" radio buttons. Persists via the existing `SyncthingPause.ini` (`ThemeMode=Dark` / `ThemeMode=Light`).

### Restart-to-apply, but seamless

The theme switch fires a fresh process when you click **Save** — same exe, same install location, same single-instance mutex. Within ~1 second the old window disappears and the new one comes up in the picked theme, with a brief toast confirming. The dying instance and the replacement coordinate via the existing single-instance mutex (5 s retry window on `--after-theme-restart`); no state is lost across the swap because the INI is written before the relaunch fires.

Why restart and not live-swap: the GDI brush/pen caches in the tray context menu renderer, the OSD form, and the various dialog forms are `static readonly` field initializers that capture the active palette at first class load. Invalidating them mid-process would mean re-creating every dialog instance and flushing every cached brush across half a dozen classes — a much bigger surface for a tiny user-visible win. Restart keeps the GDI cache contract honest.

### What's underneath

- **`Theme.cs`** (new, ~150 LOC) — dual palette static class with `Initialize` / `ResolveIsDark` / `IsSystemLightTheme` and 13 colour accessors (Bg, Fg, FgDisabled, Dim, HighlightBg, EditBg, Divider, AccentBlue, AccentGreen, AccentRed, AccentWarn, OsdBorder, ComboSelectedBg). Light palette is v2.1.x classic (pure white `#FFFFFF` BG, near-black `#1E1E1E` text, secondary text `#555555` for ~7.5:1 contrast / WCAG AAA, divider `#C8C8C8`, brand-blue `#2255AA` headers, forest-green `#2E7D32` online-device header, deep-red `#C62828` offline-device + warning, cornsilk `#FFF8DC` for hover + OSD pill bg, faint off-white `#F8F8F8` for input inset). Selected over Catppuccin Latte (the template's Variant A) — Latte's cool tint read as eye-strain at dialog scale on first look; v2.1.x classic mirrors the pre-port feel pixel-for-pixel on the slots that matter.
- **`AppConfig.cs`** — new `ThemeMode` property + case-insensitive Load + canonical-case storage so a hand-edited `themeMode=light` parses and stores as `Light`. Unknown values (typos, future "System" tier) keep the current default rather than silently clobbering.
- **`SettingsForm.cs`** — two radio buttons in the Discovery section's right column, label on the row above. Save handler snapshots the prior `ThemeMode`, detects change, and spawns the replacement process with `--after-theme-restart` after the existing discovery PATCH / RunOnStartup paths have kicked off.
- **`Program.cs`** — parses `--after-theme-restart`, extends the single-instance mutex retry from 3 s to 5 s on relaunch paths (covers the dying-instance WinForms teardown window), suppresses the same-name Kill-on-mutex-clash for relaunch (the dying instance is exiting normally, killing it would race the INI write), and fires a delayed toast confirmation ~800 ms post-launch.
- **`TrayApplicationContext.cs`** — `Theme.Initialize(...)` runs immediately after `_config = new AppConfig(...)` and before the `DarkRenderer = new DarkMenuRenderer()` body-assign. The old `static readonly DarkMenuRenderer DarkRenderer = new()` field-init was demoted to instance + ctor-body so it can't fire before Theme is set. Device-name menu header colors (Online green / Offline red / Paused grey) moved from `static readonly Color` fields to property getters that route through `Theme.*` — properties evaluate lazily so they always see the post-Initialize palette.
- **`DarkMenuRenderer.cs`, `OsdToolTip.cs`, `HelpForm.cs`, `UpdateDialog.cs`, `SyncthingUpdateDialog.cs`** — every `private static readonly Color FooColor = Color.FromArgb(...)` literal replaced with `= Theme.Foo`. These classes load lazily (first dialog open / first toast / first menu build), well after `Theme.Initialize` runs in the tray context's constructor body. The class name `DarkMenuRenderer` was kept through the dual-theme migration to avoid a sprawling rename — the class is theme-aware regardless.

### Verifier coverage

- 92 tests pass (87 prior + 5 new ThemeMode tests: defaults-to-Dark on first-run, round-trips Dark, round-trips Light, unknown-value keeps default, case-insensitive parse stores canonical). Build: 0 warnings, 0 errors.
- Tested locally on Win 11 Pro 26H2 at 100 % display scale.
- Pattern lifted from the canonical `_templates/snippets/csharp/winforms-user-selectable-theme.md`, which documents both Variant A (Catppuccin Latte) and Variant B (v2.1.x classic, what we shipped) — the template absorbed two rounds × 6 verifier agents at the origin app (CapsNumTray v2.4.6). v3.2.0 is the second adopter of the v2.1.x classic variant after MWBToggle v2.5.18.

### Migration notes

- Existing users have no `ThemeMode` key in their INI; the default `Dark` matches the visual experience they had on v3.1.x — no surprise theme swap on first launch.
- The Settings dialog's Discovery section gains a right column; the form width is unchanged (410 px), and the new controls sit in the previously-empty space alongside the Global / Local / NAT Traversal checkboxes.
- WinForms' OS-owned dropdown panel for ComboBox still paints with system colors regardless of our theme pin (white BG / black text on Win 11). This is a known WinForms limit; owner-drawing the dropdown panel would add ~80 LOC for a minor cosmetic win — deferred. The closed dropdown (which we own-draw) does honor the theme.

## v3.1.0 — 2026-05-16

### Feature: explicit one-click upgrade path for the Syncthing daemon

Settings → **Check Now** used to be a read-only probe: it asked the Syncthing daemon whether a newer version was available and showed the answer in a brief OSD ("Update available: v2.1.0" or "Syncthing is up to date"). To actually trigger the upgrade you had to right-click the tray icon → **Check for Updates** and click a second time (the existing tray-menu path posts to `/rest/system/upgrade`). Two surfaces, two clicks, no in-place affordance.

v3.1.0 collapses that into one path: when **Check Now** reports an update available, a small modal dialog opens — visually consistent with the existing GitHub self-updater dialog — showing current version → latest version with an **Upgrade Now** button. Clicking it POSTs to Syncthing's upgrade endpoint, then watches `/rest/system/version` every second until the daemon restarts on the new build (~5–15s typical, 60s ceiling). A toast confirms completion. If the daemon doesn't come back in time, the dialog says so explicitly and points you at the tray icon to verify.

The tray-menu **Check for Updates** path is unchanged — if you've trained your fingers on it, it still works exactly the same way.

### What's underneath

- **`SyncthingUpdateDialog.cs`** (new, ~390 LOC) — dark-themed modal mirroring `UpdateDialog`'s visual baseline, stripped of file-swap/SHA256/rollback machinery (Syncthing handles its own binary swap atomically, so the dialog only needs to track the request and watch for the version flip).
- **`UpdateDialog.cs`** — new shared `internal static void ShowToast(string)` helper; `ShowUpdateToast()` now delegates to it instead of duplicating the timer+ToastWindow dance. The existing GitHub self-updater dialog also gained a `_busy` synchronous double-click guard for parity (previously `Enabled = false` alone was racy across the message pump — a fast double-click could re-enter the download-and-swap flow).
- **`SettingsForm.cs`** — **Check Now** handler wired to the new dialog; gained its own double-click guard and replaced two bare `catch` blocks in **Check Config** with typed catches that surface to `tray.log`.
- **Version-flip detection has two modes.** If Syncthing's upgrade-check response included a `running` field (typical), the poll accepts any version change as success. If it didn't (`running` missing or `"unknown"`), the poll falls back to exact major.minor.patch match against the target — so pre-release suffixes (`v2.1.0-rc.1`) and build metadata (`v2.1.0+12-gabc123`) don't false-timeout the dialog on real Syncthing version strings.
- **Diagnostics.** The dialog logs every decision point to `tray.log`; if the 60s poll times out, the last observed HTTP status is included (`Last status=401` → bad API key, `Last status=-1` → couldn't reach for the whole window).

### Verifier coverage

- 4 rounds of pair-by-topic verifier swarms (Sonnet + Opus on Diff-clean / Gap-audit / Code-review). Round 1 found 8 issues (use-after-dispose in async-void continuations, missing double-click guards, silent-failing bare catches, magic-string drift across files); Round 2 caught 4 (NormalizeVersion breaking on pre-release tags, `_busy` reset asymmetry); Round 3 caught 1 (`_lastPollStatus` sentinel mismatch); Round 4 caught 1 (a regression the new `_busy` guard introduced on the cancel-during-download path of the existing self-updater). All addressed.
- Build: 0 warnings, 0 errors. No new MSTest cases (the dialog is a modal WinForms surface — testing it cleanly requires an STA pump and HTTP mocking that the existing test project isn't set up for; the API contract it depends on is stable since Syncthing v1.x).
- Tested against Syncthing v2.1.0 (currently latest, so no upgrade triggers in practice today). Full end-to-end smoke must wait for the next Syncthing release.

## v3.0.1 — 2026-05-14

### Fix: form layout broken at non-100% display scale on Windows 11

Reported by a user running v3.0.0 on a Windows 11 laptop at 125% display scale: buttons rendered with no bottom border, the "Windows startup delay" `NumericUpDown` digits were hidden, ComboBox dropdown rows clipped, other text rendered "weird." The app's `app.manifest` correctly declared `PerMonitorV2` DPI awareness, but the source-generated `ApplicationConfiguration.Initialize()` was defaulting to `SystemAware` because the csproj was missing `<ApplicationHighDpiMode>`. Individual `Form` subclasses set `AutoScaleMode = AutoScaleMode.Dpi` without first pinning `AutoScaleDimensions`, so the design baseline was non-deterministically captured from whichever monitor first realized each form. The result was a split-brain DPI declaration where the OS, the WinForms runtime, and individual controls each used a different baseline — visually correct at 100% on the dev box, off-by-a-DPI-ratio on every other display scale.

- **`SyncthingPause.csproj`** — added `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` to align the generated initializer with the manifest, and `<ApplicationDefaultFont>Segoe UI, 9pt</ApplicationDefaultFont>` so un-fonted controls don't fall back to Microsoft Sans Serif 8.25pt.
- **Every `Form` subclass** (`SettingsForm`, `HelpForm`, `UpdateDialog`, `OsdToolTip`, the nested `ToastWindow`) — pinned `AutoScaleDimensions = new SizeF(96F, 96F)` BEFORE `AutoScaleMode = AutoScaleMode.Dpi` so all literal `Size`/`Point` values are interpreted at 96 DPI design baseline regardless of monitor.
- **`NumericUpDown`** — widened the "Windows startup delay" spinner from 60→80 pixels and added a `MinimumSize` floor of `(80, 26)`. The spinner band composes via three nested HWNDs whose scaling math diverges at non-integer DPI ratios; at 60px the band ate ~25px of the cell at 125%, leaving no room for 4-digit values.
- **`OwnerDrawFixed` ComboBox dropdown rows** — `ItemHeight` is now computed per-monitor as `Math.Ceiling(Font.GetHeight(DeviceDpi)) + LogicalToDeviceUnits(4)`. `AutoScaleMode.Dpi` does not walk int-typed properties so `ItemHeight = 20` stayed at design pixels forever; rendered 9pt Segoe UI grows to ~23px at 150% scale and clipped descenders.
- **Buttons** — minimum height raised to 26 (from 22/24) so the bottom border survives scaling at 125%/150%/175%. Affects 9 buttons across Settings/Help dialogs.
- **`TextBox` helper** — minimum height bumped from 22→24 for layout consistency with the surrounding controls (single-line TextBox actually auto-sizes to Font, so the literal is layout intent rather than a hard constraint).

### What this means for users

If you've been seeing layout artifacts on a non-primary laptop (Surface, ThinkPad with HiDPI panel, any machine where Windows 11 Display Settings → Scale is set to anything other than 100%), the v3.0.1 self-update will resolve them. No state migration required. If a layout artifact persists after updating, **restart the app** — Windows 11 does not propagate display-scale changes to already-running processes (the OS itself shows a banner about this).

### Verifier coverage

- 87 tests pass (no new tests — this is a pure layout/render fix with no behavioral surface to test in MSTest).
- 3-round verifier sweep with pair-by-topic dispatch (Sonnet + Opus on each topic), 10 agent-reports total: R1 confirmed the 4 initial fixes; R2 caught helper-pattern gaps (TextBox/ComboBox helpers + `OsdToolTip` missing pin); R3 caught a missed button height + the nested `ToastWindow` missing pin + a substantive correctness issue with `Font.Height` resolution (was set post-parenting expecting per-monitor resolution; corrected to `Font.GetHeight(DeviceDpi)` which actually consults the current monitor).
- Canonical fix documented at `_.claude/_templates/snippets/csharp/winforms-dpi-scaling.md` for retrofit across sibling tray apps.

## v3.0.0 — 2026-05-08

### Renamed: SyncthingTray → SyncthingPause

To avoid confusion with [Martchus's Syncthing Tray](https://github.com/Martchus/syncthingtray) (the well-known Qt cross-platform Syncthing tray app, in the ecosystem since ~2016), v3.0.0 ships under a new name that better reflects the project's actual usage pattern: pausing Syncthing when you need bandwidth.

**The code is the same** — same C# .NET 8 WinForms tray companion, same dark-theme UI, same per-folder/per-device pause, same self-update flow. Only the brand changed.

### Migration bridge — automatic, no manual steps required

- **`pause.dat` auto-migration.** On first launch, SyncthingPause copies your paused-folder snapshot from `%LOCALAPPDATA%\SyncthingTray\pause.dat` to `%LOCALAPPDATA%\SyncthingPause\pause.dat` using the same Copy + verify + Delete pattern from v2.3.7's earlier install-dir → AppData migration. If both legacy locations exist (pre-v2.3.7 install-dir AND v2.3.7+ AppData), the more-recent AppData copy wins. The legacy file is left in place if migration fails for any reason — your pause state is never lost.
- **`SyncthingTray.ini` one-shot copy.** Co-located settings files are copied to `SyncthingPause.ini` on first launch. Legacy file is preserved for rollback. Save() always writes to the new path.
- **Startup `.lnk` cleanup.** Any `SyncthingTray.lnk` in your Startup folder is removed on first launch so old and new don't both auto-launch and fight over `syncthing.exe` ownership.
- **Running predecessor kill.** SyncthingPause's `Program.Main` kills any `SyncthingTray.exe` in your session before acquiring its single-instance mutex, so the rename predecessor releases its `pause.dat` lock before migration runs.

### Breaking changes

- **WinGet PackageIdentifier changed** from `itsnateai.SyncthingTray` to `itsnateai.SyncthingPause`. WinGet IDs are immutable, so `winget upgrade` will not carry your v2.x install across — manually `winget install itsnateai.SyncthingPause`, then `winget uninstall itsnateai.SyncthingTray` once you've verified state migrated. A deprecation manifest for the old ID points users at the new one.
- **GitHub repo renamed** from `itsnateai/synctray` to `itsnateai/syncthingpause`. Old URL auto-redirects.
- **Asset name changed** from `SyncthingTray.exe` to `SyncthingPause.exe`. The in-app updater's URL allowlist accepts both old and new repo paths during the transition window so cached release URLs keep working.
- **State paths renamed** — `%LOCALAPPDATA%\SyncthingTray\` → `%LOCALAPPDATA%\SyncthingPause\`, `SyncthingTray.ini` → `SyncthingPause.ini`, mutex name `SyncthingTray_SingleInstance` → `SyncthingPause_SingleInstance`.

### Verifier coverage

- 87 tests pass (3 new tests for the rename-predecessor migration paths: legacy-only loads from legacy, both-exist new-name wins, save-always-writes-new; 2 new tests for the new-repo-name URL allowlist).
- 8-agent verifier sweep (Sonnet + Opus on each of: diff-clean, gap-audit, code-review, blast-radius) — closed 6 convergent findings: WaitForExit after Kill (race-window fix), KilledRenamePredecessor wired to OSD, INI Copy reparse-point + 256 KB size cap, portable-mode guard around rename-predecessor pause.dat migration, RunOnStartup .lnk recreation when legacy .lnk existed, this test count.
- Pre-rename state tagged at `pre-rename-v2.3.11` for rollback if needed.

## v2.3.11 — 2026-05-07

### New
- **Tray icon now appears in the taskbar by default on Windows 11.** Previously, every fresh install (or every WinGet upgrade that landed in a new versioned dir) defaulted to hidden-in-overflow until the user manually toggled "Show icon in taskbar" under Settings → Personalization → Taskbar → Other system tray icons. SyncthingTray now writes the per-icon `IsPromoted=1` flag automatically, so the icon is visible from first launch. If you previously hid it deliberately (`IsPromoted=0`), that choice is respected — we only promote when the value is missing or already 1.
- **Cleanup of stale tray-icon entries from prior versions.** Each WinGet upgrade and each .NET single-file extraction left behind a registry subkey under `HKCU\Control Panel\NotifyIconSettings` pointing to a now-deleted install path; over time these accumulated as duplicate "SyncthingTray" entries in the Settings list. On first launch of v2.3.11, any subkey whose `ExecutablePath` basename matches our exe AND points to a path that no longer exists is reaped. Conservative — never touches sparse/orphan subkeys, never touches other apps' subkeys, never touches your currently-running install.

### Notes
- These changes are no-ops on Windows 10 and Server SKUs (the `NotifyIconSettings` registry schema is Win11 22H2+ only). The build-version guard short-circuits before any registry access.
- All registry interaction is wrapped in try/catch and goes through `tray.log` (opt-in via `DiagnosticLogging=1` in `SyncthingPause.ini`). A schema change in a future Windows build silently no-ops rather than crashing the tray.

## v2.3.10 — 2026-05-07

### Bug fixes (v2.3.9 verifier round — 3 convergent gaps closed)
- **`VerifyPauseStateFile` now bounds-checks `ticks` upper limit** (mirrors `RestorePauseStateOnStartup`'s own guard). v2.3.9's verify accepted `ticks > DateTime.MaxValue.Ticks` but restore rejected them; a tampered/corrupt pause.dat with an absurd tick value would pass migration verify, get the legacy deleted, then `RestorePauseStateOnStartup` would delete the new file for being malformed — net snapshot loss. Verify now rejects identically to restore.
- **`_devicePaused` and `_knownDevices` converted to `ConcurrentDictionary`.** Pool-thread connections-poll mutates them (write + prune) while UI-thread BuildMenu / MenuResumeAllDevices enumerates them; plain `Dictionary` enumeration during concurrent mutation throws `InvalidOperationException`. The race was pre-existing but newly amplified by v2.3.9's `MenuResumeAllDevices` enumeration site. `Remove(id)` calls migrated to `TryRemove(id, out _)`.
- **0-byte legacy `pause.dat` is now deleted instead of looped on.** Empty legacy files would post-copy-verify reject as empty, the new copy got discarded, the legacy stayed untouched, and the same migration cycle ran on every launch with no progress. Now: detect empty legacy at start of migration, delete it (no state to migrate), mark success.

## v2.3.9 — 2026-05-07

### Bug fixes (v2.3.8 verifier rejection — 6 gaps closed)
- **Migration retry now catches all exceptions, not just `IOException`.** v2.3.8's `catch (IOException)` filter missed `UnauthorizedAccessException` (corp-locked installs / Defender quarantine) — first attempt would throw, retry never fired, migration silently aborted with no actionable detail. Now catches `Exception` for retries; final attempt re-throws to the outer handler.
- **Pre-delete partial dest before each retry.** v2.3.8's `File.Copy(overwrite: false)` could throw `AlreadyExists` on retries 2-5 if attempt 1 left a partial file at the new path — every subsequent retry repeated the same failure mode, neutering the backoff. Each retry now starts by deleting any leftover partial dest.
- **Pre-existing dest is verified before deleting legacy.** v2.3.8's "new path already populated" branch trusted the existing file and deleted the legacy without verifying the new file was well-formed. A partial v2.3.8 copy left at the new path would win, real pause snapshot in legacy gets destroyed. Now `VerifyPauseStateFile` (size cap + minutes-int parse + ticks parse for timed pauses) checks the existing dest; if it fails verify, the new file is discarded and the legacy gets migrated normally.
- **Verify checks the schema's required lines, not just first-line int.** v2.3.8's verify accepted `"0\n"` as valid — it would survive verify but fail downstream parsing. New verify also bounds-checks `minutes` and parses `ticks` for timed pauses.
- **Stale `.tmp`/`.bak` cleanup runs ONLY when migration succeeded** (or wasn't needed because no legacy file existed). v2.3.8 cleaned them unconditionally, deleting the `.bak` recovery file even after an aborted migration — destroying the user's last recovery affordance.
- **Last-resort fallback uses `%TEMP%`, not install dir.** v2.3.8's last fallback was `appDir`, which on a stripped-environment session re-introduced the original Syncthing-syncs-pause.dat bug. `%TEMP%` is per-user and writable on every Windows config including service accounts; volatile across reboots in some configs but vastly preferable to silently re-bugging the install dir.

### Performance
- **Migration retry budget shrunk to 3 attempts × 200/500/1000 ms** (was 5 × 0.5/1.0/1.5/2.0 s). Worst-case ctor delay dropped from 5 s to 1.7 s. Most users hit success on attempt 0; the budget exists for the narrow case where Syncthing is actively hashing the legacy file at ctor entry, which typically resolves within 1 s.

### New
- **"Resume All Devices" menu item** — symmetric with "Resume All Folders" (also new in v2.3.7). Same gate logic (only shown when at least one device is paused-in-config AND global pause is not active). Useful for unsticking a partial-snapshot resume scenario where some devices stayed paused after a `Resume Syncing` click.

## v2.3.8 — 2026-05-07

### Bug fixes (post-v2.3.7 verifier round)
- **`pause.dat` migration is now safe under all the failure modes the audit surfaced.** Before: `File.Move` could fail silently if Syncthing held the legacy file open for hashing at ctor entry, leaving the file in the synced folder forever and defeating v2.3.7's whole purpose. Now: `Copy + verify (size cap + first-line int parse) + Delete legacy on success` instead of `File.Move`, with up to 5 retries × 0.5/1.0/1.5/2.0 second backoff on `IOException` so a transient Syncthing read lock doesn't kill the migration. If verification fails (e.g. a partial cross-volume copy left a corrupt file), the new copy is discarded and the legacy file is kept intact for retry on next launch — `RestorePauseStateOnStartup`'s "delete malformed file" safeguard can no longer destroy the user's pause snapshot mid-migration.
- **`%LOCALAPPDATA%` resolution has a fallback chain.** `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` returns `""` on stripped/sandboxed sessions (services, certain corp-locked profiles); `Path.Combine("", "SyncthingTray")` would silently create a relative path under the process cwd (often `System32` for auto-started apps) — state would leak into the wrong directory invisibly. The chain is now `SpecialFolder → %USERPROFILE%\AppData\Local → install dir`, with a warn log on the last-resort fallback so the regression is grep-able if it ever happens.
- **Stale `.tmp`/`.bak` cleanup logs on failure.** v2.3.7 had an empty `catch { }` swallowing AV-locked deletes; a `.tmp` held open for 50 ms by Defender's first-look scanner would linger in the synced folder forever with zero visibility. Now logs at warn level and retries on subsequent launches.
- **"Resume All Folders" suppressed when global pause is active.** v2.3.7 showed the item whenever any folder was paused, including when `_paused == true` (global pause held a snapshot). Clicking it would partially un-pause folders without clearing `_paused` or the global snapshot, leaving the tray in an inconsistent state where the icon stayed pause while folders were mixed. Now hidden when `_paused == true` — "Resume Syncing" is the correct action there (restores the snapshot atomically).

### Notes
- Per-folder/per-device pause requires Syncthing v1.27+ (`/rest/config` API). Older Syncthing builds will see "Failed to pause" / "Failed to resume" OSDs because the PUT endpoint returns HTTP 404. v1.27 was released in 2023; if you're on an older Syncthing, please update.

## v2.3.7 — 2026-05-07

### Bug fixes
- **`pause.dat` no longer triggers Syncthing "file not found" failed-items.** The pause-state sidecar used to live next to the .exe at `<install>\pause.dat`. If the install directory was itself a Syncthing-synced folder (e.g. `proggy\Tools\synctray\` shared between machines), every pause/resume cycle wrote+deleted the file inside that folder, and Syncthing's hashing pipeline raced the rapid create→delete and surfaced "open …pause.dat: The system cannot find the file specified" in failed items. Per-machine tray state has no business inside a synced folder anyway — now lives at `%LOCALAPPDATA%\SyncthingTray\pause.dat` (alongside `tray.log`). One-time migration on startup moves any legacy `pause.dat` from the install dir to the new location and cleans up stale `pause.dat.tmp`/`.bak` siblings. **No `.stignore` change required.**
- **Tray icon now updates immediately when you pause/resume the last individual folder or device.** The `_lastPaused*Count` cache invalidation lived on the iconTimer's 5-second poll, so a click that flipped the partial→full or full→partial transition wouldn't update the icon for up to 5 seconds. `TogglePauseFolder` and `TogglePauseDevice` now call `UpdateTrayIcon()` directly after the cache mutation so the partial.ico ↔ pause.ico ↔ sync.ico transitions are instant.

### New
- **"Resume All Folders" menu item** — appears in the main right-click menu just above the localhost link, but only when at least one folder is currently paused. One click resumes every paused folder; devices you intentionally paused are left alone (folder-only flip).

## v2.3.6 — 2026-05-07

### New
- **Partial-pause tray icon.** When some folders/devices are paused but not all (e.g. you globally paused, then resumed one folder), the tray now shows a new `partial.ico` — same blue+white main logo as `pause.ico`, but the small pause mini-icon in the bottom-right has its background recolored from white to red. Black bars stay black. State machine: `sync.ico` when nothing is paused, `partial.ico` when 1..N-1 items are paused (any combination of folders + devices), `pause.ico` when everything is paused (including the existing global `_paused` state).
- Tray-icon cache key now tracks per-folder and per-device paused counts, so the icon flips immediately when a single folder or device is paused/resumed (previously the cache only invalidated on global `_paused` change). Menu rebuild also fires on these transitions so label states stay in sync.

## v2.3.5 — 2026-05-07

### UX
- **Devices submenu: red/green coloring restored, action availability tied to connection.** v2.3.4 had moved coloring to pause-state only; restored to v2.3.0's connection-driven scheme (green=connected, red=disconnected) per user preference. Action availability now follows the same axis: **Pause Device enabled when connected, Resume Device enabled when disconnected** — the available action is the one that flips you to the state shown by the other color. Folders submenu still uses pause-state coloring (folders don't have a connection axis — they sync via their devices).
- **Pause click optimistically flips the device's "connected" cache to disconnected** so the menu color flips to red immediately rather than waiting for the next 10s poll to catch up with Syncthing actually closing the connection. Resume click doesn't pre-flip to green — Syncthing has to actually re-establish the connection — but the next poll updates the state when it lands.

## v2.3.4 — 2026-05-07

### UX
- **Devices submenu color now reflects pause state, not connection state.** v2.3.0–v2.3.3 colored Devices entries red when offline / green when online — the same convention used (correctly) in Synced Folders headers. But the Devices submenu carries pause/resume *actions*, so a red entry with "Pause Device" still enabled looked contradictory ("you can't pause a red offline"). Now the color follows the actionable axis: dim grey when paused, normal text when active. Connection state still lives in the Synced Folders headers where the meaning is purely informational. (Pausing an offline device is still valid — it stops auto-reconnect when the peer comes back — and is now expressed only through the available action, not duplicated in the color.)

## v2.3.3 — 2026-05-07

### Bug fixes
- **Pause/Resume label now actually flips after you click.** v2.3.0–v2.3.2's `BuildMenu` cached the menu and skipped rebuild when none of its tracked fields changed — but those fields don't include per-folder/per-device paused state. So when you clicked "Pause Folder" or "Pause Device", the PATCH succeeded, the in-memory cache was updated, but the menu rebuild was silently skipped — so the next time you opened the menu the label was still the pre-click state ("Pause Folder" instead of "Resume Folder", Resume still greyed instead of Pause). The toggle handlers now invalidate the menu-built flag explicitly before calling `BuildMenu()` so the rebuild always lands.

### UX
- **Synced Folders sub-submenus now show both Resume + Pause always**, with the no-op for current state greyed out — same pattern as the Devices submenu in v2.3.2. Folder pause state is now visible at a glance via which action is enabled, consistent with how Devices already works.

## v2.3.2 — 2026-05-07

### UX
- **Devices submenu now shows both Resume and Pause for every device, always.** v2.3.0/v2.3.1 only showed the action matching the current state (Pause when active, Resume when paused). Now both items appear — the action that's a no-op for the device's current state is greyed out, so you can read pause state at a glance without opening the web UI, and the action you can take is the enabled one. Resume is on top, Pause below.

## v2.3.1 — 2026-05-07

### Bug fixes
- **Device-name headers in Synced Folders are now actually colored.** v2.3.0 set the Tag color correctly but `ToolStripRenderer.OnRenderItemText` branches on `item.Enabled` and routes disabled items through `ControlPaint.DrawStringDisabled` — that path ignores `e.TextColor` and renders the system default embossed-grey instead. So the header items appeared as before (greyed). The renderer now bypasses the base call entirely when an item carries a `Tag = Color`, drawing directly with `TextRenderer.DrawText` so the green/red coloring lands on disabled headers exactly the same as it does on enabled items.

## v2.3.0 — 2026-05-07

### New features
- **Per-folder pause/resume.** Right-click → **Synced Folders** → expand any folder → **Pause Folder** / **Resume Folder**. Pauses the individual folder by writing its `paused` flag in Syncthing's config — fully persistent across daemon restart and reboot, no tray-side bookkeeping. Independent of the global "Pause Syncing" — pausing one folder does not flip the tray icon to paused state. The folder label is dimmed in the menu when paused, so you can spot which folders are off at a glance.
- **Per-device pause/resume.** New top-level **Devices** submenu next to Synced Folders. Each remote device gets a sub-submenu with **Pause Device** / **Resume Device**. PATCHes the device's `paused` flag in Syncthing's config (same persistent semantics as folder pause). Self device is excluded — Syncthing has no notion of pausing the local machine.
- **Device-name headers in Synced Folders are now color-coded.** Green when the device is currently connected, red when offline. Connection state refreshes every 10 seconds from the existing connections poll. Devices submenu items use the same color convention; paused devices override to a dim grey to surface intent over reachability. Implemented via a small extension to `DarkMenuRenderer` — items can opt into custom text colors by setting `Item.Tag = Color`.

### Bug fixes
- **Stop Syncthing now kills the entire process tree.** Syncthing v2 forks a supervisor + daemon process. The previous `Process.Kill()` fallback only killed one PID — supervisor-only kill orphaned the daemon, daemon-only kill let the supervisor restart it, and the user saw "still running" in the web UI after clicking Stop. Now uses `Process.Kill(entireProcessTree: true)` (.NET 5+) which terminates parent and all descendants atomically via Win32 Job Object semantics.

### Internals
- `FolderInfo` record gains a `Paused` field, populated from `/rest/config/folders` on every load.
- `_devicePaused: Dictionary<string,bool>` cache populated free-of-charge from the existing 10s `/rest/system/connections` poll (which already returns per-device `paused`).
- Per-folder/per-device pause is fully independent of the global pause snapshot+reapply machinery added in v2.2.39 — clicking "Pause Folder" doesn't trigger the global pause flow, and the global flow's snapshot only captures folders/devices it itself flipped, so individual user-managed pauses survive global pause/resume cycles unchanged.

## v2.2.42 — 2026-05-07

### Bug fixes (post-v2.2.41 verifier round)
- **MenuPause now bails when there's nothing to pause.** Symmetric with v2.2.40's auto-pause guard: if you click "Pause Syncing" while every folder + device is already paused-in-config (via Syncthing web UI), the tray showed "Syncing paused" but stamped `_paused=true` with an empty snapshot. A subsequent "Resume Syncing" click would then hit the empty-snapshot fallback ("unpause everything paused") and silently un-pause folders you had intentionally paused. MenuPause now shows "Already paused — nothing to do" and leaves state untouched.

### Hardening
- Reworded the post-failure `.bak` log line to reflect .NET's documented `File.Replace` behavior — the `.bak` is created during the rename half, not pre-write, so a `.bak` present after a Replace failure necessarily comes from a prior cycle whose cleanup didn't run, not from "this" failure. Avoids misleading support diagnostics.

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
