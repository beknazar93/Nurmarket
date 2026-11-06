# 2026-11-06: В5, шаг 25 (аудит скорости 05.11.2026, п. 31.1) — «слабый телефон»: эмулятор Android с 2 ядрами, 2 ГБ памяти и медленной мобильной сетью.
#   powershell -ExecutionPolicy Bypass -File tools/qa/weak-android.ps1 -Apk "C:\путь\kassa.apk"
#   powershell -ExecutionPolicy Bypass -File tools/qa/weak-android.ps1 -Check        # только проверка условий, ничего не запускается
# Что делает: (1) при необходимости поднимает AVD (-Avd) с -cores 2 -memory 2048 -netdelay umts -netspeed hsdpa; (2) ставит APK (если задан -Apk);
# (3) холодный старт N раз: `am force-stop` + `am start -W` (TotalTime/WaitTime — время до первого кадра); (4) `dumpsys gfxinfo` (доля «рваных» кадров) и
# `dumpsys meminfo` (память приложения) после прогона. Результат — таблица в консоль и в -OutDir\weak-android-<время>.md.
# Работает ТОЛЬКО с эмулятором (adb -s emulator-*) — настоящий телефон владельца не трогает. Входа в аккаунт не делает: если нужна продажа/долги, их проходят вручную
# или сценарием мобилки (tools/qa/mobile-e2e.ps1) на тестовом аккаунте NBS.
param(
    [string]$Avd = 'Pixel_7_API_35',
    [string]$Package = 'kg.nurmarket.kassa',      # касса Android; мобилка для агентов — свой id (см. NurMarketMobile/android/app/build.gradle*)
    [string]$Apk = '',
    [int]$Runs = 5,
    [int]$Cores = 2,
    [int]$MemoryMb = 2048,
    [string]$NetDelay = 'umts',
    [string]$NetSpeed = 'hsdpa',
    [string]$Sdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$OutDir = 'C:\Users\User\NurMarket-test\perf',
    [int]$BootTimeoutSec = 240,
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$adb = Join-Path $Sdk 'platform-tools\adb.exe'
$emulator = Join-Path $Sdk 'emulator\emulator.exe'
$problems = New-Object System.Collections.Generic.List[string]
if (-not (Test-Path -LiteralPath $adb)) { $problems.Add("нет adb: $adb") }
if (-not (Test-Path -LiteralPath $emulator)) { $problems.Add("нет эмулятора: $emulator") }
if ($Apk -and -not (Test-Path -LiteralPath $Apk)) { $problems.Add("нет APK: $Apk") }
if ($Runs -lt 1) { $problems.Add('-Runs не меньше 1') }
Write-Output "weak-android: AVD=$Avd пакет=$Package ядер=$Cores память=$MemoryMb МБ сеть=$NetDelay/$NetSpeed запусков=$Runs"
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output "  ! $_" }
    Write-Output 'weak-android: условия НЕ выполнены.'
    exit 2
}
if ($Check) { Write-Output 'weak-android -Check: условия выполнены, ничего не запускалось.'; exit 0 }

function Get-EmulatorSerial {
    $line = & $adb devices | Where-Object { $_ -match '^emulator-\d+\s+device' } | Select-Object -First 1
    if ($line) { return ($line -split '\s+')[0] }
    return $null
}

$startedEmulator = $null
$serial = Get-EmulatorSerial
if (-not $serial) {
    $startedEmulator = Start-Process $emulator -ArgumentList @('-avd', $Avd, '-cores', $Cores, '-memory', $MemoryMb, '-netdelay', $NetDelay, '-netspeed', $NetSpeed, '-no-snapshot-load', '-no-audio') -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $BootTimeoutSec) {
        Start-Sleep -Seconds 3
        $serial = Get-EmulatorSerial
        if ($serial -and ((& $adb -s $serial shell getprop sys.boot_completed 2>$null) -join '').Trim() -eq '1') { break }
        $serial = $null
    }
    if (-not $serial) {
        if ($startedEmulator -and -not $startedEmulator.HasExited) { Stop-Process -Id $startedEmulator.Id -ErrorAction SilentlyContinue }
        throw "эмулятор не загрузился за $BootTimeoutSec с"
    }
}
else {
    Write-Output "уже запущен $serial — используется он (параметры ядер/сети не меняются; для чистой проверки закройте его и запустите скрипт снова)"
}

$rows = New-Object System.Collections.Generic.List[string]
try {
    if ($Apk) { & $adb -s $serial install -r $Apk | Out-Null }
    if (-not (((& $adb -s $serial shell pm list packages $Package) -join "`n") -match [regex]::Escape($Package))) { throw "пакет $Package не установлен (укажите -Apk)" }
    $component = (& $adb -s $serial shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $Package | Select-Object -Last 1).Trim()

    # 1) холодный старт
    $totals = @()
    for ($i = 1; $i -le $Runs; $i++) {
        & $adb -s $serial shell am force-stop $Package | Out-Null
        Start-Sleep -Seconds 2
        $text = (& $adb -s $serial shell am start -W -n $component) -join "`n"
        $total = if ($text -match 'TotalTime:\s*(\d+)') { [int]$Matches[1] } else { $null }
        $wait = if ($text -match 'WaitTime:\s*(\d+)') { [int]$Matches[1] } else { $null }
        if ($total) { $totals += $total }
        $rows.Add("| холодный старт #$i | TotalTime $total мс, WaitTime $wait мс |")
        Start-Sleep -Seconds 3
    }
    if ($totals.Count -gt 0) {
        $sorted = $totals | Sort-Object
        $p50 = $sorted[[int][math]::Floor(($sorted.Count - 1) * 0.5)]
        $p95 = $sorted[[int][math]::Ceiling(($sorted.Count - 1) * 0.95)]
        $rows.Add("| холодный старт: p50 / p95 | $p50 / $p95 мс |")
    }

    # 2) плавность и память за время работы (без входа в аккаунт — экран входа/главный)
    & $adb -s $serial shell dumpsys gfxinfo $Package reset | Out-Null
    Start-Sleep -Seconds 20
    $gfx = (& $adb -s $serial shell dumpsys gfxinfo $Package) -join "`n"
    if ($gfx -match 'Janky frames:\s*(\d+)\s*\(([\d.]+)%\)') { $rows.Add("| рваные кадры (Janky) | $($Matches[1]) шт ($($Matches[2]) %) |") }
    if ($gfx -match 'Total frames rendered:\s*(\d+)') { $rows.Add("| кадров всего | $($Matches[1]) |") }
    $mem = (& $adb -s $serial shell dumpsys meminfo $Package) -join "`n"
    if ($mem -match 'TOTAL PSS:\s*(\d+)') { $rows.Add("| память (TOTAL PSS) | $([int]([int]$Matches[1] / 1024)) МБ |") }
    elseif ($mem -match 'TOTAL\s+(\d+)') { $rows.Add("| память (TOTAL, КБ) | $($Matches[1]) |") }
}
finally {
    if ($serial) { & $adb -s $serial shell am force-stop $Package 2>$null | Out-Null }
    if ($startedEmulator -and -not $startedEmulator.HasExited) {
        & $adb -s $serial emu kill 2>$null | Out-Null
        if (-not $startedEmulator.WaitForExit(20000)) { Stop-Process -Id $startedEmulator.Id -ErrorAction SilentlyContinue }
    }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ts = Get-Date -Format 'yyyyMMdd-HHmm'
$header = @("# weak-android $ts — $Avd, $Cores ядра, $MemoryMb МБ, сеть $NetDelay/$NetSpeed, пакет $Package", '', '| Показатель | Значение |', '|---|---|')
($header + $rows) | Set-Content (Join-Path $OutDir "weak-android-$ts.md") -Encoding UTF8
($header + $rows) | ForEach-Object { Write-Output $_ }
