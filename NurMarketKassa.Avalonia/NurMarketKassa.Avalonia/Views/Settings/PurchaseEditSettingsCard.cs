using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-30, редактор накладных закупок (ТЗ 2.1–2.2): карточка «Редактирование накладных закупок» в «Операциях».
/// 2026-11-01 (просьба владельца «правка постоянно открыта, но все изменения записывай»): выбора срока 24 ч / 48 ч больше нет — правка проведённого прихода
/// всегда открыта (ReceiptEditPolicy.Parse читает любое старое значение, в том числе «24» и «48», как «Всегда»). Взамен каждая правка пишется в журнал:
/// кто, когда, что изменилось (было → стало), итог до/после и причина — «История версий» накладной и «Журнал правок» в «Закупках» (владелец).
/// Право «Редактор документов закупок» — у владельца и администратора (с 01.11.2026), менеджеру и кассиру не выдаётся.</summary>
public sealed class PurchaseEditSettingsCard : Border
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    protected override Type StyleKeyOverride => typeof(Border);

    public PurchaseEditSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = T("Редактирование накладных закупок", "Сатып алуу накладнойлорун оңдоо", "Editing purchase invoices", "Alım faturalarını düzenleme", "Xarid yuk xatlarini tahrirlash"), Classes = { "SettingsCardTitle" }, FontSize = 14 });
        stack.Children.Add(Hint(T("Правка: всегда открыта. Владелец может исправить количество в проведённой накладной в любое время (Закупки → приход → «Изменить»), без ограничения по сроку.",
            "Оңдоо: дайыма ачык. Ээси өткөрүлгөн накладнойдогу санды каалаган убакта оңдой алат (Сатып алуулар → кирүү → «Өзгөртүү»), мөөнөт чектөөсүз.",
            "Editing: always open. The owner can correct the quantity in a posted invoice at any time (Purchases → receipt → “Edit”), with no time limit.",
            "Düzenleme: her zaman açık. Sahip, işlenmiş faturadaki miktarı istediği zaman düzeltebilir (Satın alma → giriş → «Düzenle»), süre sınırı yok.",
            "Tahrirlash: doim ochiq. Egasi o'tkazilgan yuk xatidagi miqdorni istalgan vaqtda tuzata oladi (Xaridlar → kirim → «O'zgartirish»), muddat cheklovisiz.")));
        stack.Children.Add(Hint(T("Все изменения записываются в журнал: кто, когда, что было → стало, итог до и после, причина. Смотреть: в накладной — «История версий», у владельца — «Журнал правок» во вкладке «Покупки». Правка старше 7 суток от прихода отмечается в уведомлении владельцу.",
            "Бардык өзгөртүүлөр журналга жазылат: ким, качан, эмне болчу → эмне болду, жыйынтык мурун жана кийин, себеби. Көрүү: накладнойдо — «Версиялар тарыхы», ээсинде — «Сатып алуулар» өтмөгүндөгү «Оңдоо журналы». Кирүүдөн 7 суткадан эски оңдоо ээсине билдирүүдө белгиленет.",
            "Every change is logged: who, when, was → became, total before and after, reason. See it in the invoice (“Version history”) or, for the owner, “Edit journal” in the Purchases tab. An edit made more than 7 days after the receipt is flagged in the owner notification.",
            "Tüm değişiklikler günlüğe yazılır: kim, ne zaman, önce → sonra, önceki ve sonraki toplam, neden. Faturada «Sürüm geçmişi», sahipte «Alımlar» sekmesindeki «Düzenleme günlüğü». Girişten 7 günden eski düzenleme sahibe bildirimde işaretlenir.",
            "Barcha o'zgarishlar jurnalga yoziladi: kim, qachon, nima edi → nima bo'ldi, oldingi va keyingi jami, sabab. Ko'rish: yuk xatida — «Versiyalar tarixi», egasida — «Xaridlar» bo'limidagi «Tahrir jurnali». Kirimdan 7 sutkadan eski tahrir egasiga xabarda belgilanadi.")));
        stack.Children.Add(Hint(T("Править накладные и смотреть «Журнал правок» могут владелец и администратор (решение владельца 01.11.2026). Менеджеру и кассиру это недоступно.", "Накладнойлорду оңдоого жана «Оңдоо журналын» көрүүгө ээси жана администратор укуктуу (ээсинин чечими 01.11.2026). Менеджер менен кассирге жеткиликсиз.",
            "The owner and the administrator can edit invoices and see the “Edit journal” (owner's decision, 01.11.2026). Not available to managers and cashiers.", "Faturaları düzenlemek ve «Düzenleme günlüğünü» görmek sahip ve yönetici için açıktır (sahibin kararı, 01.11.2026). Müdür ve kasiyere kapalıdır.", "Yuk xatlarini tahrirlash va «Tahrir jurnali»ni ko'rish egasi va administratorga ochiq (egasining qarori, 01.11.2026). Menejer va kassirga mavjud emas.")));
        Child = stack;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
