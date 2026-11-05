# 2026-11-05 (E2E-тесты мобилки, первый этап): прогон integration_test на эмуляторе Pixel_7_API_35 с поддельным сервером.
# Запуск (из любого места):  powershell -ExecutionPolicy Bypass -File tools/qa/mobile-e2e.ps1
#   -Window       показать окно эмулятора (по умолчанию без окна)
#   -KeepEmulator не останавливать эмулятор после прогона
#   -Port         порт поддельного сервера на самом устройстве (по умолчанию 18080)
#   -Test         какой файл integration_test запускать (по умолчанию integration_test/app_e2e_test.dart)
# Что делает: берёт замок сборки Global\NurMarketBuildLock (ПК 7,8 ГБ — пока идёт эмулятор, других сборок быть не должно) ->
# поднимает эмулятор ХОЛОДНЫМ (без снимков, программная графика, 2 ГБ, без звука; на /data мало места — порог хранилища
# временно снижается и потом возвращается) -> flutter test с --dart-define=NURMARKET_API_URL=http://127.0.0.1:порт ->
# останавливает эмулятор, отпускает замок -> пишет итог. Боевой сервер и настоящие учётки не используются.
param(
    [switch]$Window,
    [switch]$KeepEmulator,
    [int]$Port = 18080,
    [string]$Test = "integration_test/app_e2e_test.dart",
    [string]$Avd = "Pixel_7_API_35"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$mobile = Join-Path $repo "NurMarketMobile"
$sdk = "C:\Users\User\AppData\Local\Android\Sdk"
$adb = Join-Path $sdk "platform-tools\adb.exe"
$emu = Join-Path $sdk "emulator\emulator.exe"
$flutterBin = "C:\Users\User\flutter\bin"
$env:PATH = "$flutterBin;$sdk\platform-tools;$sdk\emulator;$env:PATH"
$serial = "emulator-5554"
$log = Join-Path $env:TEMP "nurmarket-e2e-emulator.log"
$apiUrl = "http://127.0.0.1:$Port"

foreach ($f in @($adb, $emu, (Join-Path $flutterBin "flutter.bat"))) {
    if (-not (Test-Path $f)) { Write-Output "E2E: НЕТ файла $f"; exit 2 }
}

# --- замок сборки (тот же, что у build-locked.ps1)
$mutex = New-Object System.Threading.Mutex($false, "Global\NurMarketBuildLock")
$got = $false
try { $got = $mutex.WaitOne(500000) } catch [System.Threading.AbandonedMutexException] { $got = $true }
if (-not $got) { Write-Output "BUILD-LOCK-BUSY: другой агент собирает, повторите позже"; exit 3 }

$emuProc = $null
$exit = 1
try {
    # --- эмулятор: уже запущенный чужой не трогаем
    $running = @(& $adb devices | Select-String -Pattern "^emulator-\d+\s+device")
    if ($running.Count -gt 0) {
        Write-Output "E2E: эмулятор уже запущен — использую его (останавливать не буду)."
        $KeepEmulator = $true
        $serial = ($running[0].Line -split "\s+")[0]
    } else {
        $emuArgs = @("-avd", $Avd, "-port", "5554", "-no-snapshot", "-no-snapshot-save", "-gpu", "swiftshader_indirect", "-memory", "2048", "-no-audio", "-no-boot-anim")
        if (-not $Window) { $emuArgs += "-no-window" }
        Write-Output "E2E: запускаю эмулятор $Avd (холодный старт)..."
        $emuProc = Start-Process -FilePath $emu -ArgumentList $emuArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        & $adb wait-for-device | Out-Null
        $deadline = (Get-Date).AddMinutes(8)
        while ($true) {
            $boot = ((& $adb -s $serial shell getprop sys.boot_completed 2>$null) | Out-String).Trim()
            if ($boot -eq "1") { break }
            if ((Get-Date) -gt $deadline) { throw "эмулятор не загрузился за 8 минут (журнал: $log)" }
            Start-Sleep -Seconds 5
        }
        Start-Sleep -Seconds 10
        Write-Output "E2E: эмулятор загружен."
    }

    # --- место на /data мало: порог хранилища Android снижаем на время прогона (потом возвращаем)
    & $adb -s $serial shell settings put global sys_storage_threshold_percentage 1 | Out-Null
    & $adb -s $serial shell settings put global sys_storage_threshold_max_bytes 10485760 | Out-Null
    # анимации системы выключаем — тесты не ждут впустую
    foreach ($k in @("window_animation_scale", "transition_animation_scale", "animator_duration_scale")) { & $adb -s $serial shell settings put global $k 0 | Out-Null }

    # --- сам прогон
    Push-Location $mobile
    try {
        Write-Output "E2E: flutter test $Test (поддельный сервер $apiUrl на устройстве)..."
        $ErrorActionPreference = "Continue"
        & flutter test $Test -d $serial "--dart-define=NURMARKET_API_URL=$apiUrl" 2>&1 | ForEach-Object { Write-Output "$_" }
        $exit = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
    } finally { Pop-Location }
} catch {
    Write-Output "E2E: ОШИБКА запуска: $($_.Exception.Message)"
    $exit = 2
} finally {
    $ErrorActionPreference = "Continue"
    try {
        foreach ($k in @("sys_storage_threshold_percentage", "sys_storage_threshold_max_bytes")) { & $adb -s $serial shell settings delete global $k 2>$null | Out-Null }
        foreach ($k in @("window_animation_scale", "transition_animation_scale", "animator_duration_scale")) { & $adb -s $serial shell settings put global $k 1.0 2>$null | Out-Null }
    } catch { }
    if (-not $KeepEmulator -and $emuProc) {
        Write-Output "E2E: останавливаю эмулятор..."
        try { & $adb -s $serial emu kill 2>$null | Out-Null } catch { }
        if (-not $emuProc.WaitForExit(60000)) { Stop-Process -Id $emuProc.Id -Force -ErrorAction SilentlyContinue }
    }
    $mutex.ReleaseMutex()
}
if ($exit -eq 0) { Write-Output "E2E ИТОГ: ВСЕ СЦЕНАРИИ ЗЕЛЁНЫЕ" } else { Write-Output "E2E ИТОГ: ЕСТЬ ОШИБКИ (код $exit)" }
exit $exit
