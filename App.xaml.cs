using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WF = System.Windows.Forms;

namespace TempTray;

public partial class App : Application
{
    private readonly CpuTempReader _reader = new();
    private Mutex? _mutex;
    private WF.NotifyIcon? _tray;
    private FlyoutWindow? _flyout;
    private readonly Dictionary<string, (Drawing.Icon Icon, IntPtr Handle)> _icons = new();
    private string _iconKey = "";
    private bool _openOnMouseUp;
    private readonly object _guardSync = new();
    private Timer? _chargeGuard;
    private volatile bool _wakeSeen;
    private bool _chargeLossSeen;
    private DateTime _lastGuardRun;
    private string _lastGuardState = "";

    public float? Temp { get; private set; }
    public float? MinTemp { get; private set; }
    public float? MaxTemp { get; private set; }

    /// <summary>Первый опрос датчиков уже прошёл.</summary>
    public bool TempChecked { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        if (DebugTools.TryRun(this, e.Args))
        {
            Shutdown();
            return;
        }
#endif
        if (!Start())
            Shutdown();
    }

    /// <summary>Обычный запуск. Возвращает false, если программа уже работает.</summary>
    internal bool Start()
    {
        _mutex = new Mutex(true, "TempTray.SingleInstance", out bool first);
        if (!first)
            return false;

        Theme.Refresh();
        Theme.Apply(Resources);

        _tray = new WF.NotifyIcon { Text = "TempTray", ContextMenuStrip = BuildMenu() };
        _tray.MouseDown += Tray_MouseDown;
        _tray.MouseUp += Tray_MouseUp;
        UpdateTray();
        _tray.Visible = true;

        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _reader.Updated += temp => Dispatcher.InvokeAsync(() => OnTemp(temp));
        _reader.Resumed += () => CheckChargeNow(afterSleep: true);
        _reader.Start();

        Task.Run(() =>
        {
            Autostart.RepairIfMoved();
            Autostart.RefreshCached();
            _ = HonorApi.Available;
            _ = ScreenApi.Available;
        });
        _chargeGuard = new Timer(_ => GuardChargeLimit(), null, 3000, Timeout.Infinite);
        return true;
    }

    /// <summary>
    /// Следит, чтобы пороги зарядки в прошивке соответствовали выбранной защите батареи:
    /// после сна и перезагрузки прошивка их сбрасывает, и зарядка снова идёт до 100 %.
    /// </summary>
    private void GuardChargeLimit()
    {
        // Пороги сами меняются редко, а каждое обращение к прошивке будит процессор, поэтому обычная пауза длинная.
        // После сна и при смене питания проверка запускается сразу (CheckChargeNow).
        // Перепроверка короткая: после пробуждения ноутбук может снова уснуть уже через секунду.
        const int usualPause = 60000, recheckPause = 300;
        if (!Monitor.TryEnter(_guardSync))
            return;
        try
        {
            int pause = usualPause;
            bool wake = _wakeSeen;
            _wakeSeen = false;
            try
            {
                if (HonorApi.Available)
                {
                    var (limit, stored) = HonorApi.GetChargeState();
                    LogChargeState(limit, stored, wake);
                    if (stored is { Protection: true } && limit != null && !stored.Matches(limit))
                    {
                        // Защиту могут как раз переключать в PC Manager: пороги и настройку он пишет не одновременно.
                        // Возвращаем пороги, только если расхождение держится и при повторной проверке.
                        if (_chargeLossSeen)
                            HonorApi.RestoreChargeLimit();
                        else
                            pause = recheckPause;
                        _chargeLossSeen = !_chargeLossSeen;
                    }
                    else
                    {
                        _chargeLossSeen = false;
                    }
                }
            }
            catch
            {
            }

            // Драйвер Intel сбрасывает цветовые таблицы после сна и смены экрана; PC Manager возвращал их сам,
            // теперь это делаем мы. Проверка у драйвера, а не у прошивки, поэтому процессор не будит.
            try
            {
                if (ScreenApi.Available)
                    ScreenApi.Reapply();
            }
            catch
            {
            }

            // Пока шла проверка, компьютер успел уснуть и проснуться: проверяем ещё раз
            if (_wakeSeen)
                pause = recheckPause;
            try
            {
                _chargeGuard?.Change(pause, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            Monitor.Exit(_guardSync);
        }
    }

    /// <summary>Внеочередная проверка порогов: после сна и при подключении или отключении зарядки.</summary>
    private void CheckChargeNow(bool afterSleep)
    {
        if (afterSleep)
            _wakeSeen = true;
        try
        {
            _chargeGuard?.Change(0, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Записывает в журнал каждое изменение порогов и каждое пробуждение — по нему видно,
    /// когда именно и на что прошивка меняет пороги, пока ноутбук спит.
    /// </summary>
    private void LogChargeState(ChargeLimit? limit, ChargePreset? stored, bool wake)
    {
        DateTime now = DateTime.Now;
        TimeSpan gap = _lastGuardRun == default ? TimeSpan.Zero : now - _lastGuardRun;
        _lastGuardRun = now;

        var power = WF.SystemInformation.PowerStatus;
        string source = power.PowerLineStatus == WF.PowerLineStatus.Online ? "сеть" : "батарея";
        string state = $"пороги {(limit != null ? $"{limit.Resume}–{limit.Stop}" : "нет ответа")}, выбрано «{stored?.Name ?? "?"}», {source}";

        // Сон виден либо по сообщению о пробуждении, либо по тому, что проверка сильно опоздала
        bool resumed = gap.TotalSeconds > 150 || (wake && gap.TotalSeconds > 20);
        if (state == _lastGuardState && !resumed)
            return;
        _lastGuardState = state;
        string pauseText = gap.TotalMinutes >= 1 ? $"{(int)gap.TotalMinutes} мин" : $"{(int)gap.TotalSeconds} с";
        string prefix = resumed ? $"после паузы {pauseText}: " : "";
        ChargeLog.Write($"{prefix}{state}, заряд {(int)Math.Round(power.BatteryLifePercent * 100)} %");
    }

    internal FlyoutWindow? Flyout => _flyout;

    // ---------- значок в трее ----------

    private WF.ContextMenuStrip BuildMenu()
    {
        var menu = new WF.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Открыть", null, (_, _) => ShowFlyout());
        menu.Items.Add("Отчёт о датчиках", null, (_, _) => ShowReport());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        StyleMenu(menu);
        return menu;
    }

    private static void StyleMenu(WF.ContextMenuStrip menu)
    {
        bool light = Theme.AppsLight;
        menu.Renderer = new WF.ToolStripProfessionalRenderer(new MenuColors(light)) { RoundedEdges = false };
        menu.BackColor = light ? Drawing.Color.FromArgb(0xF9, 0xF9, 0xF9) : Drawing.Color.FromArgb(0x2C, 0x2C, 0x2C);
        menu.ForeColor = light ? Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B) : Drawing.Color.White;
    }

    private sealed class MenuColors : WF.ProfessionalColorTable
    {
        private readonly Drawing.Color _back, _hover, _line;

        public MenuColors(bool light)
        {
            _back = light ? Drawing.Color.FromArgb(0xF9, 0xF9, 0xF9) : Drawing.Color.FromArgb(0x2C, 0x2C, 0x2C);
            _hover = light ? Drawing.Color.FromArgb(0xE9, 0xE9, 0xE9) : Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D);
            _line = light ? Drawing.Color.FromArgb(0xD0, 0xD0, 0xD0) : Drawing.Color.FromArgb(0x48, 0x48, 0x48);
        }

        public override Drawing.Color ToolStripDropDownBackground => _back;
        public override Drawing.Color MenuBorder => _line;
        public override Drawing.Color MenuItemBorder => _hover;
        public override Drawing.Color MenuItemSelected => _hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => _hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => _hover;
        public override Drawing.Color SeparatorDark => _line;
        public override Drawing.Color SeparatorLight => _line;
        public override Drawing.Color ImageMarginGradientBegin => _back;
        public override Drawing.Color ImageMarginGradientMiddle => _back;
        public override Drawing.Color ImageMarginGradientEnd => _back;
    }

    private void UpdateTray()
    {
        if (_tray == null)
            return;

        bool light = Theme.SystemLight;
        int size = TrayIconRenderer.SmallIconSize;
        string text = TrayIconRenderer.Label(Temp);
        var color = Temp is float value ? Theme.TrayTextColor(value, light) : Theme.TrayNeutral(light);
        var back = Temp is float v ? Theme.TempBackColor(v, light) : Theme.NeutralBack(light);

        string key = $"{text}|{color}|{size}";
        if (key == _iconKey)
            return;
        _iconKey = key;

        // Температура колеблется вокруг одних и тех же значений, поэтому нарисованные значки храним:
        // подбор размера цифр под плашку — самая дорогая часть обновления
        const int maxIcons = 150;
        List<(Drawing.Icon Icon, IntPtr Handle)>? stale = null;
        if (!_icons.TryGetValue(key, out var icon))
        {
            if (_icons.Count >= maxIcons)
            {
                stale = new List<(Drawing.Icon Icon, IntPtr Handle)>(_icons.Values);
                _icons.Clear();
            }
            using var bitmap = TrayIconRenderer.Draw(text,
                Drawing.Color.FromArgb(color.R, color.G, color.B), Drawing.Color.FromArgb(back.R, back.G, back.B), size);
            IntPtr handle = bitmap.GetHicon();
            icon = (Drawing.Icon.FromHandle(handle), handle);
            _icons[key] = icon;
        }
        _tray.Icon = icon.Icon;
        // Старые значки удаляем только после того, как в трее уже стоит новый
        if (stale != null)
            DestroyIcons(stale);

        _tray.Text = Temp is float shown ? $"Процессор: {Math.Round(shown)} °C" : "Температура процессора недоступна";
    }

    private static void DestroyIcons(IEnumerable<(Drawing.Icon Icon, IntPtr Handle)> icons)
    {
        foreach (var (icon, handle) in icons)
        {
            icon.Dispose();
            Native.DestroyIcon(handle);
        }
    }

    // Клик по значку при открытом окне сначала снимает с окна фокус (оно прячется), и только потом
    // приходит сюда. Чтобы окно не открылось заново, запоминаем его состояние на момент нажатия.
    private void Tray_MouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;
        bool justHidden = _flyout != null && (DateTime.UtcNow - _flyout.LastAutoHide).TotalMilliseconds < 400;
        bool visible = _flyout?.IsVisible == true;
        if (visible)
            _flyout!.Hide();
        _openOnMouseUp = !visible && !justHidden;
    }

    private void Tray_MouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left && _openOnMouseUp)
            ShowFlyout();
        _openOnMouseUp = false;
    }

    internal void ShowFlyout()
    {
        if (Theme.Refresh())
            ApplyTheme();
        _flyout ??= new FlyoutWindow(this);
        _flyout.ShowFlyout();
    }

    // ---------- события ----------

    private void OnTemp(float? temp)
    {
        Temp = temp;
        TempChecked = true;
        if (temp is float value)
        {
            MinTemp = MinTemp is float min ? Math.Min(min, value) : value;
            MaxTemp = MaxTemp is float max ? Math.Max(max, value) : value;
        }
        UpdateTray();
        if (_flyout?.IsVisible == true)
        {
            _flyout.UpdateTemp();
            _flyout.PollHonor();
        }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (Theme.Refresh())
                ApplyTheme();
            // При смене масштаба экрана меняется и размер значка
            UpdateTray();
        });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Пороги зарядки проверяем сразу: после сна прошивка могла их сбросить, а при подключении зарядки они и нужны
        if (e.Mode != PowerModes.Suspend)
            CheckChargeNow(afterSleep: e.Mode == PowerModes.Resume);
        Dispatcher.InvokeAsync(() =>
        {
            if (e.Mode == PowerModes.StatusChange && _flyout?.IsVisible == true)
                _flyout.RefreshState();
        });
    }

    private void ApplyTheme()
    {
        Theme.Apply(Resources);
        if (_tray?.ContextMenuStrip != null)
            StyleMenu(_tray.ContextMenuStrip);
        UpdateTray();
        _flyout?.ApplyChrome();
        if (_flyout?.IsVisible == true)
            _flyout.UpdateTemp();
    }

    private void ShowReport()
    {
        try
        {
            string file = Path.Combine(Path.GetTempPath(), "TempTray-report.txt");
            File.WriteAllText(file, _reader.BuildReport() + HonorReport());
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static string HonorReport()
    {
        string state = !HonorApi.Available ? "интерфейс не найден"
            : HonorApi.GetMode() switch
            {
                HonorApi.Balanced => "сбалансированный",
                HonorApi.Performance => "производительный",
                int other => $"неизвестный ({other})",
                null => "прошивка не ответила",
            };
        string error = HonorApi.LastError is { } text ? $" (последняя ошибка: {text})" : "";
        string charge = HonorApi.Available && HonorApi.GetChargeLimit() is { } limit
            ? $"Пороги зарядки: до {limit.Stop} %, возобновление с {limit.Resume} %{Environment.NewLine}"
            : "";
        string[] journal = HonorApi.RestoreJournal;
        string logNote = $"Журнал порогов зарядки: {ChargeLog.FilePath}{Environment.NewLine}";
        string restores = journal.Length > 0
            ? $"Восстановление порогов после сброса:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", journal)}{Environment.NewLine}"
            : "";
        string screen = !ScreenApi.Available
            ? $"Экран Honor: драйвер Intel или таблицы цвета недоступны{(ScreenApi.LastError is { } screenError ? $" ({screenError})" : "")}{Environment.NewLine}"
            : $"Экран Honor: защита зрения {(ScreenApi.GetBlueGain() is double blue ? $"{Math.Round(blue * 100)} % синего" : "нет ответа")}, "
                + $"режим «{ScreenApi.GetStoredMode()?.Name ?? "?"}», профиль ICC {ScreenApi.GetDefaultIcc() ?? "?"}{Environment.NewLine}";
        return $"{Environment.NewLine}Профиль Honor: {state}{error}{Environment.NewLine}{charge}{screen}{restores}{logNote}";
    }

    public void ExitApp()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        DestroyIcons(_icons.Values);
        _icons.Clear();
        _chargeGuard?.Dispose();
        _reader.Dispose();
        Shutdown();
    }
}
