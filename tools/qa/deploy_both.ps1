# Ставит dev-out в установленную кассу и перезапускает и кассу, и программу владельца.
# Запуск: powershell -ExecutionPolicy Bypass -File tools/qa/deploy_both.ps1
$S = $PSScriptRoot
# 2026-10-15: без dev-out Resolve-Path давал $null, robocopy получал мусор, а старая версия всё равно запускалась.
$src = Join-Path $S "..\..\NurMarketKassa.Avalonia\dev-out"
if (-not (Test-Path (Join-Path $src "NurMarketKassa.Avalonia.exe"))) { Write-Error "нет сборки: $src — сначала dotnet publish"; exit 1 }
$src = (Resolve-Path $src).Path
$bk = Join-Path $env:TEMP ("nurmarket-dbbackup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')); New-Item -ItemType Directory -Force $bk | Out-Null
# 2026-10-15: сначала обычное закрытие (окно сохраняет чек и закрывает SQLite), Stop-Process -Force — только
# если за 15 с не закрылась; ждём именно завершения процессов, а не 2 с.
$p = @(Get-Process NurMarketKassa.Avalonia -ErrorAction SilentlyContinue)
if ($p.Count -gt 0) {
    $p | ForEach-Object { [void]$_.CloseMainWindow() }
    $p | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    $left = @(Get-Process NurMarketKassa.Avalonia -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) { $left | Stop-Process -Force; $left | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; "принудительно остановлено: " + $left.Count }
    "processes stopped: " + $p.Count
}
$dst = "$env:LOCALAPPDATA\NurMarketKassa\current"
$exe = "$dst\NurMarketKassa.Avalonia.exe"
# 2026-10-16: запуск кассы и программы владельца — общий для успешной установки и для отката после ошибки
# (раньше при ошибке копии базы скрипт выходил, а касса на рабочем ПК владельца оставалась закрытой).
# 2026-10-03: запуск через WMI — программы, запущенные Start-Process, закрывались вместе с командой ИИ-агента.
function Start-Both {
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`""; CurrentDirectory = $dst } | Out-Null
    Start-Sleep -Seconds 12
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" --owner"; CurrentDirectory = $dst } | Out-Null
    Start-Sleep -Seconds 15
}
# 2026-10-16: место на C: мало — старые копии (кроме 4 последних) удаляются ДО новой копии, иначе при полном
# диске копия падала каждый раз, а чистка до неё не доходила.
Get-ChildItem $env:TEMP -Directory -Filter "nurmarket-dbbackup-*" | Where-Object { $_.FullName -ne $bk } | Sort-Object Name -Descending | Select-Object -Skip 4 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
# 2026-10-15: ошибка копии базы раньше не проверялась — установка шла без резервной копии.
try {
    if (Test-Path "$env:APPDATA\NurMarketKassa\data\pos_local.db") { Copy-Item "$env:APPDATA\NurMarketKassa\data\pos_local.db*" $bk -ErrorAction Stop }
    New-Item -ItemType Directory -Force "$bk\owner" | Out-Null
    if (Test-Path "$env:APPDATA\NurMarketOwner\data\pos_local.db") { Copy-Item "$env:APPDATA\NurMarketOwner\data\pos_local.db*" "$bk\owner" -ErrorAction Stop }
} catch {
    Write-Error "не удалось сохранить копию базы ($($_.Exception.Message)) — установка отменена, запускаю прежнюю версию"
    if ($p.Count -gt 0) { Start-Both }
    exit 1
}
"копия баз: $bk"
robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
$rc = $LASTEXITCODE
"robocopy код $rc"
# 2026-10-15: robocopy 0–7 — успех, 8 и выше — часть файлов не скопирована (например, занята). Тогда не запускаем
# полусобранную версию: в current смесь старых и новых файлов, владелец должен решить сам (повторить установку).
if ($rc -ge 8) { Write-Error "копирование не прошло (robocopy $rc) — программа не запущена, повторите установку"; exit 1 }
Start-Both
Get-CimInstance Win32_Process -Filter "Name='NurMarketKassa.Avalonia.exe'" | ForEach-Object {
    $proc = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
    $mem = if ($proc) { [int]($proc.WorkingSet64/1MB) } else { "?" }
    "запущена pid $($_.ProcessId) $($_.CommandLine -replace '.*\.exe"?', '') память $mem МБ"
}
