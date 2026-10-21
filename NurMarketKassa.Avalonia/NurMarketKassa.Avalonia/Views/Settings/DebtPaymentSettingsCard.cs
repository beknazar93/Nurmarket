using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-18, владелец: карточка «Оплата долга» в настройках (рядом с предоплатой долга) — общие для всех касс и
/// программы владельца способы приёма оплаты долга и «нужен код сотрудника». Хранится на сервере (DebtPaymentSettings,
/// облако кассы), меняется сразу; кассир без права записи получает понятное сообщение, переключатель возвращается.</summary>
public sealed class DebtPaymentSettingsCard : Border
{
    private readonly CheckBox _kassaCash;
    private readonly CheckBox _kassaTransfer;
    private readonly CheckBox _ownerCash;
    private readonly CheckBox _ownerTransfer;
    private readonly CheckBox _requireCode;
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool _suppress;
    private bool _saving;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Стиль «Border.SettingsCard» (общие стили настроек) — как у обычной карточки: селектор типа точный.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public DebtPaymentSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        IsVisible = TariffGate.CanUseDebts;
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = T("Оплата долга", "Карыз төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash"),
            Classes = { "SettingsCardTitle" }, FontSize = 14,
        });
        stack.Children.Add(Hint(T("Как принимать оплату долга. Общие для всех касс и программы владельца; меняют владелец и администратор.",
            "Карыз төлөмүн кантип кабыл алуу. Бардык кассалар жана ээсинин программасы үчүн бирдей; ээси жана администратор өзгөртөт.",
            "How debt payments are taken. Shared by all tills and the owner app; the owner and administrators change it.",
            "Borç ödemelerinin nasıl alınacağı. Tüm kasalar ve sahip programı için ortak; sahip ve yönetici değiştirir.",
            "Qarz to'lovini qanday qabul qilish. Barcha kassalar va egasining dasturi uchun umumiy; egasi va administrator o'zgartiradi.")));

        stack.Children.Add(Sub(T("В кассе", "Кассада", "At the till", "Kasada", "Kassada")));
        _kassaCash = Row(stack, T("Наличными — в ящик, в смену кассира", "Накталай — кутуга, кассирдин сменасына", "Cash — into the drawer, the cashier's shift",
            "Nakit — çekmeceye, kasiyerin vardiyasına", "Naqd — qutiga, kassir smenasiga"));
        _kassaTransfer = Row(stack, T("Безналом (карта, перевод) — в смене безналом, не в ящике", "Накталай эмес (карта, которуу) — сменада накталай эмес, кутуга эмес",
            "Non-cash (card, transfer) — non-cash in the shift, not the drawer", "Nakitsiz (kart, havale) — vardiyada nakitsiz, çekmeceye değil",
            "Naqd emas (karta, o'tkazma) — smenada naqd emas, qutiga emas"));

        stack.Children.Add(Sub(T("В программе владельца", "Ээсинин программасында", "In the owner app", "Sahip programında", "Egasining dasturida")));
        _ownerCash = Row(stack, T("Наличными «в руки» — без смены, касса не меняется", "Накталай «колго» — сменасыз, касса өзгөрбөйт",
            "Cash “in hand” — no shift, the till doesn't change", "Nakit «elden» — vardiyasız, kasa değişmez", "Naqd «qo'lga» — smenasiz, kassa o'zgarmaydi"));
        _ownerTransfer = Row(stack, T("Безналом (карта, перевод) — без смены", "Накталай эмес (карта, которуу) — сменасыз", "Non-cash (card, transfer) — no shift",
            "Nakitsiz (kart, havale) — vardiyasız", "Naqd emas (karta, o'tkazma) — smenasiz"));

        stack.Children.Add(Sub(T("Доступ", "Кирүү", "Access", "Erişim", "Kirish")));
        _requireCode = Row(stack, T("Нужен код сотрудника для приёма оплаты", "Төлөм кабыл алуу үчүн кызматкердин коду керек", "An employee code is needed to take a payment",
            "Ödeme almak için personel kodu gerekir", "To'lov qabul qilish uchun xodim kodi kerak"));
        stack.Children.Add(Hint(T("Код задаётся каждому сотруднику: Настройки → Сотрудники → «Код на приём оплаты долга». Сотрудник без кода оплату не примет; владелец и администратор — без кода.",
            "Код ар бир кызматкерге коюлат: Жөндөөлөр → Кызматкерлер → «Карыз төлөмүн кабыл алуу коду». Коду жок кызматкер төлөм кабыл албайт; ээси жана администратор — кодсуз.",
            "Each employee gets a code: Settings → Employees → “Debt payment code”. An employee without a code can't take a payment; the owner and administrators don't need one.",
            "Her personele kod verilir: Ayarlar → Personel → «Borç ödemesi kodu». Kodu olmayan personel ödeme alamaz; sahip ve yönetici kodsuz.",
            "Har bir xodimga kod beriladi: Sozlamalar → Xodimlar → «Qarz to'lovi kodi». Kodsiz xodim to'lov qabul qilolmaydi; egasi va administrator — kodsiz.")));
        stack.Children.Add(_status);
        Child = stack;

        Show(DebtPaymentSettings.Current);
        // 2026-10-21 (ревью денег, A1): пока значения этой компании не получены с сервера (на экране — умолчания), переключатели
        // не включаем — иначе на экране «код не нужен», хотя на кассах он нужен.
        SetTogglesEnabled(DebtPaymentSettings.KnownForCurrentCompany);
        AttachedToVisualTree += async (_, _) =>
        {
            // Свежие значения с сервера при каждом показе настроек.
            if (await DebtPaymentSettings.RefreshAsync(force: true).ConfigureAwait(true))
            {
                Show(DebtPaymentSettings.Current);
                if (!_saving)
                    SetTogglesEnabled(true);
            }
            else if (!DebtPaymentSettings.KnownForCurrentCompany)
                Status(T("Значения с сервера не получены — изменить можно, когда появится связь (откройте настройки заново).",
                    "Маанилер серверден алынган жок — байланыш пайда болгондо өзгөртсө болот (жөндөөлөрдү кайра ачыңыз).",
                    "The values were not received from the server — you can change them once the connection is back (reopen settings).",
                    "Değerler sunucudan alınamadı — bağlantı gelince değiştirebilirsiniz (ayarları yeniden açın).",
                    "Qiymatlar serverdan olinmadi — aloqa tiklanganda o'zgartirish mumkin (sozlamalarni qayta oching)."), "BrushWarning");
        };
        void OnChanged() => Dispatcher.UIThread.Post(() =>
        {
            if (!_saving)
                Show(DebtPaymentSettings.Current);
        });
        AttachedToVisualTree += (_, _) =>
        {
            DebtPaymentSettings.Changed -= OnChanged;
            DebtPaymentSettings.Changed += OnChanged;
        };
        DetachedFromVisualTree += (_, _) => DebtPaymentSettings.Changed -= OnChanged;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private TextBlock Sub(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        return t;
    }

    private CheckBox Row(StackPanel stack, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var check = new CheckBox { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(check, text);
        check.IsCheckedChanged += async (_, _) => await OnToggleAsync(check).ConfigureAwait(true);
        Grid.SetColumn(check, 1);
        grid.Children.Add(label);
        grid.Children.Add(check);
        stack.Children.Add(grid);
        return check;
    }

    private void Show(DebtPaymentOptions o)
    {
        _suppress = true;
        try
        {
            _kassaCash.IsChecked = o.KassaCash;
            _kassaTransfer.IsChecked = o.KassaTransfer;
            _ownerCash.IsChecked = o.OwnerCash;
            _ownerTransfer.IsChecked = o.OwnerTransfer;
            _requireCode.IsChecked = o.RequireEmployeeCode;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SetTogglesEnabled(bool enabled)
    {
        foreach (var c in new[] { _kassaCash, _kassaTransfer, _ownerCash, _ownerTransfer, _requireCode })
            c.IsEnabled = enabled;
    }

    private void Status(string text, string brush)
    {
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        _status.IsVisible = text.Length > 0;
    }

    private async Task OnToggleAsync(CheckBox changed)
    {
        if (_suppress || _saving)
            return;
        // 2026-10-21 (ревью денег, A1): на сервер — только нажатый переключатель поверх свежих значений сервера (раньше уходил
        // весь экранный набор: значения по умолчанию на экране выключали «Нужен код сотрудника» на всех кассах).
        var value = changed.IsChecked == true;
        Action<DebtPaymentOptions> change =
            ReferenceEquals(changed, _kassaCash) ? o => o.KassaCash = value
            : ReferenceEquals(changed, _kassaTransfer) ? o => o.KassaTransfer = value
            : ReferenceEquals(changed, _ownerCash) ? o => o.OwnerCash = value
            : ReferenceEquals(changed, _ownerTransfer) ? o => o.OwnerTransfer = value
            : o => o.RequireEmployeeCode = value;
        _saving = true;
        SetTogglesEnabled(false);
        Status(T("Сохраняю на сервере…", "Серверде сакталууда…", "Saving on the server…", "Sunucuya kaydediliyor…", "Serverda saqlanmoqda…"), "BrushTextSoft");
        try
        {
            var (outcome, error) = await DebtPaymentSettings.SaveAsync(change).ConfigureAwait(true);
            switch (outcome)
            {
                case DebtPaymentSettings.SaveOutcome.NoMethod:
                    // В каждом месте нужен хотя бы один способ (проверено по свежим значениям сервера).
                    Status(T("Нужен хотя бы один способ — включите другой, затем выключите этот.", "Жок дегенде бир ыкма керек — башкасын күйгүзүп, анан мунусун өчүрүңүз.",
                        "At least one method is needed — turn on the other one first.", "En az bir yöntem gerekir — önce diğerini açın.",
                        "Kamida bitta usul kerak — avval boshqasini yoqing."), "BrushWarning");
                    break;
                case DebtPaymentSettings.SaveOutcome.Saved:
                    Status(T("Сохранено — действует на всех кассах и в программе владельца (кассы подхватят в течение 2 минут).",
                        "Сакталды — бардык кассаларда жана ээсинин программасында иштейт (кассалар 2 мүнөттүн ичинде алат).",
                        "Saved — applies to all tills and the owner app (tills pick it up within 2 minutes).",
                        "Kaydedildi — tüm kasalarda ve sahip programında geçerli (kasalar 2 dakika içinde alır).",
                        "Saqlandi — barcha kassalarda va egasining dasturida amal qiladi (kassalar 2 daqiqa ichida oladi)."), "BrushSuccess");
                    break;
                case DebtPaymentSettings.SaveOutcome.Forbidden:
                    Status(T("Не сохранено: общие настройки оплаты долга меняют только владелец и администратор.",
                        "Сакталган жок: карыз төлөөнүн жалпы жөндөөлөрүн ээси жана администратор гана өзгөртөт.",
                        "Not saved: only the owner and administrators can change the shared debt payment settings.",
                        "Kaydedilmedi: ortak borç ödemesi ayarlarını yalnızca sahip ve yönetici değiştirebilir.",
                        "Saqlanmadi: qarz to'lovining umumiy sozlamalarini faqat egasi va administrator o'zgartiradi."), "BrushDanger");
                    break;
                case DebtPaymentSettings.SaveOutcome.Offline:
                    Status(T("Не сохранено: нет связи с сервером. Попробуйте, когда связь появится.",
                        "Сакталган жок: сервер менен байланыш жок. Байланыш пайда болгондо кайра аракет кылыңыз.",
                        "Not saved: no connection to the server. Try again when it's back.",
                        "Kaydedilmedi: sunucu bağlantısı yok. Bağlantı gelince tekrar deneyin.",
                        "Saqlanmadi: server bilan aloqa yo'q. Aloqa tiklanganda qayta urining."), "BrushDanger");
                    break;
                default:
                    Status(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + (error ?? ""), "BrushDanger");
                    break;
            }
        }
        finally
        {
            _saving = false;
            SetTogglesEnabled(DebtPaymentSettings.KnownForCurrentCompany);
            // На экране — то, что действует (после отказа переключатель возвращается).
            Show(DebtPaymentSettings.Current);
        }
    }
}
