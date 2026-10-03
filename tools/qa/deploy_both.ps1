# Ставит dev-out в установленную кассу и перезапускает и кассу, и программу владельца.
$S = $PSScriptRoot
$bk = Join-Path $env:TEMP ("nurmarket-dbbackup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')); New-Item -ItemType Directory -Force $bk | Out-Null
$p = Get-Process NurMarketKassa.Avalonia -ErrorAction SilentlyContinue
if ($p) { $p | Stop-Process -Force; Start-Sleep -Seconds 2; "processes stopped: " + $p.Count }
Copy-Item "$env:APPDATA\NurMarketKassa\data\pos_local.db*" $bk
New-Item -ItemType Directory -Force "$bk\owner" | Out-Null
Copy-Item "$env:APPDATA\NurMarketOwner\data\pos_local.db*" "$bk\owner" -ErrorAction SilentlyContinue
$dst = "$env:LOCALAPPDATA\NurMarketKassa\current"
robocopy (Resolve-Path "$S\..\..\NurMarketKassa.Avalonia\dev-out").Path $dst /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
"robocopy код $LASTEXITCODE"
# 2026-10-03: запуск через WMI — программы, запущенные Start-Process, закрывались вместе с командой ИИ-агента.
$exe = "$dst\NurMarketKassa.Avalonia.exe"
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`""; CurrentDirectory = $dst } | Out-Null
Start-Sleep -Seconds 12
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" --owner"; CurrentDirectory = $dst } | Out-Null
Start-Sleep -Seconds 15
Get-CimInstance Win32_Process -Filter "Name='NurMarketKassa.Avalonia.exe'" | ForEach-Object { "запущена pid $($_.ProcessId) $($_.CommandLine -replace '.*\.exe"?', '') память $([int]((Get-Process -Id $_.ProcessId).WorkingSet64/1MB)) МБ" }
