<#
.SYNOPSIS
  Полностью удаляет Honor PC Manager и его остатки, оставляя то, что нужно ноутбуку и TempTray:
  службы OSDServer и HonorAPOService с их файлами, калибровку и ICC экрана, звуковые драйверы Honor,
  настройки TempTray в реестре.

.DESCRIPTION
  Перед удалением выполните Honor-Services.ps1 -Action Backup. Скрипт можно запускать повторно:
  файлы, занятые загруженными драйверами, удаляются после перезагрузки вторым запуском.
  Запускать от администратора.
#>
param([string]$BackupDir = 'D:\AI\TempTray-honor-backup', [switch]$SkipRestorePoint)

$ErrorActionPreference = 'Continue'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Нужны права администратора.' }
foreach ($need in 'ProgramFiles\OSD', 'System32\RPC\OSD', 'ProgramFiles\APOService', 'System32\HonorApoService', 'color\HonorNative.icc', 'lcd-enhancement.reg', 'power-manager.reg') {
    if (-not (Test-Path (Join-Path $BackupDir $need))) { throw "В резервной копии нет '$need' ($BackupDir). Сначала выполните: Honor-Services.ps1 -Action Backup" }
}

# Что остаётся
$KeepProgramFiles = @('OSD', 'APOService')
$KeepHonorKeys = @('OSD', 'ASvidDMFT', 'PCManager')           # в PCManager остаётся только LCDEnhancement
$KeepPCManagerSubkeys = @('LCDEnhancement')
$KeepPcManagerRootKeys = @('MBAPowerManager')                 # HKLM\SOFTWARE\PCManager

function Step($t) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $t" }

if (-not $SkipRestorePoint) {
    Step 'точка восстановления'
    try {
        Enable-ComputerRestore -Drive 'C:\' -ErrorAction Stop
        # Windows по умолчанию не создаёт точки чаще раза в сутки; снимаем это ограничение на время вызова
        $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        $old = (Get-ItemProperty $k -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
        Set-ItemProperty $k SystemRestorePointCreationFrequency 0 -Type DWord
        Checkpoint-Computer -Description 'Перед удалением Honor PC Manager' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        if ($null -ne $old) { Set-ItemProperty $k SystemRestorePointCreationFrequency $old -Type DWord } else { Remove-ItemProperty $k SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
        Step 'точка восстановления создана'
    } catch { Step "точка восстановления НЕ создана: $($_.Exception.Message)" }
}

Step 'остановка процессов Honor (кроме OSD и звука)'
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and ($_.Path -like 'C:\Program Files\HONOR\*' -or $_.Path -like 'C:\Program Files (x86)\qianxin\*' -or $_.Path -like 'C:\ProgramData\Comms\*' -or $_.Path -like 'C:\ProgramData\HonorSDK\*') -and
    $_.Path -notlike 'C:\Program Files\HONOR\OSD\*' -and $_.Path -notlike 'C:\Program Files\HONOR\APOService\*'
} | ForEach-Object { Step "  стоп: $($_.Name)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

Step 'службы и драйверы PC Manager'
foreach ($n in 'MBAMainService', 'HnUpdateService', 'GuardService', 'QAXDefender.SDK', 'QaxEngManager', 'MonProcessX64', 'Dokan2', 'OS2SOC', 'HNOs2ECx64') {
    if (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$n") {
        sc.exe stop $n 2>&1 | Out-Null
        sc.exe delete $n 2>&1 | Out-Null
        Step "  удалена: $n (код $LASTEXITCODE)"
    }
}

Step 'задача PC Helper'
Unregister-ScheduledTask -TaskName 'Honor PC Helper Privileged Hardware' -Confirm:$false -ErrorAction SilentlyContinue

Step 'папки'
Get-ChildItem 'C:\Program Files\HONOR' -Force -ErrorAction SilentlyContinue | Where-Object { $KeepProgramFiles -notcontains $_.Name } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    Step ("  {0}: {1}" -f $_.FullName, $(if (Test-Path $_.FullName) { 'остались занятые файлы' } else { 'удалена' }))
}
# ProgramData\Comms: оставляем только данные OSD
Get-ChildItem 'C:\ProgramData\Comms' -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'Osd' } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    Step ("  {0}: {1}" -f $_.FullName, $(if (Test-Path $_.FullName) { 'остались занятые файлы' } else { 'удалена' }))
}
foreach ($p in 'C:\ProgramData\HONOR\UseMachineSkills', 'C:\ProgramData\Honor\UseMachineSkills', 'C:\ProgramData\HonorSDK', 'C:\ProgramData\QAXSecurity_setup',
    'C:\Program Files (x86)\qianxin', "$env:LOCALAPPDATA\HONOR", "$env:LOCALAPPDATA\Honor", "$env:APPDATA\HONOR", "$env:APPDATA\Honor", "$env:LOCALAPPDATA\PCManager", "$env:APPDATA\PCManager",
    'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\HONOR', 'D:\Downloads\HonorPCHelper.exe') {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue; Step ("  {0}: {1}" -f $p, $(if (Test-Path $p) { 'остались занятые файлы' } else { 'удалена' })) }
}
# Ярлыки на рабочем столе
Get-ChildItem "$env:USERPROFILE\Desktop", 'C:\Users\Public\Desktop' -Filter *.lnk -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Honor|HONOR|PC Manager' } | ForEach-Object { Remove-Item $_.FullName -Force; Step "  ярлык: $($_.Name)" }

Step 'реестр'
foreach ($root in 'HKLM:\SOFTWARE\HONOR', 'HKLM:\SOFTWARE\WOW6432Node\HONOR') {
    if (Test-Path $root) {
        Get-ChildItem $root | Where-Object { $KeepHonorKeys -notcontains $_.PSChildName } | ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
Get-ChildItem 'HKLM:\SOFTWARE\HONOR\PCManager' -ErrorAction SilentlyContinue | Where-Object { $KeepPCManagerSubkeys -notcontains $_.PSChildName } | ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
Get-Item 'HKLM:\SOFTWARE\HONOR\PCManager' -ErrorAction SilentlyContinue | ForEach-Object { $_.GetValueNames() | ForEach-Object { Remove-ItemProperty 'HKLM:\SOFTWARE\HONOR\PCManager' $_ -ErrorAction SilentlyContinue } }
foreach ($root in 'HKLM:\SOFTWARE\PCManager', 'HKLM:\SOFTWARE\WOW6432Node\PCManager') {
    if (Test-Path $root) { Get-ChildItem $root | Where-Object { $KeepPcManagerRootKeys -notcontains $_.PSChildName } | ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue } }
}
foreach ($k in 'HKCU:\SOFTWARE\HONOR', 'HKCU:\SOFTWARE\PCManager') { if (Test-Path $k) { Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue } }
# Записи «Программы и компоненты»
foreach ($r in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    Get-ChildItem $r -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath
        if ($p.DisplayName -match '^(Honor PC Manager|Magicore)$' -or $p.UninstallString -match 'Program Files\\HONOR\\(PCManager|Magicore|BasicService)') {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
            Step "  запись: $($p.DisplayName)"
        }
    }
}

Step 'проверка остатков'
Get-ChildItem 'C:\Program Files\HONOR' -Force -ErrorAction SilentlyContinue | ForEach-Object { Step "  осталось в Program Files\HONOR: $($_.Name)" }
Get-ChildItem 'C:\ProgramData\Comms' -Force -ErrorAction SilentlyContinue | ForEach-Object { Step "  осталось в ProgramData\Comms: $($_.Name)" }
Step 'Готово. Если остались занятые файлы, перезагрузитесь и запустите скрипт ещё раз (-SkipRestorePoint).'
