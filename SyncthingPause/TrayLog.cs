namespace SyncthingPause;

/// <summary>
/// Rolling log for field debugging. Writes to %LOCALAPPDATA%\SyncthingPause\tray.log
/// with a 1 MB size cap (rotates to tray.log.1). Opt-in via AppConfig.DiagnosticLogging
/// so privacy-conscious users can disable it entirely.
///
/// Thread-safe — all writes go through a lock. Timestamps are local time with
/// ISO 8601 offset (e.g. `2026-05-15T07:30:20.187-06:00`) so the daemon log
/// and tray log line up at a glance, but the UTC offset still survives DST
/// transitions and hibernate/wake (don't drop the offset and switch to bare
/// local — that loses information across daylight-saving).
/// </summary>
internal static class TrayLog
{
    private const long MaxBytes = 1_000_000;
    // Pre-Enable buffer cap. AppConfig.Load() can fire ≤3-4 warns (legacy-INI
    // oversize, reparse-point refuse, unknown ThemeMode) before TrayLog.Enable
    // is called from TrayApplicationContext's ctor. 64 entries is comfortable
    // headroom that still bounds memory if Enable is never called.
    private const int MaxBufferedEntries = 64;
    private static readonly object _lock = new();
    private static bool _enabled;
    private static string? _path;
    // Buffer for writes that arrive BEFORE Enable() — typically AppConfig.Load
    // warnings, since AppConfig is constructed before TrayLog.Enable is called
    // and TrayLog can't know the DiagnosticLogging preference until then.
    // Flushed on Enable(true); dropped on Enable(false) for privacy.
    private static readonly Queue<(string Level, string Message, DateTime When)> _buffered = new();
    // Counts the number of pre-Enable writes that were dropped because the
    // buffer hit MaxBufferedEntries. Surfaced as a one-line sentinel on
    // Enable(true) replay so a flooded startup doesn't silently lose context.
    private static int _droppedBufferedCount;

    public static void Enable(bool enable)
    {
        lock (_lock)
        {
            _enabled = enable;
            if (enable && _path is null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SyncthingPause");
                try { Directory.CreateDirectory(dir); }
                catch { /* fall back to TEMP on next write */ }
                _path = Path.Combine(dir, "tray.log");
            }
            if (enable && _buffered.Count > 0 && _path is not null)
            {
                // Replay anything that arrived before Enable. Preserves the
                // original timestamps so the log reflects when the event
                // actually happened (e.g. AppConfig.Load() warn at startup),
                // not when Enable was finally called.
                while (_buffered.Count > 0)
                {
                    var entry = _buffered.Dequeue();
                    WriteRaw(entry.Level, entry.Message, entry.When);
                }
                if (_droppedBufferedCount > 0)
                {
                    // Surface the drop count so a flooded startup leaves a
                    // breadcrumb instead of silently losing context.
                    WriteRaw("WARN", $"TrayLog dropped {_droppedBufferedCount} pre-Enable log entries (buffer cap {MaxBufferedEntries}).", DateTime.Now);
                    _droppedBufferedCount = 0;
                }
            }
            else if (!enable)
            {
                // Privacy: if the user opted OUT, drop anything we'd
                // buffered. No leak of pre-Enable state to disk.
                _buffered.Clear();
                _droppedBufferedCount = 0;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            if (!_enabled || _path is null)
            {
                // Buffer for replay if Enable(true) lands later. Bounded so
                // a never-Enable'd process (e.g. an early-exit crash) can't
                // grow this unboundedly.
                if (_buffered.Count < MaxBufferedEntries)
                {
                    _buffered.Enqueue((level, message, DateTime.Now));
                }
                else
                {
                    _droppedBufferedCount++;
                }
                return;
            }
            WriteRaw(level, message, DateTime.Now);
        }
    }

    private static void WriteRaw(string level, string message, DateTime when)
    {
        // Caller holds _lock.
        try
        {
            if (File.Exists(_path))
            {
                var info = new FileInfo(_path);
                if (info.Length > MaxBytes)
                {
                    var backup = _path + ".1";
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    try { File.Move(_path, backup); } catch { }
                }
            }
            var ts = when.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
            File.AppendAllText(_path!, $"{ts} {level} {message}{Environment.NewLine}");
        }
        catch
        {
            // If logging itself is broken (disk full, ACL), silently give up —
            // a failing logger must not crash the tray.
        }
    }
}
