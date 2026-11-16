using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TempTray;

internal static class DisplayApi
{
    private static Native.DEVMODE NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };

    /// <summary>Текущая частота основного дисплея и частоты, доступные при текущем разрешении.</summary>
    public static (int Current, List<int> Available) GetRefreshRates()
    {
        var current = NewMode();
        if (!Native.EnumDisplaySettingsW(null, Native.ENUM_CURRENT_SETTINGS, ref current))
            return (0, new List<int>());

        var rates = new HashSet<int>();
        var mode = NewMode();
        for (int i = 0; Native.EnumDisplaySettingsW(null, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1)
                rates.Add((int)mode.dmDisplayFrequency);
        }

        // 59 и 60 Гц (как и 119 и 120) — один и тот же режим с дробной частотой, оставляем целый
        var available = rates.Where(r => !rates.Contains(r + 1)).OrderBy(r => r).ToList();
        int now = (int)current.dmDisplayFrequency;
        if (rates.Contains(now + 1))
            now++;
        return (now, available);
    }

    public static bool SetRefreshRate(int hz, bool testOnly = false)
    {
        var mode = NewMode();
        if (!Native.EnumDisplaySettingsW(null, Native.ENUM_CURRENT_SETTINGS, ref mode))
            return false;
        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = Native.DM_BITSPERPEL | Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;
        uint flags = testOnly ? Native.CDS_TEST : Native.CDS_UPDATEREGISTRY;
        return Native.ChangeDisplaySettingsExW(null, ref mode, IntPtr.Zero, flags, IntPtr.Zero) == Native.DISP_CHANGE_SUCCESSFUL;
    }
}
