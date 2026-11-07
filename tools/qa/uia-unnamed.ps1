# 2026-11-06 (В14, Т-1): проверка «0 безымянных» — у каждого управляющего элемента окна кассы есть имя для диктора (UI Automation Name).
#   powershell -ExecutionPolicy Bypass -File tools/qa/uia-unnamed.ps1 -Hwnd <hwnd окна кассы> [-MaxDepth 40] [-Out report.txt]
#   powershell -ExecutionPolicy Bypass -File tools/qa/uia-unnamed.ps1 -Check        # самопроверка скрипта БЕЗ живого окна (ничего не трогает)
# Правила: только по hwnd окна кассы или программы владельца (процесс NurMarket*; чужое окно скрипт не читает), никаких координат экрана,
# никаких щелчков и ввода — только чтение дерева UI Automation. Окно получите через `kwins.ps1 [-Owner]` (см. AI_HANDOFF.md, раздел 5).
# Код выхода: 0 — безымянных нет; 1 — есть (список в выводе); 2 — окно не найдено/чужое; 3 — ошибка самопроверки.
param(
    [string]$Hwnd = "",
    [int]$MaxDepth = 40,
    [string]$Out = "",
    [switch]$Check
)
$ErrorActionPreference = "Stop"

# Типы элементов, у которых имя обязательно (то, с чем работает кассир и диктор).
$script:NamedTypes = @("Button", "CheckBox", "RadioButton", "ComboBox", "Edit", "Hyperlink", "MenuItem", "TabItem", "Slider", "Spinner", "SplitButton", "ListItem", "DataItem")

# Элемент безымянный, если нужен ему тип и Name пуст (и это не отключённый/скрытый элемент вне экрана — их диктор не читает).
function Test-IsUnnamed($type, $name, $isOffscreen) {
    if ($isOffscreen) { return $false }
    if ($script:NamedTypes -notcontains $type) { return $false }
    return [string]::IsNullOrWhiteSpace($name)
}

function Initialize-Uia {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if (-not ("NurUiaNative" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NurUiaNative {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
"@
    }
}

function Get-OwnerProcessName([IntPtr]$h) {
    $procId = 0
    [void][NurUiaNative]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq 0) { return $null }
    try { return (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { return $null }
}

if ($Check) {
    # Самопроверка: библиотеки загружаются, классификатор правильно отделяет безымянные, чужой/несуществующий hwnd отвергается. Живое окно не читается.
    $fail = @()
    try { Initialize-Uia } catch { $fail += "не загрузились библиотеки UI Automation: $($_.Exception.Message)" }
    if (-not (Test-IsUnnamed "Button" "" $false)) { $fail += "кнопка без имени должна считаться безымянной" }
    if (Test-IsUnnamed "Button" "Оплатить" $false) { $fail += "кнопка с именем не должна считаться безымянной" }
    if (Test-IsUnnamed "Text" "" $false) { $fail += "обычный текст не требует имени" }
    if (Test-IsUnnamed "Button" "" $true) { $fail += "скрытый вне экрана элемент не считается" }
    if (-not (Test-IsUnnamed "Edit" "   " $false)) { $fail += "поле с именем из пробелов — безымянное" }
    if (-not $fail.Count) {
        if ([NurUiaNative]::IsWindow([IntPtr]0x7FFFFFF1)) { $fail += "несуществующий hwnd принят за окно" }
        if ($null -ne (Get-OwnerProcessName ([IntPtr]0x7FFFFFF1))) { $fail += "у несуществующего hwnd не должно быть процесса" }
    }
    if ($fail.Count) { $fail | ForEach-Object { Write-Host "uia-unnamed -Check: ПРОВАЛ: $_" }; exit 3 }
    Write-Host "uia-unnamed -Check: ок (живое окно не читалось)"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Hwnd)) { Write-Host "Укажите -Hwnd окна кассы (kwins.ps1) или -Check для самопроверки."; exit 2 }
Initialize-Uia
$h = if ($Hwnd.StartsWith("0x")) { [IntPtr][Convert]::ToInt64($Hwnd.Substring(2), 16) } else { [IntPtr][long]$Hwnd }
if (-not [NurUiaNative]::IsWindow($h)) { Write-Host "Окно $Hwnd не найдено."; exit 2 }
$pn = Get-OwnerProcessName $h
if ($null -eq $pn -or $pn -notlike "NurMarket*") { Write-Host "Окно $Hwnd принадлежит процессу '$pn', а не кассе/программе владельца (NurMarket*) — не читаю."; exit 2 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$total = 0
$bad = New-Object System.Collections.Generic.List[string]

function Walk($el, [int]$depth, [string]$path) {
    if ($depth -gt $MaxDepth) { return }
    $c = $el.Current
    $type = $c.ControlType.ProgrammaticName -replace "^ControlType\.", ""
    if ($script:NamedTypes -contains $type) {
        $script:total++
        if (Test-IsUnnamed $type $c.Name $c.IsOffscreen) {
            $script:bad.Add(("{0} id='{1}' class='{2}' путь: {3}" -f $type, $c.AutomationId, $c.ClassName, $path))
        }
    }
    $child = $walker.GetFirstChild($el)
    $i = 0
    while ($null -ne $child) {
        $cn = $child.Current
        Walk $child ($depth + 1) ("{0}/{1}[{2}]" -f $path, ($cn.ControlType.ProgrammaticName -replace "^ControlType\.", ""), $i)
        $child = $walker.GetNextSibling($child)
        $i++
    }
}
Walk $root 0 "окно"

$lines = @("uia-unnamed: окно $Hwnd (процесс $pn), элементов с обязательным именем: $total, безымянных: $($bad.Count)")
$lines += $bad
$lines | ForEach-Object { Write-Host $_ }
if ($Out) { $lines | Out-File -FilePath $Out -Encoding utf8 }
if ($bad.Count -gt 0) { exit 1 }
exit 0
