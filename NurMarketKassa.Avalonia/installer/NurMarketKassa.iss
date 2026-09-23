; NurMarket Kassa — установочный файл (Inno Setup 6)
; Собирает self-contained win-x64 публикацию NurMarketKassa.Avalonia в один Setup.exe.
;
; Сборка:
;   1) dotnet publish ..\NurMarketKassa.Avalonia\NurMarketKassa.Avalonia.csproj -c Release -r win-x64 ^
;        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;        -p:EnableCompressionInSingleFile=true -o ..\NurMarketKassa.Avalonia\publish
;   2) "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" NurMarketKassa.iss
;
; Итог: installer\output\NurMarketKassa-Setup-<версия>.exe

#define MyAppName "NurMarket Kassa"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "NurMarket"
#define MyAppExeName "NurMarketKassa.Avalonia.exe"
#define MyPublishDir "..\NurMarketKassa.Avalonia\publish"

[Setup]
; Один раз сгенерированный GUID — не менять между версиями, иначе апдейт "поверх"
; перестанет узнавать предыдущую установку.
AppId={{4376EB83-9DAC-4A92-BCB8-17BBAE76FEF7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=nurmarket
SetupIconFile=app-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Ставится в Program Files -> нужны права администратора.
PrivilegesRequired=admin
; Кассовое приложение не поддерживает несколько параллельных копий на одной машине.
AppMutex=NurMarketKassaSingleInstance

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные значки:"

[Files]
; Всё содержимое публикации, кроме отладочных символов (*.pdb) — они не нужны на кассе.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Установочные файлы программы удаляются штатно. Данные кассы (локальная БД,
; логи, отложенные чеки в %LocalAppData%/%AppData%\NurMarketKassa) НЕ трогаем —
; чтобы переустановка/обновление не стирало историю продаж и настройки кассира.
