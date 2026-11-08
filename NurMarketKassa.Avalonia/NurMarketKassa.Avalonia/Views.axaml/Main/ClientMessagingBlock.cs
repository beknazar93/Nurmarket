using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-08, ответ сервера Умара п. 31 и п. 35 (Б-30, Б-74): блоки карточки клиента «Согласия на сообщения» (service / marketing, язык, кто и когда менял)
/// и «Каналы» (только чтение). Согласие пишется PATCH main/clients/{id}/consents/ только с изменёнными полями. Включить согласие можно лишь после
/// подтверждения «клиент дал согласие» (один раз на сохранение). Нет связи — переключатели неактивны с подсказкой; правка при ошибке не теряется.
/// Сервер без метода согласий (404) — блок прячется; каналы прячутся при 403/404.</summary>
public sealed class ClientMessagingBlock : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ClientMessagingApi? _api;
    private readonly StackPanel _consentsBox = new() { Spacing = 8 };
    private readonly CheckBox _service = new() { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _marketing = new() { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _language = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _updated = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save = new() { Classes = { "sec-outline" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _retry = new() { Classes = { "sec-outline" }, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly Border _channelsBox = new() { IsVisible = false };
    private readonly TextBlock _channelsText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    private string? _clientId;
    private ClientConsents? _loaded;
    private bool _busy;
    private bool _suppress;
    private CancellationTokenSource? _cts;

    /// <summary>Для стенда снимков и тестов: состояние без обращения к серверу.</summary>
    public ClientMessagingBlock() : this(App.AppHost?.Services.GetService(typeof(ClientMessagingApi)) as ClientMessagingApi)
    {
    }

    public ClientMessagingBlock(ClientMessagingApi? api)
    {
        _api = api;
        IsVisible = false;
        _retry.Content = T("Повторить", "Кайра аракет кылуу", "Retry", "Tekrar dene", "Qayta urinish");
        _save.Content = T("Сохранить согласия", "Макулдуктарды сактоо", "Save consents", "Onayları kaydet", "Roziliklarni saqlash");
        Avalonia.Automation.AutomationProperties.SetName(_save, _save.Content as string ?? "");
        Avalonia.Automation.AutomationProperties.SetName(_retry, _retry.Content as string ?? "");
        Avalonia.Automation.AutomationProperties.SetName(_language, T("Язык сообщений", "Билдирүүлөрдүн тили", "Message language", "Mesaj dili", "Xabarlar tili"));
        foreach (var (code, name) in new[] { ("ru", "Русский"), ("ky", "Кыргызча"), ("en", "English"), ("tr", "Türkçe"), ("uz", "O'zbekcha") })
            _language.Items.Add(new ComboBoxItem { Tag = code, Content = name });

        Bind(_hint, "BrushTextMuted");
        Bind(_updated, "BrushTextSoft");
        Bind(_error, "BrushDanger");
        Bind(_channelsText, "BrushText");

        _service.IsCheckedChanged += (_, _) => OnEdited();
        _marketing.IsCheckedChanged += (_, _) => OnEdited();
        _language.SelectionChanged += (_, _) => OnEdited();
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _retry.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        _consentsBox.Children.Add(Label(T("Согласия на сообщения", "Билдирүүлөргө макулдуктар", "Message consents", "Mesaj onayları", "Xabarlarga roziliklar")));
        _consentsBox.Children.Add(_hint);
        _consentsBox.Children.Add(Row(T("Сервисные сообщения (чек, долг, заказ)", "Кызматтык билдирүүлөр (чек, карыз, заказ)", "Service messages (receipt, debt, order)",
            "Hizmet mesajları (fiş, borç, sipariş)", "Xizmat xabarlari (chek, qarz, buyurtma)"), _service));
        _consentsBox.Children.Add(Row(T("Реклама и акции", "Жарнама жана акциялар", "Promotions and offers", "Reklam ve kampanyalar", "Reklama va aksiyalar"), _marketing));
        var langRow = new StackPanel { Spacing = 4 };
        langRow.Children.Add(Soft(T("Язык сообщений клиенту", "Кардарга билдирүүлөрдүн тили", "Language of messages to the client", "Müşteriye mesaj dili", "Mijozga xabarlar tili")));
        langRow.Children.Add(_language);
        _consentsBox.Children.Add(langRow);
        _consentsBox.Children.Add(_updated);
        _consentsBox.Children.Add(_error);
        _consentsBox.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _save, _retry } });

        var channels = new StackPanel { Spacing = 4 };
        channels.Children.Add(Label(T("Каналы клиента", "Кардардын каналдары", "Client channels", "Müşteri kanalları", "Mijoz kanallari")));
        channels.Children.Add(_channelsText);
        _channelsBox.Child = channels;

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(Card(_consentsBox));
        root.Children.Add(Card(_channelsBox, wrap: false));
        Content = root;
    }

    private static Border Card(Control child, bool wrap = true)
    {
        if (!wrap)
        {
            // Каналы: своя рамка, видимость задаёт сам блок (_channelsBox).
            var b = (Border)child;
            b.Padding = new Thickness(14, 12);
            b.CornerRadius = new CornerRadius(10);
            b.BorderThickness = new Thickness(1);
            b.Bind(Border.BackgroundProperty, b.GetResourceObservable("BrushSurfaceSubtle"));
            b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("BrushBorder"));
            return b;
        }
        var card = new Border { Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = child };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushSurfaceSubtle"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
        return card;
    }

    private static void Bind(TextBlock t, string brush) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Bind(t, "BrushText");
        return t;
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Bind(t, "BrushTextSoft");
        return t;
    }

    private static Grid Row(string text, CheckBox toggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Bind(label, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(toggle, text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(label);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>Подпись «кто менял» по полю source сервера.</summary>
    public static string SourceText(string? source) => (source ?? "").ToLowerInvariant() switch
    {
        "cashier" => T("кассиром", "кассир тарабынан", "by a cashier", "kasiyer tarafından", "kassir tomonidan"),
        "bot" or "telegram" or "start" => T("клиентом в боте", "кардар тарабынан ботто", "by the client in the bot", "müşteri tarafından botta", "mijoz tomonidan botda"),
        "stop" => T("клиентом (СТОП в боте)", "кардар тарабынан (ботто СТОП)", "by the client (STOP in the bot)", "müşteri tarafından (botta DUR)", "mijoz tomonidan (botda STOP)"),
        "" => "",
        var s => s,
    };

    /// <summary>Строка «Обновлено 08.11.2026 14:30 · кассиром» или «Согласие ещё не давалось».</summary>
    public static string UpdatedText(ClientConsents c)
    {
        if (c.UpdatedAt is not { } at)
            return T("Согласие ещё не давалось.", "Макулдук азырынча берилген эмес.", "No consent given yet.", "Henüz onay verilmedi.", "Rozilik hali berilmagan.");
        var by = SourceText(c.Source);
        return T("Обновлено ", "Жаңыртылды: ", "Updated ", "Güncellendi: ", "Yangilandi: ") + at.LocalDateTime.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            + (by.Length > 0 ? " · " + by : "");
    }

    /// <summary>Подсказка кассиру: у прежних клиентов согласий нет, сервер им не пишет.</summary>
    public static string NoConsentHint => T(
        "Пока клиент не согласился, сообщения ему не уходят. У прежних клиентов согласий нет: клиент сам нажимает /start в боте магазина, либо вы включаете согласие здесь — только если клиент действительно согласен.",
        "Кардар макул болмогончо, ага билдирүү жөнөтүлбөйт. Мурунку кардарларда макулдук жок: кардар дүкөндүн ботунда өзү /start басат, же сиз бул жерде макулдукту күйгүзөсүз — кардар чындап макул болсо гана.",
        "Until the client agrees, no messages are sent to them. Existing clients have no consent: the client presses /start in the shop bot, or you switch consent on here — only if the client really agreed.",
        "Müşteri onay vermeden ona mesaj gitmez. Eski müşterilerde onay yoktur: müşteri mağaza botunda /start'a basar ya da burada onayı siz açarsınız — yalnızca müşteri gerçekten kabul ettiyse.",
        "Mijoz rozilik bermaguncha unga xabar yuborilmaydi. Avvalgi mijozlarda rozilik yo'q: mijoz do'kon botida /start bosadi yoki siz shu yerda rozilikni yoqasiz — faqat mijoz haqiqatan rozi bo'lsa.");

    private bool IsOfflineNow => ServerOutageMonitor.IsOutage || OfflineModeHelper.UseLocalOperations;

    /// <summary>Показать блок для клиента (null — скрыть и отменить запросы).</summary>
    public void SetClient(string? clientId)
    {
        _cts?.Cancel();
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
        _loaded = null;
        _channelsBox.IsVisible = false;
        _error.IsVisible = false;
        _retry.IsVisible = false;
        IsVisible = _clientId != null && _api != null;
        if (!IsVisible)
            return;
        ApplyState(ClientConsents.Empty, loaded: false);
        _hint.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        _ = LoadAsync();
    }

    /// <summary>Для стенда снимков и сквозных тестов: показать блок с готовыми данными, без сервера.</summary>
    public void ShowPreview(ClientConsents consents, IReadOnlyList<ClientChannel>? channels)
    {
        IsVisible = true;
        _loaded = consents;
        ApplyState(consents, loaded: true);
        ShowChannels(channels);
    }

    private async Task LoadAsync()
    {
        var id = _clientId;
        if (id == null || _api == null)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _error.IsVisible = false;
        _retry.IsVisible = false;
        if (IsOfflineNow)
        {
            ApplyState(ClientConsents.Empty, loaded: false);
            _hint.Text = OfflineText;
            return;
        }
        try
        {
            var consentsTask = _api.GetConsentsAsync(id, cts.Token);
            var channelsTask = _api.GetChannelsAsync(id, cts.Token);
            ClientConsents consents;
            try
            {
                consents = await consentsTask.ConfigureAwait(true);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                if (_clientId == id) IsVisible = false; // старый сервер без согласий
                return;
            }
            if (cts.IsCancellationRequested || _clientId != id)
                return;
            _loaded = consents;
            ApplyState(consents, loaded: true);
            try
            {
                var channels = await channelsTask.ConfigureAwait(true);
                if (cts.IsCancellationRequested || _clientId != id)
                    return;
                ShowChannels(channels);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _channelsBox.IsVisible = false; // каналы — необязательное дополнение
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && _clientId != id)
        {
            // клиент сменился — результат не нужен
        }
        catch (Exception ex)
        {
            if (_clientId != id)
                return;
            ApplyState(ClientConsents.Empty, loaded: false);
            _hint.Text = ex is ApiException { StatusCode: 403 }
                ? T("Нет прав смотреть согласия клиента.", "Кардардын макулдуктарын көрүүгө укук жок.", "No permission to view the client's consents.", "Müşteri onaylarını görme yetkiniz yok.", "Mijoz roziliklarini ko'rish huquqi yo'q.")
                : OfflineText;
            if (ex is not ApiException { StatusCode: 403 })
            {
                _error.Text = ServerOutageMonitor.FriendlyError(ex);
                _error.IsVisible = true;
                _retry.IsVisible = true;
            }
        }
    }

    private static string OfflineText => T("Нет связи с сервером — согласия сейчас недоступны. Они откроются, когда связь появится.",
        "Сервер менен байланыш жок — макулдуктар азыр жеткиликсиз. Байланыш пайда болгондо ачылат.",
        "No connection to the server — consents are unavailable for now. They will open when the connection is back.",
        "Sunucu ile bağlantı yok — onaylar şu an kullanılamıyor. Bağlantı gelince açılır.",
        "Server bilan aloqa yo'q — roziliklar hozircha mavjud emas. Aloqa tiklanganda ochiladi.");

    private void ShowChannels(IReadOnlyList<ClientChannel>? channels)
    {
        if (channels is null)
        {
            _channelsBox.IsVisible = false; // нет прав (403) или сервер без метода (404)
            return;
        }
        _channelsBox.IsVisible = true;
        _channelsText.Text = channels.Count == 0
            ? T("Каналов связи пока не привязано.", "Байланыш каналдары азырынча байланышкан эмес.", "No channels linked yet.", "Henüz bağlı kanal yok.", "Hozircha ulangan kanallar yo'q.")
            : string.Join("\n", channels.Select(c => c.Title.Length > 0 ? c.KindDisplay + " — " + c.Title : c.KindDisplay));
    }

    private void ApplyState(ClientConsents c, bool loaded)
    {
        _suppress = true;
        try
        {
            _service.IsChecked = c.Service;
            _marketing.IsChecked = c.Marketing;
            _language.SelectedIndex = Array.FindIndex(ClientConsents.Languages, l => l == c.Language);
            _updated.Text = loaded ? UpdatedText(c) : "";
            _hint.Text = loaded && !c.Service && !c.Marketing ? NoConsentHint : loaded ? "" : _hint.Text;
            _hint.IsVisible = !string.IsNullOrEmpty(_hint.Text);
            var enabled = loaded && !IsOfflineNow;
            foreach (Control x in new Control[] { _service, _marketing, _language })
                x.IsEnabled = enabled && !_busy;
            RefreshSave();
        }
        finally
        {
            _suppress = false;
        }
    }

    private ClientConsents Edited()
    {
        var lang = (_language.SelectedItem as ComboBoxItem)?.Tag as string ?? _loaded?.Language ?? "";
        return (_loaded ?? ClientConsents.Empty) with { Service = _service.IsChecked == true, Marketing = _marketing.IsChecked == true, Language = lang };
    }

    private void OnEdited()
    {
        if (_suppress)
            return;
        _error.IsVisible = false;
        RefreshSave();
    }

    private void RefreshSave()
    {
        var dirty = _loaded != null && ClientConsents.BuildPatch(_loaded, Edited()) != null;
        _save.IsEnabled = dirty && !_busy && !IsOfflineNow;
        _save.IsVisible = _loaded != null;
        ToolTip.SetTip(_save, IsOfflineNow ? OfflineText : null);
    }

    private async Task SaveAsync()
    {
        var id = _clientId;
        if (id == null || _api == null || _loaded is not { } before || _busy)
            return;
        var after = Edited();
        if (ClientConsents.BuildPatch(before, after) == null)
            return;
        if (IsOfflineNow)
        {
            RefreshSave();
            return;
        }
        // Включить согласие кассир может только подтвердив, что клиент его дал (один вопрос на сохранение).
        if ((after.Service && !before.Service) || (after.Marketing && !before.Marketing))
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var ok = await NurMarketKassa.AvaloniaHost.Services.PosDialogs.ConfirmYesNoModalAsync(owner,
                T("Подтверждаете, что клиент сам дал согласие получать эти сообщения? В журнале останется пометка «кассиром».",
                    "Кардар бул билдирүүлөрдү алууга өзү макул болгонун ырастайсызбы? Журналда «кассир тарабынан» деген белги калат.",
                    "Do you confirm the client agreed to receive these messages? The log will keep the mark “by a cashier”.",
                    "Müşterinin bu mesajları almayı kabul ettiğini onaylıyor musunuz? Günlükte «kasiyer tarafından» işareti kalır.",
                    "Mijoz bu xabarlarni olishga rozi bo'lganini tasdiqlaysizmi? Jurnalda «kassir tomonidan» belgisi qoladi."),
                T("Согласие клиента", "Кардардын макулдугу", "Client consent", "Müşteri onayı", "Mijoz roziligi"),
                confirmText: T("Подтверждаю, что клиент дал согласие", "Кардар макулдук бергенин ырастайм", "I confirm the client gave consent",
                    "Müşterinin onay verdiğini onaylıyorum", "Mijoz rozilik berganini tasdiqlayman")).ConfigureAwait(true);
            if (!ok || _clientId != id)
                return;
        }
        _busy = true;
        _error.IsVisible = false;
        ApplyBusy();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var saved = await _api.PatchConsentsAsync(id, before, after, cts.Token).ConfigureAwait(true);
            if (_clientId != id)
                return;
            _loaded = saved;
            _busy = false;
            ApplyState(saved, loaded: true);
        }
        catch (Exception ex)
        {
            // Правку не теряем: переключатели остаются как выставил кассир, ошибка — под ними.
            _busy = false;
            if (_clientId == id)
            {
                _error.Text = (ex is ApiException { StatusCode: 403 }
                    ? T("Нет прав менять согласия клиента.", "Кардардын макулдуктарын өзгөртүүгө укук жок.", "No permission to change the client's consents.", "Müşteri onaylarını değiştirme yetkiniz yok.", "Mijoz roziliklarini o'zgartirish huquqi yo'q.")
                    : T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerTelegramBotApi.DescribeFields(ex))
                    + T(" Ваши переключатели сохранены на экране — нажмите «Сохранить согласия» ещё раз.", " Өчүргүчтөрүңүз экранда калды — «Макулдуктарды сактоо» баскычын кайра басыңыз.",
                        " Your switches are kept on screen — press “Save consents” again.", " Düğmeleriniz ekranda duruyor — «Onayları kaydet»e tekrar basın.", " O'zgartirishlaringiz ekranda saqlandi — «Roziliklarni saqlash»ni qayta bosing.");
                _error.IsVisible = true;
                ApplyBusy();
                RefreshSave();
            }
            PosLogger.Log($"Согласия клиента не сохранены: {ex.GetType().Name}", "WARNING");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ApplyBusy()
    {
        foreach (Control x in new Control[] { _service, _marketing, _language })
            x.IsEnabled = !_busy && _loaded != null && !IsOfflineNow;
        RefreshSave();
    }
}
