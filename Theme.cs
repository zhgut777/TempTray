using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TempTray;

/// <summary>Светлая или тёмная тема и акцентный цвет берутся из настроек Windows.</summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private static bool _loaded;
    private static Color _accent = Rgb(0x0078D4), _accentLight = Rgb(0x60CDFF), _accentDark = Rgb(0x005FB8);

    /// <summary>Тема приложений — для окна.</summary>
    public static bool AppsLight { get; private set; }

    /// <summary>Тема системы (панель задач) — для значка в трее.</summary>
    public static bool SystemLight { get; private set; }

    /// <summary>Перечитывает настройки Windows. Возвращает true, если что-то изменилось.</summary>
    public static bool Refresh()
    {
        bool apps = ReadFlag("AppsUseLightTheme"), system = ReadFlag("SystemUsesLightTheme");
        Color accent = _accent, light = _accentLight, dark = _accentDark;
        ReadAccent(ref accent, ref light, ref dark);

        bool changed = !_loaded || apps != AppsLight || system != SystemLight || accent != _accent;
        (AppsLight, SystemLight, _accent, _accentLight, _accentDark, _loaded) = (apps, system, accent, light, dark, true);
        return changed;
    }

    public static void Force(bool light)
    {
        Refresh();
        AppsLight = SystemLight = light;
    }

    private static bool ReadFlag(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static void ReadAccent(ref Color accent, ref Color light, ref Color dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
            // 8 оттенков по 4 байта (RGBA): Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, ...
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 28)
            {
                light = Color.FromRgb(p[4], p[5], p[6]);
                accent = Color.FromRgb(p[12], p[13], p[14]);
                dark = Color.FromRgb(p[16], p[17], p[18]);
            }
        }
        catch
        {
        }
    }

    public static void Apply(ResourceDictionary r)
    {
        bool l = AppsLight;
        Set(r, "Bg", l ? 0xFFF3F3F3 : 0xFF2C2C2C);
        Set(r, "PopupBg", l ? 0xFFF9F9F9 : 0xFF2C2C2C);
        Set(r, "Stroke", l ? 0x24000000u : 0x1FFFFFFF);
        Set(r, "Divider", l ? 0x14000000u : 0x14FFFFFF);
        Set(r, "Text", l ? 0xFF1B1B1B : 0xFFFFFFFF);
        Set(r, "TextSecondary", l ? 0xFF5E5E5E : 0xFFBDBDBD);
        Set(r, "Track", l ? 0xFFE5E5E5 : 0xFF1F1F1F);
        Set(r, "SegSelected", l ? 0xFFFFFFFF : 0xFF3F3F3F);
        Set(r, "Control", l ? 0xFFFBFBFB : 0xFF383838);
        Set(r, "ControlHover", l ? 0xFFEDEDED : 0xFF424242);
        Set(r, "Hover", l ? 0x0F000000u : 0x14FFFFFF);
        Set(r, "OnAccent", l ? 0xFFFFFFFF : 0xFF000000);

        Color accent = l ? _accentDark : _accentLight;
        r["Accent"] = Brush(accent);
        r["AccentText"] = Brush(accent);
        r["AccentTint"] = Brush(Color.FromArgb(l ? (byte)0x26 : (byte)0x3D, _accent.R, _accent.G, _accent.B));
    }

    /// <summary>Зелёный до 70 °C, жёлтый от 70 °C, красный от 85 °C.</summary>
    public static Color TempColor(float temp, bool light) =>
        temp >= 85 ? Rgb(light ? 0xC42B1C : 0xFF99A4)
        : temp >= 70 ? Rgb(light ? 0x9D5D00 : 0xFCE100)
        : Rgb(light ? 0x0F7B0F : 0x6CCB5F);

    /// <summary>Фон плашки под цвет температуры — для значка в трее.</summary>
    public static Color TempBackColor(float temp, bool light) =>
        temp >= 85 ? Rgb(light ? 0xFDE7E9 : 0x4A2326)
        : temp >= 70 ? Rgb(light ? 0xFFF4CE : 0x433519)
        : Rgb(light ? 0xDFF6DD : 0x1B3A21);

    /// <summary>Цвет цифр на значке в трее: на тёмной панели задач ярче, чем в окне.</summary>
    public static Color TrayTextColor(float temp, bool light) =>
        light ? TempColor(temp, true)
        : temp >= 85 ? Rgb(0xFF8A96)
        : temp >= 70 ? Rgb(0xFFE81A)
        : Rgb(0x7DF57D);

    public static Color TrayNeutral(bool light) => Rgb(light ? 0x5E5E5E : 0xE0E0E0);

    public static Color Neutral(bool light) => Rgb(light ? 0x5E5E5E : 0xBDBDBD);

    public static Color NeutralBack(bool light) => Rgb(light ? 0xE0E0E0 : 0x3A3A3A);

    public static int WindowBorderColorRef => AppsLight ? 0x00C4C4C4 : 0x00484848;

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void Set(ResourceDictionary r, string key, uint argb) =>
        r[key] = Brush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
