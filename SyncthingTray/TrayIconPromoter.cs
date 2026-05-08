using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SyncthingTray;

/// <summary>
/// On Windows 11, new tray icons default to hidden-in-overflow until the user
/// manually flips "Show icon in taskbar" under Settings → Personalization →
/// Taskbar → Other system tray icons. For a tray-only app like SyncthingTray
/// that delivers no value while hidden, that's a painful first-run experience.
///
/// Windows 11 22H2+ stores per-icon visibility at
/// <c>HKCU\Control Panel\NotifyIconSettings\&lt;hash&gt;</c> with a DWORD value
/// named <c>IsPromoted</c> (1 = visible in taskbar, 0 = hidden, missing =
/// Explorer's default, which shows as hidden). The per-icon subkey normally
/// carries <c>ExecutablePath</c>, <c>InitialTooltip</c>, <c>UID</c>, and
/// <c>IconSnapshot</c> fields — Explorer populates them on <c>NIM_ADD</c>
/// when the caller passes <c>NIF_TIP</c>.
///
/// Two-phase identification so we survive Explorer edge cases:
///   Phase 1: match a subkey whose <c>ExecutablePath</c> equals ours — the
///            normal case, works on reruns and whenever Explorer wrote the
///            full schema.
///   Phase 2: if Phase 1 found nothing and exactly one "orphan" subkey
///            (IconSnapshot present but ExecutablePath missing) has appeared
///            since the baseline we captured before NIM_ADD, claim it —
///            write BOTH our <c>ExecutablePath</c> AND <c>IsPromoted=1</c>.
///            Defers when multiple orphans appear (Windows-login race);
///            later ticks disambiguate as other apps' subkeys fill in.
///
/// Plus a startup-only zombie sweep (<see cref="SweepStaleEntries"/>) that
/// reaps subkeys left behind by prior versions: WinGet versioned-install dirs
/// and .NET single-file extraction caches both leave the registry pointing at
/// dead paths, accumulating "N SyncthingTrays" cruft in the Settings list.
///
/// We never override an explicit <c>IsPromoted=0</c> — that's the user
/// having deliberately hidden us, and we respect it.
///
/// ──────────────────────────────────────────────────────────────────
/// This mechanism is undocumented. It has been stable across Win11
/// 22H2, 23H2, 24H2, and 25H2. All registry interaction is wrapped in
/// try/catch so a schema change in a future build silently no-ops
/// instead of crashing.
///
/// Canonical snippet: _.claude/_templates/snippets/csharp/tray-icon-promoter.md
/// </summary>
internal static class TrayIconPromoter
{
    private const string KeyPath = @"Control Panel\NotifyIconSettings";
    private const int MinWin11Build = 22000;

    /// <summary>
    /// Startup-only sweep — delete NotifyIconSettings subkeys whose
    /// ExecutablePath points to a file that no longer exists, scoped to
    /// entries whose path basename matches our exe basename. Targets the
    /// "N entries in tray-overflow Settings" cruft that .NET single-file
    /// publish + WinGet versioned install dirs leave behind across releases.
    ///
    /// Conservative — only touches entries that:
    ///   (a) have ExecutablePath populated (skips orphans / sparse subkeys),
    ///   (b) have basename matching our exe basename (case-insensitive),
    ///   (c) point to a file that does NOT exist on disk,
    ///   (d) are NOT the currently-running exe path (defensive).
    ///
    /// Run ONCE at startup, BEFORE CaptureBaseline. Do NOT run from the
    /// TaskbarCreated handler — that fires mid-session while Explorer is
    /// actively mutating the registry.
    ///
    /// Returns the count of entries removed (0 on no-op or failure).
    /// </summary>
    internal static int SweepStaleEntries(string ourExeName, string currentExePath)
    {
        if (Environment.OSVersion.Version.Build < MinWin11Build) return 0;
        if (string.IsNullOrWhiteSpace(ourExeName)) return 0;

        string currentNormalized;
        try { currentNormalized = Path.GetFullPath(currentExePath ?? string.Empty); }
        catch { currentNormalized = currentExePath ?? string.Empty; }

        int swept = 0;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (root is null) return 0;

            // Snapshot subkey names BEFORE iteration — DeleteSubKeyTree
            // mid-enumeration would corrupt the enumerator.
            var subNames = root.GetSubKeyNames();

            foreach (var subName in subNames)
            {
                try
                {
                    string? path;
                    using (var sub = root.OpenSubKey(subName, writable: false))
                    {
                        if (sub is null) continue;
                        path = sub.GetValue("ExecutablePath") as string;
                    }

                    if (string.IsNullOrEmpty(path)) continue;          // sparse / orphan — leave alone
                    var basename = Path.GetFileName(path);
                    if (!string.Equals(basename, ourExeName, StringComparison.OrdinalIgnoreCase))
                        continue;                                       // not ours

                    string pathNormalized;
                    try { pathNormalized = Path.GetFullPath(path); }
                    catch { pathNormalized = path; }
                    if (string.Equals(pathNormalized, currentNormalized, StringComparison.OrdinalIgnoreCase))
                        continue;                                       // ours, currently running

                    if (File.Exists(path)) continue;                    // ours, but exe still on disk (multi-install) — leave alone

                    root.DeleteSubKeyTree(subName, throwOnMissingSubKey: false);
                    TrayLog.Info($"TrayIconPromoter.SweepStaleEntries: removed {subName} → {path}.");
                    swept++;
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"TrayIconPromoter.SweepStaleEntries: subkey {subName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"TrayIconPromoter.SweepStaleEntries: {ex.Message}");
        }

        if (swept > 0)
            TrayLog.Info($"TrayIconPromoter.SweepStaleEntries: removed {swept} stale entr{(swept == 1 ? "y" : "ies")} for {ourExeName}.");
        return swept;
    }

    /// <summary>
    /// Capture the set of existing subkey names BEFORE the tray icon
    /// registers (i.e. before <c>NotifyIcon.Visible = true</c>). Anything
    /// that appears later is a candidate for Phase-2 orphan matching.
    ///
    /// Returns null on failure (access denied, etc.) — callers should
    /// treat null as "skip Phase 2" rather than "no subkeys existed,"
    /// otherwise we'd confuse every pre-existing subkey with a fresh one.
    /// </summary>
    internal static HashSet<string>? CaptureBaseline()
    {
        if (Environment.OSVersion.Version.Build < MinWin11Build) return null;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (root is null) return new HashSet<string>(StringComparer.Ordinal);
            return new HashSet<string>(root.GetSubKeyNames(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"TrayIconPromoter.CaptureBaseline: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ensure the current exe's tray icon is promoted to visible. Returns
    /// true once we've identified our subkey (whether we needed to write
    /// anything or not) so the caller's retry timer can stop. Returns
    /// false while we're still waiting for Explorer to create or populate
    /// it — caller retries.
    /// </summary>
    internal static bool TryPromote(string exePath, HashSet<string>? baseline)
    {
        if (Environment.OSVersion.Version.Build < MinWin11Build) return false;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (root is null) return false;

            bool identified = false;
            var orphanCandidates = new List<string>();

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(subName, writable: true);
                    if (sub is null) continue;

                    var path = sub.GetValue("ExecutablePath") as string;

                    // Phase 1 — path match. Handles reruns and the normal case
                    // where Explorer populated the full schema on NIM_ADD.
                    if (!string.IsNullOrEmpty(path))
                    {
                        if (!string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        identified = true;
                        var current = sub.GetValue("IsPromoted");
                        if (current is int i0 && i0 == 0)
                        {
                            TrayLog.Info($"TrayIconPromoter: {subName} IsPromoted=0 — respecting user's choice.");
                            continue;
                        }
                        if (current is int i1 && i1 == 1) continue;

                        sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        TrayLog.Info($"TrayIconPromoter: promoted {subName} for {path}.");
                        continue;
                    }

                    // Phase 2 candidate — orphan subkey (IconSnapshot but no
                    // ExecutablePath) that appeared AFTER our NIM_ADD.
                    // Skip if we didn't capture a baseline (would false-claim
                    // any pre-existing orphan belonging to another app).
                    if (baseline is null) continue;
                    if (baseline.Contains(subName)) continue;
                    if (sub.GetValue("IconSnapshot") is not byte[]) continue;

                    orphanCandidates.Add(subName);
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"TrayIconPromoter: subkey {subName}: {ex.Message}");
                }
            }

            // Phase 2 commit. Only claim when exactly one orphan appeared —
            // multiple = Windows-login race with other tray apps, wait for
            // the next tick. Their subkeys will fill in ExecutablePath as
            // Explorer does its next pass, leaving ours as the sole orphan.
            if (!identified && orphanCandidates.Count == 1)
            {
                var subName = orphanCandidates[0];
                try
                {
                    using var sub = root.OpenSubKey(subName, writable: true);
                    if (sub is not null)
                    {
                        var current = sub.GetValue("IsPromoted");
                        if (current is int i0 && i0 == 0)
                        {
                            TrayLog.Info($"TrayIconPromoter: orphan {subName} IsPromoted=0 — respecting user's choice.");
                            identified = true;
                        }
                        else
                        {
                            sub.SetValue("ExecutablePath", exePath, RegistryValueKind.String);
                            sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            TrayLog.Info($"TrayIconPromoter: claimed orphan {subName} → wrote ExecutablePath + IsPromoted=1 for {exePath}.");
                            identified = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"TrayIconPromoter: claim orphan {subName}: {ex.Message}");
                }
            }
            else if (orphanCandidates.Count > 1)
            {
                TrayLog.Info($"TrayIconPromoter: {orphanCandidates.Count} new orphan subkeys — deferring to next tick.");
            }

            return identified;
        }
        catch (Exception ex)
        {
            // Registry access denied, hive locked, schema moved — anything.
            // This is UX polish, never a crash surface.
            TrayLog.Warn($"TrayIconPromoter: {ex.Message}");
            return false;
        }
    }
}
