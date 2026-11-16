using System;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32;

namespace TempTray;

/// <summary>
/// Читает температуру процессора в отдельном потоке. На процессорах Intel берёт одно значение — температуру
/// кристалла — прямо из регистра процессора; на остальных опрашивает датчики через LibreHardwareMonitor.
/// </summary>
internal sealed class CpuTempReader : IDisposable
{
    private const int MainsIntervalMs = 3000, BatteryIntervalMs = 5000;

    // Регистры Intel: температура кристалла (на сколько градусов ниже предельной) и сама предельная температура
    private const uint PackageThermStatus = 0x1B1, TemperatureTarget = 0x1A2;
    private const int DirectFailureLimit = 3;

    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private IntelMsr? _msr;
    private float _tjMax;
    private int _directFailures;
    private bool _directGivenUp;
    private Computer? _computer;
    private bool _openedWithDriver;
    private string? _openError;

    /// <summary>Вызывается из рабочего потока после каждого опроса; null — температура недоступна.</summary>
    public event Action<float?>? Updated;

    /// <summary>Между опросами прошло заметно больше положенного: компьютер спал. Вызывается из рабочего потока.</summary>
    public event Action? Resumed;

    /// <summary>LibreHardwareMonitor читает датчики процессора через драйвер PawnIO.</summary>
    public static bool DriverInstalled
    {
        get
        {
            const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(path);
                    if (key != null)
                        return true;
                }
                catch
                {
                }
            }
            return false;
        }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "CpuTemp" };
        _thread.Start();
    }

    private void Loop()
    {
        Open();
        while (!_stop.IsSet)
        {
            float? temp = null;
            try
            {
                lock (_sync)
                    temp = ReadOnce();
            }
            catch
            {
            }

            // Драйвер поставили, пока программа работала: открываем датчики заново
            if (temp == null && !_openedWithDriver && DriverInstalled)
            {
                Close();
                Open();
            }

            Updated?.Invoke(temp);

            int interval = PowerApi.OnBattery ? BatteryIntervalMs : MainsIntervalMs;
            DateTime before = DateTime.UtcNow;
            _stop.Wait(interval);
            if (!_stop.IsSet && (DateTime.UtcNow - before).TotalMilliseconds > interval + 10000)
                Resumed?.Invoke();
        }
        Close();
    }

    private void Open()
    {
        lock (_sync)
        {
            _openedWithDriver = DriverInstalled;
            _openError = null;
            if (!_directGivenUp && OpenDirect())
                return;
            _computer = OpenComputer(out _openError);
        }
    }

    private void Close()
    {
        lock (_sync)
        {
            CloseDirect();
            CloseComputer(_computer);
            _computer = null;
        }
    }

    private float? ReadOnce()
    {
        if (_msr != null)
        {
            float? direct = ReadDirect();
            _directFailures = direct.HasValue ? 0 : _directFailures + 1;
            if (direct.HasValue || _directFailures < DirectFailureLimit)
                return direct;

            // Регистр перестал отвечать: до конца работы читаем как раньше, через все датчики
            _directGivenUp = true;
            CloseDirect();
            _computer = OpenComputer(out _openError);
        }
        return ReadSensors(_computer);
    }

    // ---------- один регистр процессора ----------

    private bool OpenDirect()
    {
        try
        {
            if (!HasPackageSensor())
                return false;
            _msr = new IntelMsr();
            if (_msr.ReadMsr(TemperatureTarget, out ulong target))
            {
                _tjMax = (target >> 16) & 0xFF;
                if (_tjMax is >= 60 and <= 130 && ReadDirect().HasValue)
                {
                    _directFailures = 0;
                    return true;
                }
            }
        }
        catch
        {
        }
        CloseDirect();
        return false;
    }

    private void CloseDirect()
    {
        try
        {
            _msr?.Close();
        }
        catch
        {
        }
        _msr = null;
    }

    /// <summary>Процессор Intel с датчиком температуры всего кристалла.</summary>
    private static bool HasPackageSensor()
    {
        if (!X86Base.IsSupported)
            return false;
        var (maxLeaf, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        // «GenuineIntel»
        if (ebx != 0x756E6547 || edx != 0x49656E69 || ecx != 0x6C65746E || maxLeaf < 6)
            return false;
        return (X86Base.CpuId(6, 0).Eax & 0x40) != 0;
    }

    private float? ReadDirect()
    {
        // Старший бит — признак действительного показания; так же его проверяет LibreHardwareMonitor
        if (_msr == null || !_msr.ReadMsr(PackageThermStatus, out ulong status) || (status & 0x80000000) == 0)
            return null;
        float value = _tjMax - ((status >> 16) & 0x7F);
        return value is > 0 and <= 150 ? value : null;
    }

    // ---------- все датчики через LibreHardwareMonitor ----------

    private static Computer? OpenComputer(out string? error)
    {
        try
        {
            var computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            error = null;
            return computer;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void CloseComputer(Computer? computer)
    {
        try
        {
            computer?.Close();
        }
        catch
        {
        }
    }

    private static float? ReadSensors(Computer? computer)
    {
        if (computer == null)
            return null;

        float? best = null;
        int bestRank = int.MaxValue;
        foreach (IHardware hardware in computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu)
                continue;
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value || value <= 0 || value > 150)
                    continue;
                int rank = Rank(sensor.Name);
                if (rank < 0)
                    continue;
                if (rank < bestRank || (rank == bestRank && value > best))
                {
                    best = value;
                    bestRank = rank;
                }
            }
        }
        return best;
    }

    // Предпочитаем температуру всего кристалла, затем самое горячее ядро
    private static int Rank(string name)
    {
        if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
            return -1;
        if (name == "CPU Package" || name.StartsWith("Core (Tctl", StringComparison.Ordinal))
            return 0;
        if (name == "Core Max")
            return 1;
        if (name == "Core Average")
            return 3;
        return 2;
    }

    /// <summary>Текстовый отчёт обо всех датчиках процессора — чтобы разобраться, если температура не показывается.</summary>
    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TempTray, {DateTime.Now:g}");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version}");
        sb.AppendLine($"Права администратора: {(Environment.IsPrivilegedProcess ? "да" : "нет")}");
        sb.AppendLine($"Драйвер PawnIO: {(DriverInstalled ? "установлен" : "не установлен")}");
        lock (_sync)
        {
            if (_msr != null)
                sb.AppendLine($"Температура читается из регистра процессора: сейчас {ReadDirect()?.ToString("0", CultureInfo.InvariantCulture) ?? "нет данных"} °C, предельная {_tjMax:0} °C");
            else
                sb.AppendLine("Температура читается через LibreHardwareMonitor");
            if (_openError != null)
                sb.AppendLine($"Ошибка при открытии датчиков: {_openError}");

            // При чтении из регистра остальные датчики закрыты; для отчёта открываем их на время
            Computer? computer = _computer ?? OpenComputer(out _);
            if (computer == null)
            {
                sb.AppendLine("Датчики не открыты.");
                return sb.ToString();
            }
            try
            {
                foreach (IHardware hardware in computer.Hardware)
                {
                    sb.AppendLine();
                    sb.AppendLine($"{hardware.HardwareType}: {hardware.Name}");
                    try
                    {
                        hardware.Update();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  ошибка обновления: {ex.Message}");
                    }
                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        string value = sensor.Value is float v ? v.ToString("0.#", CultureInfo.InvariantCulture) : "нет данных";
                        sb.AppendLine($"  {sensor.SensorType,-12} {sensor.Name,-32} {value}");
                    }
                }
            }
            finally
            {
                if (computer != _computer)
                    CloseComputer(computer);
            }
        }
        return sb.ToString();
    }

#if DEBUG
    /// <summary>Сравнивает оба способа чтения: показания и сколько процессорного времени уходит на один опрос.</summary>
    public static string DebugCompare()
    {
        var sb = new StringBuilder();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        double Cpu()
        {
            process.Refresh();
            return process.TotalProcessorTime.TotalMilliseconds;
        }

        var reader = new CpuTempReader();
        sb.AppendLine($"package sensor (cpuid): {HasPackageSensor()}");
        bool direct = reader.OpenDirect();
        sb.AppendLine($"direct open: {direct}, tjMax={reader._tjMax}");
        if (reader._msr == null && HasPackageSensor())
        {
            // Почему не открылось: показываем сырые значения
            try
            {
                var msr = new IntelMsr();
                bool a = msr.ReadMsr(TemperatureTarget, out ulong target), b = msr.ReadMsr(PackageThermStatus, out ulong status);
                sb.AppendLine($"raw: target ok={a} 0x{target:X}, status ok={b} 0x{status:X}");
                msr.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"raw read failed: {ex.Message}");
            }
        }
        if (direct)
        {
            reader._msr!.ReadMsr(PackageThermStatus, out ulong raw);
            sb.AppendLine($"direct raw status=0x{raw:X} temp={reader.ReadDirect()}");
            const int count = 2000;
            double cpu = Cpu();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                reader.ReadDirect();
            sb.AppendLine($"direct: {watch.Elapsed.TotalMilliseconds / count:0.0000} ms/read, cpu {(Cpu() - cpu) / count:0.0000} ms/read");
        }

        Computer? computer = OpenComputer(out string? error);
        sb.AppendLine($"sensors open: {computer != null} {error}");
        if (computer != null)
        {
            ReadSensors(computer);
            const int count = 50;
            double cpu = Cpu();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            float? value = null;
            for (int i = 0; i < count; i++)
                value = ReadSensors(computer);
            sb.AppendLine($"sensors: temp={value}, {watch.Elapsed.TotalMilliseconds / count:0.000} ms/read, cpu {(Cpu() - cpu) / count:0.000} ms/read");
            sb.AppendLine($"direct right after: {reader.ReadDirect()}");
            foreach (IHardware hardware in computer.Hardware)
            foreach (ISensor sensor in hardware.Sensors)
                if (sensor.SensorType == SensorType.Temperature)
                    sb.AppendLine($"  {sensor.Name,-28} {sensor.Value}");
            CloseComputer(computer);
        }
        reader.CloseDirect();
        return sb.ToString();
    }
#endif

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(1500);
    }
}
