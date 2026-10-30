# NurCRM Kassa (раньше NurMarket Kassa) — коротко для агента

Касса для Windows/Linux/Android (Avalonia 11.2.5, .NET 8) поверх облачной CRM NurCRM (`https://app.nurcrm.kg/`). Своего сервера нет. Тот же exe с `--owner` — программа владельца. Видимое название — NurCRM («Нур СРМ», решение 29.10.2026); технические идентификаторы остаются `NurMarket*` (packId, проекты, папки данных, файлы выпуска) — не менять.
Подробности — `docs/AI_HANDOFF.md` (~30 тыс. токенов, НЕ читать целиком: искать Grep'ом нужный раздел; для больших файлов и журналов — навык headroom).

## Правила владельца (обязательно)
1. **Никогда `dotnet test`** — тесты ходят в боевую базу (хук `.claude/hooks/block-dotnet-test.py` блокирует). Проверка — сборка + живая проверка на тестовом аккаунте NBS.
2. Секреты не печатать и не коммитить: `token.txt`, `docs/paid-serial-keys.md`, `NurSupportBot/appsettings.json`, JWT.
3. Отвечать по-русски, просто. Все новые надписи — на 5 языках `Tr.T(ru, ky, en, tr, uz)`.
4. Минимальный дифф в стиле кода; комментарии по-русски с датой и причиной (`// 2026-10-15: …`). Не менять зависимости, схему базы, сборку без спроса.
5. Владелец часто сам работает в кассе на этом ПК — перед перезапуском смотреть журнал. UI-автоматизация только по hwnd окна кассы, никогда по координатам экрана.
6. Коммит/пуш — только по просьбе («выгрузи обновление» = выпуск). Своя экранная клавиатура запрещена (только osk.exe).

## Где что
- Решение: `NurMarketKassa.Avalonia/` → проекты `NurMarketKassa.Avalonia` (Windows-приложение), `.Infrastructure` (API NurCRM, касса, печать), `.ViewModels`, `.Core`, `.Ui.Shared`, `.Portable` (Linux), `.Android`. Portable/Android берут все `*.cs` приложения глобом.
- Таблица «тема → файлы» — AI_HANDOFF.md раздел 2.2. Ловушки (потоки, ресурсы темы, клавиши) — раздел 10.
- Журнал кассы: `%LOCALAPPDATA%\NurMarketKassa\Logs\nurmarket-kassa.log` (владелец — `NurMarketOwner`). База: `%APPDATA%\NurMarketKassa\data\pos_local.db`.

## Карта кода (Graphify) — сначала граф, потом файлы (экономия токенов)
- Граф кода: `NurMarketKassa.Avalonia/graphify-out/` (16,7 тыс. узлов; строится локально, без ИИ). Перед поиском «где/кто вызывает/как связано» — спросить граф, а не читать файлы подряд:
  `graphify explain "Класс"`, `graphify path "A" "B"`, `graphify query "вопрос"` (запуск из `NurMarketKassa.Avalonia`, PATH: `C:\Users\User\.local\bin`). Обзор модулей — `graphify-out/GRAPH_REPORT.md` (читать нужный раздел, не целиком). В сеансах с MCP — сервер `graphify` (`.mcp.json`): query_graph, get_node, get_neighbors, shortest_path.
- После правок кода: `graphify update NurMarketKassa.Avalonia` **из корня репозитория** (~1 мин; запуск из другой папки создаёт лишний `graphify-out`). `/graphify` с документами/картинками тратит ИИ — без нужды не запускать.
- Большие файлы (`docs/redesign-progress.md` > 3 тыс. строк, AI_HANDOFF.md, журналы) — только Grep по разделу или headroom.
- Заметки, ТЗ и память — хранилище Obsidian `C:\Users\User\Documents\NurMarket-vault` (ссылки на `docs`, память, отчёт графа).

## Сборка
- Проверка компиляции: `cd NurMarketKassa.Avalonia && dotnet build NurMarketKassa.Avalonia/NurMarketKassa.Avalonia.csproj -c Debug -v q -m:1 -nodeReuse:false`.
- AnyDesk убран из кассы 16.10.2026 — удалённая помощь только своя: касса `Services/RemoteSupport/RemoteSupportHost.cs` → сервер `NurRemoteSupport/` (по умолчанию `wss://support.nurcrm.kg/ws/host`, для проверки — `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt`).
- Выпуск: AI_HANDOFF.md 4.3; команды `tools/...` запускать из корня репозитория; `tools/release/gh_release.py` — release / resume / update / push / check (нужен `token.txt` в корне).
- Установка на этот ПК: `powershell -ExecutionPolicy Bypass -File tools/qa/deploy_both.ps1`.
- Памяти ПК 7,8 ГБ: при OutOfMemory в XAML — `dotnet build-server shutdown` и повтор.

## Состояние
- Последний выпуск 1.17.58 (11.10.2026). Что сделано/осталось по аудиту 15.10 — память проекта (`audit-2026-10-15-fixes`) и `git status`.
