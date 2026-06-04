// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai
#if DEBUG
using System.Drawing.Imaging;
using System.Globalization;

namespace SyncthingPause;

/// <summary>
/// DEBUG-only DPI verification harness. Renders ONE form in isolation with stub
/// dependencies and screenshots it to a PNG, so a real high-DPI display (the Tiny11Lab
/// VM at 150%) can capture exactly how a form looks without a human driving the tray.
/// This is how the layout-container DPI rebuild is verified — "I can't see 150%" is
/// false: this makes the rendering observable, and the render is ground truth that beats
/// any static "this will clip / double-scale" reasoning.
///
/// Usage (Debug build; run on the target-DPI machine):
///   SyncthingPause.exe --diag-render-form SettingsForm --out C:\Out [--offscreen] [--scale 1.5] [--hold]
///     --offscreen  render off-screen + DrawToBitmap (nothing pops onto the active desktop)
///     --scale F    host-side font-growth SIMULATION (100%-display sanity check; NOT a
///                  substitute for a real 150% render, which exercises non-font DPI effects too)
///     --hold       leave the window open after capture instead of exiting
///
/// Known forms: "SettingsForm" (more added as they're converted). Writes
/// &lt;FormName&gt;[-simF].png + a diag-render.log line (size + DeviceDpi) to --out.
/// </summary>
internal static class DiagRender
{
    public static void Run(string[] args)
    {
        string formName = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : "SettingsForm";
        string outDir = GetArg(args, "--out") ?? AppContext.BaseDirectory;
        float simScale = float.TryParse(GetArg(args, "--scale"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1f;
        bool hold = Array.IndexOf(args, "--hold") >= 0;
        bool offscreen = Array.IndexOf(args, "--offscreen") >= 0;

        // Match production's display setup. SyncthingPause is a pure tray app — csproj AND
        // app.manifest both declare PerMonitorV2, so the render must use it too (SystemAware
        // would scale differently and the PNG wouldn't match what users see).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The chrome palette + GDI caches are captured at first class load — initialise the
        // theme (dark) before any form/control is constructed.
        Theme.Initialize(true);

        Form form;
        try { form = BuildForm(formName); }
        catch (Exception ex) { WriteError(outDir, $"diag-build-error-{formName}.txt", ex); return; }

        form.StartPosition = FormStartPosition.Manual;
        form.Location = offscreen ? new Point(-32000, -32000) : new Point(24, 24);
        if (!offscreen) form.TopMost = true;   // clean on-screen capture for the real-150% run

        form.Shown += (_, _) =>
        {
            if (Math.Abs(simScale - 1f) > 0.001f) ScaleFonts(form, simScale);
            // One tick after Shown so owner-draw + DWM caption have painted and layout settled.
            var capture = new System.Windows.Forms.Timer { Interval = 1000 };
            capture.Tick += (_, _) =>
            {
                capture.Stop();
                capture.Dispose();
                Capture(form, formName, simScale, outDir, offscreen);
                if (!hold) Application.Exit();
            };
            capture.Start();
        };

        Application.Run(form);
    }

    private static void Capture(Form form, string formName, float simScale, string outDir, bool offscreen)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var size = form.Size;
            using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            if (offscreen)
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, size.Width, size.Height));
            }
            else
            {
                form.Activate();
                form.BringToFront();
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(form.Bounds.Location, Point.Empty, size);
            }
            string suffix = Math.Abs(simScale - 1f) > 0.001f
                ? $"-sim{simScale.ToString("0.0#", CultureInfo.InvariantCulture)}" : "";
            string file = Path.Combine(outDir, $"{formName}{suffix}.png");
            bmp.Save(file, ImageFormat.Png);
            File.AppendAllText(Path.Combine(outDir, "diag-render.log"),
                $"{formName}{suffix}: {size.Width}x{size.Height} DeviceDpi={form.DeviceDpi}\r\n");
        }
        catch (Exception ex)
        {
            WriteError(outDir, $"diag-capture-error-{formName}.txt", ex);
        }
    }

    /// <summary>Construct a form in isolation with stub dependencies. Add a case per form as it's converted.</summary>
    private static Form BuildForm(string name)
    {
        switch (name)
        {
            case "SettingsForm":
            {
                var config = StubConfig();
                var api = new SyncthingApi(config);
                var osd = new OsdToolTip();
                return new SettingsForm(config, api, osd, () => { }, () => { });
            }
            case "HelpForm":
                return new HelpForm(Path.Combine(Path.GetTempPath(), "stub.ini"), (_, _) => { });
            case "UpdateDialog":
                return new UpdateDialog();
            default:
                throw new ArgumentException($"DiagRender: unknown form '{name}'");
        }
    }

    /// <summary>A representative AppConfig so fields aren't all empty. No ApiKey on purpose:
    /// that keeps the Discovery probe + retry timer dormant during the render (a real probe
    /// would spawn pool threads and never resolve against a stub daemon).</summary>
    private static AppConfig StubConfig()
    {
        var c = new AppConfig(Path.GetTempPath());
        c.SyncExe = @"C:\Program Files\Syncthing\syncthing.exe";
        c.WebUI = "http://127.0.0.1:8384";
        return c;
    }

    /// <summary>Simulate system-DPI font growth by multiplying every control's font (DEBUG sim only).</summary>
    private static void ScaleFonts(Control root, float scale)
    {
        foreach (Control c in root.Controls)
        {
            try { c.Font = new Font(c.Font.FontFamily, c.Font.Size * scale, c.Font.Style); }
            catch { /* best-effort sim; ignore odd controls */ }
            ScaleFonts(c, scale);
        }
    }

    private static string? GetArg(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    private static void WriteError(string outDir, string file, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, file), ex.ToString());
        }
        catch { /* nothing more we can do */ }
    }
}
#endif
