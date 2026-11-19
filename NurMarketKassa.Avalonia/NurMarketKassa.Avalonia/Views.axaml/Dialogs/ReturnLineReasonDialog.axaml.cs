using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-13 (Л-5): куда товар после возврата покупателю.</summary>
public enum ReturnProductOutcome
{
    /// <summary>На склад (на полку) — по умолчанию.</summary>
    Shelf,
    /// <summary>В брак: на склад не возвращается (is_defect); списывать отдельно не нужно — остаток не увеличивался.</summary>
    Defect,
    /// <summary>Поставщику: сначала на склад, затем «Возврат поставщику» с этим товаром.</summary>
    Supplier,
}

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
        // 2026-10-22 (модалки, пакет 1): фокус в поле — ModalShell.InitialFocus (XAML). Enter в многострочном поле —
        // новая строка (как раньше), «Вернуть» — Ctrl+Enter: ловим до поля (Tunnel), иначе поле вставит перенос.
        KeysHint.Text = Tr.T("Ctrl+Enter — вернуть", "Ctrl+Enter — кайтаруу", "Ctrl+Enter — return", "Ctrl+Enter — iade et", "Ctrl+Enter — qaytarish");
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter
                && NurMarketKassa.AvaloniaHost.Services.PlatformKeys.HasCommand(e.KeyModifiers))
            {
                e.Handled = true;
                Ok_Click(this, new RoutedEventArgs());
            }
        }, RoutingStrategies.Tunnel);
    }

    public string ReasonText => (ReasonBox.Text ?? "").Trim();

    /// <summary>2026-11-13 (Л-5): куда товар после возврата. По умолчанию — на полку (как всегда было).</summary>
    public ReturnProductOutcome Outcome { get; private set; } = ReturnProductOutcome.Shelf;

    private readonly List<RadioButton> _outcomeButtons = new();

    /// <summary>2026-10-06 (О-33): «Брак» — сервер не вернёт товар на склад (is_defect). 2026-11-13 (Л-5): это один из трёх исходов (<see cref="Outcome"/>).</summary>
    public bool IsDefect
    {
        get => Outcome == ReturnProductOutcome.Defect;
        set => SetOutcome(value ? ReturnProductOutcome.Defect : Outcome == ReturnProductOutcome.Defect ? ReturnProductOutcome.Shelf : Outcome);
    }

    private void SetOutcome(ReturnProductOutcome outcome)
    {
        Outcome = outcome;
        if (_outcomeButtons.Count == 3 && _outcomeButtons[(int)outcome].IsChecked != true)
            _outcomeButtons[(int)outcome].IsChecked = true;
        OutcomeHint.Text = outcome switch
        {
            ReturnProductOutcome.Defect => Tr.T("Товар на склад не вернётся (брак): остаток не увеличится.", "Товар кампага кайтпайт (бузук): калдык көбөйбөйт.",
                "The item will not go back to stock (defective): the stock count will not grow.", "Ürün stoğa dönmez (kusurlu): stok artmaz.", "Mahsulot omborga qaytmaydi (nuqsonli): qoldiq ko'paymaydi."),
            ReturnProductOutcome.Supplier => Tr.T("Товар вернётся на склад, затем откроется «Возврат поставщику» с этим товаром — выберите поставщика и проведите.",
                "Товар кампага кайтат, андан кийин ушул товар менен «Жеткирүүчүгө кайтаруу» ачылат — жеткирүүчүнү тандап, жүргүзүңүз.",
                "The item goes back to stock, then “Return to supplier” opens with it — choose the supplier and post it.",
                "Ürün stoğa döner, sonra bu ürünle «Tedarikçiye iade» açılır — tedarikçiyi seçip kaydedin.",
                "Mahsulot omborga qaytadi, so'ng shu mahsulot bilan «Yetkazib beruvchiga qaytarish» ochiladi — yetkazib beruvchini tanlab, o'tkazing."),
            _ => Tr.T("Товар вернётся на склад и снова будет в продаже.", "Товар кампага кайтып, кайра сатууга чыгат.", "The item goes back to stock and on sale again.",
                "Ürün stoğa döner ve yeniden satışa çıkar.", "Mahsulot omborga qaytadi va yana sotuvga chiqadi."),
        };
    }

    /// <summary>2026-11-13 (Л-5): «Куда товар» — на полку / в брак / поставщику (по умолчанию на полку).</summary>
    private void BuildOutcome()
    {
        OutcomeLabel.Text = Tr.T("Куда товар", "Товар кайда", "Where the item goes", "Ürün nereye", "Mahsulot qayerga");
        var items = new[]
        {
            (ReturnProductOutcome.Shelf, Tr.T("На полку", "Текчеге", "Back on the shelf", "Rafa", "Javonga")),
            (ReturnProductOutcome.Defect, Tr.T("В брак", "Бракка", "To defective", "Fireye", "Nuqsonliga")),
            (ReturnProductOutcome.Supplier, Tr.T("Поставщику", "Жеткирүүчүгө", "To the supplier", "Tedarikçiye", "Yetkazib beruvchiga")),
        };
        foreach (var (outcome, text) in items)
        {
            var radio = new RadioButton { Classes = { "modal-seg" }, GroupName = "return-outcome", Content = text, Margin = new Avalonia.Thickness(0, 0, 8, 4), IsChecked = outcome == Outcome };
            Avalonia.Automation.AutomationProperties.SetName(radio, OutcomeLabel.Text + ": " + text);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && Outcome != outcome)
                    SetOutcome(outcome);
            };
            _outcomeButtons.Add(radio);
            OutcomeWrap.Children.Add(radio);
        }
        SetOutcome(Outcome);
    }

    /// <summary>2026-11-13 (Л-5): справочник быстрых причин возврата — один на все окна (текст уходит на сервер как reason, по нему же строится отчёт владельца «Возвраты по причинам»).
    /// Defect — причина сама по себе ставит исход «В брак». «Своя причина» кассир вводит в поле ниже.</summary>
    internal static (string Text, bool Defect)[] QuickReasons(bool clothing, bool electronics)
    {
        var changed = (Tr.T("Передумал", "Оюн өзгөрттү", "Changed mind", "Vazgeçti", "Fikridan qaytdi"), false);
        var defect = (Tr.T("Брак", "Бузук", "Defective", "Kusurlu", "Nuqsonli"), true);
        if (clothing)
            return new[]
            {
                (Tr.T("Не подошёл размер", "Өлчөмү туура келген жок", "Wrong size", "Beden uymadı", "O'lchami to'g'ri kelmadi"), false),
                (Tr.T("Не подошёл цвет", "Түсү жаккан жок", "Wrong colour", "Renk uymadı", "Rangi to'g'ri kelmadi"), false),
                changed, defect,
            };
        var list = new List<(string, bool)>
        {
            changed, defect,
            (Tr.T("Ошибка кассира", "Кассирдин катасы", "Cashier error", "Kasiyer hatası", "Kassir xatosi"), false),
            (Tr.T("Не тот товар", "Башка товар", "Wrong item", "Yanlış ürün", "Boshqa mahsulot"), false),
        };
        // 2026-11-13 (сервер sn1311, «Электроника»): «Гарантия» — гарантийный случай; срок гарантии касса сверяет по номеру (warranty_active).
        if (electronics)
            list.Add((ElectronicsProfile.WarrantyReason, false));
        return list.ToArray();
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-33): частые причины — одной кнопкой (их можно дописать), «Брак» сразу ставит исход «В брак».
    /// 2026-11-13 (Л-5): справочник причин общий для всех сфер (<see cref="QuickReasons"/>) + «Своя причина»; «Брак» теперь и в «Продуктах» — это выбор кассира, по умолчанию «На полку».</summary>
    private void BuildQuickReasons()
    {
        foreach (var (text, defect) in QuickReasons(MarketSpheres.IsClothing, ElectronicsProfile.IsActive))
            QuickReasonsWrap.Children.Add(MakeReasonChip(text, defect));
        var own = new Button
        {
            Content = "✎ " + Tr.T("Своя причина", "Өз себебим", "Own reason", "Kendi nedenim", "O'z sababim"), Margin = new Avalonia.Thickness(0, 0, 8, 8), FontSize = 13, Focusable = false,
            Classes = { "modal-chip" },
        };
        own.Click += (_, _) =>
        {
            ReasonBox.Text = "";
            ReasonBox.Focus();
        };
        QuickReasonsWrap.Children.Add(own);
        BuildOutcome();
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
                SetOutcome(ReturnProductOutcome.Defect);
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
            ReasonBox.Focus(); // 2026-10-22 (модалки, пакет 1): ошибка под полем — фокус в поле.
            return;
        }

        Close(true);
    }
}
