# NurRemoteSupport — сервер удалённой помощи NurMarket (замена AnyDesk)

*2026-10-16. Инструкция для бэкенд-разработчика NurCRM по развёртыванию на `support.nurcrm.kg`.*

## Что это

Касса NurMarket Kassa по кнопке «Получить код для поддержки» подключается к этому серверу и показывает кассиру
**код (9 цифр) и PIN**. Оператор поддержки открывает в браузере `https://support.nurcrm.kg/`, вводит код, PIN и своё
имя. На кассе появляется вопрос «Оператор … хочет подключиться». После «Разрешить» оператор видит экран кассы и
управляет мышью и клавиатурой, после «Только просмотр» — только видит экран.

```
Касса ──wss://support.nurcrm.kg/ws/host──┐                           ┌── браузер оператора
                                         nginx/Caddy (443, TLS) ── сервер :8080 (127.0.0.1)
Оператор ─https://support.nurcrm.kg/ ────┘    /ws/operator, /, /health
```

- Сервер — ASP.NET Core 8, без внешних пакетов и без базы данных: всё хранится в памяти, перезапуск обрывает текущие сеансы.
- Нагрузка небольшая: 50–100 МБ памяти; трафик сеанса — 0,2–2 Мбит/с, пока экран кассы меняется.
- Протокол описан в [PROTOCOL.md](PROTOCOL.md).

Состав папки:

| Путь | Что это |
|---|---|
| `Server/` | сервер (`Program.cs`, `Relay.cs`, …) и страница оператора `Server/wwwroot/` |
| `Dockerfile`, `docker-compose.yml` | запуск в Docker |
| `deploy/nginx-support.nurcrm.kg.conf` | nginx с HTTPS и WebSocket |
| `deploy/Caddyfile` | вместо nginx — Caddy (сертификат получает сам) |
| `deploy/nurremote.service` | systemd для запуска без Docker |
| `SmokeTest/` | сквозная проверка (касса + оператор) на своём компьютере |

## 1. Что нужно

- VPS на Linux (Ubuntu 22.04/24.04 или похожий), открытые порты 80 и 443.
- DNS: запись **A** (и AAAA, если есть IPv6) `support.nurcrm.kg` указывает на IP сервера.
- Docker с compose v2 **или** ASP.NET Core Runtime 8 (`sudo apt install aspnetcore-runtime-8.0`).

## 2. Запуск в Docker (рекомендуется)

```bash
sudo mkdir -p /opt/nurremote && cd /opt/nurremote
# скопировать сюда содержимое папки NurRemoteSupport (git clone / scp)

# Ключ оператора: без него страница и вход оператора закрыты для посторонних.
echo "OPERATOR_KEY=$(openssl rand -hex 24)" | sudo tee .env >/dev/null
sudo chmod 600 .env
sudo cat .env          # передать ключ операторам поддержки (в git не класть — .env в .gitignore)

sudo docker compose up -d --build
curl -s http://127.0.0.1:8080/health   # {"status":"ok",...}
sudo docker compose logs -f            # журнал
```

Порт 8080 открыт только на `127.0.0.1` этого сервера. Из интернета на сервер попадают только через nginx или Caddy с HTTPS
(раздел 4). В `docker-compose.yml` уже задано `TRUSTED_PROXIES=172.16.0.0/12,…`: nginx на хосте приходит в контейнер с адреса
шлюза docker, и сервер по `X-Forwarded-For` видит настоящий IP клиента для лимитов попыток.

## 3. Запуск без Docker (systemd)

На машине разработчика (нужен .NET SDK 8):

```bash
dotnet publish NurRemoteSupport/Server -c Release -o out /p:UseAppHost=false
# или самодостаточно, без runtime на сервере:
# dotnet publish NurRemoteSupport/Server -c Release -r linux-x64 --self-contained -o out
```

На сервере:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin nurremote
sudo mkdir -p /opt/nurremote && sudo cp -r out/* /opt/nurremote/
echo "OPERATOR_KEY=$(openssl rand -hex 24)" | sudo tee /etc/nurremote.env >/dev/null
sudo chmod 600 /etc/nurremote.env
sudo cp deploy/nurremote.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now nurremote
curl -s http://127.0.0.1:8080/health
journalctl -u nurremote -f              # журнал
```

Здесь nginx стоит на той же машине (127.0.0.1), и ему сервер доверяет по умолчанию. `TRUSTED_PROXIES` задавать не нужно.

## 4. HTTPS для support.nurcrm.kg

### Вариант А: nginx и certbot

```bash
sudo apt install nginx certbot
sudo mkdir -p /var/www/certbot
sudo cp deploy/nginx-support.nurcrm.kg.conf /etc/nginx/sites-available/support.nurcrm.kg
# Сертификата ещё нет: временно закомментировать в файле весь второй блок server { listen 443 … }
sudo ln -s /etc/nginx/sites-available/support.nurcrm.kg /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/certbot -d support.nurcrm.kg \
     --deploy-hook "systemctl reload nginx"
# Раскомментировать блок 443:
sudo nginx -t && sudo systemctl reload nginx
```

certbot сам продлевает сертификат (таймер `certbot.timer`) и после продления перезагружает nginx. Для WebSocket в
конфиге обязательны строки `proxy_http_version 1.1`, `Upgrade $http_upgrade` и `Connection $connection_upgrade`, а
`proxy_read_timeout` должен быть больше 20 с (сервер шлёт ping каждые 20 с). Всё это в файле уже есть.

### Вариант Б: Caddy (проще)

```bash
sudo apt install caddy
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

Caddy сам получает и продлевает сертификат и проксирует WebSocket.

Проверка снаружи: `curl -s https://support.nurcrm.kg/health`. В браузере `https://support.nurcrm.kg/` должна открыться
страница «Подключение к кассе».

## 5. Настройки (переменные окружения)

Задаются в `docker-compose.yml`/`.env`, в `/etc/nurremote.env` или в `Server/appsettings.json`. Переменная окружения важнее файла.

| Переменная | По умолчанию | Что это |
|---|---|---|
| `OPERATOR_KEY` | пусто | ключ оператора. Если задан, без него оператор не войдёт. **Рекомендуется задать** |
| `BIND`, `PORT` | `127.0.0.1`, `8080` | где слушать (в Docker — `0.0.0.0`) |
| `TRUSTED_PROXIES` | пусто (только 127.0.0.1/::1) | чьему `X-Forwarded-For` верить: IP или сеть (`172.16.0.0/12`), `*` — всем |
| `CODE_TTL_SECONDS` | 900 | сколько живёт код без оператора (касса пишет «15 минут») |
| `PIN_LENGTH` | 4 | длина PIN (4–8) |
| `MAX_PIN_ATTEMPTS` | 5 | неверных PIN до блокировки кода |
| `CONSENT_TIMEOUT_SECONDS` | 90 | ожидание ответа кассира |
| `SESSION_IDLE_MINUTES` | 30 | простой оператора — сеанс завершается |
| `SESSION_MAX_MINUTES` | 240 | наибольшая длительность сеанса |
| `PING_INTERVAL_SECONDS`, `DEAD_PEER_SECONDS` | 20, 60 | проверка связи |
| `RATE_WINDOW_SECONDS` | 600 | окно лимитов по IP |
| `JOIN_LIMIT`, `JOIN_FAIL_LIMIT` | 30, 10 | попыток входа оператора и неудачных входов с одного IP за окно |
| `HOST_LIMIT`, `MAX_HOSTS` | 30, 1000 | новых кодов с одного IP за окно; кодов на сервере всего |
| `MAX_CONNECTIONS_PER_IP` | 50 | одновременных соединений с одного IP |
| `MAX_TEXT_BYTES`, `MAX_FRAME_BYTES`, `MAX_QUEUE_BYTES` | 64 КБ, 1 МБ, 32 МБ | наибольшие размеры сообщения и очереди |

Если вся поддержка работает из одного офиса за одним IP, при частых ошибках `rate-limit` можно увеличить `JOIN_FAIL_LIMIT`.

## 6. Как к серверу подключается касса

- По умолчанию касса подключается к **`wss://support.nurcrm.kg/ws/host`** (`RemoteSupportHost.DefaultRelayUrl`).
- Другой адрес (для проверки) задаётся в файле `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt` (одна строка, например
  `wss://test.example.kg/ws/host`) или в переменной окружения `NURREMOTE_RELAY`. Переменная важнее файла.
- Касса принимает только `wss://`. `ws://` разрешён лишь для сервера на том же компьютере (`ws://127.0.0.1:8080/ws/host`).
- На кассе: «Тех. поддержка» → «Получить код для поддержки» → кассир диктует код и PIN → оператор вводит их на странице →
  у кассира вопрос «Разрешить / Только просмотр / Отклонить». Во время сеанса на кассе висит красная полоса «Идёт сеанс поддержки · Завершить».

## 7. Работа оператора

Страница `https://support.nurcrm.kg/` (Chrome или Edge):

1. Ввести код кассы, PIN, своё имя (его увидит кассир), при первом входе — ключ оператора («Запомнить на этом компьютере»).
2. Подождать, пока кассир нажмёт «Разрешить».
3. Экран кассы масштабируется под окно. Мышь, колесо и клавиатура работают, когда курсор на экране кассы и по нему был щелчок.
   На панели: «Качество», «Сочетания клавиш…» (Win, Alt+Tab, Alt+F4, Ctrl+Shift+Esc…), «Вставить текст» (печатает текст на кассе),
   «Режим просмотра», «Во весь экран» (в нём Esc и Win уходят на кассу; выйти — удерживать Esc), «Завершить».
4. Жёлтая метка «Только просмотр» значит, что управления нет (так решил кассир или включил сам оператор).

Ссылку можно открыть с уже заполненным кодом: `https://support.nurcrm.kg/?code=123456789`.

## 8. Брандмауэр

```bash
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw enable
```

Порт 8080 наружу **не открывать**: в Docker он и так привязан к 127.0.0.1. Учтите, что docker сам дописывает правила iptables
для опубликованных портов в обход ufw, поэтому важна именно привязка `127.0.0.1:8080:8080`.

## 9. Журнал

- Docker: `docker compose logs -f` (файлы ограничены 5×10 МБ). systemd: `journalctl -u nurremote -f`.
- В журнал пишется: подключение кассы (код, магазин, компьютер, версия, IP), запрос оператора (имя, IP), неверные PIN и
  блокировки, превышение лимитов, начало и конец сеанса (причина, длительность, объём экрана).
- **PIN, кадры экрана, текст и клавиши в журнал не пишутся.**
- `GET /health` подходит для мониторинга (uptime, число кодов и сеансов).

## 10. Обновление

Docker:

```bash
cd /opt/nurremote && git pull          # или скопировать новые файлы
sudo docker compose up -d --build
```

systemd: опубликовать заново, скопировать в `/opt/nurremote`, затем `sudo systemctl restart nurremote`.

При перезапуске идущие сеансы получают `end` с причиной `server` (оператор видит «Сервер поддержки перезапускается»), касса
и оператор подключаются заново с новым кодом. Обновлять лучше, когда сеансов нет: смотрите `sessions` в `/health`.

## 11. Проверка на своём компьютере

```bash
dotnet build NurRemoteSupport/Server -c Release
dotnet run --project NurRemoteSupport/SmokeTest            # --show-log — показать журнал сервера
```

SmokeTest сам запускает сервер на `127.0.0.1:5088` с короткими сроками, играет кассу и оператора и в конце останавливает сервер.
Проверяется:

- выдача кода и PIN;
- согласие кассира, пересылка кадров и ввода;
- «только просмотр»;
- отказ кассира и повторный запрос;
- нет ответа кассира;
- блокировка после 5 неверных PIN;
- ключ оператора;
- Origin;
- rate-limit;
- истечение кода;
- отсутствие PIN в журнале.

`dotnet test` в этом репозитории не используется.

Чтобы проверить вручную с настоящей кассой на этом же ПК:

1. Запустить `dotnet Server/bin/Release/net8.0/NurRemoteSupport.Server.dll`.
2. Записать в `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt` строку `ws://127.0.0.1:8080/ws/host`.
3. Открыть `http://127.0.0.1:8080/`.
