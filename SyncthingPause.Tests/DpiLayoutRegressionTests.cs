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
                {
                    Assert.AreEqual(AutoScaleMode.Dpi, f.AutoScaleMode,
                        f.GetType().Name + " must declare AutoScaleMode.Dpi — the per-control scaling foundation.");
                    Assert.AreEqual(new SizeF(96F, 96F), f.AutoScaleDimensions,
                        f.GetType().Name + " must pin the 96-DPI design baseline (dropping it while keeping Dpi mode re-introduces double-scaling at 150%).");
                }
                Assert.AreEqual(AutoScaleMode.Dpi, osd.AutoScaleMode, "OsdToolTip must declare AutoScaleMode.Dpi.");
                Assert.AreEqual(new SizeF(96F, 96F), osd.AutoScaleDimensions, "OsdToolTip must pin the 96-DPI design baseline.");
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

    [TestMethod]
    public void SettingsForm_ThemeRadios_ShareTheSameVerticalBaseline()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var cfg = StubConfig();
            var api = new SyncthingApi(cfg);
            using var osd = new OsdToolTip();
            using var form = new SettingsForm(cfg, api, osd, () => { }, () => { });

            var dark = FindControl<RadioButton>(form, r => r.AccessibleName == "Dark theme");
            var light = FindControl<RadioButton>(form, r => r.AccessibleName == "Light theme");
            Assert.IsNotNull(dark, "Settings must expose a Dark theme radio.");
            Assert.IsNotNull(light, "Settings must expose a Light theme radio.");
            // Both radios flow LeftToRight in one FlowLayoutPanel, which positions each control at
            // rowTop + Margin.Top — so unequal top margins stagger them vertically. The v3.2.15 bug:
            // Light kept the RadioButton default Margin (Padding(3), top 3) while Dark used top 0,
            // dropping Light 3px below Dark.
            Assert.AreEqual(dark!.Margin.Top, light!.Margin.Top,
                "Theme radios must share Margin.Top, or they render on different baselines.");
            Assert.AreEqual(dark.Margin.Bottom, light.Margin.Bottom,
                "Theme radios must share Margin.Bottom for a symmetric row.");
        });
    }

    [TestMethod]
    public void SettingsForm_PrimaryButtons_AreEvenlyDistributed()
    {
        OnSta(() =>
        {
            Theme.Initialize(true);
            var cfg = StubConfig();
            var api = new SyncthingApi(cfg);
            using var osd = new OsdToolTip();
            using var form = new SettingsForm(cfg, api, osd, () => { }, () => { });

            var save = FindButtonByText(form, "Save");
            var apply = FindButtonByText(form, "Apply");
            var cancel = FindButtonByText(form, "Cancel");
            Assert.IsNotNull(save, "Settings must expose a Save button.");
            Assert.IsNotNull(apply, "Settings must expose an Apply button.");
            Assert.IsNotNull(cancel, "Settings must expose a Cancel button.");

            // Even distribution = all three buttons in ONE TableLayoutPanel, each centred
            // (Anchor.None) in its own equal-width Percent column. The pre-fix Bars.Split put them
            // in a 2-column right-hugged group; this guards against regressing to that.
            var tlp = save!.Parent as TableLayoutPanel;
            Assert.IsNotNull(tlp, "Save must sit in a TableLayoutPanel (Bars.Distribute).");
            Assert.AreSame(tlp, apply!.Parent, "Apply must share Save's distribution panel.");
            Assert.AreSame(tlp, cancel!.Parent, "Cancel must share Save's distribution panel.");

            foreach (Control b in new[] { save, apply, cancel })
                Assert.AreEqual(AnchorStyles.None, b.Anchor,
                    "Each primary button must be centred (Anchor.None) in its column for even spacing.");

            float? firstPct = null;
            int pctCols = 0;
            foreach (ColumnStyle cs in tlp!.ColumnStyles)
            {
                if (cs.SizeType != SizeType.Percent) continue;
                pctCols++;
                firstPct ??= cs.Width;
                Assert.AreEqual(firstPct.Value, cs.Width, 0.01f,
                    "All Percent columns must be equal width for an even split.");
            }
            Assert.AreEqual(3, pctCols, "Three buttons → three equal-width Percent columns.");
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

    private static T? FindControl<T>(Control root, Func<T, bool> predicate) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t && predicate(t)) return t;
            var found = FindControl(c, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
