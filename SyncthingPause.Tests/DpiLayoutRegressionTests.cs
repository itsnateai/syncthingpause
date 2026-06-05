using System.Threading;
using System.Windows.Forms;

namespace SyncthingPause.Tests;

/// <summary>
/// Regression guards for the 2026-06-04 DPI layout-container rebuild. Empirical real-150% renders on
/// the Tiny11Lab proved the forms scale proportionally; these tests pin the STRUCTURAL invariants that
/// make that true, so a future edit can't silently regress them. (The 100% render — and most dev
/// machines, which run at 100% — cannot reveal a 150% clip, so a unit-level guard is the safety net.)
/// </summary>
[TestClass]
public class DpiLayoutRegressionTests
{
    // WinForms control construction (OsdToolTip pre-warms its handle in the ctor) needs an STA thread.
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

    // No ApiKey on purpose: keeps SettingsForm's discovery probe (and its HTTP) dormant during construction.
    private static AppConfig StubConfig()
    {
        var c = new AppConfig(Path.GetTempPath());
        c.SyncExe = @"C:\Program Files\Syncthing\syncthing.exe";
        c.WebUI = "http://127.0.0.1:8384";
        return c;
    }

    [TestMethod]
    public void AllConvertedForms_DeclareDpiAutoScaleMode()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var cfg = StubConfig();
            var api = new SyncthingApi(cfg);
            using var osd = new OsdToolTip();
            var forms = new Form[]
            {
                new SettingsForm(cfg, api, osd, () => { }, () => { }),
                new HelpForm(Path.Combine(Path.GetTempPath(), "dpi-stub.ini"), (_, _) => { }),
                new UpdateDialog(),
                new SyncthingUpdateDialog(api, "v1.0.0", "v1.2.0"),
            };
            try
            {
                foreach (var f in forms)
                    Assert.AreEqual(AutoScaleMode.Dpi, f.AutoScaleMode,
                        f.GetType().Name + " must declare AutoScaleMode.Dpi — the per-control scaling foundation.");
                Assert.AreEqual(AutoScaleMode.Dpi, osd.AutoScaleMode, "OsdToolTip must declare AutoScaleMode.Dpi.");
            }
            finally
            {
                foreach (var f in forms) f.Dispose();
            }
        });
    }

    [TestMethod]
    public void SettingsForm_PrimaryButtonsLiveOutsideTheScrollViewport()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var cfg = StubConfig();
            var api = new SyncthingApi(cfg);
            using var osd = new OsdToolTip();
            using var form = new SettingsForm(cfg, api, osd, () => { }, () => { });

            var save = FindButtonByText(form, "Save");
            Assert.IsNotNull(save, "SettingsForm must expose a Save button.");
            for (Control? c = save!.Parent; c != null; c = c.Parent)
                Assert.IsFalse(c is Panel { AutoScroll: true },
                    "Save must live in the docked footer OUTSIDE the AutoScroll viewport. Inside it, the "
                    + "primary actions scroll off-frame when the cards overflow the work-area clamp at 150% "
                    + "— the exact bug this rebuild fixed.");
        });
    }

    private static Button? FindButtonByText(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button b && b.Text == text) return b;
            var found = FindButtonByText(c, text);
            if (found != null) return found;
        }
        return null;
    }
}
