#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace TempTray;

/// <summary>Отладочные режимы запуска: проверка системных вызовов и снимок окна в файл.</summary>
internal static class DebugTools
{
    public static bool TryRun(App app, string[] args)
    {
        if (args.Length >= 2 && args[0] == "--diag")
        {
            File.WriteAllText(args[1], Diag());
            return true;
        }
        if (args.Length >= 3 && args[0] == "--render")
        {
            Render(app, args[1] == "light", args[2], args.Length > 3 ? float.Parse(args[3]) : (float?)null, args.Contains("--plans"));
            return true;
        }
        if (args.Length >= 3 && args[0] == "--honor")
        {
            int before = HonorApi.GetMode() ?? -1;
            bool ok = HonorApi.SetMode(int.Parse(args[1]));
            Thread.Sleep(2500);
            File.WriteAllText(args[2], $"before={before} set={args[1]} ok={ok} after={HonorApi.GetMode()} active={PowerApi.GetActivePlan()}");
            return true;
        }
        if (args.Length >= 3 && args[0] == "--charge")
        {
            ChargePreset preset = args[1] switch
            {
                "home" => ChargePreset.Home,
                "office" => ChargePreset.Office,
                "travel" => ChargePreset.Travel,
                _ => ChargePreset.Off,
            };
            ChargeLimit? before = HonorApi.GetChargeLimit();
            bool ok = HonorApi.SetChargePreset(preset);
            Thread.Sleep(800);
            ChargeLimit? after = HonorApi.GetChargeLimit();
            File.WriteAllText(args[2], $"before={before} set={args[1]} ok={ok} after={after} recognised={(after != null ? ChargePreset.Find(after)?.Hint : null)}");
            return true;
        }
        if (args.Length >= 2 && args[0] == "--guard")
        {
            // Те же шаги, что делает сторож порогов: заметить расхождение, перепроверить, вернуть
            var log = new StringBuilder();
            log.AppendLine($"stored={HonorApi.GetStoredChargePreset()?.Hint} firmware={HonorApi.GetChargeLimit()} lost={HonorApi.ChargeLimitLost()}");
            Thread.Sleep(2000);
            log.AppendLine($"still lost={HonorApi.ChargeLimitLost()} restored={HonorApi.RestoreChargeLimit()}");
            log.AppendLine($"firmware after={HonorApi.GetChargeLimit()} journal={string.Join(" | ", HonorApi.RestoreJournal)}");
            File.WriteAllText(args[1], log.ToString());
            return true;
        }
        if (args.Length >= 2 && args[0] == "--perf")
        {
            File.WriteAllText(args[1], Perf());
            return true;
        }
        if (args.Length >= 2 && args[0] == "--overlay-same")
        {
            Guid? mode = PowerApi.GetMode();
            File.WriteAllText(args[1], $"overlay={mode} set same={(mode is Guid g ? PowerApi.SetMode(g) : false)}");
            return true;
        }
        if (args.Length >= 2 && args[0] == "--timeout-same")
        {
            uint? value = PowerApi.GetTimeout(battery: false, sleep: false);
            File.WriteAllText(args[1], $"plan={PowerApi.GetActivePlan()} timeout={value} write same={(value is uint v ? PowerApi.SetTimeout(false, false, v) : false)}");
            return true;
        }
        if (args.Length >= 2 && args[0] == "--taskxml")
        {
            File.WriteAllText(args[1], Autostart.BuildXml(), Encoding.Unicode);
            return true;
        }
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            SelfTest(app, args[1]);
            return true;
        }
        if (args.Length >= 2 && args[0] == "--runtest")
        {
            RunTest(app, args[1]);
            return true;
        }
        if (args.Length >= 2 && args[0] == "--makeicon")
        {
            AppIcon.Save(args[1]);
            if (args.Length >= 3)
                AppIcon.SavePreview(args[2]);
            return true;
        }
        if (args.Length >= 2 && args[0] == "--icons")
        {
            Icons(args[1]);
            return true;
        }
        return false;
    }

    private static string Diag()
    {
        var sb = new StringBuilder();
        Theme.Refresh();
        sb.AppendLine($"theme: appsLight={Theme.AppsLight} systemLight={Theme.SystemLight}");
        sb.AppendLine($"icon size: {TrayIconRenderer.SmallIconSize}");

        var plans = PowerApi.GetPlans();
        Guid active = PowerApi.GetActivePlan();
        foreach (var plan in plans)
            sb.AppendLine($"plan: {plan.Id} {plan.Name}{(plan.Id == active ? " *" : "")}");

        Guid? mode = PowerApi.GetMode();
        sb.AppendLine($"mode: {mode}");
        if (mode is Guid current)
            sb.AppendLine($"set same mode: {PowerApi.SetMode(current)}");

        sb.AppendLine($"on battery: {PowerApi.OnBattery}");
        foreach (bool battery in new[] { false, true })
        foreach (bool sleep in new[] { false, true })
        {
            uint? value = PowerApi.GetTimeout(battery, sleep);
            sb.AppendLine($"timeout battery={battery} sleep={sleep}: {value}");
            if (value is uint seconds)
                sb.AppendLine($"  write same: {PowerApi.SetTimeout(battery, sleep, seconds)}");
        }

        var (hz, rates) = DisplayApi.GetRefreshRates();
        sb.AppendLine($"refresh: current={hz} available={string.Join(",", rates)}");
        foreach (int rate in rates)
            sb.AppendLine($"  test {rate}: {DisplayApi.SetRefreshRate(rate, testOnly: true)}");

        sb.AppendLine($"honor: available={HonorApi.Available} mode={HonorApi.GetMode()} performanceAllowed={HonorApi.PerformanceAllowed} error={HonorApi.LastError}");
        sb.AppendLine($"pawnio: {CpuTempReader.DriverInstalled}");
        using var reader = new CpuTempReader();
        float? temp = null;
        using var done = new ManualResetEventSlim(false);
        reader.Updated += t =>
        {
            temp = t;
            done.Set();
        };
        reader.Start();
        done.Wait(15000);
        sb.AppendLine($"temp: {temp}");
        sb.AppendLine(reader.BuildReport());
        return sb.ToString();
    }

    /// <summary>Сколько стоят регулярные действия программы: чтение температуры, обращение к прошивке, отрисовка значка.</summary>
    private static string Perf()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"admin: {Environment.IsPrivilegedProcess}");
        sb.Append(CpuTempReader.DebugCompare());

        // Обращения к прошивке: с потока окна и с потока таймера, как в работе
        const int calls = 20;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ChargeLimit? limit = HonorApi.GetChargeLimit();
        sb.AppendLine($"firmware first call: {watch.Elapsed.TotalMilliseconds:0.0} ms, limit={limit}, error={HonorApi.LastError}");
        watch.Restart();
        for (int i = 0; i < calls; i++)
            limit = HonorApi.GetChargeLimit();
        sb.AppendLine($"firmware, window thread: {watch.Elapsed.TotalMilliseconds / calls:0.00} ms/call, limit={limit}");
        watch.Restart();
        int? mode = System.Threading.Tasks.Task.Run(() =>
        {
            int? value = null;
            for (int i = 0; i < calls; i++)
                value = HonorApi.GetMode();
            return value;
        }).Result;
        sb.AppendLine($"firmware, pool thread: {watch.Elapsed.TotalMilliseconds / calls:0.00} ms/call, mode={mode}, error={HonorApi.LastError}");

        foreach (string text in new[] { "63", "64", "100" })
        {
            watch.Restart();
            using (TrayIconRenderer.Draw(text, Drawing.Color.White, Drawing.Color.Black, TrayIconRenderer.SmallIconSize))
                sb.AppendLine($"icon draw «{text}»: {watch.Elapsed.TotalMilliseconds:0.0} ms");
        }
        return sb.ToString();
    }

    private static void Render(App app, bool light, string file, float? temp, bool fakePlans)
    {
        Theme.Force(light);
        Theme.Apply(app.Resources);
        if (temp is float value)
            SetTemps(app, value);
        else
            typeof(App).GetProperty("TempChecked")!.SetValue(app, true);

        var window = new FlyoutWindow(app) { KeepOpen = true, ShowActivated = false };
        window.RefreshState();
        window.UpdateTemp();
        window.Show();
        window.Left = -10000;
        window.Top = -10000;
        if (fakePlans)
            window.DebugShowPlans();
        if (Environment.GetCommandLineArgs().Contains("--honor-battery"))
            window.DebugShowHonorOnBattery();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        target.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(file);
        encoder.Save(stream);
        window.Close();
    }

    /// <summary>Открывает настоящее окно у трея и снимает этот участок экрана: само окно и раскрытый список.</summary>
    private static void SelfTest(App app, string folder)
    {
        Theme.Refresh();
        Theme.Apply(app.Resources);
        SetTemps(app, 54);
        var window = new FlyoutWindow(app) { KeepOpen = true };
        window.ShowFlyout();
        Pump(800);
        Capture(window, Path.Combine(folder, "screen-window.png"));
        window.DebugOpenDropdown();
        Pump(600);
        Capture(window, Path.Combine(folder, "screen-dropdown.png"));
        Rect area = SystemParameters.WorkArea;
        File.WriteAllText(Path.Combine(folder, "screen.txt"),
            $"window: {window.Left},{window.Top} {window.ActualWidth}x{window.ActualHeight}; work area: {area}");
        window.Close();
    }

    /// <summary>Обычный запуск целиком: значок в трее, опрос датчиков, открытие окна, штатный выход.</summary>
    private static void RunTest(App app, string folder)
    {
        string log = Path.Combine(folder, "runtest.txt");
        void Log(string line) => File.AppendAllLines(log, new[] { line });
        File.Delete(log);
        app.DispatcherUnhandledException += (_, e) => Log($"UNHANDLED: {e.Exception}");
        Log($"start: {app.Start()}");
        Pump(4000);
        Log($"temp checked: {app.TempChecked}, temp: {app.Temp}");
        app.ShowFlyout();
        if (app.Flyout != null)
            app.Flyout.KeepOpen = true;
        Pump(1000);
        Log($"flyout visible: {app.Flyout?.IsVisible}, autostart: {Autostart.Enabled}");
        if (app.Flyout != null)
            Capture(app.Flyout, Path.Combine(folder, "screen-run.png"));
        app.ExitApp();
        Log("exit ok");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string file)
    {
        const int margin = 24;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        Point origin = window.PointToScreen(new Point(0, 0));
        int width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX) + margin * 2;
        int height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY) + margin * 2;
        using var bitmap = new Drawing.Bitmap(width, height);
        using (var g = Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen((int)origin.X - margin, (int)origin.Y - margin, 0, 0, new Drawing.Size(width, height));
        bitmap.Save(file, Drawing.Imaging.ImageFormat.Png);
    }

    private static void SetTemps(App app, float value)
    {
        foreach (var (name, v) in new[] { ("Temp", value), ("MinTemp", value - 13), ("MaxTemp", value + 17) })
            typeof(App).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(app, (float?)v);
        typeof(App).GetProperty("TempChecked")!.SetValue(app, true);
    }

    /// <summary>Значки трея для разных температур на тёмной и светлой панели задач, увеличенные для просмотра.</summary>
    private static void Icons(string file)
    {
        string[] texts = { "38", "63", "71", "88", "100", "--" };
        // (шрифт или null — основной, размер значка, светлая панель задач)
        var rows = new (string? Font, int Size, bool Light)[]
        {
            (null, 28, false),
            ("Bahnschrift SemiBold Condensed", 28, false),
            ("Bahnschrift SemiBold", 28, false),
            ("Segoe UI", 28, false),
            (null, 16, false),
            (null, 32, false),
            (null, 28, true),
            (null, 16, true),
        };
        const int zoom = 6, cell = 32 * zoom + 16;
        using var sheet = new Drawing.Bitmap(texts.Length * cell, rows.Length * cell);
        using var g = Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int r = 0; r < rows.Length; r++)
        {
            var (font, size, light) = rows[r];
            using var back = new Drawing.SolidBrush(light ? Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE) : Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C));
            g.FillRectangle(back, 0, r * cell, sheet.Width, cell);
            for (int t = 0; t < texts.Length; t++)
            {
                float? value = float.TryParse(texts[t], out float temp) ? temp : null;
                var color = value is float a ? Theme.TrayTextColor(a, light) : Theme.TrayNeutral(light);
                var plate = value is float b ? Theme.TempBackColor(b, light) : Theme.NeutralBack(light);
                using var icon = TrayIconRenderer.Draw(TrayIconRenderer.Label(value),
                    Drawing.Color.FromArgb(color.R, color.G, color.B), Drawing.Color.FromArgb(plate.R, plate.G, plate.B), size, font);
                g.DrawImage(icon, t * cell + 8, r * cell + 8, size * zoom, size * zoom);
            }
        }
        sheet.Save(file, Drawing.Imaging.ImageFormat.Png);
    }
}
#endif
