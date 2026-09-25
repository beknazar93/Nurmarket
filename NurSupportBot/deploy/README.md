# Бот поддержки NurCRM — запуск на сервере

Бот работает круглосуточно на арендованном сервере (VPS) и не зависит от касс клиентов.
Видео инструкций хранит сам Telegram, поэтому серверу хватает самого простого тарифа:
1 ядро, 1 ГБ памяти, Ubuntu 22.04 или 24.04.

## 1. Подготовка в Telegram

1. В @BotFather: `/newbot` → имя (например, «NurCRM Помощник») → username (например, `nurcrm_help_bot`).
   Скопируйте токен. **Это не тот бот, что в кассе.**
2. Создайте группу поддержки, добавьте туда операторов и бота, сделайте бота администратором.
   Режим приватности бота менять не нужно: ответы операторов на его сообщения и команды он видит и так.
3. Узнайте свой Telegram ID: напишите новому боту `/id` (после запуска) — или заранее через @userinfobot.

## 2. Сборка (на компьютере разработчика)

```
cd NurSupportBot
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out-linux
```

В папке `out-linux` появится файл `NurSupportBot`, папка `seed` и `appsettings.example.json`.

## 3. Установка на сервер

```
sudo useradd --system --create-home --home-dir /opt/nursupportbot nurbot
# скопировать содержимое out-linux в /opt/nursupportbot (scp, WinSCP и т.п.)
sudo cp /opt/nursupportbot/appsettings.example.json /opt/nursupportbot/appsettings.json
sudo nano /opt/nursupportbot/appsettings.json      # вписать BotToken и AdminIds
sudo chown -R nurbot:nurbot /opt/nursupportbot
sudo chmod 600 /opt/nursupportbot/appsettings.json
sudo chmod +x /opt/nursupportbot/NurSupportBot
sudo cp nursupportbot.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now nursupportbot
sudo journalctl -u nursupportbot -f                # журнал бота
```

## 4. Первые шаги после запуска

1. В группе поддержки напишите `/setsupport` (от имени администратора бота) — обращения клиентов начнут приходить туда.
2. Боту в личку: `/admin` → «☎️ Контакты поддержки» — телефон и Telegram оператора для клиентов.
3. `/admin` → «📚 Разделы и инструкции» — проверьте черновики текстов, к каждой инструкции нажмите «🎬 Видео» и отправьте ролик.

## Как работают операторы

- Обращение приходит в группу: клиент, компания, тариф, раздел, какую инструкцию смотрел, текст и скриншоты.
- Чтобы ответить — **ответьте (Reply) на сообщение обращения**. Ответ уйдёт клиенту в бота, клиент может ответить обратно.
- Закрыть — кнопка «✅ Закрыть обращение» или `/close` ответом на сообщение обращения.

## Данные и резервные копии

- `/opt/nursupportbot/data/bot.db` — инструкции, пользователи, обращения, статистика.
- `/opt/nursupportbot/data/token.key` — ключ шифрования токенов NurCRM. Без него сохранённые входы клиентов не расшифруются (клиенты просто войдут заново).
- Копия базы инструкций в любой момент: боту `/export`. Загрузить исправленную: отправить JSON-файл с подписью `/import`.

## Обновление бота

```
sudo systemctl stop nursupportbot
# заменить файл NurSupportBot новой сборкой (папку data не трогать)
sudo systemctl start nursupportbot
```
