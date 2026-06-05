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
                bool onAScreen = Screen.AllScreens.Any(s => s.WorkingArea.Contains(form.Location));
                Assert.IsTrue(onAScreen,
                    "The recovery path must land the dialog's top-left inside a connected screen's working area.");
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
}
