using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-20 («в один товар и доп. штрихкоды»: «Асу» и «Асу со вкусом клубники» — один товар): «Команда ИИ» над строкой ввода, пока приложена накладная — переключатель «Объединять вкусы/варианты
/// в один товар» (по умолчанию ВКЛ), «Своя команда ИИ» («не объединяй», «бренд — Асу»; добавляется к инструкции чтения накладной) и «Сбросить». Значения хранит <see cref="InvoiceVariantPrefs"/>
/// (у каждого аккаунта свои). Ту же команду можно сказать/написать в чате: «объединяй вкусы в один товар», «не объединяй» — <see cref="InvoiceVariantCommand"/> (как команды языка).</summary>
public sealed partial class AiAdvisorWindow
{
    private Border? _variantBar;
    private CheckBox? _variantMerge;
    private TextBox? _variantCommand;
    private StackPanel? _variantCommandHost;
    private Button? _variantCommandToggle;
    private TextBlock? _variantNote;
    private bool _variantSync;

    /// <summary>Панель «Команда ИИ». Скрыта, пока нет вложений (накладной); показывается из <see cref="RenderAttachments"/>.</summary>
    private Control BuildVariantBar()
    {
        _variantMerge = new CheckBox
        {
            Content = T("Объединять вкусы/варианты в один товар", "Даамдарды/варианттарды бир товарга бириктирүү", "Merge flavors/variants into one product",
                "Tatları/varyantları tek üründe birleştir", "Ta'm/variantlarni bitta mahsulotga birlashtirish"),
            MinHeight = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        _variantMerge.Classes.Add("ToggleSwitch");
        ToolTip.SetTip(_variantMerge, T("«Асу» и «Асу со вкусом клубники» — один товар: основной штрихкод и дополнительные штрихкоды с названием вкуса. Перед записью всё показывается.",
            "«Асу» жана «Асу кулпунай даамы» — бир товар: негизги штрихкод жана даамдын аты менен кошумча штрихкоддор. Жазуудан мурун баары көрсөтүлөт.",
            "“Asu” and “Asu strawberry” are one product: a main barcode plus extra barcodes named after the flavor. Everything is shown before saving.",
            "«Asu» ve «Asu çilekli» tek üründür: ana barkod ve tat adıyla ek barkodlar. Kaydetmeden önce her şey gösterilir.",
            "«Asu» va «Asu qulupnay ta'mli» — bitta mahsulot: asosiy shtrix-kod va ta'm nomi bilan qo'shimcha shtrix-kodlar. Yozishdan oldin hammasi ko'rsatiladi."));
        Avalonia.Automation.AutomationProperties.SetName(_variantMerge, T("Объединять вкусы в один товар", "Даамдарды бир товарга бириктирүү", "Merge flavors into one product", "Tatları tek üründe birleştir", "Ta'mlarni bitta mahsulotga birlashtirish"));
        _variantMerge.IsCheckedChanged += (_, _) =>
        {
            if (_variantSync)
                return;
            InvoiceVariantPrefs.SetMerge(_variantMerge.IsChecked == true);
            RefreshVariantBar();
        };

        _variantCommandToggle = UiKit.Ghost(this, T("Команда ИИ", "ЖИ буйругу", "AI command", "YZ komutu", "SI buyrug'i"));
        _variantCommandToggle.Height = 36;
        _variantCommandToggle.FontSize = 12.5;
        _variantCommandToggle.Padding = new Thickness(12, 0);
        _variantCommandToggle.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(_variantCommandToggle, T("Своя команда ИИ при чтении накладной (например «не объединяй», «бренд — Асу»)", "Накладнойду окуганда ЖИ үчүн өз буйругуңуз (мисалы «бириктирбе», «бренд — Асу»)",
            "Your own AI command for reading the invoice (for example “don't merge”, “brand — Asu”)", "Faturayı okurken YZ için kendi komutunuz (örneğin «birleştirme», «marka — Asu»)",
            "Yuk xatini o'qiyotganda SI uchun o'z buyrug'ingiz (masalan «birlashtirma», «brend — Asu»)"));
        var reset = UiKit.Ghost(this, T("Сбросить", "Баштапкыга", "Reset", "Sıfırla", "Tozalash"));
        reset.Height = 36;
        reset.FontSize = 12.5;
        reset.Padding = new Thickness(12, 0);
        ToolTip.SetTip(reset, T("Объединение снова включено, своя команда очищена", "Бириктирүү кайра күйгүзүлөт, өз буйрук тазаланат", "Merging is on again, your command is cleared", "Birleştirme yine açık, komutunuz temizlenir", "Birlashtirish yana yoqiladi, o'z buyrug'ingiz tozalanadi"));
        reset.Click += (_, _) =>
        {
            InvoiceVariantPrefs.Reset();
            RefreshVariantBar();
        };
        _variantCommandToggle.Click += (_, _) =>
        {
            if (_variantCommandHost is null)
                return;
            _variantCommandHost.IsVisible = !_variantCommandHost.IsVisible;
            if (_variantCommandHost.IsVisible)
                _variantCommand?.Focus();
        };

        var top = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
        };
        Grid.SetColumn(_variantCommandToggle, 1);
        Grid.SetColumn(reset, 2);
        top.Children.Add(_variantMerge);
        top.Children.Add(_variantCommandToggle);
        top.Children.Add(reset);

        var caption = new TextBlock { Text = T("Своя команда ИИ", "ЖИ үчүн өз буйругуңуз", "Your AI command", "Kendi YZ komutunuz", "SI uchun o'z buyrug'ingiz"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        _variantCommand = UiKit.Input(this, T("например: не объединяй · бренд — Асу", "мисалы: бириктирбе · бренд — Асу", "for example: don't merge · brand — Asu", "örneğin: birleştirme · marka — Asu", "masalan: birlashtirma · brend — Asu"), 36);
        _variantCommand.MaxLength = InvoiceVariantPrefs.MaxCommandLength;
        _variantCommand.HorizontalAlignment = HorizontalAlignment.Stretch;
        Avalonia.Automation.AutomationProperties.SetName(_variantCommand, T("Своя команда ИИ", "ЖИ үчүн өз буйругуңуз", "Your AI command", "Kendi YZ komutunuz", "SI uchun o'z buyrug'ingiz"));
        void CommitCommand()
        {
            if (_variantSync || _variantCommand is null)
                return;
            InvoiceVariantPrefs.SetCommand(_variantCommand.Text);
            RefreshVariantBar();
        }
        _variantCommand.LostFocus += (_, _) => CommitCommand();
        _variantCommand.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                CommitCommand();
                e.Handled = true;
            }
        };
        _variantNote = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
        Use(_variantNote, TextBlock.ForegroundProperty, "BrushWarning");
        _variantCommandHost = new StackPanel { IsVisible = false, Margin = new Thickness(0, 6, 0, 0), Children = { caption, _variantCommand, _variantNote } };

        _variantBar = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), Margin = new Thickness(4, 0, 0, 4), BorderThickness = new Thickness(1), IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel { Children = { top, _variantCommandHost } },
        };
        Use(_variantBar, Border.BorderBrushProperty, "BrushBorder");
        RefreshVariantBar();
        return _variantBar;
    }

    /// <summary>Привести панель в соответствие с сохранёнными настройками; показать, пока есть вложения.</summary>
    private void RefreshVariantBar()
    {
        if (_variantBar is null || _variantMerge is null || _variantCommand is null || _variantNote is null || _variantCommandHost is null)
            return;
        _variantSync = true;
        try
        {
            _variantMerge.IsChecked = InvoiceVariantPrefs.Merge;
            if (!_variantCommand.IsFocused)
                _variantCommand.Text = InvoiceVariantPrefs.Command;
            var forbids = InvoiceVariantPrefs.Merge && InvoiceVariantPrefs.CommandForbidsMerge(InvoiceVariantPrefs.Command);
            _variantNote.IsVisible = forbids;
            _variantNote.Text = forbids
                ? T("Своя команда просит не объединять — вкусы будут отдельными товарами.", "Өз буйругуңуз бириктирбөөнү суранат — даамдар өзүнчө товар болот.", "Your command asks not to merge — flavors stay separate products.",
                    "Komutunuz birleştirmeyi istemiyor — tatlar ayrı ürün olur.", "Buyrug'ingiz birlashtirmaslikni so'raydi — ta'mlar alohida mahsulot bo'ladi.")
                : "";
            if (InvoiceVariantPrefs.Command.Length > 0)
                _variantCommandHost.IsVisible = true;
            _variantBar.IsVisible = _attachments.Count > 0;
        }
        finally
        {
            _variantSync = false;
        }
    }

    /// <summary>Фраза владельца про объединение вариантов («объединяй вкусы в один товар», «не объединяй», «бренд — Асу», «сбрось команду») — выполняет программа без нейросети.
    /// Возвращает true, если это была только команда (в ИИ не уходит); с приложенной накладной команда применяется, а запрос идёт дальше.</summary>
    private bool TryHandleVariantCommand(string question, bool hasInvoice)
    {
        if (InvoiceVariantCommand.Detect(question) is not { Any: true } command)
            return false;
        var merge = command.Reset ? true : command.Merge ?? InvoiceVariantPrefs.Merge;
        var text = command.Reset ? "" : command.Command ?? InvoiceVariantPrefs.Command;
        // «не объединяй» в чате — переключатель выключается; «объединяй» — включается и своя команда «не объединяй» снимается.
        if (command.Merge == true && InvoiceVariantCommand.WantsSeparate(text))
            text = "";
        InvoiceVariantPrefs.Set(merge, text);
        RefreshVariantBar();
        PosLogger.Log($"ИИ-советник: команда по вариантам накладной — объединять: {InvoiceVariantPrefs.Merge}, своя команда: {(InvoiceVariantPrefs.Command.Length > 0 ? "задана" : "нет")}.", "INFO");
        if (!hasInvoice)
            AddBubble(InvoiceVariantCommand.ConfirmText(command), fromOwner: false);
        return !hasInvoice;
    }
}
