using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-01, ТЗ «Сайт — темы, Telegram-бот и ИИ-ассистент» (пакет Т4) и ответ Умара по ТЗ 01.11: группа «Telegram» рядом с
/// «Корзина и заказ». Ключи дизайна `cart.telegram.enabled`, `cart.telegram.label` {ru,ky,en,uz} (сверено с реальным showcase/design/ 01.11: ключ `label`, не `button_text`; языки витрины ru, ky, en, uz, kk — турецкого нет), `cart.telegram.placement[]`
/// уходят в черновик (design/draft/) как все поля редактора; покупатели видят кнопки после «Опубликовать». Публичный блок `telegram`
/// сервер включает (`enabled=true`) только при серверном боте с рабочим вебхуком. Токен бота в этом файле не читается и никуда не
/// отправляется: состояние бота — только признаки token_set / webhook_ok из GET telegram-bot/settings/.</summary>
public sealed partial class SiteEditorWindow
{
    private const string TgEnabledPath = "cart.telegram.enabled";
    private const string TgTextPath = "cart.telegram.label";
    private const string TgPlacementPath = "cart.telegram.placement";

    // 2026-11-01: языки надписи — как у сервера витрины (ru, ky, en, uz, kk); tr сервер не знает, kk у NBS не включён.
    private static readonly string[] TgLangCodes = { "ru", "ky", "en", "uz" };

    /// <summary>Состояние бота на сервере, спрошенное меньше минуты назад (редактор перестраивается при каждой загрузке — не спрашиваем заново).</summary>
    private (DateTime At, ServerBotSettings? Settings, string? Error)? _tgBot;

    private void BuildTelegramGroup()
    {
        var group = Group(T("Корзина и заказ: Telegram", "Себет жана заказ: Telegram", "Cart and order: Telegram", "Sepet ve sipariş: Telegram", "Savat va buyurtma: Telegram"));
        var box = new StackPanel { Spacing = 12 };
        group.Children.Add(box);
        box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        _ = FillTelegramAsync(box, force: false);
    }

    private static string TelegramDefaultText(string code) => code switch
    {
        "ky" => "Telegram аркылуу заказ берүү",
        "en" => "Order in Telegram",
        "uz" => "Telegram orqali buyurtma berish",
        _ => "Заказать в Telegram",
    };

    private static string SiteLangName(string code) => code switch
    {
        "ky" => "Кыргызча",
        "en" => "English",
        "uz" => "Oʻzbekcha",
        _ => "Русский",
    };

    /// <summary>«Сервер NurCRM недоступен» + «Повторить» вместо «Скоро»: сбой запроса не значит, что функции нет.</summary>
    private void ShowSiteUnavailable(StackPanel box, Exception error, Func<Task> retry)
    {
        box.Children.Add(Hint(ServerOutageMonitor.FriendlyError(error)));
        var again = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += async (_, _) =>
        {
            box.Children.Clear();
            box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            await retry().ConfigureAwait(true);
        };
        box.Children.Add(again);
    }

    private async Task FillTelegramAsync(StackPanel box, bool force)
    {
        try
        {
            // 2026-11-01 (контрольный аудит): «функции ещё нет» (ответ получен, признака нет) и «сервер недоступен» (сбой запроса) — разные сообщения.
            var (features, capError) = await ShowcaseCapabilities.TryLoadAsync(_api, force: force).ConfigureAwait(true);
            box.Children.Clear();
            if (capError != null)
            {
                ShowSiteUnavailable(box, capError, () => FillTelegramAsync(box, force: true));
                return;
            }
            var supported = features.Contains(ShowcaseCapabilities.Telegram);
            if (!supported)
            {
                box.Children.Add(Hint(T("Скоро: ждём сервер NurCRM. Кнопки «Заказать в Telegram» на сайте появятся, когда сервер включит их для вашего магазина.",
                    "Жакында: NurCRM серверин күтөбүз. Сайттагы «Telegram аркылуу заказ берүү» баскычтары сервер дүкөнүңүз үчүн күйгүзгөндө чыгат.",
                    "Coming soon: waiting for the NurCRM server. The “Order in Telegram” buttons will appear on the website once the server enables them for your shop.",
                    "Yakında: NurCRM sunucusu bekleniyor. «Telegram'dan sipariş ver» düğmeleri sunucu mağazanız için açınca sitede görünecek.",
                    "Tez orada: NurCRM serveri kutilmoqda. «Telegram orqali buyurtma berish» tugmalari server do'koningiz uchun yoqqanda saytda chiqadi.")));
                return;
            }

            // Состояние бота (токен не запрашиваем и не показываем — только признаки сервера).
            ServerBotSettings? bot = null;
            string? botError = null;
            if (!force && _tgBot is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1))
            {
                bot = cached.Settings;
                botError = cached.Error;
            }
            else
            {
                try
                {
                    bot = await App.GetRequiredService<ServerTelegramBotApi>().GetSettingsAsync().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    botError = ServerOutageMonitor.FriendlyError(ex);
                    PosLogger.Log($"Редактор сайта: состояние Telegram-бота не получено ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
                }
                _tgBot = (DateTime.UtcNow, bot, botError);
            }

            var local = !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotToken);
            var canEnable = false;
            string stateText;
            var stateOk = false;
            if (botError != null)
                stateText = T("Состояние бота не удалось узнать: ", "Боттун абалын билүү мүмкүн болгон жок: ", "Couldn't get the bot state: ", "Botun durumu alınamadı: ", "Bot holatini bilib bo'lmadi: ") + botError;
            else if (bot is { IsServerMode: true, WebhookOk: true })
            {
                canEnable = true;
                stateOk = true;
                stateText = T("Бот работает на сервере NurCRM и отвечает круглосуточно", "Бот NurCRM серверинде иштейт жана түн-күнү жооп берет",
                    "The bot runs on the NurCRM server and answers around the clock", "Bot NurCRM sunucusunda çalışıyor ve gece gündüz yanıt veriyor",
                    "Bot NurCRM serverida ishlaydi va kechayu kunduz javob beradi")
                    + (string.IsNullOrWhiteSpace(bot.BotUsername) ? "" : $" (@{bot.BotUsername})");
            }
            else if (bot is { IsServerMode: true })
                stateText = T("Бот перенесён на сервер, но Telegram не подтвердил связь (вебхук) — пока она не заработает, кнопки на сайте не появятся.",
                    "Бот серверге көчүрүлдү, бирок Telegram байланышты (вебхук) ырастаган жок — ал иштегенче сайтта баскычтар чыкпайт.",
                    "The bot is on the server, but Telegram hasn't confirmed the link (webhook) — buttons won't appear on the website until it works.",
                    "Bot sunucuya taşındı ama Telegram bağlantıyı (webhook) onaylamadı — çalışana kadar sitede düğmeler görünmez.",
                    "Bot serverga o'tkazildi, lekin Telegram aloqani (vebhuk) tasdiqlamadi — u ishlamaguncha saytda tugmalar chiqmaydi.");
            else if (local)
                stateText = T("Бот работает на кассе: когда касса выключена, он молчит. Кнопку на сайте показываем только при боте на сервере — перенесите бота на сервер.",
                    "Бот кассада иштейт: касса өчүк болсо, ал үнсүз калат. Сайттагы баскыч бот серверде болгондо гана чыгат — ботту серверге көчүрүңүз.",
                    "The bot runs on the till: when the till is off, it is silent. The website button is shown only when the bot is on the server — move the bot to the server.",
                    "Bot kasada çalışıyor: kasa kapalıyken sessiz kalır. Sitedeki düğme yalnızca bot sunucudayken gösterilir — botu sunucuya taşıyın.",
                    "Bot kassada ishlaydi: kassa o'chiq bo'lsa, u jim turadi. Saytdagi tugma faqat bot serverda bo'lganda ko'rsatiladi — botni serverga o'tkazing.");
            else
                stateText = T("Telegram-бот не подключён. Подключите бота и перенесите его на сервер — тогда на сайте появится кнопка «Заказать в Telegram».",
                    "Telegram-бот туташтырылган эмес. Ботту туташтырып, серверге көчүрүңүз — ошондо сайтта «Telegram аркылуу заказ берүү» баскычы чыгат.",
                    "The Telegram bot is not connected. Connect the bot and move it to the server — then the “Order in Telegram” button appears on the website.",
                    "Telegram botu bağlı değil. Botu bağlayıp sunucuya taşıyın — sitede «Telegram'dan sipariş ver» düğmesi görünür.",
                    "Telegram-bot ulanmagan. Botni ulab, serverga o'tkazing — saytda «Telegram orqali buyurtma berish» tugmasi paydo bo'ladi.");

            var state = new TextBlock { Text = (stateOk ? "✓ " : "⚠ ") + stateText, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Use(state, TextBlock.ForegroundProperty, stateOk ? "BrushSuccess" : "BrushWarning");
            box.Children.Add(state);

            // Переключатель. Выключить можно всегда; включить — только при серверном боте с рабочим вебхуком.
            var enabledNow = Flag(TgEnabledPath);
            var check = new CheckBox
            {
                Content = T("Кнопки Telegram на сайте", "Сайттагы Telegram баскычтары", "Telegram buttons on the website", "Sitedeki Telegram düğmeleri", "Saytdagi Telegram tugmalari"),
                IsChecked = enabledNow,
                IsEnabled = canEnable || enabledNow,
            };
            Use(check, CheckBox.ForegroundProperty, "BrushText");
            box.Children.Add(check);
            if (!canEnable && enabledNow)
                box.Children.Add(Hint(T("В черновике кнопки включены, но бот не на сервере — сайт их не покажет. Выключите или перенесите бота на сервер.",
                    "Долбоордо баскычтар күйгүзүлгөн, бирок бот серверде эмес — сайт аларды көрсөтпөйт. Өчүрүңүз же ботту серверге көчүрүңүз.",
                    "The buttons are on in the draft, but the bot isn't on the server — the website won't show them. Turn them off or move the bot to the server.",
                    "Taslakta düğmeler açık ama bot sunucuda değil — site göstermez. Kapatın veya botu sunucuya taşıyın.",
                    "Qoralamada tugmalar yoqilgan, lekin bot serverda emas — sayt ularni ko'rsatmaydi. O'chiring yoki botni serverga o'tkazing.")));

            // Где показывать.
            var places = new (string Code, string Label)[]
            {
                ("cart", T("В корзине", "Себетте", "In the cart", "Sepette", "Savatda")),
                ("product", T("На карточке товара", "Товар карточкасында", "On the product card", "Ürün kartında", "Mahsulot kartochkasida")),
                ("floating", T("Плавающая кнопка", "Калкып турган баскыч", "Floating button", "Yüzen düğme", "Suzuvchi tugma")),
            };
            var placeField = Field(box, T("Где показывать", "Кайда көрсөтүү", "Where to show", "Nerede gösterilsin", "Qayerda ko'rsatish"));
            var placeWrap = new WrapPanel();
            List<string> CurrentPlaces() => (Get(TgPlacementPath) as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>();
            void RenderPlaces()
            {
                placeWrap.Children.Clear();
                var current = CurrentPlaces();
                foreach (var (code, label) in places)
                {
                    var chip = Chip(label, current.Contains(code));
                    chip.Click += (_, _) =>
                    {
                        var list = CurrentPlaces(); // чужие значения (например, header) не теряем
                        if (!list.Remove(code))
                            list.Add(code);
                        Set(TgPlacementPath, new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()));
                        RenderPlaces();
                    };
                    placeWrap.Children.Add(chip);
                }
            }
            RenderPlaces();
            placeField.Children.Add(placeWrap);

            check.IsCheckedChanged += (_, _) =>
            {
                var on = check.IsChecked == true;
                Set(TgEnabledPath, JsonValue.Create(on));
                if (on && CurrentPlaces().Count == 0)
                {
                    // Включили, а мест нет — по умолчанию корзина и карточка (можно поменять ниже).
                    Set(TgPlacementPath, new JsonArray((JsonNode?)JsonValue.Create("cart"), JsonValue.Create("product")));
                    RenderPlaces();
                }
            };

            // Надпись на кнопке — ru, ky, en, uz (языки витрины); пусто — сервер возьмёт «Заказать в Telegram».
            var textField = Field(box, T("Надпись на кнопке (пусто — «Заказать в Telegram»)", "Баскычтагы жазуу (бош — «Telegram аркылуу заказ берүү»)",
                "Button text (empty — “Order in Telegram”)", "Düğme metni (boş — «Telegram'dan sipariş ver»)", "Tugma yozuvi (bo'sh — «Telegram orqali buyurtma berish»)"));
            foreach (var code in TgLangCodes)
            {
                var current = (Get(TgTextPath) as JsonObject)?[code]?.ToString() ?? "";
                var tb = new TextBox { Text = current, Watermark = TelegramDefaultText(code), MinHeight = 38, MaxLength = 60, VerticalContentAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(Hint(SiteLangName(code)));
                row.Children.Add(tb);
                // 2026-11-01 (контрольный аудит В2-10): очищенное поле — удаляем ключ языка (null), а не шлём "" — сервер подставит «по умолчанию».
                tb.TextChanged += (_, _) => Set($"{TgTextPath}.{code}", (tb.Text ?? "").Trim() is { Length: > 0 } txt ? JsonValue.Create(txt) : null);
                textField.Children.Add(row);
            }

            // Что видит покупатель сейчас (опубликованный вид) и перенос корзины.
            var pub = string.IsNullOrWhiteSpace(_slug) ? null : await App.GetRequiredService<ShowcaseApiService>().GetPublicTelegramBlockAsync(_slug!).ConfigureAwait(true);
            if (pub is { } p)
                box.Children.Add(Hint(p.Enabled
                    ? T("На сайте сейчас: кнопка Telegram показывается", "Сайтта азыр: Telegram баскычы көрсөтүлүп турат", "On the website now: the Telegram button is shown", "Sitede şu an: Telegram düğmesi gösteriliyor", "Saytda hozir: Telegram tugmasi ko'rsatilmoqda")
                      + (string.IsNullOrWhiteSpace(p.Label) ? "" : $" «{p.Label}»") + (string.IsNullOrWhiteSpace(p.Url) ? "" : " · " + p.Url)
                    : T("На сайте сейчас: кнопки Telegram нет (вы ещё не нажали «Опубликовать» или сервер её отключил — например, бот не на сервере).",
                        "Сайтта азыр: Telegram баскычы жок («Жарыялоо» басылган жок же сервер өчүрдү — мисалы, бот серверде эмес).",
                        "On the website now: there is no Telegram button (you haven't pressed “Publish” yet, or the server turned it off — e.g. the bot is not on the server).",
                        "Sitede şu an: Telegram düğmesi yok («Yayınla»ya basmadınız veya sunucu kapattı — örneğin bot sunucuda değil).",
                        "Saytda hozir: Telegram tugmasi yo'q («E'lon qilish» bosilmagan yoki server o'chirgan — masalan, bot serverda emas).")));
            if (await ShowcaseCapabilities.HasAsync(_api, ShowcaseCapabilities.CartLink).ConfigureAwait(true))
                box.Children.Add(Hint(T("Корзину с сайта кнопка переносит в бота: покупатель нажимает «Оформить» в Telegram, и заказ приходит в «Заказы с сайта» с пометкой «Telegram».",
                    "Сайттагы себетти баскыч ботко өткөрөт: кардар Telegram'да «Заказ берүү» басат, заказ «Сайттан заказдар» бөлүмүнө «Telegram» белгиси менен келет.",
                    "The button carries the website cart into the bot: the customer presses “Checkout” in Telegram, and the order arrives in “Website orders” marked “Telegram”.",
                    "Düğme site sepetini bota taşır: müşteri Telegram'da «Siparişi tamamla»ya basar, sipariş «Web sitesi siparişleri»ne «Telegram» işaretiyle gelir.",
                    "Tugma sayt savatini botga o'tkazadi: xaridor Telegram'da «Rasmiylashtirish»ni bosadi, buyurtma «Saytdan buyurtmalar»ga «Telegram» belgisi bilan keladi.")));

            var buttons = new WrapPanel();
            var setup = UiKit.Ghost(this, T("Настроить бота", "Ботту жөндөө", "Set up the bot", "Botu ayarla", "Botni sozlash"));
            setup.Margin = new Thickness(0, 0, 8, 6);
            setup.Click += async (_, _) =>
            {
                await new TelegramBotSetupWindow().ShowDialog(this).ConfigureAwait(true);
                _tgBot = null;
                box.Children.Clear();
                box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
                await FillTelegramAsync(box, force: true).ConfigureAwait(true);
            };
            var recheck = UiKit.Ghost(this, T("Проверить снова", "Кайра текшерүү", "Check again", "Tekrar kontrol et", "Qayta tekshirish"));
            recheck.Margin = new Thickness(0, 0, 8, 6);
            recheck.Click += async (_, _) =>
            {
                _tgBot = null;
                box.Children.Clear();
                box.Children.Add(Hint(T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
                await ShowcaseCapabilities.LoadAsync(_api, CancellationToken.None, force: true).ConfigureAwait(true);
                await FillTelegramAsync(box, force: true).ConfigureAwait(true);
            };
            buttons.Children.Add(setup);
            buttons.Children.Add(recheck);
            box.Children.Add(buttons);
            LockInputs(box);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            box.Children.Clear();
            box.Children.Add(Hint(T("Не загрузилось: ", "Жүктөлгөн жок: ", "Didn't load: ", "Yüklenmedi: ", "Yuklanmadi: ") + ServerOutageMonitor.FriendlyError(ex)));
            PosLogger.Log($"Редактор сайта: группа Telegram не построена ({SensitiveDataRedactor.Redact(ex.Message)}).", "WARNING");
        }
    }
}
