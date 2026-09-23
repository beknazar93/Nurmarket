# Avalonia host — primary UI

Точка входа: `src/NurMarketKassa.Avalonia` (`Program.cs` + `App.axaml`).

Рабочий UI: `src/NurMarketKassa.Avalonia/Views.axaml/`.

WPF эталон (резерв): `src/NurMarketKassa/Views/` — не удалять до отдельного этапа.

Запуск:
```
dotnet run --project src/NurMarketKassa.Avalonia
```

Сборка:
```
dotnet build src/NurMarketKassa.Avalonia
```

Оставшиеся Thin stubs (AXAML уже на диске, нужна финальная полировка DataGrid/Popup):
- FinanceWindow, SalesWindow, PosSettingsWindow
- ReturnSaleDialog, CashOperationsDialog, FrmKeyboard, FinanceDateRangeDialog

Исключения — в `Directory.Build.targets`.