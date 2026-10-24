using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-09: открывается прямо в момент "товар не найден" голосом (MainWindow.axaml.cs,
/// OnVoiceCommandRecognized) — предлагает сразу привязать фразу, которую только что не распознали,
/// к конкретному товару, вместо того чтобы кассир шёл в Настройки → Регистрация голоса и печатал
/// фразу заново. Тот же механизм "обучения", что и карточка в VoiceControlTestWindow
/// (VoiceLexiconStore.AddProductAlias) — эта фраза следующий раз найдёт товар напрямую, в обход
/// обычного пословного поиска (см. VoiceCommandParser.FindProducts).</summary>
public sealed class TeachVoicePhraseDialog : Window
{
    private readonly TextBox _phraseBox;
    private readonly AutoCompleteBox _productBox;
    private readonly Button _saveButton;
    private readonly TextBlock _errorText;

    public TeachVoicePhraseDialog(string phrase, System.Collections.Generic.IReadOnlyList<CatalogProductTileVm> products)
    {
        Title = Tr.T("Товар не найден — обучить?", "Товар табылган жок — үйрөтөсүзбү?", "Product not found — teach it?", "Ürün bulunamadı — öğretilsin mi?", "Mahsulot topilmadi — o'rgatilsinmi?");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (S 512) вместо системной: Esc/✕ — «Не сейчас», кнопки и
        // поля 44 px, цвета темы вместо Gray/Red, ошибка — под полем товара. В многострочном поле фраз Enter переносит
        // строку, «Привязать» — Ctrl+Enter (подсказка слева в подвале) или клик; Enter в поле товара — «Привязать».
        SectionCards.AsModal(this, 512);

        TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return label;
        }

        var panel = new StackPanel { Spacing = 8 };

        var intro = new TextBlock
        {
            Text = Tr.T(
                "Голос не нашёл товар по этой фразе. Привяжите её к товару сейчас — можно сразу дописать ещё варианты, как ещё могут это сказать (по одной фразе на строку, лучше от 3) — чем больше вариантов, тем надёжнее распознавание в следующий раз.",
                "Үн боюнча бул фраза менен товар табылган жок. Аны азыр товарга байланыштырыңыз — ошол эле жерде аны башкача кантип айтышы мүмкүн болгон варианттарды да кошуп жазсаңыз болот (ар бир сапка бир фраза, 3 же андан көп болгону жакшы) — варианттар канча көп болсо, кийинки жолу ошончолук так таанылат.",
                "Voice search couldn't match this phrase to a product. Link it now and add other ways people might say it (one per line, ideally 3+) — the more variants, the better recognition next time.",
                "Sesli komut bu ifadeyle bir ürün bulamadı. İfadeyi şimdi bir ürüne bağlayın — söylenebilecek başka varyantları da hemen ekleyebilirsiniz (her satıra bir ifade, tercihen 3 veya daha fazla). Varyant ne kadar çoksa, bir dahaki sefere tanıma o kadar güvenilir olur.",
                "Ovozli qidiruv bu ibora bo'yicha mahsulotni topmadi. Iborani hozir mahsulotga bog'lang — darhol boshqa aytilish variantlarini ham yozib qo'yish mumkin (har qatorga bitta ibora, yaxshisi kamida 3 ta) — variantlar qancha ko'p bo'lsa, keyingi safar tanib olish shuncha ishonchli bo'ladi."),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        intro.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(intro);

        var phrasesLabel = Tr.T("Фразы (по одной на строку)", "Фразалар (ар бир сапка бирден)", "Phrases (one per line)", "İfadeler (her satıra bir tane)", "Iboralar (har qatorga bittadan)");
        panel.Children.Add(Label(phrasesLabel));
        _phraseBox = SectionCards.Field(new TextBox { Text = phrase, AcceptsReturn = true, MinHeight = 88, MaxHeight = 160, TextWrapping = TextWrapping.Wrap });
        Avalonia.Automation.AutomationProperties.SetName(_phraseBox, phrasesLabel);
        panel.Children.Add(_phraseBox);

        var productLabel = Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");
        panel.Children.Add(Label(productLabel));
        _productBox = SectionCards.Field(new AutoCompleteBox
        {
            ItemsSource = products,
            Watermark = Tr.T("Начните вводить название товара…", "Товардын атын жаза баштаңыз…", "Start typing the product name…", "Ürün adını yazmaya başlayın…", "Mahsulot nomini yoza boshlang…"),
        });
        Avalonia.Automation.AutomationProperties.SetName(_productBox, productLabel);
        _productBox.ItemFilter = (search, item) =>
            item is CatalogProductTileVm p && p.Title.Contains(search ?? "", StringComparison.OrdinalIgnoreCase);
        _productBox.ItemSelector = (search, item) => item is CatalogProductTileVm p ? p.Title : "";
        _productBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CatalogProductTileVm>(
            (p, _) => new TextBlock { Text = p?.Title ?? "" });
        panel.Children.Add(_productBox);

        _errorText = SectionCards.ErrorText(this);
        panel.Children.Add(_errorText);

        var btnCancel = SectionCards.Cancel(Tr.T("Не сейчас", "Азыр эмес", "Not now", "Şimdi değil", "Hozir emas"));
        btnCancel.Click += (_, _) => Close(false);
        _saveButton = SectionCards.Primary(Tr.T("Привязать", "Байланыштыруу", "Link", "Bağla", "Bog'lash"), isDefault: true);
        _saveButton.Click += Save_Click;

        // Ctrl+Enter в поле фраз — «Привязать» (обычный Enter переносит строку).
        _phraseBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                e.Handled = true;
                Save_Click(_saveButton, new Avalonia.Interactivity.RoutedEventArgs());
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var hint = new TextBlock
        {
            Text = Tr.T("Ctrl+Enter — привязать", "Ctrl+Enter — байланыштыруу", "Ctrl+Enter — link", "Ctrl+Enter — bağla", "Ctrl+Enter — bog'lash"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        var shell = SectionCards.Shell(Title, null, panel, SectionCards.Buttons(btnCancel, _saveButton), hint);
        shell.InitialFocus = _productBox;
        Content = shell;
    }

    private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var phrases = (_phraseBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var product = _productBox.SelectedItem as CatalogProductTileVm;

        if (phrases.Count == 0 || product is null)
        {
            SectionCards.ShowError(_errorText, Tr.T(
                "Укажите хотя бы одну фразу и выберите товар из списка.",
                "Жок дегенде бир фразаны жазып, тизмеден товарды тандаңыз.",
                "Enter at least one phrase and select a product from the list.",
                "En az bir ifade girin ve listeden bir ürün seçin.",
                "Kamida bitta ibora kiriting va ro'yxatdan mahsulotni tanlang."), phrases.Count == 0 ? _phraseBox : _productBox);
            return;
        }

        try
        {
            foreach (var phrase in phrases)
                VoiceLexiconStore.AddProductAlias(phrase, product.Id, product.Title);
        }
        catch (Exception ex)
        {
            _errorText.Text = ex.Message;
            _errorText.IsVisible = true;
            return;
        }

        Close(true);
    }
}
