using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-06: В15 — часть класса MarketplaceView (разбор огромного файла на partial-файлы), логика не менялась; остальное — MarketplaceView.axaml.cs
public partial class MarketplaceView
{
    // 2026-10-05, владелец: «всё, что есть на тарифе «Стандарт», можно подключить и на «Старт», но абонплата за каждую услугу
    // добавляется по 400 сом + подключение (активация) 1500 сом». Карточки пакетов — только на «Старте» (на «Стандарте» всё
    // уже входит в тариф). Подключение — серийным ключом, как остальные доп. функции (LicenseKeys, slug = TariffGate.Packs).
    private readonly List<Border> _packCards = new();

    internal static IEnumerable<(string Slug, string Title, string Description)> StandardPacks()
    {
        yield return (TariffGate.Packs.Ai,
            Tr.T("ИИ-советник и ИИ в боте", "ИИ-кеңешчи жана боттогу ИИ", "AI advisor and the bot's AI", "Yapay zekâ danışmanı ve bottaki yapay zekâ", "SI maslahatchi va botdagi SI"),
            Tr.T("Советы по продажам и закупкам в программе владельца, ИИ в Telegram-боте, поиск фото товаров и поиск в интернете.",
                "Ээсинин программасында сатуу жана сатып алуу боюнча кеңештер, Telegram-боттогу ИИ, товардын сүрөтүн жана интернеттен издөө.",
                "Sales and purchasing advice in the owner app, AI in the Telegram bot, product photo search and web search.",
                "Sahip programında satış ve satın alma tavsiyeleri, Telegram botunda yapay zekâ, ürün fotoğrafı ve internet araması.",
                "Egasi dasturida sotuv va xarid bo'yicha maslahatlar, Telegram-botda SI, mahsulot rasmi va internetda qidirish."));
        yield return (TariffGate.Packs.Clients,
            Tr.T("Клиенты, воронка и WhatsApp", "Кардарлар, воронка жана WhatsApp", "Customers, funnel and WhatsApp", "Müşteriler, huni ve WhatsApp", "Mijozlar, voronka va WhatsApp"),
            Tr.T("База клиентов с долгами и скидками, воронка продаж, переписка в WhatsApp из программы.",
                "Карыздары жана арзандатуулары менен кардарлардын базасы, сатуу воронкасы, программадан WhatsApp'та жазышуу.",
                "A customer base with debts and discounts, a sales funnel, WhatsApp chats from the app.",
                "Borç ve indirimli müşteri tabanı, satış hunisi, programdan WhatsApp yazışmaları.",
                "Qarz va chegirmalar bilan mijozlar bazasi, savdo voronkasi, dasturdan WhatsApp yozishmalari."));
        yield return (TariffGate.Packs.SalesAnalytics,
            Tr.T("Продажи и аналитика", "Сатуулар жана аналитика", "Sales and analytics", "Satışlar ve analiz", "Sotuvlar va analitika"),
            Tr.T("Продажи, финансы, аналитика, ABC-анализ, прибыль и деньги, продажи в убыток, план продаж.",
                "Сатуулар, каржы, аналитика, ABC-анализ, пайда жана акча, зыян менен сатуулар, сатуу планы.",
                "Sales, finance, analytics, ABC analysis, profit and cash, sales at a loss, sales plan.",
                "Satışlar, finans, analiz, ABC analizi, kâr ve nakit, zararına satışlar, satış planı.",
                "Sotuvlar, moliya, analitika, ABC tahlil, foyda va pul, zarariga sotuvlar, sotuv rejasi."));
        yield return (TariffGate.Packs.Restock,
            Tr.T("Пополнение и сроки годности", "Толуктоо жана жарактуулук мөөнөттөрү", "Restock and expiry dates", "Stok yenileme ve son kullanma tarihleri", "To'ldirish va yaroqlilik muddatlari"),
            Tr.T("Что пора заказать и у каких товаров заканчивается срок годности.", "Эмнеге буйрутма берүү керек жана кайсы товарлардын мөөнөтү бүтүп баратат.",
                "What to reorder and which products are about to expire.", "Neyin sipariş edilmesi gerektiği ve hangi ürünlerin son kullanma tarihinin dolmak üzere olduğu.",
                "Nimani buyurtma qilish kerak va qaysi mahsulotlarning muddati tugayapti."));
        yield return (TariffGate.Packs.Salary,
            Tr.T("Зарплата сотрудников", "Кызматкерлердин эмгек акысы", "Staff salaries", "Personel maaşları", "Xodimlar ish haqi"),
            Tr.T("Расчёт зарплаты сотрудников по продажам и сменам.", "Кызматкерлердин эмгек акысын сатуу жана сменалар боюнча эсептөө.",
                "Staff salary calculation by sales and shifts.", "Satış ve vardiyalara göre personel maaşı hesaplama.", "Xodimlar ish haqini sotuv va smenalar bo'yicha hisoblash."));
        yield return (TariffGate.Packs.Debts,
            Tr.T("Долги и отложенные чеки", "Карыздар жана кийинкиге калтырылган чектер", "Debts and parked receipts", "Borçlar ve bekletilen fişler", "Qarzlar va kechiktirilgan cheklar"),
            Tr.T("Оплата долгов покупателей на кассе и отложенные чеки.", "Кассада сатып алуучулардын карызын төлөө жана кийинкиге калтырылган чектер.",
                "Customer debt payments at the till and parked receipts.", "Kasada müşteri borç ödemeleri ve bekletilen fişler.", "Kassada xaridorlar qarzini to'lash va kechiktirilgan cheklar."));
        yield return (TariffGate.Packs.Service,
            Tr.T("NurCRM, база знаний и поддержка", "NurCRM, билим базасы жана колдоо", "NurCRM, knowledge base and support", "NurCRM, bilgi bankası ve destek", "NurCRM, bilimlar bazasi va yordam"),
            Tr.T("Сайт NurCRM внутри программы, база знаний, удалённая тех. поддержка и журнал ошибок.",
                "Программанын ичинде NurCRM сайты, билим базасы, алыстан тех колдоо жана каталар журналы.",
                "The NurCRM site inside the app, a knowledge base, remote support and an error log.",
                "Program içinde NurCRM sitesi, bilgi bankası, uzaktan destek ve hata günlüğü.",
                "Dastur ichida NurCRM sayti, bilimlar bazasi, masofaviy texnik yordam va xatolar jurnali."));
    }

    private void RefreshStandardPackCards()
    {
        foreach (var card in _packCards)
            ExtrasWrapPanel.Children.Remove(card);
        _packCards.Clear();
        if (!TariffGate.IsStartTariff)
            return;

        foreach (var (slug, title, description) in StandardPacks())
        {
            var card = BuildPriceTagFeatureCard(title, description,
                openButtonLabel: Tr.T("✓ Подключено — разделы в меню", "✓ Туташтырылды — бөлүмдөр менюда", "✓ Connected — sections are in the menu",
                    "✓ Bağlandı — bölümler menüde", "✓ Ulandi — bo'limlar menyuda"),
                openAction: () => System.Threading.Tasks.Task.CompletedTask,
                unlocked: TariffGate.HasPack(slug),
                onUnlockClick: (_, _) => UnlockPack(slug, title),
                priceLabel: TariffGate.PackPriceLabel);
            _packCards.Add(card);
            ExtrasWrapPanel.Children.Add(card);
        }
    }

    private void UnlockPack(string slug, string title)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, title);
        if (serial == null)
            return;
        _ = UnlockPackAsync(owner, slug, title, serial);
    }

    /// <summary>2026-10-05, ТЗ ч.13, п. 2.3: ключ — сначала на сервер (POST users/company/features/activate/): там функция
    /// включается всей компании, на всех устройствах. Сервер ключа не знает или нет связи — проверка ключа кассой, как раньше.</summary>
    private async Task UnlockPackAsync(Window? owner, string slug, string title, string serial)
    {
        try
        {
            var data = await App.GetRequiredService<NurMarketKassa.Services.NurMarketApiClient>().RequestAsync(System.Net.Http.HttpMethod.Post,
                "api/users/company/features/activate/", new Dictionary<string, object?> { ["key"] = serial.Trim() }, null,
                CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var code = data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("code", out var c) ? c.GetString() : null;
            PosLogger.Log($"Маркетплейс: ключ принят сервером — функция компании {code}.", "INFO");
            await CompanyInfoService.RefreshAsync(App.AuthApi).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: сервер не включил функцию по ключу ({ex.Message}) — проверка ключа кассой.", "WARNING");
        }
        if (TariffGate.HasPack(slug) && !UserPreferences.Instance.UnlockedPacks.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            // Включено сервером для всей компании.
            TariffGate.RaisePacksChanged();
            RefreshStandardPackCards();
            PosAlertDialog.Show(owner, title,
                Tr.T("Функция подключена для всей компании — на всех кассах и в программе владельца.", "Функция бүт компанияга туташтырылды — бардык кассаларда жана ээсинин программасында.",
                     "Feature connected for the whole company — on every till and in the owner app.", "Özellik tüm şirket için bağlandı — tüm kasalarda ve sahip programında.",
                     "Funksiya butun kompaniya uchun ulandi — barcha kassalarda va egasi dasturida."),
                PosAlertKind.Success);
            return;
        }

        if (!TryValidateSerial(serial, slug, out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        var prefs = UserPreferences.Instance;
        if (!prefs.UnlockedPacks.Contains(slug, StringComparer.OrdinalIgnoreCase))
            prefs.UnlockedPacks.Add(slug);
        if (isPermanent)
            prefs.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor(slug);
        PosLogger.Log($"Маркетплейс: подключён пакет «Стандарта» {slug} ({(isPermanent ? "постоянный ключ" : "тестовый доступ")}).", "INFO");
        TariffGate.RaisePacksChanged();
        RefreshStandardPackCards();

        PosAlertDialog.Show(owner, title,
            Tr.T("Функция подключена. Разделы появились в меню.", "Функция туташтырылды. Бөлүмдөр менюда пайда болду.", "Feature connected. The sections are now in the menu.",
                 "Özellik bağlandı. Bölümler menüde göründü.", "Funksiya ulandi. Bo'limlar menyuda paydo bo'ldi."),
            PosAlertKind.Success);
    }

    private Border? _telegramBotCard;

    /// <summary>«Телеграм-бот владельца» (2026-09-22). Правило владельца: новые функции входят
    /// в «Стандарт», а на «Старт» покупаются здесь. Поэтому на «Стандарт» карточка показывает
    /// функцию уже открытой — платить второй раз за то, что входит в тариф, нельзя.</summary>
    private void RefreshTelegramBotCard()
    {
        if (_telegramBotCard is not null)
            ExtrasWrapPanel.Children.Remove(_telegramBotCard);

        _telegramBotCard = BuildPriceTagFeatureCard(
            title: Tr.T("Телеграм-бот владельца", "Ээсинин телеграм-боту", "Owner's Telegram bot",
                        "Sahibin Telegram botu", "Egasi uchun Telegram-bot"),
            description: Tr.T(
                "Сводка по закрытой смене приходит вам в Telegram. Бот отвечает на команды: выручка за день и неделю, топ товаров, что пора заказать, что заканчивается, список должников со ссылками для напоминания.",
                "Жабылган смена боюнча жыйынтык сизге Telegram'га келет. Бот буйруктарга жооп берет: күндүк жана жумалык түшүм, топ товарлар, эмнеге буйрутма берүү керек, эмне түгөнүп баратат, эскертүү шилтемелери менен карызкорлордун тизмеси.", "You get a summary of each closed shift in Telegram. The bot answers commands: revenue for the day and week, top products, what to reorder, what's running low, and a list of debtors with reminder links.", "Kapanan vardiyanın özeti size Telegram'dan gelir. Bot şu komutlara yanıt verir: günlük ve haftalık ciro, en çok satanlar, sipariş edilmesi gerekenler, tükenmek üzere olanlar, hatırlatma bağlantılı borçlu listesi.", "Yopilgan smena bo'yicha hisobot sizga Telegram'da keladi. Bot buyruqlarga javob beradi: kunlik va haftalik tushum, top mahsulotlar, nimani buyurtma qilish kerak, nima tugayapti, eslatma havolalari bilan qarzdorlar ro'yxati."),
            openButtonLabel: Tr.T("Настроить бота", "Ботту жөндөө", "Set up the bot",
                                  "Botu ayarla", "Botni sozlash"),
            openAction: () =>
            {
                var dialog = new NurMarketKassa.AvaloniaHost.Views.TelegramBotSetupWindow();
                return dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window);
            },
            unlocked: TariffGate.CanUseTelegramBot,
            onUnlockClick: TelegramBotUnlockButton_Click,
            priceLabel: TariffGate.PackPriceLabel);

        ExtrasWrapPanel.Children.Add(_telegramBotCard);
    }

    private void TelegramBotUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = Tr.T("Телеграм-бот владельца", "Ээсинин телеграм-боту", "Owner's Telegram bot",
                        "Sahibin Telegram botu", "Egasi uchun Telegram-bot");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "telegram", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.TelegramBotUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("telegram");
        RefreshTelegramBotCard();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.",
                 "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    private Border? _shiftAnalyticsCard;

    /// <summary>«Расширенные итоги смены» (2026-09-22): возвраты, списания, расход, оплата
    /// долгов и скидки в окне смены, X/Z-отчёте и сводке в Telegram.</summary>
    private void RefreshShiftAnalyticsCard()
    {
        if (_shiftAnalyticsCard is not null)
            ExtrasWrapPanel.Children.Remove(_shiftAnalyticsCard);

        _shiftAnalyticsCard = BuildPriceTagFeatureCard(
            title: Tr.T("Расширенные итоги смены", "Кеңейтилген смена жыйынтыктары",
                        "Extended shift totals", "Genişletilmiş vardiya toplamları",
                        "Kengaytirilgan smena yakunlari"),
            description: Tr.T(
                "Возвраты, списания, расход, оплата долгов и скидки (в том числе оплаченные бонусами) — в окне смены, в X/Z-отчёте и в сводке закрытия смены. Считаются самой кассой, поэтому доступны и без интернета.",
                "Кайтаруулар, эсептен чыгаруулар, чыгымдар, карыз төлөмдөрү жана арзандатуулар (бонус менен төлөнгөндөрү да) — смена терезесинде, X/Z-отчётто жана смена жабылгандагы жыйынтыкта. Аларды касса өзү эсептейт, ошондуктан интернетсиз да көрүнөт.", "Returns, write-offs, expenses, debt payments and discounts (including those paid with points) — in the shift window, the X/Z report and the shift closing summary. Calculated by the till itself, so they're available offline too.", "İadeler, stok düşümleri, giderler, borç ödemeleri ve indirimler (bonusla ödenenler dahil) — vardiya penceresinde, X/Z raporunda ve vardiya kapanış özetinde. Kasa bunları kendisi hesaplar, bu yüzden internetsiz de kullanılabilir.", "Qaytarishlar, hisobdan chiqarishlar, xarajatlar, qarz to'lovlari va chegirmalar (jumladan, bonuslar bilan to'langanlari) — smena oynasida, X/Z-hisobotda va smena yopilishi hisobotida. Kassaning o'zi hisoblaydi, shuning uchun internetsiz ham ishlaydi."),
            openButtonLabel: Tr.T("Открыть смены", "Сменаларды көрүү", "View shifts",
                                  "Vardiyaları aç", "Smenalar bo'limini ochish"),
            openAction: () =>
            {
                PosAlertDialog.Show(
                    TopLevel.GetTopLevel(this) as Window,
                    Tr.T("Где смотреть", "Кайдан көрсө болот", "Where to look",
                         "Nereye bakmalı", "Qayerda ko'rish mumkin"),
                    Tr.T("Итоги открываются из меню кассы: «Смены» → выбрать смену. Те же цифры печатаются в X/Z-отчёте и приходят в Telegram при закрытии смены.",
                         "Жыйынтыктар касса менюсунан ачылат: «Смена» → сменаны тандаңыз. Ошол эле сандар X/Z-отчётто басылат жана смена жабылганда Telegram'га келет.", "Totals open from the till menu: “Shifts” → select a shift. The same figures are printed in the X/Z report and sent to Telegram when the shift is closed.", "Özet kasa menüsünden açılır: «Vardiyalar» → vardiyayı seçin. Aynı rakamlar X/Z raporunda yazdırılır ve vardiya kapanınca Telegram'a gelir.", "Yakunlar kassa menyusidan ochiladi: «Smenalar» → smenani tanlang. Xuddi shu raqamlar X/Z-hisobotda chop etiladi va smena yopilganda Telegram'ga keladi."),
                    PosAlertKind.Info);
                return System.Threading.Tasks.Task.CompletedTask;
            },
            unlocked: TariffGate.CanUseShiftAnalytics,
            onUnlockClick: ShiftAnalyticsUnlockButton_Click,
            priceLabel: TariffGate.PackPriceLabel);

        ExtrasWrapPanel.Children.Add(_shiftAnalyticsCard);
    }

    private void ShiftAnalyticsUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = Tr.T("Расширенные итоги смены", "Кеңейтилген смена жыйынтыктары",
                        "Extended shift totals", "Genişletilmiş vardiya toplamları",
                        "Kengaytirilgan smena yakunlari");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "shiftstats", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.ShiftAnalyticsUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("shiftstats");
        RefreshShiftAnalyticsCard();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.",
                 "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    private Border? _labelEditorCard;

    /// <summary>Отдельный, независимый от «Склада» вход в редактор шаблона этикетки
    /// (2026-09-06, по просьбе пользователя — раньше редактор открывался только изнутри
    /// диалога печати этикетки для конкретного товара, Склад → строка товара → «Печать» →
    /// «Редактор этикетки»). 2026-09-07: раньше был бесплатным, по явной просьбе пользователя
    /// стал платным — тот же паттерн, что и у редактора/массовой печати ценников выше.</summary>
    private void RefreshLabelEditorCard()
    {
        if (_labelEditorCard is not null)
            ExtrasWrapPanel.Children.Remove(_labelEditorCard);

        _labelEditorCard = BuildPriceTagFeatureCard(
            title: Tr.T("🎨 Редактор этикетки", "🎨 Этикетка редактору", "🎨 Label editor", "🎨 Etiket düzenleyici", "🎨 Yorliq muharriri"),
            description: Tr.T(
                "Расположение штрих-кода, названия, цены, артикула, единицы измерения и магазина на этикетке — с перетаскиванием и настройкой каждого элемента.",
                "Штрих-коддун, аталыштын, баанын, артикулдун, өлчөм бирдигинин жана дүкөндүн этикеткадагы жайгашуусу — ар бир элементти сүйрөп жылдырууга жана жөндөөгө болот.", "Place the barcode, name, price, SKU, unit and store name on the label — drag and configure each element.", "Etiketteki barkod, ad, fiyat, stok kodu, ölçü birimi ve mağaza adının yerleşimi — her öğe sürüklenip ayrı ayrı ayarlanabilir.", "Yorliqdagi shtrix-kod, nom, narx, artikul, o'lchov birligi va do'kon nomining joylashuvi — har bir elementni sudrab ko'chirish va sozlash imkoni bilan."),
            openButtonLabel: Tr.T("🎨 Открыть редактор", "🎨 Редакторду ачуу", "🎨 Open editor", "🎨 Düzenleyiciyi aç", "🎨 Muharrirni ochish"),
            openAction: () =>
            {
                var editor = new LabelTemplateEditorDialog();
                return editor.ShowDialog(TopLevel.GetTopLevel(this) as Window);
            },
            unlocked: UserPreferences.Instance.LabelEditorUnlocked,
            onUnlockClick: LabelEditorUnlockButton_Click);

        ExtrasWrapPanel.Children.Add(_labelEditorCard);
    }

    private void LabelEditorUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "labeleditor", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.LabelEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("labeleditor");
        RefreshLabelEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Редактор этикетки активирован", "Этикетка редактору иштетилди", "Label editor activated", "Etiket düzenleyici etkinleştirildi", "Yorliq muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступен редактор этикеток — Маркетплейс → Доп. функции или Склад → Товары.",
                "Эми этикетка редактору жеткиликтүү — Маркетплейс → Кошумча функциялар же Кампа → Товарлар.", "The label editor is now available — Marketplace → Extras or Warehouse → Products.", "Etiket düzenleyici artık kullanılabilir — Mağaza → Ek özellikler veya Depo → Ürünler.", "Endi yorliq muharriri mavjud — Bozor → Qo'shimcha funksiyalar yoki Ombor → Mahsulotlar."),
            PosAlertKind.Success);
    }

    private Border? _languagePackCard;

    /// <summary>Карточка «Языковой пакет» (2026-09-07, по решению владельца): английский, турецкий
    /// и узбекский интерфейс стали платным дополнением, по умолчанию доступны русский и кыргызский.
    /// Словари встроены в кассу, поэтому активация серийным номером мгновенная (см. LanguagePackGate);
    /// платные языки появляются в Настройки → Экран сразу после активации.</summary>
    private void RefreshLanguagePackCard()
    {
        if (_languagePackCard is not null)
            ExtrasWrapPanel.Children.Remove(_languagePackCard);

        var unlocked = LanguagePackGate.IsUnlocked;

        var titleText = new TextBlock
        {
            Text = Tr.T("🌐 Языковой пакет", "🌐 Тил пакети", "🌐 Language pack", "🌐 Dil paketi", "🌐 Til paketi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = MakeLayoutBadge(
            unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
            unlocked ? "BrushAccent" : "BrushWarning");

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "English, Türkçe, O'zbekcha — дополнительные языки интерфейса кассы. По умолчанию доступны русский и кыргызский. После активации языки появятся в Настройки → Экран.",
                "English, Türkçe, O'zbekcha — кассанын кошумча интерфейс тилдери. Демейки боюнча орусча жана кыргызча жеткиликтүү. Активациядан кийин тилдер Жөндөөлөр → Экран бөлүмүндө пайда болот.", "English, Türkçe, O'zbekcha — additional interface languages for the till. Russian and Kyrgyz are available by default. After activation, the languages appear in Settings → Screen.", "English, Türkçe, O'zbekcha — kasa arayüzü için ek diller. Varsayılan olarak Rusça ve Kırgızca kullanılabilir. Etkinleştirmeden sonra diller Ayarlar → Ekran bölümünde görünür.", "English, Türkçe, O'zbekcha — kassa interfeysining qo'shimcha tillari. Standart holatda rus va qirg'iz tillari mavjud. Faollashtirilgach, tillar Sozlamalar → Ekran bo'limida paydo bo'ladi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var button = new Button
        {
            Classes = { unlocked ? "btn-secondary" : "btn-primary" },
            Content = unlocked
                ? Tr.T("✓ Активировано", "✓ Активдештирилди", "✓ Activated", "✓ Etkinleştirildi", "✓ Faollashtirildi")
                : Tr.T("🔒 Активировать (серийный номер)", "🔒 Активдештирүү (сериялык номер)", "🔒 Activate (serial number)", "🔒 Etkinleştir (seri numarası)", "🔒 Faollashtirish (seriya raqami)"),
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = !unlocked,
        };
        if (!unlocked)
            button.Click += LanguagePackUnlockButton_Click;

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);
        content.Children.Add(button);

        _languagePackCard = new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };

        ExtrasWrapPanel.Children.Add(_languagePackCard);
    }

    private void LanguagePackUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Языковой пакет", "Тил пакети", "Language pack", "Dil paketi", "Til paketi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "language", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.LanguagePackUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("language");

        PosAlertDialog.Show(
            owner,
            Tr.T("Языковой пакет активирован", "Тил пакети активдештирилди", "Language pack activated", "Dil paketi etkinleştirildi", "Til paketi faollashtirildi"),
            Tr.T("English, Türkçe и O'zbekcha теперь доступны в Настройки → Экран.",
                 "English, Türkçe жана O'zbekcha эми Жөндөөлөр → Экран бөлүмүндө жеткиликтүү.", "English, Türkçe and O'zbekcha are now available in Settings → Screen.", "English, Türkçe ve O'zbekcha artık Ayarlar → Ekran bölümünde kullanılabilir.", "English, Türkçe va O'zbekcha endi Sozlamalar → Ekran bo'limida mavjud."),
            PosAlertKind.Success);
        RefreshLanguagePackCard();
    }

    private Border? _layoutModeCard;

    /// <summary>Карточка-переключатель альтернативной раскладки главного экрана кассы "1С-стиль"
    /// (2026-09-06) — бесплатная, тот же UserPreferences.MainLayoutMode/App.ApplyMainLayoutMode,
    /// что и переключатель в Настройки → Экран (см. ScreenSettingsView) — просто ещё один вход
    /// сразу из Маркетплейса, без серийника.</summary>
    /// <summary>Карточка «Интерфейс кассира 1С» в «Доп. функциях» (2026-09-07, переработана):
    /// вместо длинного абзаца — миниатюра макета (строка сканера, крупная активная строка чека,
    /// список позиций, сводка с кнопкой «Оплата», ряд быстрых кнопок), бейджи «Бесплатно» и
    /// состояние, короткое описание и одна кнопка-переключатель. Это живой переключатель, а не
    /// paywall (см. App.ApplyMainLayoutMode / MainWindow.RefreshLayoutMode), поэтому не через
    /// BuildPriceTagFeatureCard.</summary>
    private void RefreshLayoutModeCard()
    {
        if (_layoutModeCard is not null)
            ExtrasWrapPanel.Children.Remove(_layoutModeCard);

        var isOneC = string.Equals(UserPreferences.Instance.MainLayoutMode, "onec", StringComparison.OrdinalIgnoreCase);

        var titleText = new TextBlock
        {
            Text = Tr.T("Интерфейс кассира 1С", "1С кассир интерфейси", "1C cashier interface", "1C kasiyer arayüzü", "1C kassir interfeysi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badges.Children.Add(MakeLayoutBadge(Tr.T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul"), "BrushSuccess"));
        badges.Children.Add(isOneC
            ? MakeLayoutBadge(Tr.T("✓ Включено", "✓ Күйгүзүлгөн", "✓ Enabled", "✓ Etkin", "✓ Yoqilgan"), "BrushAccent")
            : MakeLayoutBadge(Tr.T("Выключено", "Өчүрүлгөн", "Off", "Kapalı", "O'chirilgan"), "BrushBorder", mutedText: true));

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badges, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badges);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Крупная активная строка чека, список позиций и сводка с кнопкой «Оплата» справа — как в 1С «Рабочее место кассира». Товар добавляется только сканером.",
                "Чектин чоң активдүү сабы, позициялар тизмеси жана оң жакта «Төлөм» баскычы бар жыйынтык — 1С «Кассирдин жумуш орду» сыяктуу. Товар сканер менен гана кошулат.", "A large active receipt line, the item list and a summary with the “Payment” button on the right — like the 1C “Cashier workplace”. Products are added by scanner only.", "Büyük etkin fiş satırı, kalem listesi ve sağda «Ödeme» düğmeli özet — 1C'deki «Kasiyer çalışma yeri» gibi. Ürün yalnızca barkod okuyucuyla eklenir.", "Chekning katta faol qatori, pozitsiyalar ro'yxati va o'ngda «To'lash» tugmasi bilan yakuniy ma'lumot — 1C «Kassir ish joyi»dagidek. Mahsulot faqat skaner orqali qo'shiladi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var hintText = new TextBlock
        {
            Text = Tr.T("Переключается мгновенно, без перезапуска. Также: Настройки → Экран.",
                        "Дароо которулат, кайра ачуунун кереги жок. Ошондой эле: Жөндөөлөр → Экран.", "Switches instantly, no restart needed. Also in Settings → Screen.", "Yeniden başlatma gerekmeden anında geçiş yapılır. Ayrıca: Ayarlar → Ekran.", "Qayta ishga tushirmasdan darhol almashadi. Shuningdek: Sozlamalar → Ekran."),
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextMuted", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var toggleButton = new Button
        {
            Classes = { isOneC ? "btn-secondary" : "btn-primary" },
            Content = isOneC
                ? Tr.T("Вернуть обычный интерфейс", "Кадимки интерфейске кайтуу", "Switch back to the standard interface", "Standart arayüze dön", "Oddiy interfeysga qaytish")
                : Tr.T("Включить интерфейс 1С", "1С интерфейсин күйгүзүү", "Enable the 1C interface", "1C arayüzünü etkinleştir", "1C interfeysini yoqish"),
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        toggleButton.Click += (_, _) =>
        {
            App.ApplyMainLayoutMode(isOneC ? "standard" : "onec");
            RefreshLayoutModeCard();
            Store.Reload();
        };

        // 2026-09-28: видов кассы теперь шесть — отсюда одна ссылка на вкладку «Виды кассы».
        var allLayoutsButton = new Button
        {
            Classes = { "btn-secondary" },
            Content = Tr.T("Все виды кассы", "Кассанын бардык түрлөрү", "All till layouts", "Tüm kasa görünümleri", "Kassaning barcha ko'rinishlari"), // 2026-10-19, О7: без «→»
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        allLayoutsButton.Click += (_, _) => ShowLayoutsTab();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(headerRow);
        content.Children.Add(BuildOneCLayoutPreview(isOneC));
        content.Children.Add(descText);
        content.Children.Add(hintText);
        content.Children.Add(toggleButton);
        content.Children.Add(allLayoutsButton);

        _layoutModeCard = new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };

        ExtrasWrapPanel.Children.Add(_layoutModeCard);
    }

    private Border MakeLayoutBadge(string text, string brushKey, bool mutedText = false) => new()
    {
        Background = ThemeBrush($"{brushKey}Soft", Brushes.Transparent),
        BorderBrush = ThemeBrush(brushKey, Brushes.Gray),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush(mutedText ? "BrushTextSoft" : brushKey, Brushes.Gray),
        },
    };

    /// <summary>Миниатюра макета «1С» — те же цвета, что у настоящего OneCLayoutView (кремовая
    /// строка сканера, мятная активная строка, тан-подсветка выбранной позиции, зелёные пилюли
    /// быстрых действий, оранжевая «Оплата»), чтобы карточка показывала, ЧТО именно включится,
    /// а не описывала это словами. Цвета фиксированные: это макет светлой раскладки, не тема.</summary>
    private static Border BuildOneCLayoutPreview(bool active)
    {
        static Border Block(string hex, double height, double radius = 3) => new()
        {
            Background = new SolidColorBrush(Color.Parse(hex)),
            Height = height,
            CornerRadius = new CornerRadius(radius),
        };

        var scanner = Block("#FFF8EC", 14, 4);
        scanner.Child = new Border
        {
            Background = Brushes.White,
            Height = 8,
            Margin = new Thickness(3, 3, 70, 3),
            CornerRadius = new CornerRadius(2),
        };

        var receipt = new StackPanel { Spacing = 3 };
        receipt.Children.Add(Block("#E4F5E6", 22, 4));
        receipt.Children.Add(Block("#FDECC8", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));

        var summary = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var summaryLines = new[] { Block("#E5E7EB", 6), Block("#E5E7EB", 6), Block("#CBD5E1", 9) };
        for (var i = 0; i < summaryLines.Length; i++)
        {
            summaryLines[i].Margin = new Thickness(0, 0, 0, 4);
            Grid.SetRow(summaryLines[i], i);
            summary.Children.Add(summaryLines[i]);
        }
        var pay = Block("#F97316", 14, 4);
        Grid.SetRow(pay, 4);
        summary.Children.Add(pay);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,60") };
        Grid.SetColumn(summary, 1);
        body.Children.Add(receipt);
        body.Children.Add(summary);

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < 5; i++)
        {
            var pill = Block("#DCF3DE", 9, 4);
            pill.Width = 36;
            pills.Children.Add(pill);
        }

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(scanner);
        stack.Children.Add(body);
        stack.Children.Add(pills);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? "#F97316" : "#E2E8F0")),
            BorderThickness = new Thickness(active ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = stack,
        };
    }

    private Border BuildPriceTagFeatureCard(string title, string description, string openButtonLabel, Func<System.Threading.Tasks.Task> openAction, bool unlocked, EventHandler<RoutedEventArgs> onUnlockClick,
        string? priceLabel = null)
    {
        var titleText = new TextBlock
        {
            Text = title,
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = description,
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = priceLabel ?? Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += onUnlockClick;
            content.Children.Add(unlockButton);
        }
        else
        {
            var openButton = new Button
            {
                Classes = { "btn-primary" },
                Content = openButtonLabel,
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            openButton.Click += async (_, _) => await openAction().ConfigureAwait(true);
            content.Children.Add(openButton);
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void PriceTagEditorUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Редактор ценников", "Баа белгиси редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorliqlari muharriri"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "pricetag", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.PriceTagEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("pricetag");
        RefreshPriceTagEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Редактор ценников активирован", "Баа белгиси редактору иштетилди", "Price tag editor activated", "Fiyat etiketi düzenleyici etkinleştirildi", "Narx yorliqlari muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступна печать ценника для одного товара: Склад → Товары → кнопка «💲 Ценник» на карточке товара.",
                "Эми бир товардын баа белгисин басып чыгарса болот: Кампа → Товарлар → товар карточкасындагы «💲 Баа белгиси» баскычы.", "Single-product price tag printing is now available: Warehouse → Products → the “💲 Price tag” button on the product card.", "Artık tek ürün için fiyat etiketi yazdırabilirsiniz: Depo → Ürünler → ürün kartındaki «💲 Fiyat etiketi» düğmesi.", "Endi bitta mahsulotning narx yorlig'ini chop etish mumkin: Ombor → Mahsulotlar → mahsulot kartasidagi «💲 Narx yorlig'i» tugmasi."),
            PosAlertKind.Success);
    }

    /// <summary>Независимая от PriceTagEditorUnlockButton_Click доп. услуга (2026-09-04, см.
    /// UserPreferences.BulkPriceTagUnlocked) — тот же серийник (единый мастер-ключ), но своя
    /// активация и свой флаг, чтобы включение одной карточки не включало вторую.</summary>
    private void BulkPriceTagUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Массовая печать ценников", "Баа белгилерин топтоп басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi baskısı", "Narx yorliqlarini ommaviy chop etish"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "bulktag", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("bulktag");
        RefreshPriceTagEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Массовая печать ценников активирована", "Баа белгилерин топтоп басып чыгаруу иштетилди", "Bulk price tag printing activated", "Toplu fiyat etiketi baskısı etkinleştirildi", "Narx yorliqlarini ommaviy chop etish faollashtirildi"),
            Tr.T(
                "Теперь доступна кнопка «🏷 Массовая печать ценников» в Склад → Товары — выберите нужные товары и распечатайте ценники сразу для всех.",
                "Эми Кампа → Товарлар бетинде «🏷 Баа белгилерин топтоп басып чыгаруу» баскычы бар — керектүү товарларды тандап, баарынын баа белгилерин бир жолу басып чыгарыңыз.", "The “🏷 Bulk price tag printing” button is now available in Warehouse → Products — select the products you need and print price tags for all of them at once.", "Depo → Ürünler bölümünde artık «🏷 Toplu fiyat etiketi yazdır» düğmesi var — ürünleri seçip hepsinin fiyat etiketini tek seferde yazdırın.", "Endi Ombor → Mahsulotlar bo'limida «🏷 Narx yorliqlarini ommaviy chop etish» tugmasi bor — kerakli mahsulotlarni tanlang va hammasining narx yorlig'ini bir yo'la chop eting."),
            PosAlertKind.Success);
    }
}
