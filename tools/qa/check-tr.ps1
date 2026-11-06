# 2026-11-06: В1 — статическая проверка «надписи в коде на 5 языках, без пустых» (критерий 3.8: 0 надписей без Tr.T на пять языков). ТОЛЬКО ЧИТАЕТ файлы.
#   powershell -ExecutionPolicy Bypass -File tools/qa/check-tr.ps1 [-Root <корень>] [-AllowTwoArg] [-MaxViolations N] [-ShowAll]
# Что ищет в *.cs (кроме obj, bin, тестов): вызовы Tr.T( ... ) и разбирает аргументы настоящим разборщиком C# (строки обычные, @"", $"", """ """, символы, комментарии,
# вложенные скобки). Нарушения:
#   - пустой перевод: аргумент "" или только пробелы — кассир увидит пустую надпись на этом языке;
#   - число аргументов не 5 и не 2: опечатка (потерян язык);
#   - 2 аргумента (ru, ky): en / tr / uz получают русский — НЕ пять языков. Считается нарушением; -AllowTwoArg это разрешает (для промежуточных замеров).
# Код выхода: 0 — нарушений не больше -MaxViolations (по умолчанию 0); 1 — больше.
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$AllowTwoArg,
    [int]$MaxViolations = 0,
    [switch]$ShowAll
)
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;

public static class TrScanner
{
    public sealed class Call { public int Line; public int ArgCount; public List<string> Args = new List<string>(); }

    // Пропускает строковый/символьный литерал, начиная с позиции i (на кавычке или префиксе $ @); возвращает позицию ПОСЛЕ литерала.
    static int SkipLiteral(string s, int i)
    {
        bool interp = false, verbatim = false;
        while (i < s.Length && (s[i] == '$' || s[i] == '@')) { if (s[i] == '$') interp = true; else verbatim = true; i++; }
        if (i >= s.Length) return i;
        if (s[i] == '\'')
        {
            i++;
            while (i < s.Length && s[i] != '\'') { if (s[i] == '\\') i++; i++; }
            return Math.Min(s.Length, i + 1);
        }
        // """ raw string
        int q = 0; while (i + q < s.Length && s[i + q] == '"') q++;
        if (q >= 3)
        {
            i += q;
            while (i < s.Length)
            {
                if (s[i] == '"') { int k = 0; while (i + k < s.Length && s[i + k] == '"') k++; if (k >= q) return i + k; i += k; }
                else i++;
            }
            return i;
        }
        i++; // открывающая "
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (interp && depth == 0 && c == '{') { if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; } depth = 1; i++; continue; }
            if (interp && depth > 0)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == '"' || c == '\'') { i = SkipLiteral(s, i); continue; }
                i++; continue;
            }
            if (verbatim) { if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; } return i + 1; } i++; continue; }
            if (c == '\\') { i += 2; continue; }
            if (c == '"') return i + 1;
            i++;
        }
        return i;
    }

    static bool IsIdent(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

    public static List<Call> Scan(string s)
    {
        var result = new List<Call>();
        int line = 1;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i += 2; while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; } i += 2; continue; }
            if (c == '"' || c == '\'' || ((c == '$' || c == '@') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '$' || s[i + 1] == '@')))
            {
                int end = SkipLiteral(s, i);
                for (int k = i; k < end && k < s.Length; k++) if (s[k] == '\n') line++;
                i = end; continue;
            }
            if (c == 'T' && i >= 3 && s[i - 1] == '.' && s[i - 2] == 'r' && s[i - 3] == 'T' && (i < 4 || !IsIdent(s[i - 4])) && i + 1 < s.Length && s[i + 1] == '(')
            {
                var call = new Call { Line = line };
                int p = i + 2; int depth = 0; int start = p; int ln = line;
                while (p < s.Length)
                {
                    char d = s[p];
                    if (d == '\n') { ln++; p++; continue; }
                    if (d == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
                    if (d == '/' && p + 1 < s.Length && s[p + 1] == '*') { p += 2; while (p + 1 < s.Length && !(s[p] == '*' && s[p + 1] == '/')) { if (s[p] == '\n') ln++; p++; } p += 2; continue; }
                    if (d == '"' || d == '\'' || ((d == '$' || d == '@') && p + 1 < s.Length && (s[p + 1] == '"' || s[p + 1] == '$' || s[p + 1] == '@')))
                    {
                        int end = SkipLiteral(s, p);
                        for (int k = p; k < end && k < s.Length; k++) if (s[k] == '\n') ln++;
                        p = end; continue;
                    }
                    if (d == '(' || d == '[' || d == '{') depth++;
                    else if (d == ')' || d == ']' || d == '}')
                    {
                        if (depth == 0) { call.Args.Add(s.Substring(start, p - start).Trim()); break; }
                        depth--;
                    }
                    else if (d == ',' && depth == 0) { call.Args.Add(s.Substring(start, p - start).Trim()); start = p + 1; }
                    p++;
                }
                call.ArgCount = call.Args.Count;
                result.Add(call);
                i += 2; // дальше сканируем изнутри вызова (вложенные Tr.T тоже учтём); номера строк ведёт основной цикл
                continue;
            }
            i++;
        }
        return result;
    }
}
'@

$solution = Join-Path $Root "NurMarketKassa.Avalonia"
$files = Get-ChildItem -LiteralPath $solution -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|graphify-out|NurMarketKassa\.Tests)\\' }

$blank = [regex]'^\$?@?"\s*"$'
$total = 0; $two = 0
$violations = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    if ($text.IndexOf('Tr.T(', [StringComparison]::Ordinal) -lt 0) { continue }
    $rel = $f.FullName.Substring($Root.Length).TrimStart([char]92)
    foreach ($call in [TrScanner]::Scan($text)) {
        $total++
        $brief = ($call.Args | Select-Object -First 1)
        if ($brief.Length -gt 70) { $brief = $brief.Substring(0, 70) + "…" }
        $brief = $brief -replace "\s+", " "
        if ($call.ArgCount -eq 2) {
            $two++
            if (-not $AllowTwoArg) { $violations.Add(("{0}:{1}  2 языка вместо 5: {2}" -f $rel, $call.Line, $brief)) }
        }
        elseif ($call.ArgCount -ne 5) {
            $violations.Add(("{0}:{1}  аргументов {2}, нужно 5: {3}" -f $rel, $call.Line, $call.ArgCount, $brief))
        }
        for ($k = 0; $k -lt $call.Args.Count; $k++) {
            if ($blank.IsMatch($call.Args[$k])) { $violations.Add(("{0}:{1}  пустой перевод (аргумент {2}): {3}" -f $rel, $call.Line, ($k + 1), $brief)) }
        }
    }
}
Write-Host ("Вызовов Tr.T: {0}; из них на 2 языка: {1}; нарушений: {2}" -f $total, $two, $violations.Count)
$limit = if ($ShowAll) { $violations.Count } else { [Math]::Min(40, $violations.Count) }
for ($i = 0; $i -lt $limit; $i++) { Write-Host ("  " + $violations[$i]) }
if ($violations.Count -gt $limit) { Write-Host ("  … и ещё {0} (-ShowAll покажет все)" -f ($violations.Count - $limit)) }
if ($violations.Count -gt $MaxViolations) { Write-Host ("ОШИБКА: нарушений {0} > допустимых {1}" -f $violations.Count, $MaxViolations); exit 1 }
Write-Host "РЕЗУЛЬТАТ: все Tr.T на 5 языках, без пустых"
exit 0
