# 2026-11-06: В1 — статическая проверка «каждый денежный POST идёт с ключом повтора» (критерий 3.1, Л4-08). ТОЛЬКО ЧИТАЕТ файлы, ничего не меняет и не запускает.
#   powershell -ExecutionPolicy Bypass -File tools/qa/check-money-keys.ps1 [-Verbose] [-Root <корень репозитория>]
# Как работает:
#   1) из MoneyRequestKeys.cs берёт списки «денежных» и «исключённых» подстрок пути (одни и те же, что в коде — расхождения быть не может);
#   2) проверяет, что NurMarketApiClient.RequestAsync по-прежнему подставляет ключ по умолчанию (MoneyRequestKeys.Applies);
#   3) находит в коде приложения все места `HttpMethod.Post` с путём-литералом и для денежных путей определяет, откуда берётся ключ:
#        - явный ключ рядом (PostWithIdempotencyKeyAsync, idempotencyKey, Idempotency-Key, idempotency_key в теле);
#        - RequestAsync / RequestWithHeadersAsync — ключ по умолчанию от MoneyRequestKeys (RequestDataAsync его НЕ ставит: нужен явный);
#        - всё остальное (прямой HttpClient, RequestDataAsync без ключа) — ОШИБКА: денежный POST без ключа.
#   4) места с путём не литералом (переменная) — в список «не определено» для ручного взгляда, ошибкой не считаются.
# Код выхода: 0 — нарушений нет; 1 — есть денежный POST без ключа или сломан ключ по умолчанию.
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$ShowAll
)
$ErrorActionPreference = "Stop"
$solution = Join-Path $Root "NurMarketKassa.Avalonia"
$keysFile = Join-Path $solution "NurMarketKassa.Infrastructure\Services\Api\MoneyRequestKeys.cs"
$clientFile = Join-Path $solution "NurMarketKassa.Infrastructure\Services\NurMarketApiClient.cs"
if (-not (Test-Path -LiteralPath $keysFile)) { Write-Host "ОШИБКА: не найден $keysFile"; exit 2 }

function Get-StringList([string]$text, [string]$name) {
    $m = [regex]::Match($text, $name + '\s*=\s*\{(?<body>.*?)\};', 'Singleline')
    if (-not $m.Success) { return @() }
    return @([regex]::Matches($m.Groups['body'].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() })
}

$keysText = Get-Content -LiteralPath $keysFile -Raw -Encoding UTF8
$moneyMarkers = Get-StringList $keysText 'MoneyMarkers'
$excluded = Get-StringList $keysText 'ExcludedMarkers'
if ($moneyMarkers.Count -eq 0) { Write-Host "ОШИБКА: список MoneyMarkers в MoneyRequestKeys.cs не прочитан"; exit 2 }
Write-Host ("Денежных подстрок пути: {0}, исключений: {1}" -f $moneyMarkers.Count, $excluded.Count)

$errors = New-Object System.Collections.Generic.List[string]

# 2) ключ по умолчанию в RequestAsync на месте
$clientText = Get-Content -LiteralPath $clientFile -Raw -Encoding UTF8
if ($clientText -notmatch 'MoneyRequestKeys\.Applies\(method, relativePath\)' -or $clientText -notmatch 'idempotencyKey:\s*moneyKey') {
    $errors.Add("NurMarketApiClient.RequestAsync больше не подставляет ключ повтора по умолчанию (MoneyRequestKeys.Applies / idempotencyKey: moneyKey)")
}

function Test-Money([string]$path) {
    $p = ($path -replace '\{[^}]*\}', '*').ToLowerInvariant()
    foreach ($x in $excluded) { if ($p.Contains($x)) { return $false } }
    foreach ($m in $moneyMarkers) { if ($p.Contains($m)) { return $true } }
    return $false
}

$files = Get-ChildItem -LiteralPath $solution -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|graphify-out|NurMarketKassa\.Tests)\\' -and $_.Name -ne 'MoneyRequestKeys.cs' }

$ok = 0; $unknown = New-Object System.Collections.Generic.List[string]; $bad = New-Object System.Collections.Generic.List[string]
$pathRegex = [regex]'\$?@?"(/?api/[^"]*)"'
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -notmatch 'HttpMethod\.Post') { continue }
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
        # контекст вызова: строка с POST и несколько соседних (путь и ключ обычно рядом)
        $from = [Math]::Max(0, $i - 3); $to = [Math]::Min($lines.Length - 1, $i + 6)
        $ctx = ($lines[$from..$to] -join "`n")
        $m = $pathRegex.Match($ctx)
        if (-not $m.Success) { $unknown.Add(("{0}:{1}" -f $rel, ($i + 1))); continue }
        $path = $m.Groups[1].Value
        if (-not (Test-Money $path)) { continue }
        $wide = ($lines[([Math]::Max(0, $i - 12))..([Math]::Min($lines.Length - 1, $i + 12))] -join "`n")
        $explicit = $wide -match '(?i)PostWithIdempotencyKeyAsync|idempotencyKey|Idempotency-Key|idempotency_key|IdempotencyKey'
        $viaRequest = $ctx -match 'RequestAsync\(|RequestWithHeadersAsync\('
        if ($explicit) { $ok++; if ($ShowAll) { Write-Host ("  ключ явный      {0}:{1}  {2}" -f $rel, ($i + 1), $path) } }
        elseif ($viaRequest -and $ctx -notmatch 'RequestDataAsync') { $ok++; if ($ShowAll) { Write-Host ("  ключ по умолч.  {0}:{1}  {2}" -f $rel, ($i + 1), $path) } }
        else { $bad.Add(("{0}:{1}  {2}" -f $rel, ($i + 1), $path)) }
    }
}

Write-Host ("Денежных POST с ключом: {0}" -f $ok)
Write-Host ("POST с путём не литералом (смотреть вручную): {0}" -f $unknown.Count)
if ($ShowAll) { $unknown | ForEach-Object { Write-Host "  ? $_" } }
if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host ("ОШИБКА: денежных POST без ключа повтора: {0}" -f $bad.Count)
    $bad | ForEach-Object { Write-Host "  $_" }
}
foreach ($e in $errors) { Write-Host "ОШИБКА: $e" }
if ($bad.Count -gt 0 -or $errors.Count -gt 0) { exit 1 }
Write-Host "РЕЗУЛЬТАТ: все найденные денежные POST идут с ключом повтора"
exit 0
