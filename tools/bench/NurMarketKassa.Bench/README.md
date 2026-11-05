# NurMarketKassa.Bench — замеры скорости и памяти

Отдельный маленький проект (BenchmarkDotNet, MIT). **Не входит в решение выпуска и не попадает в установку кассы.** Папка данных — временная, сеть — заглушка `127.0.0.1:9`, настоящие данные кассы не читаются и не пишутся.

Что меряет: поиск по каталогу из 15 000 товаров (`CatalogSearchBench`), сопоставление накладной (`InvoiceMatcherBench`), выгрузка 15 000 строк в Excel — OpenXml против MiniExcel (`ExcelExportBench`).

Запуск (из корня репозитория; ПК слабый — закрыть кассу и другие тяжёлые программы, одну сборку за раз, через `build-locked.ps1` или замок `Global\NurMarketBuildLock`):

```
dotnet run -c Release --project tools/bench/NurMarketKassa.Bench -- --list flat      # список замеров, ничего не запуская
dotnet run -c Release --project tools/bench/NurMarketKassa.Bench -- --filter "*InvoiceMatcherBench*"
dotnet run -c Release --project tools/bench/NurMarketKassa.Bench -- --filter "*CatalogSearchBench*"
dotnet run -c Release --project tools/bench/NurMarketKassa.Bench -- --filter "*ExcelExportBench*"
```

Прогон «короткий» (`ShortRun`, в этом же процессе, без пересборки на каждый замер). Результаты — в `BenchmarkDotNet.Artifacts/` рядом (в `.gitignore`). Память кассы на ПК — см. `docs/Отчеты/Как снимать память — dotnet-counters и dotnet-gcdump (05.11.2026).md`.
