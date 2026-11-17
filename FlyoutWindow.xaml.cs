using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TempTray;

public partial class FlyoutWindow : Window
{
    private const string DriverCommand = "winget install namazso.PawnIO";

    private sealed record TimeoutItem(uint Seconds, string Label)
    {
        public override string ToString() => Label;
    }

    // Секунды; 0 — никогда
    private static readonly uint[] TimeoutPresets = { 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000, 0 };

    private readonly App _app;
    private bool _loading = true;
    private bool _battery;
    private int? _honorMode;
    private ChargeLimit? _chargeLimit;
    private bool _honorPolling;
    private DispatcherTimer? _refreshTimer;

    /// <summary>Когда окно само закрылось из-за потери фокуса — чтобы клик по значку не открыл его тут же снова.</summary>
    public DateTime LastAutoHide { get; private set; }

    /// <summary>Не закрывать при потере фокуса (отладочный снимок окна).</summary>
    public bool KeepOpen { get; set; }

    public FlyoutWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyChrome();
    }

    /// <summary>Скруглённые углы и рамка окна в цвет темы.</summary>
    public void ApplyChrome()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int corners = Native.DWMWCP_ROUND, border = Theme.WindowBorderColorRef;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    public void ShowFlyout()
    {
        _battery = PowerApi.OnBattery;
        RefreshState();
        UpdateTemp();
        Show();
        UpdateLayout();
        Reposition();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    private void Reposition()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (KeepOpen || !IsVisible)
            return;
        LastAutoHide = DateTime.UtcNow;
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Hide();
    }

    // ---------- температура ----------

    public void UpdateTemp()
    {
        float? temp = _app.Temp;
        TempText.Text = temp is float t ? $"{Math.Round(t)} °C" : "— °C";
        MinText.Text = _app.MinTemp is float min ? $"мин. {Math.Round(min)} °C" : "";
        MaxText.Text = _app.MaxTemp is float max ? $"макс. {Math.Round(max)} °C" : "";

        double share = temp is float v ? Math.Clamp(v / 100.0, 0, 1) : 0;
        TempBar.Width = Math.Round((Width - 32) * share);
        if (temp is float value)
        {
            var brush = new SolidColorBrush(Theme.TempColor(value, Theme.AppsLight));
            brush.Freeze();
            TempBar.Background = brush;
        }

        string? notice = null;
        bool offerCommand = false;
        if (temp == null && _app.TempChecked)
        {
            if (!CpuTempReader.DriverInstalled)
            {
                notice = $"Температура недоступна: не установлен драйвер PawnIO. Выполните в терминале команду {DriverCommand} и перезапустите программу.";
                offerCommand = true;
            }
            else if (!Environment.IsPrivilegedProcess)
                notice = "Температура недоступна: программа запущена без прав администратора.";
            else
                notice = "Датчик температуры не отвечает. Подробности — в пункте «Отчёт о датчиках» меню значка.";
        }
        NoticeBox.Visibility = notice != null ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = notice ?? "";
        CopyCommand.Visibility = offerCommand ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DriverCommand);
            CopyCommand.Content = "Скопировано";
        }
        catch
        {
        }
    }

    // ---------- состояние системы ----------

    /// <summary>Перечитывает из Windows схемы, режим питания, частоту экрана и таймеры.</summary>
    public void RefreshState()
    {
        _loading = true;
        try
        {
            LoadPlans();
            LoadHonor();
            LoadCharge();
            LoadScreen();
            LoadMode();
            LoadRefreshRates();
            SourceAc.IsChecked = !_battery;
            SourceDc.IsChecked = _battery;
            LoadTimeouts();
            AutostartBox.IsChecked = Autostart.Enabled;
            CopyCommand.Content = "Скопировать команду";
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadPlans()
    {
        List<PowerPlan> plans = PowerApi.GetPlans();
        // Схемой Honor управляет PC Manager: она появляется и исчезает вместе с профилем Honor (блок ниже)
        if (HonorApi.Available)
            plans.RemoveAll(p => p.Id == HonorApi.PerformancePlan);
        Guid active = PowerApi.GetActivePlan();
        // На ноутбуках с Modern Standby схема одна, выбирать не из чего
        PlansSection.Visibility = plans.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PlansPanel.Children.Clear();
        foreach (PowerPlan plan in plans)
        {
            var row = new RadioButton
            {
                Style = (Style)FindResource("RowRadio"),
                GroupName = "Plan",
                Content = plan.Name,
                Tag = PlanIcon(plan.Id),
                DataContext = plan,
                IsChecked = plan.Id == active,
            };
            row.Checked += Plan_Checked;
            PlansPanel.Children.Add(row);
        }
    }

    private static string PlanIcon(Guid id) => id.ToString() switch
    {
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "", // экономия энергии
        "381b4222-f694-41f0-9685-ff5bb260df2e" => "", // сбалансированная
        _ => "",
    };

    private void LoadHonor()
    {
        _honorMode = HonorApi.Available ? HonorApi.GetMode() : null;
        HonorSection.Visibility = _honorMode.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!_honorMode.HasValue)
            return;
        bool allowed = HonorApi.PerformanceAllowed;
        HonorBalanced.IsChecked = _honorMode == HonorApi.Balanced;
        HonorPerformance.IsChecked = _honorMode == HonorApi.Performance;
        HonorPerformance.IsEnabled = allowed || _honorMode == HonorApi.Performance;
        HonorHint.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadCharge()
    {
        ChargeLimit? limit = _chargeLimit = HonorApi.Available ? HonorApi.GetChargeLimit() : null;
        ChargeSection.Visibility = limit != null ? Visibility.Visible : Visibility.Collapsed;
        if (limit == null)
            return;
        ChargePreset? current = ChargePreset.Find(limit);
        ChargeOff.IsChecked = current == ChargePreset.Off;
        ChargeHome.IsChecked = current == ChargePreset.Home;
        ChargeOffice.IsChecked = current == ChargePreset.Office;
        ChargeTravel.IsChecked = current == ChargePreset.Travel;
        // Пороги, заданные не через PC Manager и не нами, показываем как есть
        ChargeHint.Text = current?.Hint ?? $"Зарядка до {limit.Stop} %, возобновление с {limit.Resume} %.";
    }

    private void Charge_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ChargePreset preset = sender == ChargeHome ? ChargePreset.Home
            : sender == ChargeOffice ? ChargePreset.Office
            : sender == ChargeTravel ? ChargePreset.Travel
            : ChargePreset.Off;
        HonorApi.SetChargePreset(preset);
        Reload(LoadCharge);
    }

    private void LoadScreen()
    {
        bool available = ScreenApi.Available;
        ScreenSection.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
            return;

        double? blue = ScreenApi.GetBlueGain();
        EyePreset? preset = blue.HasValue ? EyePreset.Find(blue.Value) : null;
        EyeOff.IsChecked = preset == EyePreset.Off;
        EyeCold.IsChecked = preset == EyePreset.Cold;
        EyeStandard.IsChecked = preset == EyePreset.Standard;
        EyeWarm.IsChecked = preset == EyePreset.Warm;
        // Усиление, заданное не через PC Manager и не нами, показываем как есть
        EyeHint.Text = preset?.Hint ?? (blue.HasValue ? $"Синий канал ослаблен до {Math.Round(blue.Value * 100)} %." : "");

        IReadOnlyList<ColorMode> modes = ScreenApi.AvailableModes();
        ColorModeBox.ItemsSource = modes;
        ColorMode? stored = ScreenApi.GetStoredMode();
        ColorModeBox.SelectedItem = modes.FirstOrDefault(m => m.Index == stored?.Index);
        ColorModeBox.IsEnabled = modes.Count > 0;
    }

    private async void Eye_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        EyePreset preset = sender == EyeCold ? EyePreset.Cold
            : sender == EyeStandard ? EyePreset.Standard
            : sender == EyeWarm ? EyePreset.Warm
            : EyePreset.Off;
        bool ok = await Task.Run(() => ScreenApi.SetEyeProtect(preset.Blue));
        Reload(LoadScreen);
        if (!ok)
            EyeHint.Text = "Не удалось изменить цвет экрана.";
    }

    private async void ColorMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ColorModeBox.SelectedItem is not ColorMode mode)
            return;
        bool ok = await Task.Run(() => ScreenApi.SetColorMode(mode));
        if (!ok)
            Reload(LoadScreen);
    }

    /// <summary>Профиль Honor и пороги зарядки могут сменить и без нас — Fn+P, PC Manager, подключение зарядки. Сверяемся, пока окно открыто.</summary>
    public async void PollHonor()
    {
        if (_honorPolling || !HonorApi.Available)
            return;
        _honorPolling = true;
        try
        {
            var (mode, limit) = await Task.Run(() => (HonorApi.GetMode(), HonorApi.GetChargeLimit()));
            if (IsVisible && !_loading && (mode != _honorMode || limit != _chargeLimit))
                RefreshState();
        }
        finally
        {
            _honorPolling = false;
        }
    }

    private void LoadMode()
    {
        Guid? mode = PowerApi.GetMode();
        ModeSection.Visibility = mode.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ModeEfficiency.IsChecked = mode == PowerApi.ModeEfficiency;
        ModeBalanced.IsChecked = mode == PowerApi.ModeBalanced;
        ModePerformance.IsChecked = mode == PowerApi.ModePerformance;
    }

    private void LoadRefreshRates()
    {
        var (current, available) = DisplayApi.GetRefreshRates();
        HzSection.Visibility = available.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HzPanel.Children.Clear();
        foreach (int hz in available)
        {
            var cell = new RadioButton
            {
                Style = (Style)FindResource("Seg"),
                GroupName = "Hz",
                Content = $"{hz} Гц",
                DataContext = hz,
                IsChecked = hz == current,
            };
            cell.Checked += Hz_Checked;
            HzPanel.Children.Add(cell);
        }
    }

    private void LoadTimeouts()
    {
        FillTimeouts(DisplayTimeout, PowerApi.GetTimeout(_battery, sleep: false));
        FillTimeouts(SleepTimeout, PowerApi.GetTimeout(_battery, sleep: true));
    }

    private static void FillTimeouts(ComboBox box, uint? current)
    {
        var items = TimeoutPresets.Select(s => new TimeoutItem(s, FormatTimeout(s))).ToList();
        // Значение, заданное в Windows вне нашего списка, показываем как есть
        if (current is uint value && items.All(i => i.Seconds != value))
        {
            int index = items.FindIndex(i => i.Seconds == 0 || i.Seconds > value);
            items.Insert(index, new TimeoutItem(value, FormatTimeout(value)));
        }
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(i => i.Seconds == current);
        box.IsEnabled = current.HasValue;
    }

    private static string FormatTimeout(uint seconds)
    {
        if (seconds == 0)
            return "Никогда";
        if (seconds % 3600 == 0)
            return $"{seconds / 3600} ч";
        if (seconds % 60 == 0)
            return $"{seconds / 60} мин";
        return seconds > 60 ? $"{seconds / 60} мин {seconds % 60} с" : $"{seconds} с";
    }

    // ---------- действия пользователя ----------

    private void Plan_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.DataContext is not PowerPlan plan)
            return;
        PowerApi.SetActivePlan(plan.Id);
        // Таймеры и режим питания у каждой схемы свои
        RefreshState();
    }

    private void Honor_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        HonorApi.SetMode(sender == HonorPerformance ? HonorApi.Performance : HonorApi.Balanced);
        Reload(LoadHonor);
        // PC Manager меняет схему питания Windows примерно через секунду после смены профиля,
        // а таймеры у каждой схемы свои — перечитываем всё после этой паузы
        RefreshLater();
    }

    private void RefreshLater()
    {
        if (_refreshTimer == null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                if (IsVisible)
                    RefreshState();
            };
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Guid mode = sender == ModeEfficiency ? PowerApi.ModeEfficiency
            : sender == ModePerformance ? PowerApi.ModePerformance
            : PowerApi.ModeBalanced;
        if (!PowerApi.SetMode(mode))
            Reload(LoadMode);
    }

    private void Hz_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.DataContext is not int hz)
            return;
        if (!DisplayApi.SetRefreshRate(hz))
            Reload(LoadRefreshRates);
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _battery = sender == SourceDc;
        Reload(LoadTimeouts);
    }

    private void Timeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || (sender as ComboBox)?.SelectedItem is not TimeoutItem item)
            return;
        if (!PowerApi.SetTimeout(_battery, sleep: sender == SleepTimeout, item.Seconds))
            Reload(LoadTimeouts);
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        if (AutostartBox.IsChecked == true)
            Autostart.Enable();
        else
            Autostart.Disable();
        AutostartBox.IsChecked = Autostart.RefreshCached();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitApp();

#if DEBUG
    /// <summary>Показывает блок схем с примерами — на машине с одной схемой его иначе не увидеть.</summary>
    public void DebugShowPlans()
    {
        PlansSection.Visibility = Visibility.Visible;
        PlansPanel.Children.Clear();
        var samples = new[]
        {
            ("Экономия энергии", "a1841308-3541-4fab-bc81-f71556f20b4a"),
            ("Сбалансированная", "381b4222-f694-41f0-9685-ff5bb260df2e"),
            ("Высокая производительность", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        };
        foreach (var (name, id) in samples)
            PlansPanel.Children.Add(new RadioButton
            {
                Style = (Style)FindResource("RowRadio"), GroupName = "Plan", Content = name,
                Tag = PlanIcon(new Guid(id)), IsChecked = name == "Сбалансированная",
            });
    }

    /// <summary>Вид блока Honor при работе от батареи, когда производительный профиль недоступен.</summary>
    public void DebugShowHonorOnBattery()
    {
        HonorSection.Visibility = Visibility.Visible;
        HonorBalanced.IsChecked = true;
        HonorPerformance.IsEnabled = false;
        HonorHint.Visibility = Visibility.Visible;
    }

    public void DebugOpenDropdown() => SleepTimeout.IsDropDownOpen = true;
#endif

    private void Reload(Action load)
    {
        _loading = true;
        try
        {
            load();
        }
        finally
        {
            _loading = false;
        }
    }
}
