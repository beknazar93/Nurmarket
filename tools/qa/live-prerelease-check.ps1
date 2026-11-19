# 2026-11-13 (Л-12 «Скорость и надёжность»): живой прогон ПЕРЕД ВЫПУСКОМ — автоматический чек-лист на ТЕСТОВОМ аккаунте, БЕЗ продаж и без записи данных.
#   powershell -ExecutionPolicy Bypass -File tools/qa/live-prerelease-check.ps1 -ConfirmTestAccount [-SkipTests] [-Base https://app.nurcrm.kg/]
# Логин и пароль тестового аккаунта — ТОЛЬКО из переменных окружения NURMARKET_LIVE_EMAIL и NURMARKET_LIVE_PASSWORD (в файлы, журнал и вывод не попадают).
# Что делает:
#   1) сервер, только чтение: вход, профиль, компания, каталог (первая страница + число товаров), категории, бренды, кассы, открытые смены, список продаж,
#      список клиентов — у каждого запроса код ответа и время (цель сервера — до 300 мс; больше — предупреждение, не провал);
#   2) окна и скорость на этом ПК: прогон автотестов через tools/qa/test-safe.ps1 — SpeedBudget* (пределы скорости: запуск до окна, скан, поиск 15 тыс.,
#      оплата, проведение с подставным сервером, склад/приёмка, вкладки владельца) и DailyReconciliation* (сверка касса↔сервер); окна открываются в Headless
#      на подставном сервере, настоящая касса не запускается и не закрывается, настоящие данные охраняет test-safe.ps1;
#   3) итог «ВЫПУСК МОЖНО ПРОДОЛЖАТЬ / НЕЛЬЗЯ» и файл отчёта %LOCALAPPDATA%\Temp\nurmarket-tests\live-check-<время>.txt. Код выхода 0 — всё зелёное, 1 — есть провал.
# Вход делается БЕЗ ключа устройства и типа клиента (как старые версии) — слот устройств тестового аккаунта не занимается и другую кассу не отключает.
# Если сервер ответит про слоты или пароль — прогон остановится с понятной строкой; «заменить устройство» скрипт никогда не делает.
# Чего скрипт НЕ делает: не открывает смену, не пробивает чек, не создаёт и не меняет товары/клиентов (все запросы GET, кроме входа). Живую проверку руками
# (печать чека, весы, оплата QR) скрипт не заменяет — см. docs/QA_LIVE_PRERELEASE.md.
param(
    [switch]$ConfirmTestAccount,
    [switch]$SkipTests,
    [string]$Base = "https://app.nurcrm.kg/",
    [int]$ServerLimitMs = 300
)
$ErrorActionPreference = "Stop"
if (-not $ConfirmTestAccount) {
    Write-Host "Нужен ключ -ConfirmTestAccount: прогон — только на ТЕСТОВОМ аккаунте (NBS / «ТЕСТ Хозмаг»), не на настоящем магазине клиента." -ForegroundColor Yellow
    exit 2
}
$email = $env:NURMARKET_LIVE_EMAIL
$password = $env:NURMARKET_LIVE_PASSWORD
if ([string]::IsNullOrWhiteSpace($email) -or [string]::IsNullOrWhiteSpace($password)) {
    Write-Host "Задайте NURMARKET_LIVE_EMAIL и NURMARKET_LIVE_PASSWORD (тестовый аккаунт) в этом окне PowerShell. В файлы они не пишутся." -ForegroundColor Yellow
    exit 2
}
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$tempBase = Join-Path ([IO.Path]::GetTempPath()) "nurmarket-tests"
New-Item -ItemType Directory -Force -Path $tempBase | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportFile = Join-Path $tempBase "live-check-$stamp.txt"
$Base = $Base.TrimEnd("/") + "/"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$rows = New-Object System.Collections.Generic.List[object]
$failed = 0
$warned = 0

function Add-Row([string]$name, [string]$verdict, [string]$detail) {
    $script:rows.Add([pscustomobject]@{ Name = $name; Verdict = $verdict; Detail = $detail })
    $color = switch ($verdict) { "OK" { "Green" } "ПРЕДУПРЕЖДЕНИЕ" { "Yellow" } default { "Red" } }
    Write-Host ("[{0}] {1} — {2}" -f $verdict, $name, $detail) -ForegroundColor $color
    if ($verdict -eq "ПРОВАЛ") { $script:failed++ }
    if ($verdict -eq "ПРЕДУПРЕЖДЕНИЕ") { $script:warned++ }
}

# Один GET/POST с замером времени. Тело ответа возвращается объектом; текст ошибки сервера (без токенов) — в detail.
function Invoke-Timed([string]$method, [string]$path, $body, [hashtable]$headers) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $req = @{ Uri = ($Base + $path); Method = $method; UseBasicParsing = $true; TimeoutSec = 60; Headers = $headers }
        if ($null -ne $body) { $req.Body = ($body | ConvertTo-Json -Compress); $req.ContentType = "application/json" }
        $resp = Invoke-WebRequest @req
        $sw.Stop()
        $json = $null
        try { $json = $resp.Content | ConvertFrom-Json } catch { }
        return [pscustomobject]@{ Ok = $true; Status = [int]$resp.StatusCode; Ms = $sw.ElapsedMilliseconds; Json = $json; Error = $null }
    } catch {
        $sw.Stop()
        $status = 0
        $text = $_.Exception.Message
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            try { $text = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch { }
        }
        if ($text.Length -gt 200) { $text = $text.Substring(0, 200) }
        return [pscustomobject]@{ Ok = $false; Status = $status; Ms = $sw.ElapsedMilliseconds; Json = $null; Error = $text }
    }
}

function Check-Get([string]$name, [string]$path, [hashtable]$headers, [scriptblock]$describe) {
    $r = Invoke-Timed "GET" $path $null $headers
    if (-not $r.Ok) { Add-Row $name "ПРОВАЛ" ("HTTP {0}, {1} мс: {2}" -f $r.Status, $r.Ms, $r.Error); return $null }
    $detail = "{0} мс" -f $r.Ms
    if ($describe) { $detail += ", " + (& $describe $r.Json) }
    if ($r.Ms -gt $ServerLimitMs) { Add-Row $name "ПРЕДУПРЕЖДЕНИЕ" ($detail + " (цель сервера до $ServerLimitMs мс)") }
    else { Add-Row $name "OK" $detail }
    return $r.Json
}

function CountOf($json) {
    if ($null -eq $json) { return "пусто" }
    if ($json.PSObject.Properties.Name -contains "count") { return "всего " + $json.count }
    if ($json.PSObject.Properties.Name -contains "results") { return "на странице " + @($json.results).Count }
    return "на странице " + @($json).Count
}

Write-Host "=== Живой прогон перед выпуском (только чтение), сервер $Base ===" -ForegroundColor Cyan

# --- 1. вход (без ключа устройства: слот не занимается)
$login = Invoke-Timed "POST" "api/users/auth/login/" @{ email = $email.Trim(); password = $password } @{}
if (-not $login.Ok -or -not $login.Json -or -not $login.Json.access) {
    $why = if ($login.Ok) { "в ответе нет токена" } else { "HTTP $($login.Status): $($login.Error)" }
    $why = $why -replace [regex]::Escape($password), "***"
    Add-Row "Вход тестового аккаунта" "ПРОВАЛ" ("$($login.Ms) мс, $why (если речь о слотах устройств — освободите слот вручную в настройках аккаунта, скрипт устройства не заменяет)")
}
else {
    $limitNote = if ($login.Ms -gt $ServerLimitMs * 3) { "ПРЕДУПРЕЖДЕНИЕ" } else { "OK" }
    Add-Row "Вход тестового аккаунта" $limitNote ("{0} мс" -f $login.Ms)
    $h = @{ Authorization = "Bearer " + $login.Json.access }
    $null = Check-Get "Профиль и права" "api/users/profile/" $h { param($j) "роль: " + $(if ($j.role) { $j.role } else { "не указана" }) }
    $null = Check-Get "Компания" "api/users/company/" $h { param($j) "сфера: " + $(if ($j.market_sphere) { $j.market_sphere } else { "не указана" }) }
    $cat = Check-Get "Каталог — первая страница (300)" "api/main/products/list/?page_size=300&page=1" $h { param($j) CountOf $j }
    if ($cat -ne $null) {
        $items = if ($cat.results) { @($cat.results) } else { @($cat) }
        if ($items.Count -eq 0) { Add-Row "Каталог не пуст" "ПРЕДУПРЕЖДЕНИЕ" "в тестовом аккаунте нет товаров — проверка окон каталога будет пустой" }
        else { Add-Row "Каталог не пуст" "OK" ("товаров на странице: {0}" -f $items.Count) }
    }
    $null = Check-Get "Категории" "api/main/categories/" $h { param($j) CountOf $j }
    $null = Check-Get "Бренды" "api/main/brands/" $h { param($j) CountOf $j }
    $null = Check-Get "Кассы" "api/construction/cashboxes/" $h { param($j) CountOf $j }
    $null = Check-Get "Открытые смены" "api/construction/shifts/?status=open" $h { param($j) CountOf $j }
    $null = Check-Get "Список продаж (страница 20)" "api/main/pos/sales/?page=1&page_size=20" $h { param($j) CountOf $j }
    $null = Check-Get "Клиенты (страница 20)" "api/main/clients/?page=1&page_size=20" $h { param($j) CountOf $j }
}
$login = $null; $password = $null; $h = $null

# --- 2. окна и скорость: автотесты через безопасный запуск
if ($SkipTests) {
    Add-Row "Автотесты скорости, окон и сверки" "ПРЕДУПРЕЖДЕНИЕ" "пропущены ключом -SkipTests"
}
else {
    $safe = Join-Path $repo "tools\qa\test-safe.ps1"
    Write-Host "Запуск автотестов (SpeedBudget*, DailyReconciliation*) через test-safe.ps1 — это 5–15 минут..." -ForegroundColor Cyan
    $filter = "FullyQualifiedName~SpeedBudget|FullyQualifiedName~DailyReconciliation"
    $out = & powershell -ExecutionPolicy Bypass -File $safe -Phase all -Filter $filter 2>&1 | Out-String
    $code = $LASTEXITCODE
    $summary = ($out -split "`r?`n" | Where-Object { $_ -match "РЕЗУЛЬТАТ|EXIT=|Passed!|Failed!|Пройден|BUILD-LOCK" } | Select-Object -First 6) -join " | "
    if ($code -eq 0) { Add-Row "Автотесты скорости, окон и сверки (пределы-«храповики»)" "OK" $summary }
    else { Add-Row "Автотесты скорости, окон и сверки (пределы-«храповики»)" "ПРОВАЛ" ("код $code. " + $summary) }
    $speedLog = Join-Path $tempBase "speed-budget.txt"
    if (Test-Path $speedLog) { Add-Row "Замеры скорости этого прогона" "OK" ("журнал цифр: " + $speedLog) }
}

# --- 3. итог
$verdictText = if ($failed -gt 0) { "ВЫПУСК НЕЛЬЗЯ ПРОДОЛЖАТЬ: провалов — $failed, предупреждений — $warned" }
               elseif ($warned -gt 0) { "ВЫПУСК МОЖНО ПРОДОЛЖАТЬ, но прочитайте предупреждения ($warned)" }
               else { "ВЫПУСК МОЖНО ПРОДОЛЖАТЬ: все автоматические проверки зелёные" }
$lines = @("Живой прогон перед выпуском — $(Get-Date -Format 'dd.MM.yyyy HH:mm'), сервер $Base", "")
foreach ($r in $rows) { $lines += ("[{0}] {1} — {2}" -f $r.Verdict, $r.Name, $r.Detail) }
$lines += ""
$lines += $verdictText
$lines += "Руками всё равно нужно: печать чека, весы, оплата QR, продажа и возврат на NBS (docs/QA_LIVE_PRERELEASE.md)."
$lines | Set-Content -Path $reportFile -Encoding UTF8
Write-Host ""
Write-Host $verdictText -ForegroundColor $(if ($failed -gt 0) { "Red" } elseif ($warned -gt 0) { "Yellow" } else { "Green" })
Write-Host "Отчёт: $reportFile"
if ($failed -gt 0) { exit 1 }
exit 0
