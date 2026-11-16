using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TempTray;

/// <summary>
/// Автозапуск через планировщик заданий: только задание с наивысшими правами
/// запускает программу от администратора без запроса UAC при каждом входе.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "TempTray";
    private const string SettingsKey = @"Software\TempTray";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Последнее известное состояние: опрос планировщика занимает заметное время, окно его не ждёт.</summary>
    public static bool Enabled { get; private set; }

    public static bool RefreshCached() => Enabled = IsEnabled();

    private static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    internal static string BuildXml()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Запускает TempTray (температура процессора в трее) при входе в систему.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(identity.Name)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{identity.User?.Value}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static bool Enable()
    {
        string file = Path.Combine(Path.GetTempPath(), $"TempTray-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(), Encoding.Unicode);
            if (Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0)
                return false;
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("AutostartPath", ExePath);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    public static bool Disable() => Run($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>Если программу перенесли в другую папку, задание указывает на старый путь — обновляем его.</summary>
    public static void RepairIfMoved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (key?.GetValue("AutostartPath") is string saved &&
                !string.Equals(saved, ExePath, StringComparison.OrdinalIgnoreCase) && IsEnabled())
                Enable();
        }
        catch
        {
        }
    }

    private static int Run(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process == null)
                return -1;
            process.WaitForExit(15000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
