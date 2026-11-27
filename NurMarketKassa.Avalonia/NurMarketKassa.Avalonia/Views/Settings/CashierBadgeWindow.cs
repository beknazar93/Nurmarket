using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-27 (вход кассира по QR): «Сотрудники» → «QR для входа» в программе владельца. Выпустить бейдж (прежний
/// перестаёт работать), срок действия по желанию, PIN по желанию («QR + PIN»), отозвать, сохранить картинку бейджа (PNG) для печати.
/// Код показывается ОДИН раз — в реестре магазина (UserPreferences.CashierBadgesJson → облако → все кассы) только его хэш.</summary>
internal sealed class CashierBadgeWindow : Window
{
    private readonly string _employeeId;
    private readonly string _name;
    private readonly string? _login;
    private readonly TextBlock _status = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _validity = new() { MinHeight = 44, MinWidth = 220 };
    private readonly TextBox _pin;
    private readonly TextBlock _error;
    private readonly ReceiptQrView _qr = new() { Size = 220, Dark = Brushes.Black, Light = Brushes.White };
    private readonly Border _qrFrame;
    private readonly TextBlock _qrHint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _revoke;
    private readonly Button _save;
    private string? _code;

    private static readonly (int Days, Func<string> Text)[] Validities =
    {
        (0, () => Tr.T("Бессрочно", "Мөөнөтсүз", "No expiry", "Süresiz", "Muddatsiz")),
        (30, () => Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun")),
        (90, () => Tr.T("90 дней", "90 күн", "90 days", "90 gün", "90 kun")),
        (365, () => Tr.T("1 год", "1 жыл", "1 year", "1 yıl", "1 yil")),
    };

    public CashierBadgeWindow(string employeeId, string name, string? login)
    {
        _employeeId = employeeId;
        _name = string.IsNullOrWhiteSpace(name) ? (login ?? "") : name.Trim();
        _login = login;
        Title = Tr.T("QR для входа", "Кирүү үчүн QR", "Sign-in QR", "Giriş QR'ı", "Kirish uchun QR");
        SectionCards.AsModal(this, 780); // 2026-11-27 (живая проверка 1.17.97, D6): после выпуска QR внизу 4 кнопки — при 640 «Выпустить QR» обрезалась справа

        foreach (var v in Validities)
            _validity.Items.Add(v.Text());
        _validity.SelectedIndex = 0;
        Avalonia.Automation.AutomationProperties.SetName(_validity, Tr.T("Срок действия", "Мөөнөтү", "Valid for", "Geçerlilik", "Amal qilish muddati"));

        _pin = SectionCards.Field(new TextBox
        {
            PasswordChar = '●',
            MaxLength = CashierBadgeRegistry.PinMaxLength,
            MinWidth = 160,
            Watermark = Tr.T("PIN (необязательно)", "PIN (милдеттүү эмес)", "PIN (optional)", "PIN (isteğe bağlı)", "PIN (ixtiyoriy)"),
        });
        Avalonia.Automation.AutomationProperties.SetName(_pin, Tr.T("PIN бейджа", "Бейдждин PIN-коду", "Badge PIN", "Rozet PIN'i", "Beydj PIN-kodi"));
        _error = SectionCards.ErrorText(this);

        _qrFrame = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left, Child = _qr, IsVisible = false,
        };
        Avalonia.Automation.AutomationProperties.SetName(_qrFrame, Tr.T("QR-бейдж кассира", "Кассирдин QR-бейджи", "Cashier QR badge", "Kasiyer QR rozeti", "Kassir QR-beydji"));
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushText"));
        _qrHint.Bind(TextBlock.ForegroundProperty, _qrHint.GetResourceObservable("BrushTextSoft"));

        var about = new TextBlock
        {
            Text = Tr.T("Кассир подносит QR к сканеру на экране входа кассы — касса открывается от его имени, с его правами. В QR нет пароля: это случайный ключ, касса хранит только его отпечаток. Первый раз на каждой кассе (и снова, только если истечёт вход NurCRM) кассир один раз вводит пароль. Потерял бейдж — нажмите «Отозвать».",
                "Кассир кассанын кирүү экранында QR'ды сканерге жакындатат — касса анын атынан, анын укуктары менен ачылат. QR'да сырсөз жок: бул кокус ачкыч, касса анын изин гана сактайт. Ар бир кассада биринчи жолу (жана NurCRM кирүүсүнүн мөөнөтү бүтсө гана кайра) кассир сырсөздү бир жолу киргизет. Бейджди жоготсо — «Жокко чыгаруу» басыңыз.",
                "The cashier holds the QR to the scanner on the till's sign-in screen — the till opens in their name, with their rights. The QR holds no password: it is a random key, the till keeps only its fingerprint. The first time on each till (and again only if the NurCRM sign-in expires) the cashier enters the password once. Badge lost — press “Revoke”.",
                "Kasiyer QR'ı kasa giriş ekranında tarayıcıya tutar — kasa onun adına, onun yetkileriyle açılır. QR'da şifre yok: rastgele bir anahtardır, kasa yalnızca parmak izini saklar. Her kasada ilk kez (ve yalnızca NurCRM girişinin süresi dolarsa yeniden) kasiyer şifreyi bir kez girer. Rozet kaybolursa «İptal et»e basın.",
                "Kassir QR'ni kassaning kirish ekranida skanerga yaqinlashtiradi — kassa uning nomidan, uning huquqlari bilan ochiladi. QR'da parol yo'q: bu tasodifiy kalit, kassa faqat uning izini saqlaydi. Har bir kassada birinchi marta (va faqat NurCRM kirish muddati tugasa qayta) kassir parolni bir marta kiritadi. Beydj yo'qolsa — «Bekor qilish»ni bosing."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        about.Bind(TextBlock.ForegroundProperty, about.GetResourceObservable("BrushTextSoft"));

        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        options.Children.Add(Labeled(Tr.T("Срок действия", "Мөөнөтү", "Valid for", "Geçerlilik", "Amal qilish muddati"), _validity));
        options.Children.Add(Labeled(Tr.T("PIN после QR (4–8 цифр)", "QR'дан кийин PIN (4–8 сан)", "PIN after QR (4–8 digits)", "QR'dan sonra PIN (4–8 hane)", "QR'dan keyin PIN (4–8 raqam)"), _pin));

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_status);
        content.Children.Add(about);
        content.Children.Add(options);
        content.Children.Add(_error);
        content.Children.Add(_qrFrame);
        content.Children.Add(_qrHint);

        var issue = SectionCards.Primary(Tr.T("Выпустить QR", "QR чыгаруу", "Issue QR", "QR oluştur", "QR chiqarish"));
        issue.Click += async (_, _) => await IssueAsync();
        _revoke = SectionCards.Danger(Tr.T("Отозвать", "Жокко чыгаруу", "Revoke", "İptal et", "Bekor qilish"));
        _revoke.Click += async (_, _) => await RevokeAsync();
        _save = SectionCards.Secondary(Tr.T("Сохранить картинку", "Сүрөттү сактоо", "Save image", "Resmi kaydet", "Rasmni saqlash"));
        _save.IsVisible = false;
        _save.Click += async (_, _) => await SavePngAsync();
        var close = SectionCards.Cancel(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        close.Click += (_, _) => Close();

        Content = SectionCards.Shell(Title!, _name, content, SectionCards.Buttons(close, _save, _revoke, issue));
        RefreshStatus();
        Closed += (_, _) => _code = null; // код живёт только пока открыто окно
    }

    private static Control Labeled(string label, Control field)
    {
        var t = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return new StackPanel { Margin = new Thickness(0, 0, 16, 0), Children = { t, field } };
    }

    private void RefreshStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var active = CashierBadgeRegistry.ActiveFor(CashierBadgeRegistry.Parse(UserPreferences.Instance.CashierBadgesJson), _employeeId, now);
        _revoke.IsVisible = active is not null;
        if (active is null)
        {
            _status.Text = Tr.T("Действующего QR-бейджа нет.", "Иштеп жаткан QR-бейдж жок.", "No active QR badge.", "Geçerli QR rozet yok.", "Amaldagi QR-beydj yo'q.");
            return;
        }
        var created = DateTimeOffset.FromUnixTimeMilliseconds(active.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var until = active.ExpiresAt is { } exp
            ? DateTimeOffset.FromUnixTimeMilliseconds(exp).ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : null;
        var pin = active.RequiresPin
            ? Tr.T(", с PIN", ", PIN менен", ", with PIN", ", PIN'li", ", PIN bilan")
            : "";
        _status.Text = until is null
            ? Tr.T($"QR-бейдж действует: выпущен {created}, бессрочно{pin}.", $"QR-бейдж иштейт: {created} чыгарылган, мөөнөтсүз{pin}.", $"QR badge is active: issued {created}, no expiry{pin}.",
                $"QR rozet geçerli: {created} oluşturuldu, süresiz{pin}.", $"QR-beydj amalda: {created} chiqarilgan, muddatsiz{pin}.")
            : Tr.T($"QR-бейдж действует: выпущен {created}, до {until}{pin}.", $"QR-бейдж иштейт: {created} чыгарылган, {until} чейин{pin}.", $"QR badge is active: issued {created}, until {until}{pin}.",
                $"QR rozet geçerli: {created} oluşturuldu, {until} tarihine kadar{pin}.", $"QR-beydj amalda: {created} chiqarilgan, {until} gacha{pin}.");
    }

    /// <summary>Общие настройки магазина получены и их можно менять (владелец/администратор) — иначе реестр не трогаем.</summary>
    private bool CanWrite()
    {
        if (!SettingsCloudSync.CompanySettingsLoaded)
        {
            SectionCards.ShowError(_error, Tr.T("Настройки магазина ещё загружаются с сервера — подождите несколько секунд и повторите.", "Дүкөндүн жөндөөлөрү серверден жүктөлүп жатат — бир нече секунд күтүп, кайталаңыз.",
                "Shop settings are still loading from the server — wait a few seconds and try again.", "Mağaza ayarları hâlâ sunucudan yükleniyor — birkaç saniye bekleyip tekrar deneyin.", "Do'kon sozlamalari hali serverdan yuklanmoqda — bir necha soniya kutib, qayta urinib ko'ring."));
            return false;
        }
        if (!SettingsCloudSync.CompanySettingsWritable)
        {
            SectionCards.ShowError(_error, Tr.T("Выпускать QR-бейджи может только владелец или администратор.", "QR-бейджтерди ээси же администратор гана чыгара алат.",
                "Only the owner or an administrator can issue QR badges.", "QR rozetleri yalnızca sahip veya yönetici oluşturabilir.", "QR-beydjlarni faqat egasi yoki administrator chiqara oladi."));
            return false;
        }
        return true;
    }

    private async Task IssueAsync()
    {
        SectionCards.ShowError(_error, null);
        if (!CanWrite())
            return;
        var pin = (_pin.Text ?? "").Trim();
        if (pin.Length > 0 && !CashierBadgeRegistry.IsValidPin(pin))
        {
            SectionCards.ShowError(_error, Tr.T("PIN — от 4 до 8 цифр (или оставьте поле пустым).", "PIN — 4төн 8ге чейин сан (же талааны бош калтырыңыз).",
                "PIN must be 4 to 8 digits (or leave it empty).", "PIN 4 ile 8 hane arası olmalı (veya boş bırakın).", "PIN — 4 dan 8 gacha raqam (yoki maydonni bo'sh qoldiring)."), _pin);
            return;
        }
        var prefs = UserPreferences.Instance;
        var now = DateTimeOffset.UtcNow;
        var current = CashierBadgeRegistry.Parse(prefs.CashierBadgesJson);
        if (CashierBadgeRegistry.ActiveFor(current, _employeeId, now) is not null)
        {
            var ok = await PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Выпустить новый QR?", "Жаңы QR чыгарылсынбы?", "Issue a new QR?", "Yeni QR oluşturulsun mu?", "Yangi QR chiqarilsinmi?"),
                Tr.T("Прежний QR-бейдж этого сотрудника перестанет работать на всех кассах.", "Бул кызматкердин мурунку QR-бейджи бардык кассаларда иштебей калат.",
                    "This employee's previous QR badge will stop working on all tills.", "Bu personelin önceki QR rozeti tüm kasalarda çalışmayı durduracak.", "Bu xodimning oldingi QR-beydji barcha kassalarda ishlamay qoladi."),
                Tr.T("Выпустить", "Чыгаруу", "Issue", "Oluştur", "Chiqarish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                PosConfirmAccent.Danger, enterCancels: true);
            if (!ok)
                return;
        }
        var days = Validities[Math.Clamp(_validity.SelectedIndex, 0, Validities.Length - 1)].Days;
        var (list, code, record) = CashierBadgeRegistry.Issue(current, _employeeId, _name, _login, now,
            days > 0 ? TimeSpan.FromDays(days) : null, pin.Length > 0 ? pin : null);
        prefs.CashierBadgesJson = CashierBadgeRegistry.Serialize(list);
        prefs.SaveToDisk();
        _pin.Text = "";
        PosLogger.Log($"QR-бейдж выпущен (бейдж {CashierBadgeCode.Mask(record.Id)}, срок {(days > 0 ? days + " дн." : "бессрочно")}, PIN {(record.RequiresPin ? "да" : "нет")}).", "STAFF");

        _code = code;
        _qr.Data = code;
        _qrFrame.IsVisible = true;
        _save.IsVisible = true;
        _qrHint.Text = Tr.T("QR показан один раз: сохраните картинку и распечатайте (или покажите кассиру). Потеряли — выпустите новый. Кассы получат бейдж при следующем обновлении настроек (вход или до минуты).",
            "QR бир жолу көрсөтүлөт: сүрөттү сактап, басып чыгарыңыз (же кассирге көрсөтүңүз). Жоготсоңуз — жаңысын чыгарыңыз. Кассалар бейджди жөндөөлөр кийинки жаңыртылганда алат (кирүүдө же бир мүнөткө чейин).",
            "The QR is shown once: save the image and print it (or show it to the cashier). Lost — issue a new one. Tills receive the badge at the next settings update (sign-in or within a minute).",
            "QR bir kez gösterilir: resmi kaydedip yazdırın (veya kasiyere gösterin). Kaybolursa yenisini oluşturun. Kasalar rozeti bir sonraki ayar güncellemesinde alır (girişte veya bir dakika içinde).",
            "QR bir marta ko'rsatiladi: rasmni saqlab, chop eting (yoki kassirga ko'rsating). Yo'qolsa — yangisini chiqaring. Kassalar beydjni sozlamalar keyingi yangilanganda oladi (kirishda yoki bir daqiqagacha).");
        RefreshStatus();
    }

    private async Task RevokeAsync()
    {
        SectionCards.ShowError(_error, null);
        if (!CanWrite())
            return;
        var ok = await PosConfirmDialog.ShowModalAsync(this,
            Tr.T("Отозвать QR-бейдж?", "QR-бейдж жокко чыгарылсынбы?", "Revoke the QR badge?", "QR rozet iptal edilsin mi?", "QR-beydj bekor qilinsinmi?"),
            Tr.T("По этому QR больше нельзя будет войти ни на одной кассе (кассы узнают при следующем обновлении настроек). Вход паролем остаётся.",
                "Бул QR менен эч бир кассага кирүүгө болбой калат (кассалар жөндөөлөр кийинки жаңыртылганда билет). Сырсөз менен кирүү калат.",
                "No till will accept this QR any more (tills learn of it at the next settings update). Password sign-in stays.",
                "Bu QR ile artık hiçbir kasaya girilemeyecek (kasalar bir sonraki ayar güncellemesinde öğrenir). Şifreyle giriş kalır.",
                "Bu QR bilan endi hech bir kassaga kirib bo'lmaydi (kassalar sozlamalar keyingi yangilanganda biladi). Parol bilan kirish qoladi."),
            Tr.T("Отозвать", "Жокко чыгаруу", "Revoke", "İptal et", "Bekor qilish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger, enterCancels: true);
        if (!ok)
            return;
        var prefs = UserPreferences.Instance;
        var list = CashierBadgeRegistry.Parse(prefs.CashierBadgesJson);
        if (CashierBadgeRegistry.Revoke(list, _employeeId, DateTimeOffset.UtcNow))
        {
            prefs.CashierBadgesJson = CashierBadgeRegistry.Serialize(list);
            prefs.SaveToDisk();
            PosLogger.Log("QR-бейдж сотрудника отозван.", "STAFF");
        }
        _code = null;
        _qr.Data = null;
        _qrFrame.IsVisible = false;
        _save.IsVisible = false;
        _qrHint.Text = "";
        RefreshStatus();
    }

    /// <summary>Картинка бейджа 600×760: QR, имя сотрудника, подпись. Код в картинке — тот же ключ, храните её как ключ.</summary>
    private async Task SavePngAsync()
    {
        if (_code is null)
            return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Tr.T("Сохранить QR-бейдж", "QR-бейджди сактоо", "Save QR badge", "QR rozeti kaydet", "QR-beydjni saqlash"),
                SuggestedFileName = "badge-" + SafeName(_name) + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
            });
            if (file is null)
                return;
            await using var stream = await file.OpenWriteAsync();
            RenderBadge(_code, _name).Save(stream);
            PosLogger.Log("QR-бейдж сохранён картинкой.", "STAFF");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SectionCards.ShowError(_error, Tr.T("Не удалось сохранить картинку: ", "Сүрөттү сактоо мүмкүн болгон жок: ", "Could not save the image: ", "Resim kaydedilemedi: ", "Rasmni saqlab bo'lmadi: ") + ex.Message);
        }
    }

    private static string SafeName(string name)
    {
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] == ' ')
                chars[i] = '_';
        var s = new string(chars);
        return s.Length == 0 ? "cashier" : s;
    }

    internal static RenderTargetBitmap RenderBadge(string code, string name)
    {
        const int w = 600, h = 760, qrSize = 480;
        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using var ctx = rtb.CreateDrawingContext();
        ctx.FillRectangle(Brushes.White, new Rect(0, 0, w, h));
        var hints = new System.Collections.Generic.Dictionary<ZXing.EncodeHintType, object>
        {
            [ZXing.EncodeHintType.MARGIN] = 0,
            [ZXing.EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        };
        var m = new ZXing.QrCode.QRCodeWriter().encode(code, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
        var cell = Math.Floor((double)qrSize / m.Width);
        var size = cell * m.Width;
        var left = (w - size) / 2;
        const double top = 40;
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < m.Width; x++)
                if (m[x, y])
                    ctx.FillRectangle(Brushes.Black, new Rect(left + x * cell, top + y * cell, cell, cell));
        var title = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 34, Brushes.Black) { MaxTextWidth = w - 40, TextAlignment = TextAlignment.Center };
        ctx.DrawText(title, new Point(20, top + size + 30));
        var caption = new FormattedText(Tr.T("Вход в кассу NurCRM", "NurCRM кассасына кирүү", "NurCRM till sign-in", "NurCRM kasa girişi", "NurCRM kassasiga kirish"),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 22, Brushes.DimGray) { MaxTextWidth = w - 40, TextAlignment = TextAlignment.Center };
        ctx.DrawText(caption, new Point(20, top + size + 82));
        return rtb;
    }
}
