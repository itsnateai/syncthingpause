using System.Linq;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

namespace SyncthingPause.Tests;

/// <summary>
/// Integration guards for the Settings-dialog "remember where it was closed" feature.
/// These realize a REAL <see cref="SettingsForm"/> on an STA thread and pump its Load
/// handler, exercising the actual WinForms restore/capture path that the AppConfig
/// round-trip unit tests can't reach:
///   • does setting Location in Load actually stick (vs. WinForms re-centering over it)?
///   • does an off-screen saved point recover to a visible screen?
///   • does closing the dialog persist its position for the next open?
/// Like <see cref="DpiLayoutRegressionTests"/>, these need a desktop session (real Forms).
/// </summary>
[TestClass]
public class SettingsPositionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SyncthingPause_Pos_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static void OnSta(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { captured = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured != null)
            throw new Exception("STA test body threw: " + captured.Message, captured);
    }

    // No ApiKey → SettingsForm's discovery probe/timer stays dormant during the show.
    private AppConfig ConfigInTempDir()
    {
        var c = new AppConfig(_tempDir);
        c.SyncExe = @"C:\Program Files\Syncthing\syncthing.exe";
        c.WebUI = "http://127.0.0.1:8384";
        return c;
    }

    /// <summary>Realize the form (Opacity 0 so it never flashes on the user's screen),
    /// pump Load so RestorePosition runs, then hand it to <paramref name="body"/>.</summary>
    private static void WithForm(AppConfig cfg, Action<SettingsForm> body)
    {
        var api = new SyncthingApi(cfg);
        using var osd = new OsdToolTip();
        using var form = new SettingsForm(cfg, api, osd, () => { }, () => { })
        {
            ShowInTaskbar = false,
            Opacity = 0,   // positioned + Load-pumped, but invisible during the test
        };
        form.Show();
        Application.DoEvents();   // pump Load → RestorePosition + the layout pass
        body(form);
    }

    [TestMethod]
    public void Restore_OnScreenSavedPosition_IsHonoredVerbatim()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var saved = new Point(wa.Left + 120, wa.Top + 90);

            var cfg = ConfigInTempDir();
            cfg.WindowX = saved.X;
            cfg.WindowY = saved.Y;

            WithForm(cfg, form =>
                Assert.AreEqual(saved, form.Location,
                    "A saved position on a connected screen must be restored verbatim — StartPosition "
                    + "is Manual when a saved position exists, so WinForms can't re-center over the Load-set "
                    + "Location."));
        });
    }

    [TestMethod]
    public void Restore_OffScreenSavedPosition_RecoversToAVisibleScreen()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var cfg = ConfigInTempDir();
            cfg.WindowX = 50000;   // no monitor lives here — simulates an unplugged / resized display
            cfg.WindowY = 50000;

            WithForm(cfg, form =>
            {
                Assert.AreNotEqual(new Point(50000, 50000), form.Location,
                    "An off-screen saved position must NOT be restored — it would open the dialog on a "
                    + "phantom display, invisible to the user.");
                // Stronger than "top-left is on a screen": assert the recovered window's title bar
                // is actually GRABBABLE — the same predicate production uses to accept a position.
                // (A top-left-only check would pass a window placed 1px inside a screen's right edge
                // with the rest off-screen.)
                Assert.IsTrue(TitleBarReachable(form.Bounds),
                    "The recovery path must leave the title bar reachable on a connected screen, "
                    + "not merely place the top-left pixel on one.");
            });
        });
    }

    [TestMethod]
    public void Close_PersistsMovedPosition_ForNextOpen()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var moved = new Point(wa.Left + 200, wa.Top + 150);

            var cfg = ConfigInTempDir();   // no saved position yet → opens centered
            WithForm(cfg, form =>
            {
                form.Location = moved;     // user drags the dialog
                form.Close();              // OnFormClosing must capture + persist on this path
            });

            // A fresh load from the same dir proves it round-tripped through the INI.
            var reopened = new AppConfig(_tempDir);
            Assert.AreEqual(moved.X, reopened.WindowX, "Close must persist the moved X for next open.");
            Assert.AreEqual(moved.Y, reopened.WindowY, "Close must persist the moved Y for next open.");
        });
    }

    [TestMethod]
    public void Close_VetoedByCancel_DoesNotPersist()
    {
        // Guard added in v3.2.18: OnFormClosing honors e.Cancel. A handler that vetoes the close
        // must leave nothing persisted. Non-vacuous as a pair with Close_PersistsMovedPosition_*
        // above (which proves a NORMAL close DOES write) — together they isolate the e.Cancel guard.
        OnSta(() =>
        {
            Theme.Initialize(true);
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var cfg = ConfigInTempDir();   // no saved position yet
            WithForm(cfg, form =>
            {
                form.FormClosing += (_, e) => e.Cancel = true;   // veto the close
                form.Location = new Point(wa.Left + 200, wa.Top + 150);
                form.Close();   // base raises FormClosing → handler cancels → override must early-return
            });

            var reopened = new AppConfig(_tempDir);
            Assert.IsNull(reopened.WindowX, "A vetoed close must not persist position (X).");
            Assert.IsNull(reopened.WindowY, "A vetoed close must not persist position (Y).");
        });
    }

    [TestMethod]
    public void Close_OnCorruptIni_DoesNotRewriteOrPersist()
    {
        // Guard added in v3.2.18: OnFormClosing skips persistence when LoadResult != None, so merely
        // opening (and moving) Settings on a damaged INI never rewrites it as a side effect of viewing.
        OnSta(() =>
        {
            Theme.Initialize(true);
            var iniPath = Path.Combine(_tempDir, "SyncthingPause.ini");
            const string corrupt = "this line has no equals sign\nneither does this one\n";
            File.WriteAllText(iniPath, corrupt, new System.Text.UTF8Encoding(false));

            var cfg = new AppConfig(_tempDir);
            cfg.SyncExe = @"C:\Program Files\Syncthing\syncthing.exe";
            Assert.AreEqual(AppConfigLoadResult.Corrupt, cfg.LoadResult, "precondition: INI must load as corrupt");

            var wa = Screen.PrimaryScreen!.WorkingArea;
            WithForm(cfg, form =>
            {
                form.Location = new Point(wa.Left + 200, wa.Top + 150);
                form.Close();   // OnFormClosing must early-return on LoadResult != None
            });

            // The damaged file is untouched, and a reload still sees no position.
            Assert.AreEqual(corrupt, File.ReadAllText(iniPath), "corrupt INI must not be rewritten on close.");
            var reopened = new AppConfig(_tempDir);
            Assert.IsNull(reopened.WindowX, "Position must not persist when the INI is corrupt.");
        });
    }

    /// <summary>Mirror of SettingsForm.IsTitleBarReachable (private) — same physical-px predicate, so
    /// the off-screen-recovery test can assert the recovered window is genuinely grabbable.</summary>
    private static bool TitleBarReachable(Rectangle windowBounds)
    {
        const int stripH = 36, minVisible = 120;
        var strip = new Rectangle(windowBounds.X, windowBounds.Y, windowBounds.Width, stripH);
        foreach (var s in Screen.AllScreens)
        {
            var o = Rectangle.Intersect(s.WorkingArea, strip);
            if (o.Width >= minVisible && o.Height >= stripH / 2) return true;
        }
        return false;
    }
}
