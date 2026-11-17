<#
.SYNOPSIS
  Сохраняет и устанавливает службы Honor, которые нужны ноутбуку без PC Manager:
  OSDServer (экранные подсказки яркости и громкости) и HonorAPOService (звук),
  а также файлы калибровки экрана и настройки, которые использует TempTray.

.DESCRIPTION
  Backup  — копирует файлы, ключи реестра и файлы цвета в папку резервной копии.
  Install — разворачивает всё из резервной копии, создаёт службы и запускает их. Можно запускать повторно.
  Status  — показывает, что из этого сейчас есть в системе.

  Запускать от администратора. Файлы Honor в репозиторий не входят: резервная копия лежит вне его.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Backup', 'Install', 'Status')][string]$Action,
    [string]$BackupDir = 'D:\AI\TempTray-honor-backup'
)

$ErrorActionPreference = 'Stop'

# Папки, которые нужны службам: (откуда в системе, куда в копии)
$Folders = @(
    @{ Source = 'C:\Program Files\HONOR\OSD';          Name = 'ProgramFiles\OSD' },
    @{ Source = 'C:\Windows\System32\RPC\OSD';         Name = 'System32\RPC\OSD' },
    @{ Source = 'C:\Program Files\HONOR\APOService';   Name = 'ProgramFiles\APOService' },
    @{ Source = 'C:\Windows\System32\HonorApoService'; Name = 'System32\HonorApoService' },
    @{ Source = 'C:\ProgramData\Comms\Osd';              Name = 'ProgramData\Comms\Osd' }
)

# Службы: имя, исполняемый файл, отображаемое имя, действия при сбое (OSD перезапускается через секунду)
$Services = @(
    @{ Name = 'OSDServer';        Exe = 'C:\Windows\System32\RPC\OSD\osdservice.exe';                  Display = 'OSD Service';        Restart = $true },
    @{ Name = 'HonorAPOService';  Exe = 'C:\Windows\System32\HonorApoService\HonorAPOService.exe';     Display = 'Honor APO Service';  Restart = $false }
)

# Ключи реестра с настройками: OSD и то, что TempTray хранит там же, где PC Manager
$RegKeys = @(
    @{ Key = 'HKLM\SOFTWARE\HONOR\OSD';                          File = 'osd.reg' },
    @{ Key = 'HKLM\SOFTWARE\HONOR\ASvidDMFT';                    File = 'asvid.reg' },
    @{ Key = 'HKLM\SOFTWARE\HonorAPO';                            File = 'honorapo.reg' },
    @{ Key = 'HKLM\SOFTWARE\HONOR\PCManager\LCDEnhancement';     File = 'lcd-enhancement.reg' },
    @{ Key = 'HKLM\SOFTWARE\PCManager\MBAPowerManager';          File = 'power-manager.reg' }
)

# Файлы цвета: калибровка панели и ICC-профили режимов (кладёт в систему установщик Honor)
$ColorDir = 'C:\Windows\System32\spool\drivers\color'
$ColorPatterns = @('*_display_factory_calibration_*.xml', 'HonorNative.icc', 'Internet_and_Web.icc', 'Photography.icc',
    'Digital_Cinema_P3-D65.icc', 'Digital_Cinema_P3-DCI.icc', 'Graphic_Design.icc', 'HDTV.icc', 'SDTV_NTSC_Video.icc',
    'SDTV_NTSC_Video(9300K).icc', 'SDTV_PAL_Video.icc', 'Photography_and_Printing.icc', 'Game_Pro.icc', 'MagicP3.icc', 'MagicUnify.icc')

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Copy-Folder($from, $to) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    # /E подпапки, /R:1 /W:1 не ждать занятые файлы, /NFL /NDL /NJH /NJS тише
    robocopy $from $to /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy '$from' -> '$to' завершился кодом $LASTEXITCODE" }
}

if ($Action -ne 'Status' -and -not (Test-Admin)) { throw 'Нужны права администратора.' }

switch ($Action) {
    'Status' {
        foreach ($s in $Services) {
            $svc = Get-Service $s.Name -ErrorAction SilentlyContinue
            '{0,-18} {1}' -f $s.Name, $(if ($svc) { "$($svc.Status), $($svc.StartType)" } else { 'не установлена' })
        }
        foreach ($f in $Folders) { '{0,-28} {1}' -f $f.Source, $(if (Test-Path $f.Source) { 'есть' } else { 'нет' }) }
        'Файлов калибровки: ' + @(Get-ChildItem $ColorDir -Filter '*_display_factory_calibration_*.xml' -ErrorAction SilentlyContinue).Count
        'Резервная копия: ' + $(if (Test-Path $BackupDir) { $BackupDir } else { 'нет' })
    }

    'Backup' {
        New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
        foreach ($f in $Folders) {
            if (Test-Path $f.Source) { Copy-Folder $f.Source (Join-Path $BackupDir $f.Name); "папка: $($f.Source)" }
            else { "пропущено (нет): $($f.Source)" }
        }
        foreach ($r in $RegKeys) {
            # Проверяем ключ заранее: ошибка reg.exe при ErrorActionPreference=Stop обрывает весь скрипт
            if (Test-Path ('Registry::' + $r.Key)) {
                reg export $r.Key (Join-Path $BackupDir $r.File) /y | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "reg export $($r.Key) завершился кодом $LASTEXITCODE" }
                "реестр: $($r.Key)"
            }
            else { "пропущено (нет): $($r.Key)" }
        }
        $colorOut = Join-Path $BackupDir 'color'
        New-Item -ItemType Directory -Force -Path $colorOut | Out-Null
        $count = 0
        foreach ($p in $ColorPatterns) {
            Get-ChildItem $ColorDir -Filter $p -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName $colorOut -Force; $count++ }
        }
        "файлов цвета: $count"
        'Готово: ' + $BackupDir
    }

    'Install' {
        if (-not (Test-Path $BackupDir)) { throw "Нет резервной копии: $BackupDir. Сначала выполните Backup на ноутбуке с установленным PC Manager." }
        foreach ($f in $Folders) {
            $src = Join-Path $BackupDir $f.Name
            if (Test-Path $src) { Copy-Folder $src $f.Source; "папка: $($f.Source)" }
        }
        foreach ($s in $Services) {
            if (-not (Test-Path $s.Exe)) { "пропущено, нет файла: $($s.Exe)"; continue }
            if (-not (Get-Service $s.Name -ErrorAction SilentlyContinue)) {
                sc.exe create $s.Name binPath= "`"$($s.Exe)`"" start= auto obj= LocalSystem DisplayName= "`"$($s.Display)`"" | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "sc create $($s.Name) завершился кодом $LASTEXITCODE" }
                "служба создана: $($s.Name)"
            }
            Set-Service $s.Name -StartupType Automatic
            if ($s.Restart) { sc.exe failure $s.Name reset= 86400 actions= restart/1000 | Out-Null }
            if ((Get-Service $s.Name).Status -ne 'Running') { Start-Service $s.Name }
            "служба работает: $($s.Name)"
        }
        foreach ($r in $RegKeys) {
            $file = Join-Path $BackupDir $r.File
            # Настройки TempTray не затираем, если они уже есть; остальное возвращаем
            if ((Test-Path $file) -and -not (Test-Path ('Registry::' + $r.Key))) { reg import $file 2>$null | Out-Null; "реестр: $($r.Key)" }
        }
        $colorSrc = Join-Path $BackupDir 'color'
        if (Test-Path $colorSrc) {
            Get-ChildItem $colorSrc -File | ForEach-Object {
                $dst = Join-Path $ColorDir $_.Name
                if (-not (Test-Path $dst)) { Copy-Item $_.FullName $dst; "цвет: $($_.Name)" }
            }
        }
        'Готово.'
    }
}
