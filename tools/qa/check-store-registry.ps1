# 2026-11-06: В1 — статическая проверка «реестр хранилищ компании полон» (критерий 3.2, Л4-05). ТОЛЬКО ЧИТАЕТ файлы.
#   powershell -ExecutionPolicy Bypass -File tools/qa/check-store-registry.ps1 [-Root <корень>] [-ShowAll]
# Что делает: берёт из NurMarketKassa.Infrastructure\Services\CompanyStoreRegistry.cs все имена хранилищ (CompanyItems, CompanyPatterns, CompanyLocalItems,
# SharedByCompanyItems, TerminalItems) и ищет в коде имена файлов и папок данных, которые строятся от AppPaths.Roaming / AppPaths.Local / DataDirOverride,
# но в реестре не числятся. Такой файл при смене компании не унесётся в карантин — следующая компания увидит чужие данные.
# Два источника имён:
#   1) строки `Path.Combine(...)`, где рядом AppPaths.* / AppMode.DataFolderName / DataDirOverride — берутся строковые литералы-имена;
#   2) литералы с расширением json/jsonl/dat/db/sqlite/flag/txt в файлах, которые используют AppPaths (имена-константы вида `FileName = "x.json"`).
# Игнорируются: корневые папки (NurMarketKassa, NurMarketOwner, …), имена в tools/qa/store-registry.ignore.txt (по одному в строке, `#` — комментарий; причина обязательна рядом).
# Код выхода: 0 — всё в реестре; 1 — есть имена вне реестра; 2 — реестр не прочитан.
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$ShowAll
)
$ErrorActionPreference = "Stop"
$solution = Join-Path $Root "NurMarketKassa.Avalonia"
$registryFile = Join-Path $solution "NurMarketKassa.Infrastructure\Services\CompanyStoreRegistry.cs"
if (-not (Test-Path -LiteralPath $registryFile)) { Write-Host "ОШИБКА: не найден реестр $registryFile"; exit 2 }
$regText = Get-Content -LiteralPath $registryFile -Raw -Encoding UTF8

function Get-RegistryNames([string]$text, [string]$field) {
    $m = [regex]::Match($text, $field + '\s*=\s*\[(?<body>.*?)\];', 'Singleline')
    if (-not $m.Success) { return @() }
    # комментарии `// ...` режем, чтобы кавычки в тексте комментариев не считались именами
    $body = ($m.Groups['body'].Value -split "`n" | ForEach-Object { $i = $_.IndexOf('//'); if ($i -ge 0) { $_.Substring(0, $i) } else { $_ } }) -join "`n"
    return @([regex]::Matches($body, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
}

$names = @()
foreach ($field in 'CompanyItems', 'CompanyLocalItems', 'SharedByCompanyItems', 'TerminalItems') { $names += Get-RegistryNames $regText $field }
$patterns = Get-RegistryNames $regText 'CompanyPatterns'
if ($names.Count -lt 20) { Write-Host ("ОШИБКА: из реестра прочитано слишком мало имён ({0}) — формат файла изменился?" -f $names.Count); exit 2 }
$tails = @('.bak', '.pending', '.pending.old', '.tmp')

function Test-InRegistry([string]$name) {
    $n = $name.Trim().TrimEnd('\', '/')
    $cands = @($n)
    foreach ($t in $tails) { if ($n.EndsWith($t, [StringComparison]::OrdinalIgnoreCase) -and $n.Length -gt $t.Length) { $cands += $n.Substring(0, $n.Length - $t.Length) } }
    foreach ($c in $cands) {
        if ($names | Where-Object { $_ -ieq $c }) { return $true }
        foreach ($p in $patterns) { if ($c -imatch ('^' + ([regex]::Escape($p) -replace '\\\*', '.*') + '$')) { return $true } }
    }
    return $false
}

$ignoreFile = Join-Path $PSScriptRoot "store-registry.ignore.txt"
$ignore = @("NurMarketKassa", "NurMarketOwner", "NurMarket", "Roaming", "Local", "data", "Logs", "Temp", "tmp", "bin", "obj")
if (Test-Path -LiteralPath $ignoreFile) { $ignore += @(Get-Content -LiteralPath $ignoreFile -Encoding UTF8 | ForEach-Object { ($_ -split '#')[0].Trim() } | Where-Object { $_ }) }

$files = Get-ChildItem -LiteralPath $solution -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|graphify-out|NurMarketKassa\.Tests)\\' -and $_.Name -ne 'CompanyStoreRegistry.cs' }

$litRegex = [regex]'(?<![\w$])\$?@?"([^"{}\/:*?<>|]{2,80})"'
$dataExt = [regex]'^[\w.\-]+\.(json|jsonl|dat|db|sqlite|flag|txt)(\.bak|\.pending)?$'
$rootsRegex = [regex]'AppPaths\.(Roaming|Local)|DataFolderName|AppMode\.'
$top = @{}      # имя прямо под папкой данных (Path.Combine(AppPaths.*, ...)) -> место: ошибка, если нет в реестре
$other = @{}    # прочие литералы-имена данных (внутри папки, переменной DataDir, константы) -> место: предупреждение
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Root.Length).TrimStart([char]92)
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    if ($text -notmatch 'AppPaths\.|DataDirOverride|DataFolderName') { continue }
    $lines = $text -split "`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $t = $line.TrimStart()
        if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
        $code = $line; $c = $line.IndexOf('//'); if ($c -ge 0 -and ($line.Substring(0, $c).Split('"').Count % 2) -eq 1) { $code = $line.Substring(0, $c) }
        # Path.Combine может переноситься на следующую строку: имя папки данных обычно в следующей
        $codeWide = $code
        if ($code -match 'Path\.Combine\(') {
            # вызов на нескольких строках: склеиваем до закрывающей скобки (не больше 6 строк), комментарии в хвостах строк режем
            $depthNow = ([regex]::Matches($code, '\(')).Count - ([regex]::Matches($code, '\)')).Count
            $k = 1
            while ($depthNow -gt 0 -and $k -le 6 -and $i + $k -lt $lines.Length) {
                $next = $lines[$i + $k]; $cc = $next.IndexOf('//'); if ($cc -ge 0 -and ($next.Substring(0, $cc).Split('"').Count % 2) -eq 1) { $next = $next.Substring(0, $cc) }
                $codeWide += " " + $next
                $depthNow += ([regex]::Matches($next, '\(')).Count - ([regex]::Matches($next, '\)')).Count
                $k++
            }
        }
        $isRooted = $codeWide -match 'Path\.Combine\(' -and $rootsRegex.IsMatch($codeWide)
        $first = $null
        foreach ($m in $litRegex.Matches($codeWide)) {
            $lit = $m.Groups[1].Value
            if ($lit -match '\s' -or $lit -match '^(http|https|api)$') { continue }
            if ($ignore | Where-Object { $_ -ieq $lit }) { continue }
            $isData = $dataExt.IsMatch($lit)
            if ($isRooted -and -not $first -and $lit -match '^[A-Za-z][\w.\-]*$' -and $lit.Length -ge 3) {
                $first = $lit
                if (-not $top.ContainsKey($lit)) { $top[$lit] = "{0}:{1}" -f $rel, ($i + 1) }
            }
            elseif ($isData -and -not $other.ContainsKey($lit)) { $other[$lit] = "{0}:{1}" -f $rel, ($i + 1) }
        }
    }
}
$found = @{}; foreach ($k in $top.Keys) { $found[$k] = $top[$k] }; foreach ($k in $other.Keys) { if (-not $found.ContainsKey($k)) { $found[$k] = $other[$k] } }

$missing = @($top.Keys | Where-Object { -not (Test-InRegistry $_) } | Sort-Object)
$warn = @($other.Keys | Where-Object { -not $top.ContainsKey($_) -and -not (Test-InRegistry $_) } | Sort-Object)
Write-Host ("В реестре имён: {0} (+ шаблонов: {1}); найдено в коде имён данных: {2}" -f $names.Count, $patterns.Count, $found.Count)
if ($ShowAll) { $found.Keys | Sort-Object | ForEach-Object { Write-Host ("  {0}  {1}" -f $_, $found[$_]) } }
if ($warn.Count -gt 0) {
    Write-Host ""
    Write-Host ("Предупреждение (проверить вручную, ошибкой не считается): имена файлов внутри папки данных или констант вне реестра: {0}" -f $warn.Count)
    foreach ($n in $warn) { Write-Host ("  {0}   <- {1}" -f $n, $other[$n]) }
}
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host ("ОШИБКА: имён вне реестра хранилищ: {0}" -f $missing.Count)
    foreach ($n in $missing) { Write-Host ("  {0}   <- {1}" -f $n, $top[$n]) }
    Write-Host "Добавьте имя в нужный список CompanyStoreRegistry (Company / Terminal / SharedByCompanyField) или, если это не хранилище, в tools/qa/store-registry.ignore.txt с причиной."
    exit 1
}
Write-Host "РЕЗУЛЬТАТ: все найденные имена файлов данных есть в реестре"
exit 0
