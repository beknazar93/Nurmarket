using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-10-04, владелец: «это программа для бонусов: при скане, если клиента нет в базе — добавить,
/// если есть — дать бонусы»; формат и правила — по ТЗ разработчика приложения NurCRM. Покупатель показывает
/// на кассе QR «NURCRM» + 12 цифр телефона (<see cref="ClientQrCode"/>). Касса находит клиента NurCRM по
/// телефону (<see cref="ClientPhoneLookup"/>) и выбирает его в ТЕКУЩЕМ чеке — окно оплаты откроется уже с
/// этим клиентом, как после «Выбрать клиента», и бонусы начислятся обычным путём при оплате
/// (CreditOrRedeemLoyaltyPoints). Клиента нет — молча не заводим: кассиру открывается существующее окно
/// «Новый клиент» с подставленным телефоном, ФИО вводит он сам. Продажу скан клиента не останавливает.</summary>
public sealed partial class BasketPanelViewModel
{
    /// <summary>2026-10-04, стенд «сбои сервера»: 8 → 3 с. Скан QR идёт внутри обработки скана (чек занят),
    /// и при молчащем сервере кассир ждал 8 с; живой сервер находит клиента по телефону за доли секунды.
    /// Не успел — как без связи: клиент из памяти кассы.</summary>
    private static readonly TimeSpan ClientQrLookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Поиск клиентов по телефону; помнит найденных — без связи с сервером постоянного
    /// покупателя можно выбрать только из этой памяти (до закрытия окна кассы).</summary>
    private ClientPhoneLookup? _clientPhoneLookup;

    /// <summary>Клиента с телефоном из QR в базе нет: окно кассы предлагает завести его существующим окном
    /// «Выбрать клиента → Новый клиент» с подставленным телефоном (вызывается на UI-потоке). Возвращает
    /// выбранного/заведённого клиента или null, если кассир закрыл окно.</summary>
    public Func<string, Task<ClientOption?>>? OfferNewClientFromQr { get; set; }

    private static string ClientsUnavailableOfflineText => Tr.T("Клиенты недоступны офлайн.",
        "Клиенттер офлайн режимде жеткиликсиз.",
        "Clients are unavailable offline.",
        "Müşteriler çevrimdışı modda kullanılamaz.",
        "Mijozlar oflayn rejimda mavjud emas.");

    /// <summary>Скан начинается с «NURCRM» — это QR клиента, а не товар.</summary>
    private async Task HandleClientQrAsync(ClientQrCode.Result qr)
    {
        switch (qr.Kind)
        {
            case ClientQrKind.Phone:
                await AttachClientFromQrAsync(qr.Phone!, qr.National!).ConfigureAwait(false);
                return;

            case ClientQrKind.Token:
                // 2026-10-05: сервер NurCRM обменивает одноразовый код на клиента (POST clients/resolve-qr/).
                await AttachClientFromTokenAsync(qr.Token!).ConfigureAwait(false);
                return;

            default:
                PosLogger.Log("Скан: префикс NURCRM есть, но после него не 12 цифр телефона 996… — QR клиента не распознан.", "CART");
                await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                    "QR клиента не распознан: после «NURCRM» должно быть 12 цифр номера (996…). Попросите клиента открыть код в приложении заново.",
                    "Кардардын QR коду таанылган жок: «NURCRM» дан кийин номердин 12 цифрасы (996…) болушу керек. Кардардан кодду тиркемеде кайра ачууну сураныңыз.",
                    "Customer QR not recognized: “NURCRM” must be followed by the 12-digit phone number (996…). Ask the customer to reopen the code in the app.",
                    "Müşteri QR'ı tanınmadı: «NURCRM»den sonra 12 haneli telefon numarası (996…) olmalı. Müşteriden kodu uygulamada yeniden açmasını isteyin.",
                    "Mijoz QR kodi tanilmadi: «NURCRM» dan keyin 12 xonali raqam (996…) bo'lishi kerak. Mijozdan kodni ilovada qayta ochishini so'rang.")).ConfigureAwait(false);
                return;
        }
    }

    private async Task AttachClientFromQrAsync(string phone, string national)
    {
        var masked = ClientQrCode.MaskPhone(national);
        PosLogger.Log($"Скан: распознан QR клиента NurCRM {masked}.", "CART");

        if (_clientsApi is null)
        {
            await RunOnUiThreadAsync(() => CartMessage = ClientsUnavailableOfflineText).ConfigureAwait(false);
            return;
        }

        var lookup = _clientPhoneLookup ??= new ClientPhoneLookup(_clientsApi);
        // Чек, к которому относится скан: пока идёт запрос, кассир может переключить вкладку.
        var sessionId = _activeSessionId;
        ClientPhoneMatch? match = null;
        var fromMemory = false;
        var online = !OfflineModeHelper.SellLocally;

        if (online)
        {
            try
            {
                using var findCts = new CancellationTokenSource(ClientQrLookupTimeout);
                match = await lookup.FindAsync(national, findCts.Token).ConfigureAwait(false);
            }
            // 2026-10-04: 5xx/429/HTML вместо JSON — сервер не ответил по существу: как без связи
            // (память кассы, catch ниже), а не «сервер отказал».
            catch (ApiException ex) when (!ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode))
            {
                // Сервер ответил отказом — связь есть, память тут не поможет.
                PosLogger.Log($"QR клиента {masked}: сервер отказал ({ex.StatusCode}): {ex.Message}", "WARNING");
                await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                    $"Клиента {phone} найти не удалось: {ex.Message} Продажу можно продолжить без клиента.",
                    $"{phone} кардарын табуу мүмкүн болгон жок: {ex.Message} Сатууну кардарсыз улантсаңыз болот.",
                    $"Could not find customer {phone}: {ex.Message} You can continue the sale without a customer.",
                    $"{phone} müşterisi bulunamadı: {ex.Message} Satışa müşterisiz devam edebilirsiniz.",
                    $"{phone} mijozini topib bo'lmadi: {ex.Message} Sotuvni mijozsiz davom ettirish mumkin.")).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // Нет сети или сервер не ответил вовремя — дальше как без интернета.
                PosLogger.Log($"QR клиента {masked}: нет ответа сервера ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                online = false;
            }
        }

        if (!online)
        {
            // Своей таблицы клиентов у кассы нет — только найденные за время работы окна кассы.
            match = lookup.FindRemembered(national);
            fromMemory = match != null;
            if (match is null)
            {
                PosLogger.Log($"QR клиента {masked}: без связи, в памяти кассы клиента нет.", "CART");
                await RunOnUiThreadAsync(() => CartMessage = ClientsUnavailableOfflineText).ConfigureAwait(false);
                return;
            }
        }

        ClientOption? client;
        var created = false;
        if (match != null)
        {
            client = ToClientOption(match);
        }
        else
        {
            // По ТЗ — молча не заводим: окно «Новый клиент» с подставленным телефоном, ФИО вводит кассир.
            PosLogger.Log($"QR клиента {masked}: в базе нет — кассиру предложено завести клиента.", "CART");
            client = null;
            if (OfferNewClientFromQr is { } offer)
                await _dispatcher.InvokeAsync(async () => client = await offer(phone).ConfigureAwait(true)).ConfigureAwait(false);

            if (client is null || string.IsNullOrWhiteSpace(client.Id))
            {
                PosLogger.Log($"QR клиента {masked}: кассир не завёл клиента.", "CART");
                await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                    $"Клиента {phone} нет в базе, он не добавлен — продажа без клиента.",
                    $"{phone} кардары базада жок жана кошулган жок — сатуу кардарсыз.",
                    $"Customer {phone} is not in the database and was not added — selling without a customer.",
                    $"{phone} müşterisi veritabanında yok ve eklenmedi — müşterisiz satış.",
                    $"{phone} mijozi bazada yo'q va qo'shilmadi — sotuv mijozsiz.")).ConfigureAwait(false);
                return;
            }

            // Кассир мог и выбрать из списка другого клиента — «новый» только если телефон тот же.
            created = ClientQrCode.NationalDigits(client.Phone) == national;
            lookup.Remember(client.Id, client.Name, client.Phone);
        }

        var loyaltyOn = UserPreferences.Instance.LoyaltyEnabled;
        var balance = loyaltyOn ? (await ServerLoyalty.GetBalanceAsync(client.Id).ConfigureAwait(false) ?? ClientLoyaltyStore.GetBalance(client.Id)) : 0;
        var message = BuildClientQrMessage(client, phone, created, fromMemory, loyaltyOn, balance);
        PosLogger.Log($"QR клиента {masked}: клиент выбран в чеке (новый={created}, из памяти без связи={fromMemory}).", "CART");

        var attached = client;
        await RunOnUiThreadAsync(() =>
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? GetActiveSession();
            if (session != null)
                session.Client = attached;
            CartMessage = message;
        }).ConfigureAwait(false);
    }

    /// <summary>2026-10-05: одноразовый QR «NURCRMT…» из приложения покупателя → сервер возвращает клиента компании (нет —
    /// заводит из профиля приложения) → клиент в текущем чеке, как при QR с телефоном. Без связи код не проверить.</summary>
    private async Task AttachClientFromTokenAsync(string token)
    {
        PosLogger.Log("Скан: QR клиента NurCRM с одноразовым кодом (NURCRMT…) — спрашиваю сервер.", "CART");
        if (_clientsApi is null || OfflineModeHelper.SellLocally)
        {
            await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                "Без интернета одноразовый QR клиента не проверить. Продажу можно продолжить без клиента.",
                "Интернетсиз кардардын бир жолку QR кодун текшерүүгө болбойт. Сатууну кардарсыз улантсаңыз болот.",
                "A one-time customer QR can't be checked offline. You can continue the sale without a customer.",
                "Tek kullanımlık müşteri QR'ı çevrimdışı kontrol edilemez. Satışa müşterisiz devam edebilirsiniz.",
                "Mijozning bir martalik QR kodini internetsiz tekshirib bo'lmaydi. Sotuvni mijozsiz davom ettirish mumkin.")).ConfigureAwait(false);
            return;
        }

        var sessionId = _activeSessionId;
        System.Text.Json.JsonElement data;
        try
        {
            using var cts = new CancellationTokenSource(ClientQrLookupTimeout + TimeSpan.FromSeconds(3));
            data = await _clientsApi.ResolveQrAsync(ClientQrCode.TokenPrefix + token, cts.Token).ConfigureAwait(false);
        }
        catch (ApiException ex) when (!ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode))
        {
            PosLogger.Log($"QR клиента (одноразовый): сервер отказал ({ex.StatusCode}): {ex.Message}", "WARNING");
            await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                // Сервер сам пишет «попросите клиента обновить QR» — свою такую же фразу не добавляем.
                $"QR клиента не принят: {ex.Message} Продажу можно продолжить без клиента.",
                $"Кардардын QR коду кабыл алынган жок: {ex.Message} Сатып алуучудан тиркемеде кодду жаңыртууну сураныңыз. Сатууну кардарсыз улантсаңыз болот.",
                $"Customer QR not accepted: {ex.Message} Ask the customer to refresh the code in the app. You can continue the sale without a customer.",
                $"Müşteri QR'ı kabul edilmedi: {ex.Message} Müşteriden uygulamada kodu yenilemesini isteyin. Satışa müşterisiz devam edebilirsiniz.",
                $"Mijoz QR kodi qabul qilinmadi: {ex.Message} Xaridordan ilovada kodni yangilashini so'rang. Sotuvni mijozsiz davom ettirish mumkin.")).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"QR клиента (одноразовый): нет ответа сервера ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                "Сервер не ответил — одноразовый QR клиента не проверен. Продажу можно продолжить без клиента.",
                "Сервер жооп берген жок — кардардын бир жолку QR коду текшерилген жок. Сатууну кардарсыз улантсаңыз болот.",
                "The server didn't respond — the one-time customer QR wasn't checked. You can continue the sale without a customer.",
                "Sunucu yanıt vermedi — tek kullanımlık müşteri QR'ı kontrol edilmedi. Satışa müşterisiz devam edebilirsiniz.",
                "Server javob bermadi — mijozning bir martalik QR kodi tekshirilmadi. Sotuvni mijozsiz davom ettirish mumkin.")).ConfigureAwait(false);
            return;
        }

        // Ответ: клиент целиком или {"client": {...}, "created": true}.
        var c = data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("client", out var inner)
                && inner.ValueKind == System.Text.Json.JsonValueKind.Object ? inner : data;
        static string? Str(System.Text.Json.JsonElement e, string name) =>
            e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
                ? v.ToString() : null;
        var id = Str(c, "id") ?? Str(c, "client_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            PosLogger.Log("QR клиента (одноразовый): в ответе сервера нет клиента.", "WARNING");
            await RunOnUiThreadAsync(() => CartMessage = Tr.T(
                "Сервер не вернул клиента по QR. Продажу можно продолжить без клиента.",
                "Сервер QR боюнча кардарды кайтарган жок. Сатууну кардарсыз улантсаңыз болот.",
                "The server returned no customer for the QR. You can continue the sale without a customer.",
                "Sunucu QR için müşteri döndürmedi. Satışa müşterisiz devam edebilirsiniz.",
                "Server QR bo'yicha mijozni qaytarmadi. Sotuvni mijozsiz davom ettirish mumkin.")).ConfigureAwait(false);
            return;
        }
        var name = Str(c, "full_name") ?? Str(c, "name") ?? "";
        var phone = Str(c, "phone") ?? "";
        var created = data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("created", out var cr) && cr.ValueKind == System.Text.Json.JsonValueKind.True;
        var client = ToClientOption(new ClientPhoneMatch(id, name, phone));
        (_clientPhoneLookup ??= new ClientPhoneLookup(_clientsApi)).Remember(id, name, phone);

        var loyaltyOn = UserPreferences.Instance.LoyaltyEnabled;
        var balance = loyaltyOn ? (await ServerLoyalty.GetBalanceAsync(id).ConfigureAwait(false) ?? ClientLoyaltyStore.GetBalance(id)) : 0;
        var message = BuildClientQrMessage(client, string.IsNullOrWhiteSpace(phone) ? name : phone, created, fromMemory: false, loyaltyOn, balance);
        PosLogger.Log($"QR клиента (одноразовый): клиент выбран в чеке (новый={created}, {ClientQrCode.MaskPhone(ClientQrCode.NationalDigits(phone))}).", "CART");
        await RunOnUiThreadAsync(() =>
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? GetActiveSession();
            if (session != null)
                session.Client = client;
            CartMessage = message;
        }).ConfigureAwait(false);
    }

    /// <summary>Клиент вкладки чека снимается, когда чек оплачен или очищен — следующий покупатель
    /// не должен получить чужие бонусы.</summary>
    private void ForgetReceiptClient(string? sessionId)
    {
        var session = string.IsNullOrEmpty(sessionId)
            ? GetActiveSession()
            : _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
            session.Client = null;
    }

    private static ClientOption ToClientOption(ClientPhoneMatch match)
    {
        // Так же, как строит строку клиента окно оплаты (CheckoutViewModel.ToClientOption).
        var display = string.IsNullOrWhiteSpace(match.Phone) ? match.FullName : $"{match.FullName} · {match.Phone}";
        return new ClientOption { Id = match.Id, DisplayName = display, Name = match.FullName, Phone = match.Phone };
    }

    private static string BuildClientQrMessage(ClientOption client, string phone, bool created, bool fromMemory, bool loyaltyOn, double balance)
    {
        var who = string.IsNullOrWhiteSpace(client.Name) ? phone : client.Name;
        if (!string.IsNullOrWhiteSpace(client.Phone) && !who.Contains(client.Phone, StringComparison.Ordinal))
            who = $"{who} · {client.Phone}";

        var text = created
            ? Tr.T($"Новый клиент {who} добавлен и выбран в чеке.",
                $"Жаңы кардар {who} кошулуп, чекте тандалды.",
                $"New customer {who} added and selected for the receipt.",
                $"Yeni müşteri {who} eklendi ve fişte seçildi.",
                $"Yangi mijoz {who} qo'shildi va chekda tanlandi.")
            : loyaltyOn
                ? Tr.T($"Клиент {who} — бонусов доступно: {balance:0.##} сом. Выбран в чеке.",
                    $"Кардар {who} — жеткиликтүү бонус: {balance:0.##} сом. Чекте тандалды.",
                    $"Customer {who} — points available: {balance:0.##} som. Selected for the receipt.",
                    $"Müşteri {who} — kullanılabilir puan: {balance:0.##} som. Fişte seçildi.",
                    $"Mijoz {who} — mavjud bonuslar: {balance:0.##} so'm. Chekda tanlandi.")
                : Tr.T($"Клиент {who} выбран в чеке.",
                    $"Кардар {who} чекте тандалды.",
                    $"Customer {who} selected for the receipt.",
                    $"Müşteri {who} fişte seçildi.",
                    $"Mijoz {who} chekda tanlandi.");

        if (fromMemory)
            text = Tr.T("Без связи: клиент из памяти кассы. ", "Байланыш жок: кардар кассанын эсинен алынды. ",
                "Offline: customer taken from the till's memory. ", "Bağlantı yok: müşteri kasanın belleğinden alındı. ",
                "Aloqa yo'q: mijoz kassa xotirasidan olindi. ") + text;

        if (!loyaltyOn)
            text += Tr.T(" Бонусы выключены — включаются в Маркетплейс → Доп. функции → «Программа лояльности».",
                " Бонустар өчүк — «Маркетплейс → Кошумча функциялар → Лоялдуулук программасы» бөлүмүндө күйгүзүлөт.",
                " Bonus points are off — turn them on in Marketplace → Extras → “Loyalty program”.",
                " Puanlar kapalı — «Pazar yeri → Ek özellikler → Sadakat programı» bölümünden açılır.",
                " Bonuslar o'chirilgan — «Marketpleys → Qo'shimcha funksiyalar → Sodiqlik dasturi» bo'limida yoqiladi.");
        else if (created)
            text += Tr.T(" Бонусы начислятся при оплате.", " Бонус төлөгөндө кошулат.", " Points will be earned at payment.",
                " Puanlar ödemede kazanılacak.", " Bonuslar to'lovda beriladi.");

        return text;
    }
}
