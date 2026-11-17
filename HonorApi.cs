using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace TempTray;

/// <summary>Пороги зарядки батареи в процентах.</summary>
internal sealed record ChargeLimit(int Resume, int Stop);

/// <summary>Вариант защиты батареи — те же, что предлагает Honor PC Manager.</summary>
internal sealed record ChargePreset(string Name, int Resume, int Stop, bool Protection, int? ManagerMode, string Hint)
{
    public static readonly ChargePreset Off = new("Выкл.", 0, 100, false, null, "Защита выключена: батарея заряжается до 100 %.");
    public static readonly ChargePreset Home = new("Дом", 40, 70, true, 0, "Дом: зарядка до 70 %, возобновление с 40 %.");
    public static readonly ChargePreset Office = new("Офис", 70, 90, true, 1, "Офис: зарядка до 90 %, возобновление с 70 %.");
    public static readonly ChargePreset Travel = new("Путешествия", 95, 100, true, 2, "Путешествия: зарядка до 100 %, возобновление с 95 %.");

    private static readonly ChargePreset[] All = { Off, Home, Office, Travel };

    public static ChargePreset? Find(ChargeLimit limit) =>
        limit.Stop == 0 ? Off : All.FirstOrDefault(p => p.Resume == limit.Resume && p.Stop == limit.Stop);

    /// <summary>Вариант по номеру режима, под которым его хранит PC Manager.</summary>
    public static ChargePreset? FromManagerMode(int mode) => All.FirstOrDefault(p => p.ManagerMode == mode);

    public bool Matches(ChargeLimit limit) => limit.Resume == Resume && limit.Stop == Stop;
}

/// <summary>
/// Профиль производительности ноутбуков Honor. Он хранится в прошивке: Honor PC Manager переключает его
/// командами через ACPI-WMI, а узнав о смене профиля, сам включает или убирает свою схему питания
/// «Honor Performance». Поэтому профиль нельзя менять как обычную схему Windows — только этой командой.
/// </summary>
internal static class HonorApi
{
    public const int Balanced = 0, Performance = 1;

    /// <summary>Схема Windows, которой PC Manager управляет сам; показывать её в общем списке схем нельзя.</summary>
    public static readonly Guid PerformancePlan = new("b8a2c9f4-7d3e-4a1b-9c2f-5e8d6a3b1c4f");

    private const string Scope = @"root\WMI";
    private const string MethodClass = "OemWMIMethod";
    private const string MethodName = "OemWMIfun";

    // Команды прошивки: первый байт — раздел, второй — действие
    private const byte PerfSection = 0x04, GetModeCommand = 0x0E, SetModeCommand = 0x0F;
    private const byte BatterySection = 0x03, GetLimitCommand = 0x11, SetLimitCommand = 0x10;

    // Здесь PC Manager хранит выбранный вариант защиты батареи и показывает его в своём окне
    private const string ManagerPowerKey = @"SOFTWARE\PCManager\MBAPowerManager";

    private static readonly object Sync = new();
    private static readonly Queue<string> Journal = new();
    private static bool? _available;
    private static DateTime _probedAt;
    private static ManagementObject? _method;

    /// <summary>Есть ли у ноутбука этот интерфейс.</summary>
    public static bool Available
    {
        get
        {
            lock (Sync)
            {
                if (_available == true)
                    return true;
                // Сразу после входа в систему WMI бывает ещё не готов, поэтому отказ не считаем окончательным
                if (_available == null || (DateTime.UtcNow - _probedAt).TotalSeconds > 20)
                {
                    _available = GetMode() != null;
                    _probedAt = DateTime.UtcNow;
                }
                return _available == true;
            }
        }
    }

    /// <summary>Текущий профиль или null, если прошивка не ответила.</summary>
    public static int? GetMode()
    {
        byte[]? output = Call(PerfSection, GetModeCommand);
        return output is { Length: > 1 } && output[0] == 0 ? output[1] : null;
    }

    public static bool SetMode(int mode)
    {
        byte[]? output = Call(PerfSection, SetModeCommand, (byte)mode);
        return output is { Length: > 0 } && output[0] == 0;
    }

    /// <summary>Пороги зарядки: до какого заряда заряжать и с какого возобновлять. Null — прошивка не ответила.</summary>
    public static ChargeLimit? GetChargeLimit()
    {
        byte[]? output = Call(BatterySection, GetLimitCommand);
        return output is { Length: > 2 } && output[0] == 0 ? new ChargeLimit(output[1], output[2]) : null;
    }

    /// <summary>Задаёт пороги зарядки и записывает выбор туда же, куда его пишет PC Manager.</summary>
    public static bool SetChargePreset(ChargePreset preset)
    {
        // Под замком, чтобы сторож порогов не застал состояние «прошивка уже новая, настройка ещё старая»
        lock (Sync)
        {
            if (!WriteChargeLimit(preset))
                return false;
            ChargeLog.Write($"выбрано в программе: «{preset.Name}» ({preset.Resume}–{preset.Stop} %)");
            try
            {
                // Раздел создаём, если его нет: без PC Manager он не появится сам, а сторожу нужен выбранный вариант
                using RegistryKey? key = Registry.LocalMachine.CreateSubKey(ManagerPowerKey, writable: true);
                if (key != null)
                {
                    key.SetValue("PowerSafeManagerStatus", preset.Protection ? 1 : 0, RegistryValueKind.DWord);
                    if (preset.ManagerMode is int mode)
                        key.SetValue("PowerSafeManagerMode", mode, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Без прав администратора раздел недоступен; пороги в прошивке при этом уже заданы
            }
            return true;
        }
    }

    private static bool WriteChargeLimit(ChargePreset preset)
    {
        byte[]? output = Call(BatterySection, SetLimitCommand, (byte)preset.Resume, (byte)preset.Stop);
        return output is { Length: > 0 } && output[0] == 0;
    }

    /// <summary>Выбранный вариант защиты: его хранит PC Manager, туда же записывает выбор и TempTray.</summary>
    public static ChargePreset? GetStoredChargePreset()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ManagerPowerKey);
            if (key?.GetValue("PowerSafeManagerStatus") is not int status)
                return null;
            if (status == 0)
                return ChargePreset.Off;
            return key.GetValue("PowerSafeManagerMode") is int mode ? ChargePreset.FromManagerMode(mode) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Пороги в прошивке и выбранный вариант защиты, прочитанные за один раз.</summary>
    public static (ChargeLimit? Limit, ChargePreset? Stored) GetChargeState()
    {
        lock (Sync)
            return (GetChargeLimit(), GetStoredChargePreset());
    }

    /// <summary>
    /// Прошивка сбрасывает пороги зарядки после сна и перезагрузки, и сами они не возвращаются.
    /// True — защита выбрана, а пороги в прошивке ей не соответствуют.
    /// </summary>
    public static bool ChargeLimitLost()
    {
        lock (Sync)
        {
            return GetStoredChargePreset() is { Protection: true } wanted
                && GetChargeLimit() is { } actual
                && !wanted.Matches(actual);
        }
    }

    /// <summary>Возвращает в прошивку пороги выбранного варианта защиты, если они оттуда пропали.</summary>
    public static bool RestoreChargeLimit()
    {
        lock (Sync)
        {
            if (GetStoredChargePreset() is not { Protection: true } wanted || GetChargeLimit() is not { } actual || wanted.Matches(actual))
                return false;
            bool restored = WriteChargeLimit(wanted);
            string entry = $"пороги в прошивке были {actual.Resume}–{actual.Stop} %, "
                + (restored ? $"возвращены {wanted.Resume}–{wanted.Stop} %" : "вернуть не удалось");
            Journal.Enqueue($"{DateTime.Now:dd.MM HH:mm:ss}  {entry}");
            ChargeLog.Write("ВОССТАНОВЛЕНИЕ: " + entry);
            while (Journal.Count > 20)
                Journal.Dequeue();
            return restored;
        }
    }

    /// <summary>Когда и какие пороги пришлось возвращать — для отчёта о датчиках.</summary>
    public static string[] RestoreJournal
    {
        get
        {
            lock (Sync)
                return Journal.ToArray();
        }
    }

    /// <summary>PC Manager разрешает производительный профиль только от сети и при заряде не ниже 20 %.</summary>
    public static bool PerformanceAllowed
    {
        get
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            return status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online && status.BatteryLifePercent >= 0.2f;
        }
    }

    private static byte[]? Call(params byte[] command)
    {
        try
        {
            lock (Sync)
            {
                // Объект держим открытым: каждое новое подключение к WMI обходится заметно дороже самого вызова
                if (_method == null)
                {
                    string? path = FindInstance();
                    if (path == null)
                        return null;
                    _method = new ManagementObject(path);
                }
                using ManagementBaseObject input = _method.GetMethodParameters(MethodName);
                var buffer = new byte[64];
                command.CopyTo(buffer, 0);
                input["u8Input"] = buffer;
                using ManagementBaseObject? output = _method.InvokeMethod(MethodName, input, null);
                return output?["u8Output"] as byte[];
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            // Подключение могло оборваться: в следующий раз открываем заново
            lock (Sync)
            {
                _method?.Dispose();
                _method = null;
            }
            return null;
        }
    }

    // У прошивки два экземпляра интерфейса; PC Manager работает с первым, HWMI_0
    private static string? FindInstance()
    {
        using var methodClass = new ManagementClass(Scope, MethodClass, null);
        string? first = null;
        foreach (ManagementBaseObject item in methodClass.GetInstances())
        {
            using (item)
            {
                if (item is not ManagementObject instance)
                    continue;
                first ??= instance.Path.Path;
                if ((instance["InstanceName"] as string)?.EndsWith("HWMI_0", StringComparison.OrdinalIgnoreCase) == true)
                    return instance.Path.Path;
            }
        }
        return first;
    }

    /// <summary>Текст последней ошибки обращения к прошивке — для отчёта о датчиках.</summary>
    public static string? LastError { get; private set; }
}
