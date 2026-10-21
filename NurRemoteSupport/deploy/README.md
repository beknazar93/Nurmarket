# Сервер удалённой поддержки NurMarket — установка на VPS

Своя замена AnyDesk: касса и браузер оператора соединяются через этот сервер (ретранслятор).
Протокол — [`../PROTOCOL.md`](../PROTOCOL.md). В этой папке всё для запуска в Docker: сам сервер + Caddy (он сам получает HTTPS-сертификат).

## Что нужно

- VPS с **Ubuntu 22.04 или 24.04**, 1 ядро и 1 ГБ памяти хватит на десятки одновременных сеансов (см. «Нагрузка» ниже). Главное — канал: один сеанс занимает 0,5–3 Мбит/с.
- Публичный IP. Домен не обязателен: без домена используем бесплатный `sslip.io` — адрес вида `203-0-113-5.sslip.io` сам указывает на IP `203.0.113.5`.
- Открытые порты **80** и **443** (80 нужен только для получения сертификата).

## 1. Поставить Docker

```bash
sudo apt update && sudo apt install -y ca-certificates curl
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER      # затем выйти и зайти по SSH заново
docker compose version             # проверка: должна показаться версия
```

## 2. Скопировать файлы

С компьютера, где лежит репозиторий, скопировать **всю папку `NurRemoteSupport`** на сервер (кроме `bin/obj`), например:

```bash
scp -r NurRemoteSupport  user@203.0.113.5:~/
```

(или `git clone` репозитория на сервере). Дальше все команды — в папке `~/NurRemoteSupport/deploy`.

## 3. Настроить адрес

```bash
cd ~/NurRemoteSupport/deploy
cp .env.example .env
nano .env
```

В `.env` одна строка:

- есть домен (A-запись указывает на IP сервера): `RELAY_DOMAIN=support.nurmarket.kg`
- нет домена: `RELAY_DOMAIN=203-0-113-5.sslip.io` (свой IP, точки заменить на дефисы)

Папка данных (операторы, журнал сеансов, заметки операторов `notes.jsonl` — с 17.10.2026, ключ токенов) — `deploy/data`. Сервер внутри контейнера работает не от root (uid 1654), поэтому:

```bash
mkdir -p data && sudo chown 1654:1654 data && chmod 700 data
```

Секретов в настройках нет: ключ подписи токенов создаётся сам в `data/token-secret` при первом запуске. Если нужен свой — `Relay__TokenSecret` (≥ 32 символов) в `environment` у `relay`.

## 4. Открыть порты и запустить

```bash
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw allow 443/udp
sudo ufw enable        # если ufw ещё не включён; SSH (22) уже разрешён строкой выше
docker compose up -d --build
docker compose logs -f     # выход — Ctrl+C; ждём строку «Ретранслятор NurMarket запущен»
```

У некоторых хостеров есть ещё свой файрвол в панели — там тоже открыть 80 и 443.

## 5. Добавить операторов

```bash
docker compose run --rm relay add-operator aibek "Айбек"
```

Пароль спросит дважды (не показывается на экране), не короче 8 символов. Та же команда для существующего логина **сбрасывает пароль** — старые входы этого оператора сразу перестают действовать.

```bash
docker compose run --rm relay list-operators
docker compose run --rm relay remove-operator aibek
```

Перезапускать сервер после этого не нужно — он перечитывает `data/operators.json` сам.

## 6. Проверить

```bash
curl https://RELAY_DOMAIN/health        # должно ответить: ok
```

В браузере открыть `https://RELAY_DOMAIN/` → вход оператора → «Подключение» → код и PIN, которые продиктует кассир.
В кассе в настройках тех. поддержки указать адрес `wss://RELAY_DOMAIN/ws/host`.

Проверить без кассы можно поддельной кассой с любого Windows-ПК (папка `Tools/FakeHost`):

```
dotnet run -c Release --project Tools/FakeHost -- --url wss://RELAY_DOMAIN/ws/host
```

Она покажет код и PIN, после «подключения» оператор увидит движущуюся картинку; клики и набранный текст рисуются на ней (на самом ПК ничего не нажимается).

## Обновление

```bash
cd ~/NurRemoteSupport && git pull          # или скопировать новые файлы поверх
cd deploy && docker compose up -d --build
docker image prune -f                       # удалить старые образы
```

Идущие сеансы при обновлении обрываются (в журнале — «Сервер перезапущен»), операторам заново входить не нужно.

## Журнал и резервная копия

Журнал сеансов — `deploy/data/journal.jsonl`, одна строка JSON на сеанс (кто, к какой кассе, IP, начало, конец, длительность, итог). Он только дописывается и растёт медленно (≈ 0,5 КБ на сеанс). Смотреть удобнее на странице «Журнал» в консоли оператора.

Резервная копия всей папки данных (операторы + журнал + ключ):

```bash
cd ~/NurRemoteSupport/deploy
tar czf ~/relay-backup-$(date +%F).tar.gz data
```

Каждую ночь автоматически (хранить 30 дней):

```bash
crontab -e
# добавить строку:
15 3 * * * cd ~/NurRemoteSupport/deploy && tar czf ~/relay-backup-$(date +\%F).tar.gz data && find ~ -name 'relay-backup-*.tar.gz' -mtime +30 -delete
```

Восстановление: остановить (`docker compose down`), распаковать архив в `deploy/`, `sudo chown -R 1654:1654 data`, `docker compose up -d`.

## Настройки (необязательно)

Все параметры — в [`../Server/appsettings.example.json`](../Server/appsettings.example.json). Менять переменными окружения в `docker-compose.yml` у `relay`, например `Relay__SessionTtlSeconds: "900"`. Основные: время жизни кода (15 мин), попыток PIN (5), размер сообщения (4 МБ), пинг/тишина (15/45 с), пределы частоты входа и подключений.

## Локальный запуск (разработка, Windows)

```powershell
cd NurRemoteSupport\Server
dotnet build -c Release
dotnet bin\Release\net8.0\Server.dll add-operator test "Тест"       # пароль спросит
dotnet bin\Release\net8.0\Server.dll --urls http://127.0.0.1:5080
# браузер: http://127.0.0.1:5080/   касса: ..\Tools\FakeHost  (--url ws://127.0.0.1:5080/ws/host)
```

Автопроверка и нагрузка (поднимают свой сервер на 127.0.0.1 с временной папкой, боевых данных не касаются):

```powershell
cd NurRemoteSupport\Tools\FakeHost;   dotnet build -c Release
cd ..\RelayTests;                     dotnet build -c Release
dotnet bin\Release\net8.0\RelayTests.dll e2e
dotnet bin\Release\net8.0\RelayTests.dll load --sessions 50 --seconds 30
```

## Если что-то не так

| Признак | Что проверить |
|---|---|
| `curl` не отвечает | `docker compose ps` (оба контейнера `running`?), порты 80/443 в ufw и в панели хостера |
| Сертификат не выдаётся | `docker compose logs caddy`; домен/sslip.io должен указывать на этот IP; порт 80 открыт |
| `relay` перезапускается | `docker compose logs relay`; права на папку `data` (`sudo chown 1654:1654 data`) |
| Оператор не входит | `docker compose run --rm relay list-operators`; 10 неверных паролей блокируют логин на 15 мин |
| Касса не получает код | адрес в кассе именно `wss://…/ws/host`; время на кассе верное (иначе TLS не сходится) |
