using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TempTray;

internal sealed record PowerPlan(Guid Id, string Name);

internal static class PowerApi
{
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    // Режимы питания Windows 11 (ползунок «Режим питания» в параметрах)
    public static readonly Guid ModeEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid ModeBalanced = Guid.Empty;
    public static readonly Guid ModePerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static bool OnBattery =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;

    public static List<PowerPlan> GetPlans()
    {
        var plans = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.ACCESS_SCHEME, i, out Guid id, ref size) != 0)
                break;
            plans.Add(new PowerPlan(id, ReadName(id)));
        }
        return plans;
    }

    private static string ReadName(Guid id)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0)
            return id.ToString();
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return id.ToString();
            return Marshal.PtrToStringUni(buffer) ?? id.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static Guid GetActivePlan()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
            return Guid.Empty;
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            Native.LocalFree(ptr);
        }
    }

    public static bool SetActivePlan(Guid id) => Native.PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;

    /// <summary>Текущий режим питания или null, если система его не поддерживает.</summary>
    public static Guid? GetMode()
    {
        try
        {
            return Native.PowerGetEffectiveOverlayScheme(out Guid mode) == 0 ? mode : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static bool SetMode(Guid mode)
    {
        try
        {
            return Native.PowerSetActiveOverlayScheme(mode) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Таймер в секундах (0 — никогда) для активной схемы.</summary>
    public static uint? GetTimeout(bool battery, bool sleep)
    {
        Guid plan = GetActivePlan(), sub = sleep ? SubSleep : SubVideo, setting = sleep ? StandbyIdle : VideoIdle;
        uint rc = battery
            ? Native.PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out uint value)
            : Native.PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out value);
        return rc == 0 ? value : null;
    }

    public static bool SetTimeout(bool battery, bool sleep, uint seconds)
    {
        Guid plan = GetActivePlan(), sub = sleep ? SubSleep : SubVideo, setting = sleep ? StandbyIdle : VideoIdle;
        uint rc = battery
            ? Native.PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, seconds)
            : Native.PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, seconds);
        // Изменение вступает в силу после повторной активации схемы
        return rc == 0 && Native.PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;
    }
}
