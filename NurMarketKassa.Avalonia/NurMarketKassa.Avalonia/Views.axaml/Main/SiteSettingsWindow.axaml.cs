using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Настройки сайта (2026-09-29, владелец: «настройки сайта тоже»). Сервер NurCRM отдаёт
/// для витрины два поля — их и можно менять здесь, с той же проверкой, что на сайте:
/// • адрес витрины — slug в https://nurcrm.kg/catalog/{slug} (латиница, цифры, дефис, 3–50; занятость
///   проверяет сервер, GET api/users/company/check-slug/);
/// • номер WhatsApp, на который витрина отправляет заказ покупателя (phones_howcase). Витрина берёт
///   из поля все цифры подряд, поэтому номер без кода страны 996 уводит заказ не туда — окно об этом
///   предупреждает и предлагает исправить.
/// Сохраняются только изменённые поля (PATCH api/users/settings/company/); название и адрес магазина
/// окно не отправляет никогда.</summary>
public partial class SiteSettingsWindow : Window, IOwnerSection
{
    private enum SlugCheck
    {
        None,
        Checking,
        Available,
        Taken,
        Failed,
    }

    private readonly ShowcaseApiService _api;
    private readonly DispatcherTimer _slugDebounce;
    private readonly CancellationTokenSource _lifetime = new();
    private ShowcaseSettings? _settings;
    private int? _productCount;
    private bool _loading;
    private bool _saving;
    private bool _suppressEvents;
    private bool _offline;
    private string? _accessError;
    private SlugCheck _slugCheck;
    private string _slugCheckedFor = "";
    private string? _slugCheckMessage;
    private CancellationTokenSource? _slugCts;

    public SiteSettingsWindow()
    {
        InitializeComponent();
        _api = App.GetRequiredService<ShowcaseApiService>();
        _slugDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _slugDebounce.Tick += async (_, _) =>
        {
            _slugDebounce.Stop();
            await CheckSlugAsync().ConfigureAwait(true);
        };
        Closed += (_, _) =>
        {
            _slugDebounce.Stop();
            _slugCts?.Cancel();
            _lifetime.Cancel();
        };
        EscapeKey.Attach(this);
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplyTexts();
        Render();
        await LoadAsync().ConfigureAwait(true);
    }

    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        RefreshButton.Margin = new Thickness(0);
        SubtitleText.Margin = new Thickness(0, 0, 16, 0);
        if (SubtitleText.Parent is Control subtitlePanel)
            subtitlePanel.VerticalAlignment = VerticalAlignment.Center;
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    private void ApplyTexts()
    {
        Title = Tr.T("Настройки сайта", "Сайттын жөндөөлөрү", "Website settings", "Web sitesi ayarları", "Sayt sozlamalari");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T(
            "Витрина магазина на сайте NurCRM: адрес и номер WhatsApp для заказов. Сохраняется сразу на сервере — как на сайте.",
            "NurCRM сайтындагы дүкөндүн витринасы: дареги жана заказдар үчүн WhatsApp номери. Дароо серверде сакталат — сайттагыдай.",
            "Your store showcase on the NurCRM website: its address and the WhatsApp number for orders. Saved straight to the server — as on the website.",
            "NurCRM sitesindeki mağaza vitrini: adresi ve siparişler için WhatsApp numarası. Doğrudan sunucuya kaydedilir — sitedeki gibi.",
            "NurCRM saytidagi do'kon vitrinasi: manzili va buyurtmalar uchun WhatsApp raqami. To'g'ridan-to'g'ri serverga saqlanadi — saytdagidek.");
        RefreshButton.Content = Tr.T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash");
        CloseButton.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        OpenShowcaseButton.Content = Tr.T("Открыть витрину", "Витринаны ачуу", "Open showcase", "Vitrini aç", "Vitrinani ochish");
        CopyLinkButton.Content = Tr.T("Скопировать ссылку", "Шилтемени көчүрүү", "Copy link", "Bağlantıyı kopyala", "Havolani nusxalash");

        SlugTitle.Text = Tr.T("Адрес витрины", "Витринанын дареги", "Showcase address", "Vitrin adresi", "Vitrina manzili");
        SlugPrefixText.Text = ShowcaseApiService.PublicSiteBase.Replace("https://", "", StringComparison.Ordinal) + "/catalog/";
        SlugHintText.Text = Tr.T(
            $"Строчные латинские буквы, цифры и дефис, от {ShowcaseApiService.SlugMinLength} до {ShowcaseApiService.SlugMaxLength} символов; кириллица переводится в латиницу сама. После смены старая ссылка перестанет открываться — обновите её в соцсетях и на визитках.",
            $"Кичине латын тамгалары, сандар жана дефис, {ShowcaseApiService.SlugMinLength}–{ShowcaseApiService.SlugMaxLength} белги; кириллица латынчага өзү өтөт. Өзгөрткөндөн кийин эски шилтеме ачылбай калат — аны соцтармактарда жана визиткаларда жаңыртыңыз.",
            $"Lowercase Latin letters, digits and hyphens, {ShowcaseApiService.SlugMinLength} to {ShowcaseApiService.SlugMaxLength} characters; Cyrillic is converted to Latin automatically. After a change the old link stops working — update it on social media and business cards.",
            $"Küçük Latin harfleri, rakamlar ve kısa çizgi, {ShowcaseApiService.SlugMinLength}–{ShowcaseApiService.SlugMaxLength} karakter; Kiril harfleri otomatik olarak Latin harflerine çevrilir. Değişiklikten sonra eski bağlantı açılmaz — sosyal medyada ve kartvizitlerde güncelleyin.",
            $"Kichik lotin harflari, raqamlar va chiziqcha, {ShowcaseApiService.SlugMinLength}–{ShowcaseApiService.SlugMaxLength} ta belgi; kirill harflari o'zi lotinchaga o'tadi. O'zgartirgandan keyin eski havola ochilmaydi — uni ijtimoiy tarmoqlarda va vizitkalarda yangilang.");

        PhoneTitle.Text = Tr.T("Номер WhatsApp для заказов с витрины", "Витринадан заказдар үчүн WhatsApp номери", "WhatsApp number for showcase orders",
            "Vitrin siparişleri için WhatsApp numarası", "Vitrina buyurtmalari uchun WhatsApp raqami");
        PhoneBox.Watermark = "996 700 123 456";
        PhoneHintText.Text = Tr.T(
            "Покупатель нажимает «Оформить заказ» на витрине — сайт открывает WhatsApp с текстом заказа на этот номер. Один номер, с кодом страны 996.",
            "Кардар витринада «Оформить заказ» («Заказ берүү») баскычын басат — сайт заказдын тексти менен WhatsApp'ты ушул номерге ачат. Бир номер, 996 өлкө коду менен.",
            "The customer taps “Оформить заказ” (Place order) on the showcase — the site opens WhatsApp with the order text to this number. One number, with the 996 country code.",
            "Müşteri vitrinde «Оформить заказ» (Sipariş ver) düğmesine basar — site sipariş metniyle WhatsApp'ı bu numaraya açar. Tek numara, 996 ülke koduyla.",
            "Xaridor vitrinada «Оформить заказ» (Buyurtma berish) tugmasini bosadi — sayt buyurtma matni bilan WhatsApp'ni shu raqamga ochadi. Bitta raqam, 996 mamlakat kodi bilan.");

        ReadonlyTitle.Text = Tr.T("Что меняется в NurCRM, а не здесь", "Бул жерде эмес, NurCRMде эмне өзгөрөт", "What is changed in NurCRM, not here",
            "Burada değil, NurCRM'de değiştirilenler", "Bu yerda emas, NurCRMda nima o'zgaradi");
        SaveButton.Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
        ResetButton.Content = Tr.T("Отменить изменения", "Өзгөртүүлөрдү жокко чыгаруу", "Discard changes", "Değişiklikleri geri al", "O'zgarishlarni bekor qilish");
    }

    // ------------------------------------------------------------------ загрузка

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        LoadingBar.IsVisible = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var settings = await _api.GetSettingsAsync(_lifetime.Token).ConfigureAwait(true);
            var hadChanges = HasChanges();
            _settings = settings;
            _offline = false;
            _accessError = null;
            // Несохранённые правки владельца «Обновить» не стирает — обновляется только то, что с сервера.
            if (!hadChanges)
                FillBoxes(settings);

            _productCount = null;
            if (settings.Slug.Length > 0)
            {
                try
                {
                    _productCount = await _api.CountShowcaseProductsAsync(settings.Slug, _lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Витрина: число товаров не получено: {ex.Message}", "SHOWCASE");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            _accessError = SiteOrdersWindow.SessionEndedText;
            PosLogger.Log("Настройки сайта: сессия недействительна (401)", "SHOWCASE");
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            _accessError = Tr.T("Нет доступа к настройкам компании: их может менять только владелец компании в NurCRM.",
                "Компаниянын жөндөөлөрүнө уруксат жок: аларды NurCRMде компаниянын ээси гана өзгөртө алат.",
                "No access to company settings: only the company owner in NurCRM can change them.",
                "Şirket ayarlarına erişim yok: bunları yalnızca NurCRM'deki şirket sahibi değiştirebilir.",
                "Kompaniya sozlamalariga ruxsat yo'q: ularni faqat NurCRMdagi kompaniya egasi o'zgartira oladi.");
            PosLogger.Log($"Настройки сайта: нет доступа ({ex.StatusCode})", "SHOWCASE");
        }
        catch (Exception ex)
        {
            _offline = true;
            PosLogger.Log($"Настройки сайта: не загружены: {ex.Message}", "SHOWCASE");
        }
        finally
        {
            _loading = false;
            LoadingBar.IsVisible = false;
            RefreshButton.IsEnabled = true;
        }

        Render();
    }

    private void FillBoxes(ShowcaseSettings settings)
    {
        _suppressEvents = true;
        SlugBox.Text = settings.Slug;
        PhoneBox.Text = settings.ShowcasePhone;
        _suppressEvents = false;
        _slugCheck = SlugCheck.None;
        _slugCheckedFor = "";
    }

    // ------------------------------------------------------------------ состояние и проверка

    private string EnteredSlug => ShowcaseApiService.TrimSlug(SlugBox.Text);

    private string EnteredPhone => (PhoneBox.Text ?? "").Trim();

    private bool SlugChanged => _settings != null && !string.Equals(EnteredSlug, _settings.Slug, StringComparison.Ordinal);

    private bool PhoneChanged => _settings != null && !string.Equals(EnteredPhone, _settings.ShowcasePhone.Trim(), StringComparison.Ordinal);

    private bool HasChanges() => SlugChanged || PhoneChanged;

    private void Render()
    {
        RenderState();
        RenderSlugStatus();
        RenderPhoneStatus();

        var name = _settings?.CompanyName is { Length: > 0 } n ? n : "—";
        ReadonlyText.Text = Tr.T(
            $"Название магазина на витрине («{name}») и адрес магазина меняются в NurCRM (Моя компания) — программа их не трогает. На витрине показываются все товары склада с ценой, скидкой и фото из NurCRM: убрать товар с витрины можно только убрав его со склада.",
            $"Витринадагы дүкөндүн аталышы («{name}») жана дүкөндүн дареги NurCRMде (Менин компаниям) өзгөрөт — программа аларга тийбейт. Витринада кампадагы бардык товарлар NurCRMдеги баасы, арзандатуусу жана сүрөтү менен көрсөтүлөт: товарды витринадан кампадан алып салуу менен гана алууга болот.",
            $"The store name on the showcase (“{name}”) and the store address are changed in NurCRM (My company) — the program doesn't touch them. The showcase lists all warehouse products with their price, discount and photo from NurCRM: a product can only be removed from the showcase by removing it from the warehouse.",
            $"Vitrindeki mağaza adı («{name}») ve mağaza adresi NurCRM'de (Şirketim) değiştirilir — program bunlara dokunmaz. Vitrinde depodaki tüm ürünler NurCRM'deki fiyatı, indirimi ve fotoğrafıyla gösterilir: bir ürün vitrinden yalnızca depodan kaldırılarak çıkarılabilir.",
            $"Vitrinadagi do'kon nomi («{name}») va do'kon manzili NurCRMda (Mening kompaniyam) o'zgartiriladi — dastur ularga tegmaydi. Vitrinada ombordagi barcha mahsulotlar NurCRMdagi narxi, chegirmasi va rasmi bilan ko'rsatiladi: mahsulotni vitrinadan faqat ombordan olib tashlab olib tashlash mumkin.");

        var editable = _settings != null && _accessError == null;
        SlugBox.IsEnabled = editable && !_saving;
        PhoneBox.IsEnabled = editable && !_saving;
        ResetButton.IsEnabled = editable && !_saving && HasChanges();
        SaveButton.IsEnabled = CanSave();
    }

    private bool CanSave()
    {
        if (_settings == null || _accessError != null || _saving || !HasChanges())
            return false;
        if (SlugChanged && (ShowcaseApiService.ValidateSlug(EnteredSlug) != null
                            || _slugCheck != SlugCheck.Available
                            || !string.Equals(_slugCheckedFor, EnteredSlug, StringComparison.Ordinal)))
            return false;
        return !PhoneChanged || ShowcaseApiService.CheckShowcasePhone(EnteredPhone).Error == null;
    }

    private void RenderState()
    {
        var connected = ShowcaseApiService.IsShowcaseConnected;
        var slug = _settings?.Slug ?? "";
        var hasLink = connected && slug.Length > 0;
        OpenShowcaseButton.IsVisible = hasLink;
        CopyLinkButton.IsVisible = hasLink;
        LinkText.IsVisible = hasLink;
        LinkText.Text = hasLink ? ShowcaseApiService.CatalogUrl(slug) : "";

        if (_accessError != null)
        {
            SetDot("BrushWarning");
            StateText.Text = Tr.T("Нет доступа", "Уруксат жок", "No access", "Erişim yok", "Ruxsat yo'q");
            StateHintText.Text = _accessError;
            return;
        }

        if (_settings == null)
        {
            SetDot(_offline ? "BrushWarning" : "BrushTextSoft");
            StateText.Text = _offline
                ? Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q")
                : Tr.T("Загружаю настройки…", "Жөндөөлөр жүктөлүүдө…", "Loading settings…", "Ayarlar yükleniyor…", "Sozlamalar yuklanmoqda…");
            StateHintText.Text = _offline
                ? Tr.T("Настройки сайта хранятся на сервере NurCRM — их можно менять, когда связь вернётся. Нажмите «Обновить».",
                    "Сайттын жөндөөлөрү NurCRM серверинде сакталат — байланыш калыбына келгенде өзгөртүүгө болот. «Жаңылоо» баскычын басыңыз.",
                    "Website settings are stored on the NurCRM server — you can change them when the connection is back. Press “Refresh”.",
                    "Web sitesi ayarları NurCRM sunucusunda saklanır — bağlantı geri geldiğinde değiştirebilirsiniz. «Yenile»ye basın.",
                    "Sayt sozlamalari NurCRM serverida saqlanadi — aloqa tiklanganda o'zgartirish mumkin. «Yangilash»ni bosing.")
                : "";
            return;
        }

        if (!connected)
        {
            SetDot("BrushWarning");
            StateText.Text = Tr.T("Витрина не подключена", "Витрина туташтырылган эмес", "The showcase is not connected", "Vitrin bağlı değil", "Vitrina ulanmagan");
            StateHintText.Text = Tr.T(
                "На тарифе «Старт» онлайн-витрина — платная услуга NurCRM, её подключают по заявке. Адрес и номер можно задать заранее — они заработают после подключения.",
                "«Старт» тарифинде онлайн-витрина — NurCRMдин акылуу кызматы, ал өтүнмө боюнча туташтырылат. Даректи жана номерди алдын ала коюуга болот — алар туташтырылгандан кийин иштейт.",
                "On the “Start” plan the online showcase is a paid NurCRM service, connected on request. You can set the address and number in advance — they will work once it's connected.",
                "«Start» tarifesinde çevrimiçi vitrin, talep üzerine bağlanan ücretli bir NurCRM hizmetidir. Adresi ve numarayı önceden belirleyebilirsiniz — bağlandıktan sonra çalışır.",
                "«Start» tarifida onlayn vitrina — NurCRMning pullik xizmati, u ariza bo'yicha ulanadi. Manzil va raqamni oldindan kiritish mumkin — ular ulangandan keyin ishlaydi.");
            return;
        }

        if (slug.Length == 0)
        {
            SetDot("BrushWarning");
            StateText.Text = Tr.T("У витрины нет адреса", "Витринанын дареги жок", "The showcase has no address", "Vitrinin adresi yok", "Vitrinaning manzili yo'q");
            StateHintText.Text = Tr.T("Задайте адрес ниже — без него ссылки на витрину нет.", "Төмөндө дарек коюңуз — ансыз витринанын шилтемеси жок.",
                "Set the address below — without it there is no showcase link.", "Aşağıda adresi belirleyin — o olmadan vitrin bağlantısı olmaz.",
                "Quyida manzilni kiriting — usiz vitrina havolasi bo'lmaydi.");
            return;
        }

        SetDot("BrushSuccess");
        StateText.Text = _productCount is { } count
            ? Tr.T($"Витрина работает · товаров на витрине: {count}", $"Витрина иштеп жатат · витринадагы товарлар: {count}",
                $"The showcase is live · products on the showcase: {count}", $"Vitrin yayında · vitrindeki ürünler: {count}",
                $"Vitrina ishlayapti · vitrinadagi mahsulotlar: {count}")
            : Tr.T("Витрина работает", "Витрина иштеп жатат", "The showcase is live", "Vitrin yayında", "Vitrina ishlayapti");
        StateHintText.Text = _settings.ShowcasePhone.Length > 0
            ? Tr.T($"Заказы с витрины приходят в WhatsApp на номер {_settings.ShowcasePhone}.", $"Витринадан заказдар WhatsApp'ка {_settings.ShowcasePhone} номерине келет.",
                $"Showcase orders arrive in WhatsApp at {_settings.ShowcasePhone}.", $"Vitrin siparişleri WhatsApp'ta {_settings.ShowcasePhone} numarasına gelir.",
                $"Vitrina buyurtmalari WhatsApp'ga {_settings.ShowcasePhone} raqamiga keladi.")
            : Tr.T("Номер WhatsApp не задан — покупатели не смогут отправить заказ.", "WhatsApp номери коюлган эмес — кардарлар заказ жөнөтө алышпайт.",
                "No WhatsApp number is set — customers can't send an order.", "WhatsApp numarası belirlenmemiş — müşteriler sipariş gönderemez.",
                "WhatsApp raqami kiritilmagan — xaridorlar buyurtma yubora olmaydi.");
    }

    private void RenderSlugStatus()
    {
        if (_settings == null || !SlugChanged)
        {
            SlugStatusText.IsVisible = false;
            return;
        }

        var slug = EnteredSlug;
        var error = ShowcaseApiService.ValidateSlug(slug);
        if (error != null)
        {
            SetStatus(SlugStatusText, error, "BrushDanger");
            return;
        }

        switch (_slugCheck)
        {
            case SlugCheck.Available when _slugCheckedFor == slug:
                SetStatus(SlugStatusText, Tr.T($"Адрес свободен: {ShowcaseApiService.CatalogUrl(slug)}", $"Дарек бош: {ShowcaseApiService.CatalogUrl(slug)}",
                    $"The address is free: {ShowcaseApiService.CatalogUrl(slug)}", $"Adres boşta: {ShowcaseApiService.CatalogUrl(slug)}",
                    $"Manzil bo'sh: {ShowcaseApiService.CatalogUrl(slug)}"), "BrushSuccess");
                break;
            case SlugCheck.Taken when _slugCheckedFor == slug:
                SetStatus(SlugStatusText, SlugTakenText(_slugCheckMessage), "BrushDanger");
                break;
            case SlugCheck.Failed when _slugCheckedFor == slug:
                SetStatus(SlugStatusText, Tr.T("Не удалось проверить адрес: нет связи с сервером.", "Даректи текшерүү мүмкүн болгон жок: сервер менен байланыш жок.",
                    "Couldn't check the address: no connection to the server.", "Adres kontrol edilemedi: sunucuyla bağlantı yok.",
                    "Manzilni tekshirib bo'lmadi: server bilan aloqa yo'q."), "BrushWarning");
                break;
            default:
                SetStatus(SlugStatusText, Tr.T("Проверяю, свободен ли адрес…", "Дарек бошбу, текшерип жатам…", "Checking whether the address is free…",
                    "Adresin boş olup olmadığı kontrol ediliyor…", "Manzil bo'shligini tekshiryapman…"), "BrushTextSoft");
                break;
        }
    }

    private static string SlugTakenText(string? serverMessage)
    {
        // Сервер отвечает то по-русски, то по-английски («Slug already exists») — своё на языке программы.
        if (string.IsNullOrWhiteSpace(serverMessage) || serverMessage.Contains("exist", StringComparison.OrdinalIgnoreCase))
            return Tr.T("Этот адрес уже занят другим магазином.", "Бул даректи башка дүкөн ээлеген.", "This address is already taken by another store.",
                "Bu adres başka bir mağaza tarafından kullanılıyor.", "Bu manzilni boshqa do'kon egallagan.");
        return serverMessage;
    }

    private void RenderPhoneStatus()
    {
        AddCodeButton.IsVisible = false;
        if (_settings == null)
        {
            PhoneStatusText.IsVisible = false;
            return;
        }

        var check = ShowcaseApiService.CheckShowcasePhone(EnteredPhone);
        if (check.Error != null)
        {
            SetStatus(PhoneStatusText, check.Error, "BrushDanger");
            return;
        }

        if (check.Warning != null)
        {
            SetStatus(PhoneStatusText, check.Warning, "BrushWarning");
            if (check.Suggested != null)
            {
                AddCodeButton.Content = Tr.T("Добавить код 996", "996 кодун кошуу", "Add code 996", "996 kodunu ekle", "996 kodini qo'shish");
                AddCodeButton.Tag = check.Suggested;
                AddCodeButton.IsVisible = !_saving && _accessError == null;
            }
            return;
        }

        if (PhoneChanged)
        {
            SetStatus(PhoneStatusText, Tr.T($"Заказы будут приходить в WhatsApp на wa.me/{ShowcaseApiService.WhatsAppDigits(EnteredPhone)}",
                $"Заказдар WhatsApp'ка wa.me/{ShowcaseApiService.WhatsAppDigits(EnteredPhone)} дарегине келет",
                $"Orders will arrive in WhatsApp at wa.me/{ShowcaseApiService.WhatsAppDigits(EnteredPhone)}",
                $"Siparişler WhatsApp'ta wa.me/{ShowcaseApiService.WhatsAppDigits(EnteredPhone)} adresine gelecek",
                $"Buyurtmalar WhatsApp'ga wa.me/{ShowcaseApiService.WhatsAppDigits(EnteredPhone)} manziliga keladi"), "BrushSuccess");
            return;
        }

        PhoneStatusText.IsVisible = false;
    }

    private void SetStatus(TextBlock target, string text, string brushKey)
    {
        target.IsVisible = true;
        target.Text = text;
        target.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brushKey));
    }

    private void SetDot(string brushKey) => StateDot.Bind(Border.BackgroundProperty, this.GetResourceObservable(brushKey));

    private async Task CheckSlugAsync()
    {
        var slug = EnteredSlug;
        if (!SlugChanged || ShowcaseApiService.ValidateSlug(slug) != null)
            return;

        _slugCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _slugCts = cts;
        _slugCheck = SlugCheck.Checking;
        _slugCheckedFor = slug;
        Render();
        try
        {
            var (available, message) = await _api.CheckSlugAsync(slug, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || EnteredSlug != slug)
                return;
            _slugCheck = available ? SlugCheck.Available : SlugCheck.Taken;
            _slugCheckMessage = message;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки сайта: адрес «{slug}» не проверен: {ex.Message}", "SHOWCASE");
            _slugCheck = SlugCheck.Failed;
        }

        Render();
    }

    // ------------------------------------------------------------------ поля

    private void SlugBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
            return;

        // Как на сайте: вводимое сразу приводится к виду адреса (строчные, латиница, дефисы).
        var text = SlugBox.Text ?? "";
        var normalized = ShowcaseApiService.NormalizeSlugInput(text);
        if (!string.Equals(text, normalized, StringComparison.Ordinal))
        {
            _suppressEvents = true;
            SlugBox.Text = normalized;
            SlugBox.CaretIndex = normalized.Length;
            _suppressEvents = false;
        }

        _slugCheck = SlugCheck.None;
        _slugDebounce.Stop();
        if (SlugChanged && ShowcaseApiService.ValidateSlug(EnteredSlug) == null)
            _slugDebounce.Start();
        ShowNotice(null, true);
        Render();
    }

    private void PhoneBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        ShowNotice(null, true);
        Render();
    }

    private void AddCode_Click(object? sender, RoutedEventArgs e)
    {
        if (AddCodeButton.Tag is string suggested)
            PhoneBox.Text = suggested;
    }

    // ------------------------------------------------------------------ кнопки

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings == null)
            return;
        FillBoxes(_settings);
        ShowNotice(null, true);
        Render();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanSave() || _settings is not { } current)
            return;

        var newSlug = SlugChanged ? EnteredSlug : null;
        var newPhone = PhoneChanged ? EnteredPhone : null;
        if (newSlug != null && current.Slug.Length > 0
            && !PosConfirmDialog.Show(this,
                Tr.T("Сменить адрес витрины?", "Витринанын дарегин өзгөртөсүзбү?", "Change the showcase address?", "Vitrin adresi değiştirilsin mi?", "Vitrina manzili o'zgartirilsinmi?"),
                Tr.T($"Старая ссылка {ShowcaseApiService.CatalogUrl(current.Slug)} перестанет открываться. Новая: {ShowcaseApiService.CatalogUrl(newSlug)}",
                    $"Эски шилтеме {ShowcaseApiService.CatalogUrl(current.Slug)} ачылбай калат. Жаңысы: {ShowcaseApiService.CatalogUrl(newSlug)}",
                    $"The old link {ShowcaseApiService.CatalogUrl(current.Slug)} will stop working. New one: {ShowcaseApiService.CatalogUrl(newSlug)}",
                    $"Eski bağlantı {ShowcaseApiService.CatalogUrl(current.Slug)} açılmayacak. Yenisi: {ShowcaseApiService.CatalogUrl(newSlug)}",
                    $"Eski havola {ShowcaseApiService.CatalogUrl(current.Slug)} ochilmay qoladi. Yangisi: {ShowcaseApiService.CatalogUrl(newSlug)}"),
                Tr.T("Сменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")))
            return;

        _saving = true;
        LoadingBar.IsVisible = true;
        Render();
        try
        {
            var saved = await _api.SaveSettingsAsync(newSlug, newPhone, _lifetime.Token).ConfigureAwait(true);
            _settings = saved;
            FillBoxes(saved);
            ShowNotice(Tr.T("Сохранено на сервере NurCRM.", "NurCRM серверинде сакталды.", "Saved on the NurCRM server.", "NurCRM sunucusuna kaydedildi.", "NurCRM serverida saqlandi.")
                       + (saved.Slug.Length > 0 ? " " + ShowcaseApiService.CatalogUrl(saved.Slug) : ""), success: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            ShowNotice(Tr.T("Не сохранено", "Сакталган жок", "Not saved", "Kaydedilmedi", "Saqlanmadi") + ". " + SiteOrdersWindow.SessionEndedText, success: false);
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            ShowNotice(Tr.T("Не сохранено: менять настройки сайта может только владелец компании.", "Сакталган жок: сайттын жөндөөлөрүн компаниянын ээси гана өзгөртө алат.",
                "Not saved: only the company owner can change website settings.", "Kaydedilmedi: web sitesi ayarlarını yalnızca şirket sahibi değiştirebilir.",
                "Saqlanmadi: sayt sozlamalarini faqat kompaniya egasi o'zgartira oladi."), success: false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки сайта: не сохранены: {ex.Message}", "SHOWCASE");
            ShowNotice(Tr.T("Не сохранено", "Сакталган жок", "Not saved", "Kaydedilmedi", "Saqlanmadi") + ": "
                       + (SiteOrdersWindow.IsNoConnection(ex)
                           ? Tr.T("нет связи с сервером. Изменения остались в полях — нажмите «Сохранить» ещё раз.",
                               "сервер менен байланыш жок. Өзгөртүүлөр талааларда калды — «Сактоо» баскычын дагы басыңыз.",
                               "no connection to the server. Your changes are still in the fields — press “Save” again.",
                               "sunucuyla bağlantı yok. Değişiklikler alanlarda duruyor — «Kaydet»e tekrar basın.",
                               "server bilan aloqa yo'q. O'zgarishlar maydonlarda qoldi — «Saqlash»ni yana bosing.")
                           : ex.Message), success: false);
        }
        finally
        {
            _saving = false;
            LoadingBar.IsVisible = false;
        }

        Render();
    }

    private void OpenShowcase_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is { Slug.Length: > 0 } s)
            SiteOrdersWindow.OpenUrl(ShowcaseApiService.CatalogUrl(s.Slug));
    }

    private async void CopyLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is not { Slug.Length: > 0 } s)
            return;
        var url = ShowcaseApiService.CatalogUrl(s.Slug);
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(url).ConfigureAwait(true);
                ShowNotice(Tr.T($"Скопировано: {url}", $"Көчүрүлдү: {url}", $"Copied: {url}", $"Kopyalandı: {url}", $"Nusxalandi: {url}"), success: true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки сайта: ссылка не скопирована: {ex.Message}", "SHOWCASE");
        }
    }

    private void ShowNotice(string? message, bool success)
    {
        NoticeBox.IsVisible = !string.IsNullOrWhiteSpace(message);
        NoticeText.Text = message ?? "";
        NoticeBanner.Apply(NoticeBox, NoticeText, success);
    }
}
