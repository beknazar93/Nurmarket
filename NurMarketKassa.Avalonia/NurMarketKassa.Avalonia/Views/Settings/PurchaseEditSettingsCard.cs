using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-30, редактор накладных закупок (ТЗ 2.1–2.2): карточка «Редактирование накладных закупок» в «Операциях» — окно, в течение которого владелец может
/// править проведённый приход: 24 часа (по умолчанию) / 48 часов / всегда. Отсчёт — от даты прихода. Общее для всех касс (SettingsCloudSync, CompanyKeys,
/// UserPreferences.PurchaseEditWindow); менять может только владелец (роль owner в профиле), остальные видят значение. Право «Редактор документов закупок»
/// пока только у владельца — сотрудникам и администратору не выдаётся.</summary>
public sealed class PurchaseEditSettingsCard : Border
{
    private readonly StackPanel _segs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly bool _owner;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    protected override Type StyleKeyOverride => typeof(Border);

    public PurchaseEditSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        _owner = App.AuthApi is { } auth && PermissionService.CanEditPurchaseDocuments(auth.UserPayload);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = T("Редактирование накладных закупок", "Сатып алуу накладнойлорун оңдоо", "Editing purchase invoices", "Alım faturalarını düzenleme", "Xarid yuk xatlarini tahrirlash"), Classes = { "SettingsCardTitle" }, FontSize = 14 });
        stack.Children.Add(Hint(T("Сколько времени после прихода владелец может исправить количество в проведённой накладной (Закупки → приход → «Изменить»). Время считается от даты прихода. Общее для всех касс и программы владельца.",
            "Кирүүдөн кийин ээси өткөрүлгөн накладнойдогу санды канча убакытка чейин оңдой алат (Сатып алуулар → кирүү → «Өзгөртүү»). Убакыт кирүү күнүнөн эсептелет. Бардык кассалар жана ээсинин программасы үчүн бирдей.",
            "How long after a receipt the owner can fix the quantity in a posted invoice (Purchases → receipt → “Edit”). Counted from the receipt date. Shared by all tills and the owner app.",
            "Girişten sonra sahibin işlenmiş faturadaki miktarı ne kadar süre düzeltebileceği (Satın alma → giriş → «Düzenle»). Giriş tarihinden sayılır. Tüm kasalar ve sahip programı için ortaktır.",
            "Kirimdan keyin egasi o'tkazilgan yuk xatidagi miqdorni qancha vaqt tuzata oladi (Xaridlar → kirim → «O'zgartirish»). Vaqt kirim sanasidan hisoblanadi. Barcha kassalar va egasi dasturi uchun umumiy.")));
        stack.Children.Add(_segs);
        stack.Children.Add(Hint(_owner
            ? T("Меняет только владелец. Право «Редактор документов закупок» пока только у владельца — сотрудникам и администратору оно не выдаётся.", "Ээси гана өзгөртөт. «Сатып алуу документтеринин редактору» укугу азырынча ээсинде гана — кызматкерлерге жана администраторго берилбейт.",
                "Only the owner changes this. The “Purchase documents editor” right is owner-only for now — it is not given to staff or the administrator.", "Yalnızca sahip değiştirir. «Alım belgeleri düzenleyicisi» yetkisi şimdilik yalnızca sahipte — personele ve yöneticiye verilmez.", "Faqat egasi o'zgartiradi. «Xarid hujjatlari muharriri» huquqi hozircha faqat egasida — xodimlar va administratorga berilmaydi.")
            : T("Значение меняет только владелец (пока только он может править накладные закупок).", "Маанини ээси гана өзгөртөт (азырынча накладнойлорду ал гана оңдой алат).", "Only the owner can change the value (for now only the owner can edit purchase invoices).",
                "Değeri yalnızca sahip değiştirir (şimdilik alım faturalarını yalnızca o düzenleyebilir).", "Qiymatni faqat egasi o'zgartiradi (hozircha xarid yuk xatlarini faqat u tahrirlay oladi).")));
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(_status);
        Child = stack;
        Build();
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private void Build()
    {
        _segs.Children.Clear();
        var current = ReceiptEditPolicy.Current;
        foreach (var w in new[] { ReceiptEditWindow.Hours24, ReceiptEditWindow.Hours48, ReceiptEditWindow.Always })
        {
            var seg = UiKit.Seg(ReceiptEditPolicy.WindowName(w), w == current);
            seg.MinHeight = 44;
            seg.IsEnabled = _owner;
            seg.Click += async (_, _) => await ChooseAsync(w).ConfigureAwait(true);
            _segs.Children.Add(seg);
        }
    }

    private async Task ChooseAsync(ReceiptEditWindow w)
    {
        if (!_owner || w == ReceiptEditPolicy.Current)
            return;
        if (w == ReceiptEditWindow.Always)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var ok = await NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog.ShowModalAsync(owner, T("Править накладные всегда?", "Накладнойлорду дайыма оңдоого болобу?", "Allow editing invoices at any time?", "Faturalar her zaman düzenlensin mi?", "Yuk xatlari doim tahrirlansinmi?"),
                T("Настройка действует сразу на все приходы, в том числе старые: любой проведённый приход можно будет исправить. Прибыль за прошлые дни не пересчитывается, но остатки и долг поставщику изменятся.",
                    "Жөндөө бардык кирүүгө, эскилерине да дароо таасир этет: ар кандай өткөрүлгөн кирүүнү оңдоого болот. Өткөн күндөрдүн пайдасы кайра эсептелбейт, бирок калдык жана жеткирүүчүгө карыз өзгөрөт.",
                    "The setting applies at once to all receipts, including old ones: any posted receipt can be corrected. Profit for past days is not recalculated, but stock and the supplier debt change.",
                    "Ayar hemen tüm girişlere, eskiler dahil, uygulanır: işlenmiş her giriş düzeltilebilir. Geçmiş günlerin kârı yeniden hesaplanmaz ama stok ve tedarikçi borcu değişir.",
                    "Sozlama darhol barcha kirimlarga, eskilariga ham amal qiladi: har qanday o'tkazilgan kirimni tuzatish mumkin. O'tgan kunlar foydasi qayta hisoblanmaydi, lekin qoldiq va yetkazib beruvchi qarzi o'zgaradi."),
                T("Включить «Всегда»", "«Дайыма» күйгүзүү", "Turn on “Always”", "«Her zaman» aç", "«Doim»ni yoqish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmAccent.Danger).ConfigureAwait(true);
            if (!ok)
                return;
        }
        var prefs = UserPreferences.Instance;
        prefs.PurchaseEditWindow = ReceiptEditPolicy.ToKey(w);
        prefs.SaveToDisk();
        PosLogger.Log($"AUDIT: окно правки накладных закупок = {ReceiptEditPolicy.ToKey(w)} (меняет владелец).", "INFO");
        _status.Text = T("Сохранено — действует на всех кассах и в программе владельца (кассы подхватят в течение 2 минут).", "Сакталды — бардык кассаларда жана ээсинин программасында иштейт (кассалар 2 мүнөттүн ичинде алат).",
            "Saved — applies to all tills and the owner app (tills pick it up within 2 minutes).", "Kaydedildi — tüm kasalarda ve sahip programında geçerli (kasalar 2 dakika içinde alır).", "Saqlandi — barcha kassalarda va egasi dasturida amal qiladi (kassalar 2 daqiqa ichida oladi).");
        Build();
    }
}
