# 2026-11-05 (аудит №4, волна 0-A): ЕДИНСТВЕННЫЙ разрешённый способ запускать тесты NurMarketKassa.Tests.
#   powershell -ExecutionPolicy Bypass -File tools/qa/test-safe.ps1 [-Filter "FullyQualifiedName~Money"] [-TimeoutMinutes 15]
# Что делает:
#   1) NURMARKET_DATA_ROOT -> уникальная временная папка (все пути данных кассы — AppPaths.Roaming/Local — ведут внутрь неё; тесты
#      дополнительно создают свою подпапку run-XXXX и удаляют её при выходе); DESKTOP_MARKET_API_URL -> заглушка 127.0.0.1:9
#      (случайный сетевой вызов не дойдёт до боевого app.nurcrm.kg);
#   2) берёт общий замок сборки Global\NurMarketBuildLock (ПК 7,8 ГБ: ничего параллельно);
#   3) `dotnet test NurMarketKassa.Tests -m:1 -nodeReuse:false`;
#   4) сверяет SHA-256 настоящих %APPDATA%\NurMarketKassa\data\pos_local.db (+ -wal) и user-settings.json ДО и ПОСЛЕ и список изменённых
#      файлов; результат пишет в консоль и в %LOCALAPPDATA%\Temp\nurmarket-tests\test-safe-last.txt.
# Если касса запущена и сама пишет в pos_local.db, хэш может измениться без вины тестов — тогда смотрите список «изменённые файлы»:
# файлы с именами тестов (test-*, run-*) или новые файлы в боевой папке = тесты писали в боевые данные.
param(
    [string]$Filter = "",
    [int]$TimeoutMinutes = 15,
    [string]$Project = "NurMarketKassa.Tests/NurMarketKassa.Tests.csproj"
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$solution = Join-Path $repo "NurMarketKassa.Avalonia"
$tempBase = Join-Path ([IO.Path]::GetTempPath()) "nurmarket-tests"
New-Item -ItemType Directory -Force -Path $tempBase | Out-Null
$resultFile = Join-Path $tempBase "test-safe-last.txt"
$dataRoot = Join-Path $tempBase ("root-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

# --- настоящие данные кассы: что охраняем
$realRoaming = [Environment]::GetFolderPath("ApplicationData")
$guarded = @(
    (Join-Path $realRoaming "NurMarketKassa\data\pos_local.db"),
    (Join-Path $realRoaming "NurMarketKassa\user-settings.json")
)

function Get-FileHashSafe([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "<нет файла>" }
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $sha = [Security.Cryptography.SHA256]::Create(); return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace "-", "") }
        finally { $fs.Dispose() }
    } catch { return "<не прочитан: $($_.Exception.Message)>" }
}

function Get-Snapshot {
    $snap = [ordered]@{}
    foreach ($f in $guarded) { $snap[$f] = Get-FileHashSafe $f }
    return $snap
}

# База в режиме WAL: записи сначала попадают в pos_local.db-wal, а сам .db меняется только при контрольной точке — хэш одного .db слаб.
# Поэтому дополнительно читаем (только чтение) число строк в Products и OfflineSales через python, если он есть.
function Get-DbCounts {
    $db = Join-Path $realRoaming "NurMarketKassa\data\pos_local.db"
    if (-not (Test-Path -LiteralPath $db)) { return "<нет базы>" }
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { return "<python не найден>" }
    $code = "import sqlite3,sys; c=sqlite3.connect('file:'+sys.argv[1].replace(chr(92),'/')+'?mode=ro', uri=True, timeout=5); print(' '.join(t+'='+str(c.execute('select count(*) from '+t).fetchone()[0]) for t in ('Products','OfflineSales')))"
    try { return (& $py.Source -I -c $code $db 2>&1 | Out-String).Trim() } catch { return "<не прочитано: $($_.Exception.Message)>" }
}

# список файлов боевой папки с временем изменения — чтобы увидеть, ЧТО именно менялось
function Get-TreeStamp {
    $map = @{}
    $root = Join-Path $realRoaming "NurMarketKassa"
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch "\\(Logs|_previous)\\" -and $_.Name -notmatch "\.(db-wal|db-shm)$" } |
            ForEach-Object { $map[$_.FullName] = "$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
    }
    return $map
}

$before = Get-Snapshot
$countsBefore = Get-DbCounts
$treeBefore = Get-TreeStamp

$mutex = New-Object System.Threading.Mutex($false, "Global\NurMarketBuildLock")
$got = $false
try { $got = $mutex.WaitOne([TimeSpan]::FromMinutes($TimeoutMinutes)) } catch [System.Threading.AbandonedMutexException] { $got = $true }
if (-not $got) {
    $msg = "BUILD-LOCK-BUSY: другой агент собирает, повторите позже"
    $msg | Tee-Object -FilePath $resultFile
    exit 3
}
$testExit = 1
$outFile = Join-Path $tempBase "test-safe-output.txt"
try {
    $env:NURMARKET_DATA_ROOT = $dataRoot
    $env:DESKTOP_MARKET_API_URL = "http://127.0.0.1:9"
    Set-Location $solution
    $dotnetArgs = @("test", $Project, "-m:1", "-nodeReuse:false", "--nologo")
    if ($Filter) { $dotnetArgs += @("--filter", $Filter) }
    $ErrorActionPreference = "Continue"
    & dotnet @dotnetArgs 2>&1 | Out-File -FilePath $outFile -Encoding utf8
    $testExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
} finally {
    Remove-Item Env:\NURMARKET_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\DESKTOP_MARKET_API_URL -ErrorAction SilentlyContinue
    $mutex.ReleaseMutex()
}

$after = Get-Snapshot
$countsAfter = Get-DbCounts
$treeAfter = Get-TreeStamp

$changed = @()
foreach ($f in $guarded) { if ($before[$f] -ne $after[$f]) { $changed += $f } }
$touched = @()
foreach ($k in $treeAfter.Keys) { if (-not $treeBefore.ContainsKey($k)) { $touched += "новый: $k" } elseif ($treeBefore[$k] -ne $treeAfter[$k]) { $touched += "изменён: $k" } }
foreach ($k in $treeBefore.Keys) { if (-not $treeAfter.ContainsKey($k)) { $touched += "удалён: $k" } }

$summary = Select-String -Path $outFile -Pattern "Passed!|Failed!|Failed |error |Пройден|Не пройден|Total tests|Passed:|Failed:" | Select-Object -First 40 | ForEach-Object { $_.Line.Trim() }

$lines = @()
$lines += "test-safe: dotnet test EXIT=$testExit"
$lines += "NURMARKET_DATA_ROOT (временная): $dataRoot"
$lines += $summary
$lines += "--- настоящие данные кассы ---"
$lines += "строк в базе (Products / OfflineSales) до: $countsBefore ; после: $countsAfter"
foreach ($f in $guarded) { $lines += ("{0}: {1}" -f (Split-Path $f -Leaf), $(if ($before[$f] -eq $after[$f]) { "НЕ ИЗМЕНЁН ($($after[$f].Substring(0, [Math]::Min(12, $after[$f].Length))))" } else { "ИЗМЕНЁН: $($before[$f]) -> $($after[$f])" })) }
if ($touched.Count -gt 0) { $lines += "изменённые файлы в боевой папке NurMarketKassa (без Logs):"; $lines += ($touched | ForEach-Object { "  " + $_ }) } else { $lines += "боевая папка NurMarketKassa: ни один файл не менялся" }
# Касса или программа владельца могут быть запущены и сами писать настройки/состояние (масштаб, шифрование ключей, «последний онлайн»).
# Тогда изменение user-settings.json/state.json — предупреждение (смотрите время и список выше), а не провал. Провал — только если
# изменилось число строк в базе (Products/OfflineSales) или кассы нет в запущенных, а файлы поменялись.
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "NurMarket*" }).Count -gt 0
$failed = @()
if ($countsBefore -ne $countsAfter) { $failed += "число строк в базе: $countsBefore -> $countsAfter" }
if (-not $running -and $changed.Count -gt 0) { $failed += $changed }
$warn = @()
if ($running -and $changed.Count -gt 0) { $warn = $changed | ForEach-Object { Split-Path $_ -Leaf } }
$lines += "касса/программа владельца запущены: $running"
if ($failed.Count -gt 0) { $verdict = "РЕЗУЛЬТАТ: ПРОВАЛ, тесты, возможно, писали в настоящие данные: " + ($failed -join "; ") }
elseif ($warn.Count -gt 0) { $verdict = "РЕЗУЛЬТАТ: число строк в базе не изменилось; предупреждение: запущенная касса сама меняла " + ($warn -join ", ") }
else { $verdict = "РЕЗУЛЬТАТ: настоящие данные кассы не тронуты" }
$lines += $verdict
$lines += "Полный вывод dotnet test: $outFile"
$lines | Tee-Object -FilePath $resultFile

try { Remove-Item -Recurse -Force -LiteralPath $dataRoot -ErrorAction SilentlyContinue } catch {}
if ($testExit -ne 0) { exit $testExit }
if ($failed.Count -gt 0) { exit 4 }
exit 0
