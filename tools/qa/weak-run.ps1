# 2026-11-06: В5, шаг 25 (аудит скорости 05.11.2026, п. 31.1) — повторяемая проверка «слабый ПК + плохая сеть» для замеров «до/после».
#   powershell -ExecutionPolicy Bypass -File tools/qa/weak-run.ps1 -Net slownet -Cores 2 -Exe "C:\путь\NurMarketKassa.Avalonia.exe"
#   powershell -ExecutionPolicy Bypass -File tools/qa/weak-run.ps1 -Check        # только проверка условий, ничего не запускается
# Что делает:
#   1) ПРОВЕРЯЕТ условия и отказывается работать, если на ПК уже запущена касса или программа владельца (правило 5: владелец работает в кассе на этом ПК;
#      в кассе единственный экземпляр — вторая не запустится, а чужой процесс мы не трогаем) — флаг -AllowRunning снимает только проверку, не защиту;
#   2) поднимает прокси-стенд tools/qa/slow_server.py на 127.0.0.1:18777 к настоящему серверу (ТОЛЬКО тестовый аккаунт NBS) с правилом задержки профиля -Net;
#   3) запускает кассу с DESKTOP_MARKET_API_URL = стенд и NURMARKET_DATA_ROOT = отдельная папка данных (настоящие данные кассы не читаются и не пишутся),
#      привязывает к -Cores ядрам и ставит приоритет BelowNormal (слабый моноблок);
#   4) снимает «процесс → окно», ресурсы (measure.ps1 стенда, если есть) и прогоняет сценарии стенда по hwnd окна кассы (НЕ по координатам экрана);
#   5) вырезает из журнала кассы за время прогона: Application started, API SLOW, OUTAGE, CATALOG cache ready, SQLITE SLOW, PERF, STARTUP;
#   6) пишет отчёт (таблица «сценарий × профиль») в -OutDir и закрывает ТОЛЬКО запущенную здесь кассу и свой прокси.
# Профили сети (-Net): fast (без задержки), slownet (+400 мс на запрос, 3G), badnet (+2 с, плохой Wi-Fi), serverdown (запись чеков не отвечает), slowbytes (каталог тонкой струйкой).
# Сценарии по hwnd берутся из папки стенда (-Stand: act.py, nbs.py, stress.py, slowact.py, login*.py); этой папки в репозитории нет — она локальная, с тестовым логином NBS.
# Ручной логин (сценарий 2) выполняет человек или login.py стенда; пароли в этот скрипт НЕ вписываются.
param(
    [ValidateSet('fast', 'slownet', 'badnet', 'serverdown', 'slowbytes')] [string]$Net = 'slownet',
    [int]$Cores = 2,
    [string]$Exe = "$env:LOCALAPPDATA\NurMarketKassa\current\NurMarketKassa.Avalonia.exe",
    [string]$Stand = 'C:\Users\User\NurMarket-test\stands\live-nbs-1103',
    [string]$OutDir = 'C:\Users\User\NurMarket-test\perf',
    [string]$DataRoot = '',
    [string[]]$Scenarios = @('scan'),     # сценарии стенда: scan (30 сканов, stress.py add), pay (stress.py pay) — см. $Stand\stress.py
    [int]$Port = 18777,
    [int]$StartTimeoutSec = 90,
    [int]$MeasureSeconds = 300,
    [switch]$AllowRunning,
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$proxyScript = Join-Path $PSScriptRoot 'slow_server.py'
$rules = @{
    fast       = 'api/=proxy:0'
    slownet    = 'api/=proxy:0.4'
    badnet     = 'api/=proxy:2'
    serverdown = 'pos/sales=blackhole'
    slowbytes  = 'products=slowbytes:20'
}
$ts = Get-Date -Format 'yyyyMMdd-HHmm'
if (-not $DataRoot) { $DataRoot = Join-Path $OutDir "weak-data-$Net" }

# --- 1) проверка условий
$problems = New-Object System.Collections.Generic.List[string]
if (-not (Test-Path -LiteralPath $proxyScript)) { $problems.Add("нет прокси-стенда: $proxyScript") }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { $problems.Add('не найден python') }
if (-not (Test-Path -LiteralPath $Exe)) { $problems.Add("не найдена касса: $Exe (укажите -Exe)") }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'NurMarketKassa.Avalonia*' })
if ($running.Count -gt 0 -and -not $AllowRunning) {
    $problems.Add("уже запущена касса/программа владельца (PID $($running.Id -join ', ')) — владелец работает на этом ПК; закройте её сами или запустите проверку позже")
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { $problems.Add("порт $Port занят") }
if ($Cores -lt 1 -or $Cores -gt [Environment]::ProcessorCount) { $problems.Add("-Cores 1..$([Environment]::ProcessorCount)") }
$standOk = (Test-Path -LiteralPath (Join-Path $Stand 'nbs.py')) -and (Test-Path -LiteralPath (Join-Path $Stand 'stress.py'))
Write-Output "weak-run: профиль=$Net ядер=$Cores касса=$Exe стенд-сценарии=$(if ($standOk) { 'есть' } else { 'нет (будут только старт, ресурсы и журнал)' })"
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output "  ! $_" }
    if ($Check) { Write-Output 'weak-run -Check: условия НЕ выполнены (см. выше).'; exit 2 }
    exit 2
}
if ($Check) { Write-Output 'weak-run -Check: условия выполнены, ничего не запускалось.'; exit 0 }

New-Item -ItemType Directory -Force -Path $OutDir, $DataRoot | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$proxy = $null; $kassa = $null; $measure = $null
try {
    # --- 2) прокси к настоящему серверу (тестовый аккаунт NBS)
    $proxyArgs = @('-I', $proxyScript, '--port', $Port, '--upstream', 'https://app.nurcrm.kg', '--rule', $rules[$Net])
    $proxy = Start-Process python -ArgumentList $proxyArgs -PassThru -WindowStyle Hidden
    $ready = $false
    for ($i = 0; $i -lt 20 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 500
        try { Invoke-WebRequest "http://127.0.0.1:$Port/__stats" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true } catch { }
    }
    if (-not $ready) { throw "прокси-стенд не ответил на порту $Port" }

    # --- 3) касса на стенд: свои данные, 2 ядра, пониженный приоритет
    $env:DESKTOP_MARKET_API_URL = "http://127.0.0.1:$Port"
    $env:NURMARKET_DATA_ROOT = $DataRoot
    $log = Join-Path $DataRoot 'Local\NurMarketKassa\Logs\nurmarket-kassa.log'
    $mark = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log -Encoding UTF8).Count } else { 0 }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $kassa = Start-Process $Exe -PassThru
    $kassa.ProcessorAffinity = [IntPtr]([int]([math]::Pow(2, $Cores) - 1))
    $kassa.PriorityClass = 'BelowNormal'

    # --- 4) сценарий 1: процесс -> окно
    $windowMs = $null
    while ($clock.Elapsed.TotalSeconds -lt $StartTimeoutSec) {
        $kassa.Refresh()
        if ($kassa.HasExited) { throw "касса закрылась при старте (код $($kassa.ExitCode))" }
        if ($kassa.MainWindowHandle -ne [IntPtr]::Zero) { $windowMs = [int]$clock.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 100
    }
    $report.Add("| 1. Холодный старт: процесс → окно | $Net | $(if ($windowMs) { "$windowMs мс" } else { "не дождались за $StartTimeoutSec с" }) |")

    $measureScript = Join-Path (Split-Path $OutDir -Parent) 'perf\measure.ps1'
    if (Test-Path -LiteralPath $measureScript) {
        $measure = Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$measureScript`" -Seconds $MeasureSeconds -Interval 2 -Tag weak-$Net -Pid0 $($kassa.Id) -OutDir `"$OutDir`"" -PassThru -WindowStyle Hidden
    }

    # --- сценарии стенда по hwnd окна (ручной логин — до них; без входа сценарии пропускаются)
    if ($standOk) {
        foreach ($name in $Scenarios) {
            $script = switch ($name) { 'scan' { @('stress.py', 'add') } 'pay' { @('stress.py', 'pay') } default { $null } }
            if (-not $script) { $report.Add("| $name | $Net | неизвестный сценарий |"); continue }
            $t = [Diagnostics.Stopwatch]::StartNew()
            # nbs.py стенда хранит PID кассы константой; подставляем PID запущенной здесь кассы и выполняем сценарий в том же процессе python
            $runner = "import sys; sys.path.insert(0, r'$Stand'); import nbs; nbs.KASSA_PID = $($kassa.Id); sys.argv = ['$($script[0])', '$($script[1])']; exec(compile(open(r'$(Join-Path $Stand $script[0])', encoding='utf-8').read(), '$($script[0])', 'exec'))"
            $out = & python -I -c $runner 2>&1 | Out-String
            $report.Add("| $name ($($script[0]) $($script[1])) | $Net | $([int]$t.Elapsed.TotalSeconds) с; $(($out.Trim() -split "`n" | Select-Object -Last 1)) |")
        }
    } else {
        $report.Add("| сценарии стенда | $Net | пропущены: нет $Stand\nbs.py и stress.py |")
    }

    # --- 5) журнал за время прогона
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $log) {
        $pattern = 'Application started|UI scale|API SLOW|OUTAGE|CATALOG cache ready|SQLITE SLOW|\[PERF\]|STARTUP'
        $new = @(Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip $mark | Where-Object { $_ -match $pattern })
        $new | Set-Content (Join-Path $OutDir "weak-$Net-$ts.log") -Encoding UTF8
        $report.Add("| журнал: API SLOW / OUTAGE / SQLITE SLOW | $Net | $(@($new | Where-Object { $_ -match 'API SLOW' }).Count) / $(@($new | Where-Object { $_ -match 'OUTAGE' }).Count) / $(@($new | Where-Object { $_ -match 'SQLITE SLOW' }).Count) |")
    }
    $kassa.Refresh()
    if (-not $kassa.HasExited) { $report.Add("| пик памяти (рабочий набор) | $Net | $([int]($kassa.PeakWorkingSet64 / 1MB)) МБ |") }
}
finally {
    # --- 6) закрываем только своё
    if ($measure -and -not $measure.HasExited) { Stop-Process -Id $measure.Id -ErrorAction SilentlyContinue }
    if ($kassa -and -not $kassa.HasExited) {
        [void]$kassa.CloseMainWindow()
        if (-not $kassa.WaitForExit(15000)) { Stop-Process -Id $kassa.Id -ErrorAction SilentlyContinue }
    }
    if ($proxy -and -not $proxy.HasExited) { Stop-Process -Id $proxy.Id -ErrorAction SilentlyContinue }
    Remove-Item Env:\DESKTOP_MARKET_API_URL, Env:\NURMARKET_DATA_ROOT -ErrorAction SilentlyContinue
}
$header = @("# weak-run $ts — профиль $Net, $Cores ядра, приоритет BelowNormal", '', '| Сценарий | Профиль | Результат |', '|---|---|---|')
($header + $report) | Set-Content (Join-Path $OutDir "weak-$Net-$ts.md") -Encoding UTF8
($header + $report) | ForEach-Object { Write-Output $_ }
