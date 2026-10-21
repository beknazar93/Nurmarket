using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum ReturnReasonDialogKind
{
    LineItems,
    FullReceipt,
}

public partial class ReturnLineReasonDialog : Window
{
    public ReturnLineReasonDialog() : this(1) { }

    public ReturnLineReasonDialog(int selectedItemCount = 1, ReturnReasonDialogKind kind = ReturnReasonDialogKind.LineItems)
    {
        InitializeComponent();

        // 2026-09-08: раньше эти строки жёстко перезаписывали Title/HintText.Text по-русски
        // ПОСЛЕ InitializeComponent — хотя сам заголовок в теле диалога (returnReason.header)
        // уже был корректно переведён через DynamicResource в XAML, эти три поля (заголовок
        // окна, подсказка, текст ошибки) молча оставались русскими на любом языке интерфейса —
        // владелец пожаловался: "объясняют не на кыргызском а на русском".
        if (kind == ReturnReasonDialogKind.FullReceipt)
        {
            Title = Tr.T("Причина полного возврата", "Толук кайтаруунун себеби", "Reason for full return", "Tam iade sebebi", "To'liq qaytarish sababi");
            HintText.Text = Tr.T("Укажите причину. Она будет передана в CRM для возврата всего чека целиком.",
                "Себебин көрсөтүңүз. Ал бүт чекти кайтаруу үчүн CRMге өткөрүлөт.",
                "Specify the reason. It will be sent to the CRM to return the whole receipt.",
                "Nedenini belirtin. Tüm fişin iadesi için CRM'e gönderilecektir.",
                "Sababini ko'rsating. U butun chekni qaytarish uchun CRM'ga yuboriladi.");
        }
        else
        {
            Title = Tr.T("Причина возврата", "Кайтаруунун себеби", "Reason for return", "İade sebebi", "Qaytarish sababi");
            HintText.Text = selectedItemCount <= 1
                ? Tr.T("Комментарий будет передан в CRM вместе с возвратом позиции.",
                    "Комментарий позицияны кайтаруу менен бирге CRMге өткөрүлөт.",
                    "The comment will be sent to the CRM together with the item return.",
                    "Yorum, kalem iadesiyle birlikte CRM'e gönderilecektir.",
                    "Izoh pozitsiyani qaytarish bilan birga CRM'ga yuboriladi.")
                : Tr.T($"Одна и та же причина будет указана для {selectedItemCount} выбранных позиций и передана в CRM.",
                    $"Бир эле себеп тандалган {selectedItemCount} позиция үчүн көрсөтүлүп, CRMге өткөрүлөт.",
                    $"The same reason will be recorded for all {selectedItemCount} selected items and sent to the CRM.",
                    $"Seçilen {selectedItemCount} kalem için aynı neden belirtilip CRM'e gönderilecektir.",
                    $"{selectedItemCount} ta tanlangan pozitsiya uchun bir xil sabab ko'rsatilib, CRM'ga yuboriladi.");
        }

        BuildQuickReasons();
        Opened += (_, _) => ReasonBox.Focus();
    }

    public string ReasonText => (ReasonBox.Text ?? "").Trim();

    /// <summary>2026-10-06 (О-33): кассир отметил «Брак» — сервер не вернёт товар на склад (is_defect).</summary>
    public bool IsDefect
    {
        get => DefectCheck.IsChecked == true;
        set => DefectCheck.IsChecked = value;
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-33): частые причины — одной кнопкой (их можно дописать),
    /// «Брак» сразу ставит отметку брака.</summary>
    private void BuildQuickReasons()
    {
        // 2026-10-06, владелец: «главное не трогай продуктовый» — в «Продуктах» окно как было: без кнопок причин и «Брака».
        // 2026-10-21 (пакет 6, п. 35, живой аудит кассы: «нет быстрых причин»): в «Продуктах» — частые причины кнопками,
        // но без «Брака» и его отметки: возврат на склад работает как раньше.
        if (!MarketSpheres.IsClothing)
        {
            DefectCheck.IsVisible = false;
            foreach (var text in new[]
                     {
                         Tr.T("Передумал", "Оюн өзгөрттү", "Changed mind", "Vazgeçti", "Fikridan qaytdi"),
                         Tr.T("Ошибка кассира", "Кассирдин катасы", "Cashier error", "Kasiyer hatası", "Kassir xatosi"),
                         Tr.T("Не тот товар", "Башка товар", "Wrong item", "Yanlış ürün", "Boshqa mahsulot"),
                     })
                QuickReasonsWrap.Children.Add(MakeReasonChip(text, defect: false));
            return;
        }
        DefectCheck.Content = Tr.T("Брак — товар не вернётся в продажу (на склад не возвращается)",
            "Бузук — товар сатууга кайтпайт (кампага кайтарылбайт)",
            "Defective — the item will not go back on sale (not returned to stock)",
            "Kusurlu — ürün satışa dönmez (stoğa geri alınmaz)",
            "Nuqsonli — mahsulot sotuvga qaytmaydi (omborga qaytarilmaydi)");
        var reasons = new[]
        {
            (Tr.T("Не подошёл размер", "Өлчөмү туура келген жок", "Wrong size", "Beden uymadı", "O'lchami to'g'ri kelmadi"), false),
            (Tr.T("Не подошёл цвет", "Түсү жаккан жок", "Wrong colour", "Renk uymadı", "Rangi to'g'ri kelmadi"), false),
            (Tr.T("Передумал", "Оюн өзгөрттү", "Changed mind", "Vazgeçti", "Fikridan qaytdi"), false),
            (Tr.T("Брак", "Бузук", "Defective", "Kusurlu", "Nuqsonli"), true),
        };
        foreach (var (text, defect) in reasons)
            QuickReasonsWrap.Children.Add(MakeReasonChip(text, defect));
    }

    /// <summary>Кнопка частой причины (2026-10-21: вынесено, чтобы и «Продукты» брали тот же вид).</summary>
    private Button MakeReasonChip(string text, bool defect)
    {
        // 2026-10-17, этап 3: вид чипа — общий стиль модалок modal-chip (44 px, PosDialogStyles.axaml).
        var chip = new Button
        {
            Content = text, Margin = new Avalonia.Thickness(0, 0, 8, 8), FontSize = 13, Focusable = false,
            Classes = { "modal-chip" },
        };
        chip.Click += (_, _) =>
        {
            ReasonBox.Text = text;
            ReasonBox.CaretIndex = text.Length;
            if (defect)
                DefectCheck.IsChecked = true;
            ReasonBox.Focus();
        };
        return chip;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        if (ReasonText.Length == 0)
        {
            ErrorText.Text = Tr.T("Введите причину возврата.", "Кайтаруунун себебин киргизиңиз.", "Enter the return reason.", "İade nedenini girin.", "Qaytarish sababini kiriting.");
            ErrorText.IsVisible = true;
            return;
        }

        Close(true);
    }
}
