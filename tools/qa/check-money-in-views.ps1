# 2026-11-06: В1 — статическая проверка «нет денежных расчётов во view» (критерий 3.1: ни одной денежной формулы в *.axaml.cs). ТОЛЬКО ЧИТАЕТ файлы.
#   powershell -ExecutionPolicy Bypass -File tools/qa/check-money-in-views.ps1 [-Strict] [-SaveBaseline] [-Root <корень>]
# Что ищет в окнах и диалогах (NurMarketKassa.Avalonia\Views и Views.axaml, файлы *.cs): ComputeRefundSplit, Math.Round, Math.Floor, Math.Ceiling, Floor(, MidpointRounding
# (строки-комментарии и хвосты после // не считаются).
# Два режима:
#   по умолчанию — «не хуже базы»: сверка с tools/qa/money-in-views.baseline.txt (файл;число). Новый файл с попаданием или рост числа в файле — ошибка.
#                  Базу перезаписывает -SaveBaseline (после того как волна осознанно убрала расчёт из окна — число падает, база обновляется).
#   -Strict      — цель критерия: 0 попаданий вне белого списка tools/qa/money-in-views.allow.txt. Строка списка: `путь\к\файлу.cs` (весь файл законный:
#                  размеры, анимация, предпросмотр) или `путь\к\файлу.cs|подстрока строки` (законна только строка с этой подстрокой). Пустой список = 0 допусков.
# Код выхода: 0 — всё в порядке; 1 — нарушение; 2 — не найдены папки.
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$Strict,
    [switch]$SaveBaseline
)
$ErrorActionPreference = "Stop"
$app = Join-Path $Root "NurMarketKassa.Avalonia\NurMarketKassa.Avalonia"
$dirs = @("Views", "Views.axaml") | ForEach-Object { Join-Path $app $_ } | Where-Object { Test-Path -LiteralPath $_ }
if ($dirs.Count -eq 0) { Write-Host "ОШИБКА: папки Views не найдены в $app"; exit 2 }
$baselineFile = Join-Path $PSScriptRoot "money-in-views.baseline.txt"
$allowFile = Join-Path $PSScriptRoot "money-in-views.allow.txt"
$pattern = [regex]'ComputeRefundSplit|Math\.Round|Math\.Floor|Math\.Ceiling|\bFloor\(|MidpointRounding'

function Strip-Comment([string]$line) {
    $t = $line.TrimStart()
    if ($t.StartsWith('//')) { return '' }
    $i = $line.IndexOf('//')
    # `//` внутри строки-литерала (адреса http://) не комментарий: режем, только если слева чётное число кавычек
    while ($i -ge 0) {
        $left = $line.Substring(0, $i)
        if ((($left.ToCharArray() | Where-Object { $_ -eq '"' }).Count % 2) -eq 0) { return $left }
        $i = $line.IndexOf('//', $i + 2)
    }
    return $line
}

$hits = New-Object System.Collections.Generic.List[object]
foreach ($d in $dirs) {
    foreach ($f in (Get-ChildItem -LiteralPath $d -Recurse -Filter *.cs -File)) {
        $rel = $f.FullName.Substring($app.Length + 1)
        $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $code = Strip-Comment $lines[$i]
            if ($code -and $pattern.IsMatch($code)) { $hits.Add([pscustomobject]@{ File = $rel; Line = $i + 1; Text = $lines[$i].Trim() }) }
        }
    }
}
$perFile = $hits | Group-Object File | Sort-Object Count -Descending
Write-Host ("Попаданий в окнах: {0} в {1} файлах" -f $hits.Count, @($perFile).Count)

if ($SaveBaseline) {
    $perFile | Sort-Object Name | ForEach-Object { "{0};{1}" -f $_.Name, $_.Count } | Set-Content -LiteralPath $baselineFile -Encoding UTF8
    Write-Host "База записана: $baselineFile"
    exit 0
}

if ($Strict) {
    $allow = @()
    if (Test-Path -LiteralPath $allowFile) { $allow = @(Get-Content -LiteralPath $allowFile -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') }) }
    $bad = $hits | Where-Object {
        $h = $_
        -not ($allow | Where-Object {
            $parts = $_.Split('|', 2)
            ($parts[0].Trim() -ieq $h.File) -and ($parts.Count -lt 2 -or $h.Text.Contains($parts[1]))
        })
    }
    if (@($bad).Count -gt 0) {
        Write-Host ("ОШИБКА: денежные расчёты в окнах вне белого списка: {0}" -f @($bad).Count)
        $bad | Select-Object -First 60 | ForEach-Object { Write-Host ("  {0}:{1}  {2}" -f $_.File, $_.Line, $_.Text) }
        exit 1
    }
    Write-Host "РЕЗУЛЬТАТ: вне белого списка расчётов в окнах нет"
    exit 0
}

if (-not (Test-Path -LiteralPath $baselineFile)) {
    Write-Host "Базы нет ($baselineFile). Запустите с -SaveBaseline, чтобы записать текущее состояние, или с -Strict для проверки «0 вне белого списка»."
    $perFile | Select-Object -First 15 | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Count, $_.Name) }
    exit 1
}
$base = @{}
Get-Content -LiteralPath $baselineFile -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $p = $_.Split(';'); $base[$p[0]] = [int]$p[1] }
$worse = New-Object System.Collections.Generic.List[string]
foreach ($g in $perFile) {
    $was = if ($base.ContainsKey($g.Name)) { $base[$g.Name] } else { 0 }
    if ($g.Count -gt $was) { $worse.Add(("{0}: было {1}, стало {2}" -f $g.Name, $was, $g.Count)) }
}
$better = 0
foreach ($k in $base.Keys) { $now = ($perFile | Where-Object { $_.Name -eq $k }).Count; if (-not $now) { $now = 0 }; if ($now -lt $base[$k]) { $better++ } }
if ($worse.Count -gt 0) {
    Write-Host ("ОШИБКА: денежных расчётов в окнах стало БОЛЬШЕ базы: {0} файл(ов)" -f $worse.Count)
    $worse | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host ("РЕЗУЛЬТАТ: не хуже базы (файлов, где стало меньше: {0}). Цель — {1}" -f $better, "0 в режиме -Strict")
exit 0
