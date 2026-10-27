# NurRemoteSupport — сервер удалённой помощи NurMarket (замена AnyDesk)

*Инструкция для бэкенд-разработчика NurCRM по развёртыванию на `support.nurcrm.kg`. Переписана 2026-10-27 под актуальный сервер (`Server/Server.csproj`); старая ветка с `OPERATOR_KEY`, `BIND`, `PORT` и `Relay.cs` удалена.*

## Что это

Касса NurMarket Kassa по кнопке «Получить код для поддержки» подключается к этому серверу и показывает кассиру
**код (9 цифр) и PIN**. Сотрудник поддержки входит в программу оператора `NurMarketSupport.exe` (или на страницу `https://support.nurcrm.kg/`)
со своим логином и паролем, выбирает кассу в очереди «Ждут помощи» (кассир диктует только PIN) или вводит код и PIN. На кассе появляется вопрос
«Оператор … хочет подключиться». После «Разрешить» оператор видит экран кассы и управляет мышью и клавиатурой, после «Только просмотр» — только видит экран
(сервер при этом не пересылает кассе ввод оператора).

```
Касса ──wss://support.nurcrm.kg/ws/host──┐                          ┌── программа оператора / браузер
                                         nginx/Caddy (443, TLS) ── сервер :8080 (127.0.0.1)
Оператор ─https://support.nurcrm.kg/ ────┘   /ws/operator, /api/*, /, /health
```

- Сервер — ASP.NET Core 8, без внешних пакетов и без базы данных. Сеансы живут в памяти (перезапуск обрывает текущие); на диске — `operators.json`, `journal*.jsonl`, `notes*.jsonl`, `token-secret`, `revoked-tokens.txt`.
- Нагрузка небольшая: 50–100 МБ памяти; трафик сеанса — 0,2–2 Мбит/с, пока экран кассы меняется.
- Протокол — [PROTOCOL.md](PROTOCOL.md) (актуальная версия v1.3), программа оператора — [Operator/README.md](Operator/README.md).

Состав папки:

| Путь | Что это |
|---|---|
| `Server/` | сервер: `Program.cs` (HTTP-адреса, вход, команды `add-operator`…), `RelayEndpoints.cs` (WebSocket `/ws/host` и `/ws/operator`), `SessionRegistry.cs` (сеансы), `WsConnection.cs`, `TokenService.cs`, `OperatorStore.cs`, `AttemptLimiter.cs`, `Journal.cs`, `Notes.cs`, `Jsonl.cs`, `Msg.cs`, `RelayOptions.cs` |
| `Server/wwwroot/` | страница оператора в браузере (запасная; основной инструмент — программа `Operator/`) |
| `Operator/` | программа оператора (Avalonia, `NurMarketSupport.exe`) |
| `Tools/RelayTests` | сквозные проверки сервера: `e2e` и `auth` (сами поднимают сервер на 127.0.0.1) и нагрузка `load` |
| `Tools/FakeHost`, `Tools/HostHarness` | поддельная касса и стенд с настоящим кодом кассы для проверок |
| `deploy/` | всё для VPS: `Dockerfile`, `docker-compose.yml` (сервер + Caddy), `nginx-support.nurcrm.kg.conf`, `Caddyfile`, `nurremote.service`; инструкция — [deploy/README.md](deploy/README.md) |

## 1. Что нужно

- VPS на Linux (Ubuntu 22.04/24.04 или похожий), открытые порты 80 и 443.
- DNS: запись **A** (и AAAA, если есть IPv6) `support.nurcrm.kg` указывает на IP сервера.
- Docker с compose v2 **или** ASP.NET Core Runtime 8 (`sudo apt install aspnetcore-runtime-8.0`).

## 2. Запуск

Выберите один путь.

### Docker + Caddy (рекомендуется)

Полная пошаговая инструкция — [deploy/README.md](deploy/README.md). Коротко:

```bash
cd NurRemoteSupport/deploy
cp .env.example .env          # RELAY_DOMAIN=support.nurcrm.kg
mkdir -p data && sudo chown 1654:1654 data && chmod 700 data
docker compose up -d --build
docker compose run --rm relay add-operator aibek "Айбек"      # пароль со stdin или по запросу (не короче 10 символов)
curl https://support.nurcrm.kg/health                          # ok
```

### systemd без Docker

```bash
dotnet publish NurRemoteSupport/Server -c Release -o out /p:UseAppHost=false
sudo useradd --system --no-create-home --shell /usr/sbin/nologin nurremote
sudo mkdir -p /opt/nurremote && sudo cp -r out/* /opt/nurremote/
sudo cp deploy/nurremote.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now nurremote
sudo -u nurremote env Relay__DataDir=/var/lib/nurremote dotnet /opt/nurremote/Server.dll add-operator aibek "Айбек"
curl -s http://127.0.0.1:8080/health
journalctl -u nurremote -f
```

Сервер слушает адрес из `ASPNETCORE_URLS` (в `nurremote.service` — `http://127.0.0.1:8080`, в Docker — порт 8080 внутри контейнера). Порт 8080 наружу **не открывать**.

## 3. HTTPS для support.nurcrm.kg

Либо Caddy (в `deploy/docker-compose.yml` он уже есть, сертификат получает и продлевает сам), либо nginx и certbot:

```bash
sudo apt install nginx certbot
sudo mkdir -p /var/www/certbot
sudo cp deploy/nginx-support.nurcrm.kg.conf /etc/nginx/sites-available/support.nurcrm.kg
# Сертификата ещё нет: временно закомментировать в файле весь второй блок server { listen 443 … }
sudo ln -s /etc/nginx/sites-available/support.nurcrm.kg /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/certbot -d support.nurcrm.kg --deploy-hook "systemctl reload nginx"
# Раскомментировать блок 443:
sudo nginx -t && sudo systemctl reload nginx
```

Для WebSocket в конфиге обязательны `proxy_http_version 1.1`, `Upgrade $http_upgrade`, `Connection $connection_upgrade`; `proxy_read_timeout` больше 15 с (сервер шлёт ping каждые 15 с). Всё это в файле уже есть.
В `nginx-support.nurcrm.kg.conf` журнал доступа ведётся форматом `nurremote` **без строки запроса** (в ней могут быть `?ticket=` или `?token=` WebSocket); Caddy вырезает эти параметры из журнала сам.

Настоящий IP кассы и оператора (для лимитов и журнала) сервер берёт из `X-Forwarded-For` **только от доверенных адресов**:
- nginx или Caddy на той же машине — это loopback, доверяется по умолчанию;
- Caddy в контейнере docker — адрес из сети docker, он задан в `docker-compose.yml`: `Relay__TrustedProxies: "172.16.0.0/12"`;
- свой список — `Relay__TrustedProxies="10.0.0.5,172.16.0.0/12"`. От остальных адресов заголовок игнорируется (подделка в обход прокси не работает).

Проверка снаружи: `curl -s https://support.nurcrm.kg/health` → `ok`.

## 4. Операторы и пароли

```bash
dotnet Server.dll add-operator <login> "<Имя>"      # пароль со stdin или по запросу; новый — не короче 10 символов
dotnet Server.dll remove-operator <login>
dotnet Server.dll list-operators
```

- Повторный `add-operator` для существующего логина **сбрасывает пароль**: все прежние входы этого оператора перестают действовать, открытые соединения закрываются в течение минуты.
- Оператор сам меняет пароль в программе («Сменить пароль») или на странице — `POST /api/password`.
- Выход оператора (`POST /api/logout`) отзывает его токен на сервере.
- Хэши паролей — PBKDF2-SHA256, для новых — 600 тыс. итераций (старые, 210 тыс., проверяются как раньше и обновятся при сбросе или смене пароля).
- Папка данных по умолчанию — `data` **рядом с программой** (не текущая папка), `Relay__DataDir` её меняет.

## 5. Настройки

Задаются переменными окружения `Relay__<Имя>` (docker-compose, `/etc/nurremote.env`) или файлом `relay.json` (путь в `RELAY_CONFIG`); образец со всеми именами — [Server/appsettings.example.json](Server/appsettings.example.json). Переменная окружения важнее файла.

| Настройка | По умолчанию | Что это |
|---|---|---|
| `DataDir` | `data` | папка данных |
| `TokenSecret` | пусто (создаётся в `{DataDir}/token-secret`) | ключ подписи токенов, ≥ 32 символов |
| `TokenLifetimeHours` | 12 | срок токена оператора |
| `SessionTtlSeconds` | 900 | сколько живёт код без оператора |
| `ConsentTimeoutSeconds` | 90 | ожидание ответа кассира |
| `MaxPinAttempts` | 5 | неверных PIN до блокировки кода |
| `SessionIdleMinutes` | 30 | без ввода оператора и чата — сеанс завершается (`end idle`); 0 — без предела |
| `SessionMaxMinutes` | 240 | наибольшая длительность сеанса (`end max`); 0 — без предела |
| `TrustedProxies` | пусто (только loopback) | чьему `X-Forwarded-For/Proto` верить: адреса и сети через запятую |
| `MaxBinaryMessageBytes`, `MaxTextMessageBytes` | 4 МБ, 256 КБ | наибольшие сообщения |
| `PreSessionTextBytes` | 4096 | наибольший текст от кассы до начала сеанса (двоичное до сеанса — разрыв) |
| `ControlQueueBytes` | 1 МБ | предел очереди служебных ответов одного соединения |
| `SendQueueBytes`, `SendStallSeconds` | 16 МБ, 30 | очередь кадров и время ожидания места в ней |
| `PingIntervalSeconds`, `IdleTimeoutSeconds` | 15, 45 | проверка связи |
| `MaxSessions`, `MaxPendingPerIp` | 2000, 10 | кодов на сервере и ожидающих кодов с одной сети |
| `MaxHostConnectionsPerIp` | 200 | одновременных соединений кассы с одной сети |
| `HostConnectsPerMinutePerIp`, `JoinPerMinute` | 30, 20 | подключений кассы и `join` в минуту |
| `LoginPerMinutePerIp` | 10 | попыток входа в минуту с одной сети |
| `LoginFailuresPer15MinPerLoginIp` | 10 | ошибок входа «логин + сеть» за 15 мин до блокировки |
| `LoginGlobalFailureDelayAfter` | 100 | ошибок входа на логин со всех адресов, после которых ответ замедляется (блокировки нет) |
| `MinPasswordLength` | 10 | наименьшая длина нового пароля |
| `NotesPerMinute` | 30 | заметок в минуту от одного оператора |
| `OperatorRecheckSeconds` | 60 | как часто закрываются соединения операторов с отозванным входом |

IPv6 в лимитах считается по сети /64. Если вся поддержка работает из одного офиса за одним IP, при частых `rate-limited` увеличьте `LoginFailuresPer15MinPerLoginIp` и `JoinPerMinute`.

## 6. Как к серверу подключается касса

- По умолчанию касса подключается к **`wss://support.nurcrm.kg/ws/host`** (`RemoteSupportHost.DefaultRelayUrl`).
- Другой адрес (для проверки) задаётся в файле `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt` (одна строка, например `wss://test.example.kg/ws/host`) или в переменной окружения `NURREMOTE_RELAY`.
- Касса принимает только `wss://`; `ws://` разрешён лишь для сервера на том же компьютере (`ws://127.0.0.1:8080/ws/host`).
- На кассе: «Тех. поддержка» → «Получить код для поддержки» → кассир диктует код и PIN (или только PIN, если оператор выбрал кассу в очереди) → вопрос «Разрешить / Только просмотр / Отказать». Во время сеанса на кассе висит красная полоса «Идёт сеанс поддержки · Завершить».

## 7. Брандмауэр

```bash
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw allow 443/udp && sudo ufw enable
```

Порт 8080 наружу **не открывать**: в Docker он не публикуется (`expose`), в systemd слушает только 127.0.0.1. docker сам дописывает правила iptables для опубликованных портов в обход ufw — поэтому в `docker-compose.yml` у сервера `expose`, а не `ports`.

## 8. Журналы

- Docker: `docker compose logs -f` (файлы ограничены 5×20 МБ). systemd: `journalctl -u nurremote -f`.
- В лог пишется: подключение кассы (код сеанса, магазин, компьютер, версия, IP), вход оператора (логин без управляющих символов, IP), неверные PIN, блокировки, превышение лимитов, начало и конец сеанса (причина, длительность, объём).
- **PIN, пароли, токены, кадры экрана, текст и клавиши в журналы не пишутся.**
- Журнал сеансов — `{DataDir}/journal.jsonl`, заметки — `notes.jsonl`; в начале каждого месяца прошлый уходит в `journal-ГГГГ-ММ.jsonl` / `notes-ГГГГ-ММ.jsonl` (читаются вместе с текущим).
- `GET /health` → `ok` — для мониторинга.

## 9. Обновление

```bash
cd NurRemoteSupport && git pull
cd deploy && docker compose up -d --build
```

systemd: опубликовать заново, скопировать в `/opt/nurremote`, `sudo systemctl restart nurremote`. При перезапуске идущие сеансы получают `end` с причиной `shutdown`; касса и оператор подключаются заново с новым кодом. Обновлять лучше, когда сеансов нет (в программе оператора — «Идут сейчас»).

## 10. Проверка на своём компьютере (боевой сервер не нужен)

```powershell
cd NurRemoteSupport\Server;       dotnet build -c Release
cd ..\Tools\FakeHost;             dotnet build -c Release
cd ..\RelayTests;                 dotnet build -c Release
dotnet bin\Release\net8.0\RelayTests.dll e2e      # ретранслятор по PROTOCOL.md, включая защиту (просмотр, отзыв входа, флуд, IPv6, пределы сеанса)
dotnet bin\Release\net8.0\RelayTests.dll auth     # вход, смена пароля, выход, билет WebSocket, прокси, ротация журнала
dotnet bin\Release\net8.0\RelayTests.dll load --sessions 50 --seconds 30
```

Каждый прогон поднимает свой сервер на 127.0.0.1 с временной папкой данных и боевых данных не касается. Вручную с настоящей кассой на этом же ПК:

1. Запустить `dotnet Server/bin/Release/net8.0/Server.dll --urls http://127.0.0.1:8080`, добавить оператора `add-operator`.
2. Записать в `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt` строку `ws://127.0.0.1:8080/ws/host`.
3. Открыть программу оператора (адрес сервера `http://127.0.0.1:8080`) или страницу `http://127.0.0.1:8080/`.
