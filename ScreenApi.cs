using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TempTray;

/// <summary>Вариант защиты зрения: насколько ослабляется синий канал экрана.</summary>
internal sealed record EyePreset(string Name, double Blue, string Hint)
{
    public static readonly EyePreset Off = new("Выкл.", 1.0, "Защита зрения выключена.");
    public static readonly EyePreset Cold = new("Холоднее", 0.95, "Защита зрения: синий канал ослаблен до 95 %.");
    public static readonly EyePreset Standard = new("Стандарт", 0.91, "Защита зрения: синий канал ослаблен до 91 %.");
    public static readonly EyePreset Warm = new("Теплее", 0.86, "Защита зрения: синий канал ослаблен до 86 %.");

    public static readonly EyePreset[] All = { Off, Cold, Standard, Warm };

    /// <summary>Ближайший вариант к текущему усилению синего; null, если значение не наше.</summary>
    public static EyePreset? Find(double blue) => All.FirstOrDefault(p => Math.Abs(p.Blue - blue) < 0.02);
}

/// <summary>Цветовой режим экрана из раздела «Управление цветом» PC Manager.</summary>
internal sealed record ColorMode(int Index, string Name, string Icc)
{
    public override string ToString() => Name;
}

/// <summary>
/// Функции экрана Honor PC Manager без PC Manager. Они устроены так же, как у него: коррекцию цвета
/// выполняет графический драйвер Intel, и PC Manager просто задаёт ему таблицы через Intel Graphics Control Library
/// (ControlLib.dll, ctlPixelTransformationSetConfig):
///  — защита зрения — одномерная таблица по трём каналам, в которой ослаблен синий;
///  — цветовой режим — трёхмерная таблица 17×17×17 из файла калибровки *_display_factory_calibration_N.xml
///    (значения делятся на 4095) плюс ICC-профиль режима, привязанный к экрану.
/// Выбор записывается туда же, где его хранит PC Manager (HKLM\SOFTWARE\HONOR\PCManager\LCDEnhancement),
/// поэтому окно PC Manager и наша программа видят одно и то же состояние.
/// </summary>
internal static unsafe class ScreenApi
{
    public static readonly ColorMode[] Modes =
    {
        new(0, "Интернет и офис (sRGB)", "Internet_and_Web"),
        new(1, "Фотография (P3)", "Photography"),
        new(2, "Цифровое кино (P3-D65)", "Digital_Cinema_P3-D65"),
        new(3, "Цифровое кино (P3-DCI)", "Digital_Cinema_P3-DCI"),
        new(4, "Графический дизайн (P3-D50)", "Graphic_Design"),
        new(5, "HDTV-видео", "HDTV"),
        new(6, "SDTV-видео (SMPTE-C)", "SDTV_NTSC_Video"),
        new(7, "SDTV-видео (9300K)", "SDTV_NTSC_Video(9300K)"),
        new(8, "SDTV-видео (EBU)", "SDTV_PAL_Video"),
        new(9, "Фото и печать", "Photography_and_Printing"),
        new(10, "Игровой", "Game_Pro"),
        new(11, "MagicP3", "MagicP3"),
    };

    private const string Dll = "ControlLib.dll";
    private const string ManagerKey = @"SOFTWARE\HONOR\PCManager\LCDEnhancement";
    private const string ColorDir = @"C:\Windows\System32\spool\drivers\color";

    // Параметры таблиц, которые драйвер Intel отдаёт для встроенного экрана
    private const uint Lut1Samples = 2049;
    private const uint Lut3Samples = 17;

    private static readonly object Sync = new();
    private static IntPtr _api;
    private static IntPtr _display;
    private static uint _lut1Id, _lut3Id;
    private static bool? _available;
    private static DateTime _probedAt;
    private static readonly Dictionary<int, double[]> Calibrations = new();

    // Выбор, который не удалось записать в реестр (нет прав администратора): иначе Reapply откатил бы его к старому
    private static int? _modeOverride;
    private static double? _blueOverride;

    /// <summary>Текст последней ошибки — для отчёта о датчиках.</summary>
    public static string? LastError { get; private set; }

    // ---------- Intel Graphics Control Library ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs { public uint Size; public byte Version; public uint AppVersion; public uint Flags; public uint SupportedVersion; public fixed byte Uid[16]; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorPrimaries { public uint Size; public byte Version; public double Rx, Ry, Gx, Gy, Bx, By, Wx, Wy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormat
    {
        public uint Size; public byte Version; public uint BitsPerColor; public byte IsFloat;
        public uint Encoding; public uint ColorSpace; public uint ColorModel;
        public ColorPrimaries Primaries; public double MaxBrightness, MinBrightness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OneD { public uint Size; public byte Version; public uint Sampling; public uint NumSamples; public uint NumChannels; public double* Values; public double* Positions; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreeD { public uint Size; public byte Version; public uint NumSamples; public double* Values; }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Union { [FieldOffset(0)] public OneD Lut1; [FieldOffset(0)] public ThreeD Lut3; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Block { public uint Size; public byte Version; public uint BlockId; public uint BlockType; public Union Config; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetConfig { public uint Size; public byte Version; public uint QueryType; public PixelFormat In, Out; public uint NumBlocks; public Block* Blocks; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetConfig { public uint Size; public byte Version; public uint Operation; public uint Flags; public uint NumBlocks; public Block* Blocks; }

    private const uint BlockType1D = 1, BlockType3D = 2;

    [DllImport(Dll)] private static extern uint ctlInit(InitArgs* args, out IntPtr api);
    [DllImport(Dll)] private static extern uint ctlClose(IntPtr api);
    [DllImport(Dll)] private static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, IntPtr* devices);
    [DllImport(Dll)] private static extern uint ctlEnumerateDisplayOutputs(IntPtr device, ref uint count, IntPtr* outputs);
    [DllImport(Dll)] private static extern uint ctlPixelTransformationGetConfig(IntPtr display, GetConfig* args);
    [DllImport(Dll)] private static extern uint ctlPixelTransformationSetConfig(IntPtr display, SetConfig* args);

    /// <summary>Есть ли у ноутбука этот интерфейс: драйвер Intel и встроенный экран с нужными таблицами.</summary>
    public static bool Available
    {
        get
        {
            lock (Sync)
            {
                if (_available == true)
                    return true;
                // Сразу после входа в систему драйвер бывает ещё не готов, поэтому отказ не считаем окончательным
                if (_available == null || (DateTime.UtcNow - _probedAt).TotalSeconds > 20)
                {
                    _available = Connect();
                    _probedAt = DateTime.UtcNow;
                }
                return _available == true;
            }
        }
    }

    private static bool Connect()
    {
        Disconnect();
        try
        {
            var init = new InitArgs { Size = (uint)sizeof(InitArgs), Version = 0, AppVersion = 1u << 16 };
            if (ctlInit(&init, out _api) != 0)
                return Fail("ctlInit");

            uint deviceCount = 0;
            if (ctlEnumerateDevices(_api, ref deviceCount, null) != 0 || deviceCount == 0)
                return Fail("ctlEnumerateDevices");
            var devices = new IntPtr[deviceCount];
            fixed (IntPtr* pd = devices)
                if (ctlEnumerateDevices(_api, ref deviceCount, pd) != 0)
                    return Fail("ctlEnumerateDevices");

            foreach (IntPtr device in devices)
            {
                uint outputCount = 0;
                if (ctlEnumerateDisplayOutputs(device, ref outputCount, null) != 0 || outputCount == 0)
                    continue;
                var outputs = new IntPtr[outputCount];
                fixed (IntPtr* po = outputs)
                    if (ctlEnumerateDisplayOutputs(device, ref outputCount, po) != 0)
                        continue;
                foreach (IntPtr output in outputs)
                {
                    if (FindBlocks(output))
                    {
                        _display = output;
                        return true;
                    }
                }
            }
            return Fail("встроенный экран с таблицами цвета не найден");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool Fail(string what)
    {
        LastError = "IGCL: " + what;
        return false;
    }

    private static void Disconnect()
    {
        if (_api != IntPtr.Zero)
        {
            try { ctlClose(_api); } catch { }
        }
        _api = IntPtr.Zero;
        _display = IntPtr.Zero;
    }

    /// <summary>Узнаёт у драйвера номера блоков: трёхмерной таблицы и одномерной таблицы по трём каналам.</summary>
    private static bool FindBlocks(IntPtr output)
    {
        var query = new GetConfig { Size = (uint)sizeof(GetConfig), QueryType = 0 };
        if (ctlPixelTransformationGetConfig(output, &query) != 0 || query.NumBlocks == 0)
            return false;

        int count = (int)query.NumBlocks;
        var blocks = (Block*)NativeMemory.AllocZeroed((nuint)(count * sizeof(Block)));
        try
        {
            for (int i = 0; i < count; i++)
                blocks[i].Size = (uint)sizeof(Block);
            query.Blocks = blocks;
            if (ctlPixelTransformationGetConfig(output, &query) != 0)
                return false;
            uint lut1 = 0, lut3 = 0;
            for (int i = 0; i < count; i++)
            {
                if (blocks[i].BlockType == BlockType3D && blocks[i].Config.Lut3.NumSamples == Lut3Samples)
                    lut3 = blocks[i].BlockId;
                else if (blocks[i].BlockType == BlockType1D && blocks[i].Config.Lut1.NumChannels == 3 && blocks[i].Config.Lut1.NumSamples == Lut1Samples)
                    lut1 = blocks[i].BlockId;
            }
            if (lut1 == 0 || lut3 == 0)
                return false;
            _lut1Id = lut1;
            _lut3Id = lut3;
            return true;
        }
        finally
        {
            NativeMemory.Free(blocks);
        }
    }

    // ---------- защита зрения ----------

    /// <summary>Усиление синего на белом в том, что сейчас стоит в драйвере; 1 — защиты нет. Null — драйвер не ответил.</summary>
    public static double? GetBlueGain()
    {
        lock (Sync)
        {
            if (!Available)
                return null;
            double[]? lut = ReadLut1();
            return lut == null ? null : lut[3 * Lut1Samples - 1];
        }
    }

    /// <summary>Что выбрано: вариант из PC Manager или нашей программы, как он записан в реестре.</summary>
    public static EyePreset? GetStoredEyePreset()
    {
        double? blue = StoredBlueGain();
        return blue.HasValue ? EyePreset.Find(blue.Value) : null;
    }

    private static double? StoredBlueGain()
    {
        if (_blueOverride.HasValue)
            return _blueOverride;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ManagerKey);
            if (key == null)
                return null;
            if (ReadInt(key, "EyeProtectSwitch") == 0)
                return 1.0;
            return ReadInt(key, "CurrentBlueProtect") is int value and > 0 and <= 1000 ? value / 1000.0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Включает защиту зрения с заданным усилением синего (1 — выключить) и записывает выбор как PC Manager.</summary>
    public static bool SetEyeProtect(double blue)
    {
        lock (Sync)
        {
            if (!Available || !WriteLut1(blue))
                return false;
            bool on = blue < 0.999;
            bool stored = WriteManager(key =>
            {
                key.SetValue("EyeProtectSwitch", on ? "1" : "0", RegistryValueKind.String);
                if (on)
                {
                    key.SetValue("CurrentBlueProtect", ((int)Math.Round(blue * 1000)).ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
                    key.SetValue("LastEnableEyeProtect", "1", RegistryValueKind.String);
                }
            });
            _blueOverride = stored ? null : blue;
            return true;
        }
    }

    // Кривая синего у PC Manager — не простое умножение: в тёмных тонах ослабление слабее
    // (на чёрном около 96 %, на белом — заданное значение). Форма подобрана по таблице, которую он ставит драйверу.
    private const double RollC = 264.6, RollD = 0.534, DarkShare = 0.451;

    private static double BlueCurve(double x, double white)
    {
        double u0 = 1.0, u1 = Math.Pow(1 + RollC, -RollD);
        double h = (Math.Pow(1 + RollC * x, -RollD) - u1) / (u0 - u1);   // 1 на чёрном, 0 на белом
        double dark = 1 - (1 - white) * DarkShare;                        // усиление на чёрном
        return x * (white + (dark - white) * h);
    }

    private static bool WriteLut1(double blue)
    {
        const uint n = Lut1Samples;
        var values = (double*)NativeMemory.AllocZeroed((nuint)(n * 3 * sizeof(double)));
        var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
        try
        {
            for (uint i = 0; i < n; i++)
            {
                double x = (double)i / (n - 1);
                values[i] = x;
                values[n + i] = x;
                values[2 * n + i] = blue >= 0.999 ? x : BlueCurve(x, blue);
            }
            block->Size = (uint)sizeof(Block);
            block->BlockId = _lut1Id;
            block->BlockType = BlockType1D;
            block->Config.Lut1.Size = (uint)sizeof(OneD);
            block->Config.Lut1.NumSamples = n;
            block->Config.Lut1.NumChannels = 3;
            block->Config.Lut1.Values = values;
            return Apply(block);
        }
        finally
        {
            NativeMemory.Free(block);
            NativeMemory.Free(values);
        }
    }

    private static double[]? ReadLut1()
    {
        const uint n = Lut1Samples;
        var values = (double*)NativeMemory.AllocZeroed((nuint)(n * 3 * sizeof(double)));
        var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
        try
        {
            block->Size = (uint)sizeof(Block);
            block->BlockId = _lut1Id;
            block->BlockType = BlockType1D;
            block->Config.Lut1.Size = (uint)sizeof(OneD);
            block->Config.Lut1.NumSamples = n;
            block->Config.Lut1.NumChannels = 3;
            block->Config.Lut1.Values = values;
            if (!Query(block))
                return null;
            var result = new double[n * 3];
            for (int i = 0; i < result.Length; i++)
                result[i] = values[i];
            return result;
        }
        finally
        {
            NativeMemory.Free(block);
            NativeMemory.Free(values);
        }
    }

    // ---------- цветовые режимы ----------

    /// <summary>Режимы, для которых на этом ноутбуке есть файл калибровки.</summary>
    public static IReadOnlyList<ColorMode> AvailableModes() => Modes.Where(m => CalibrationFile(m.Index) != null).ToList();

    private static string? CalibrationFile(int index)
    {
        try
        {
            return Directory.EnumerateFiles(ColorDir, $"*_display_factory_calibration_{index}.xml").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Режим, выбранный в PC Manager или в нашей программе. Null — не записан или неизвестен.</summary>
    public static ColorMode? GetStoredMode()
    {
        if (_modeOverride.HasValue)
            return Modes.FirstOrDefault(m => m.Index == _modeOverride);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ManagerKey);
            return key != null && ReadInt(key, "ColorMode") is int index ? Modes.FirstOrDefault(m => m.Index == index) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool SetColorMode(ColorMode mode)
    {
        lock (Sync)
        {
            if (!Available)
                return false;
            double[]? lut = LoadCalibration(mode.Index);
            if (lut == null || !WriteLut3(lut))
                return false;
            // ICC нужен программам, которые сами пересчитывают цвета под профиль экрана; сбой здесь таблицу не отменяет
            ApplyIcc(mode.Icc);
            bool stored = WriteManager(key => key.SetValue("ColorMode", mode.Index.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String));
            _modeOverride = stored ? null : mode.Index;
            return true;
        }
    }

    /// <summary>Таблица из файла калибровки: 17×17×17 троек, значения от 0 до 4095.</summary>
    private static double[]? LoadCalibration(int index)
    {
        if (Calibrations.TryGetValue(index, out double[]? cached))
            return cached;
        double[]? lut = ParseCalibration(index);
        if (lut != null)
            Calibrations[index] = lut;
        return lut;
    }

    private static double[]? ParseCalibration(int index)
    {
        string? path = CalibrationFile(index);
        if (path == null)
            return null;
        try
        {
            string text = File.ReadAllText(path);
            Match match = Regex.Match(text, @"<GMP_Feature>(.*?)</GMP_Feature>", RegexOptions.Singleline);
            if (!match.Success)
                return null;
            double[] values = Regex.Matches(match.Groups[1].Value, @"-?\d+")
                .Select(m => Math.Clamp(double.Parse(m.Value, CultureInfo.InvariantCulture) / 4095.0, 0.0, 1.0))
                .ToArray();
            return values.Length == Lut3Samples * Lut3Samples * Lut3Samples * 3 ? values : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static bool WriteLut3(double[] lut)
    {
        fixed (double* data = lut)
        {
            var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
            try
            {
                block->Size = (uint)sizeof(Block);
                block->BlockId = _lut3Id;
                block->BlockType = BlockType3D;
                block->Config.Lut3.Size = (uint)sizeof(ThreeD);
                block->Config.Lut3.NumSamples = Lut3Samples;
                block->Config.Lut3.Values = data;
                return Apply(block);
            }
            finally
            {
                NativeMemory.Free(block);
            }
        }
    }

    /// <summary>Читает из драйвера трёхмерную таблицу целиком; null — драйвер не ответил.</summary>
    public static double[]? ReadLut3()
    {
        lock (Sync)
        {
            if (!Available)
                return null;
            var result = new double[Lut3Samples * Lut3Samples * Lut3Samples * 3];
            fixed (double* data = result)
            {
                var block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
                try
                {
                    block->Size = (uint)sizeof(Block);
                    block->BlockId = _lut3Id;
                    block->BlockType = BlockType3D;
                    block->Config.Lut3.Size = (uint)sizeof(ThreeD);
                    block->Config.Lut3.NumSamples = Lut3Samples;
                    block->Config.Lut3.Values = data;
                    return Query(block) ? result : null;
                }
                finally
                {
                    NativeMemory.Free(block);
                }
            }
        }
    }

    private static bool Apply(Block* block)
    {
        var args = new SetConfig { Size = (uint)sizeof(SetConfig), Operation = 2, Flags = 0, NumBlocks = 1, Blocks = block };
        uint result = ctlPixelTransformationSetConfig(_display, &args);
        if (result == 0)
            return true;
        LastError = $"IGCL SetConfig 0x{result:X}";
        // Экран мог смениться (подключение монитора, смена режима): в следующий раз ищем его заново
        _available = null;
        return false;
    }

    private static bool Query(Block* block)
    {
        var args = new GetConfig { Size = (uint)sizeof(GetConfig), QueryType = 1, NumBlocks = 1, Blocks = block };
        uint result = ctlPixelTransformationGetConfig(_display, &args);
        if (result == 0)
            return true;
        LastError = $"IGCL GetConfig 0x{result:X}";
        _available = null;
        return false;
    }

    // ---------- возврат настроек ----------

    /// <summary>
    /// Драйвер сбрасывает таблицы после сна и смены экрана, и сами они не возвращаются
    /// (PC Manager ставит их заново сам). Возвращает выбранные защиту зрения и цветовой режим, если они пропали.
    /// </summary>
    public static bool Reapply()
    {
        lock (Sync)
        {
            if (!Available)
                return false;
            bool changed = false;

            if (GetStoredMode() is { } mode && LoadCalibration(mode.Index) is { } expected && ReadLut3() is { } actual && !SameLut3(expected, actual))
                changed |= SetColorMode(mode);

            if (StoredBlueGain() is double blue && GetBlueGain() is double current && Math.Abs(current - blue) > 0.01)
                changed |= SetEyeProtect(blue);

            return changed;
        }
    }

    // Драйвер хранит таблицу с точностью до 1/2048, поэтому сравниваем с допуском
    private static bool SameLut3(double[] expected, double[] actual)
    {
        for (int i = 0; i < expected.Length; i += 37)
            if (Math.Abs(expected[i] - actual[i]) > 0.003)
                return false;
        return Math.Abs(expected[^1] - actual[^1]) <= 0.003;
    }

    // ---------- ICC-профиль экрана ----------

    private const uint ScopeCurrentUser = 1, ProfileTypeIcc = 0, ProfileSubtypeNone = 4, ClassMonitor = 0x6D6E7472;

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsAssociateColorProfileWithDevice(uint scope, string profileName, string deviceName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsSetDefaultColorProfile(uint scope, string deviceName, uint type, uint subtype, uint profileId, string profileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsSetUsePerUserProfiles(string deviceName, uint deviceClass, bool usePerUser);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    private const uint PrimaryDevice = 4;

    /// <summary>Имя экрана для цветового управления Windows, вида MONITOR\EDO4179\{класс}\0002, у основного дисплея.</summary>
    private static string? PrimaryMonitorId()
    {
        var adapter = new DisplayDevice { cb = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & PrimaryDevice) == 0)
                continue;
            var monitor = new DisplayDevice { cb = (uint)Marshal.SizeOf<DisplayDevice>() };
            return EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceID : null;
        }
        return null;
    }

    /// <summary>ICC-профиль, который сейчас назначен экрану по умолчанию.</summary>
    public static string? GetDefaultIcc()
    {
        try
        {
            if (PrimaryMonitorId() is not { Length: > 0 } device
                || !WcsGetDefaultColorProfileSize(ScopeCurrentUser, device, ProfileTypeIcc, ProfileSubtypeNone, 0, out uint size) || size == 0)
                return null;
            var name = new char[size / 2 + 1];
            return WcsGetDefaultColorProfile(ScopeCurrentUser, device, ProfileTypeIcc, ProfileSubtypeNone, 0, size, name)
                ? new string(name).TrimEnd('\0')
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfileSize(uint scope, string deviceName, uint type, uint subtype, uint profileId, out uint size);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfile(uint scope, string deviceName, uint type, uint subtype, uint profileId, uint size, [Out] char[] name);

    private static void ApplyIcc(string iccName)
    {
        try
        {
            string file = iccName + ".icc";
            if (!File.Exists(Path.Combine(ColorDir, file)) || PrimaryMonitorId() is not { Length: > 0 } device)
                return;
            WcsSetUsePerUserProfiles(device, ClassMonitor, true);
            WcsAssociateColorProfileWithDevice(ScopeCurrentUser, file, device);
            WcsSetDefaultColorProfile(ScopeCurrentUser, device, ProfileTypeIcc, ProfileSubtypeNone, 0, file);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            LastError = ex.Message;
        }
    }

    // ---------- реестр PC Manager: все значения там строки ----------

    /// <summary>Записывает выбор туда, где его хранит PC Manager. False — нет прав администратора или раздела.</summary>
    private static bool WriteManager(Action<RegistryKey> write)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ManagerKey, writable: true);
            if (key == null)
                return false;
            write(key);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static int? ReadInt(RegistryKey key, string name) =>
        int.TryParse(key.GetValue(name)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
