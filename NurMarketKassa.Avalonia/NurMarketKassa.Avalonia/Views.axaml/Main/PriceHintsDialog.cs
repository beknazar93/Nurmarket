using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05 (волна 2-А, функция 7): «Подсказки цены» после прихода — список «Поднять цену с 85 до 95 (наценка 30 %)?» по товарам, у которых закупка
/// выросла, а цена продажи осталась прежней. Галочки по строкам, «Поднять выбранные» — пакетная правка цены тем же путём, что «цена продажи» в карточке
/// (ProductActions.UpdateFieldsAsync), затем «Печать ценников…» (окно печати ценников). Ничего не меняется без нажатия.</summary>
public sealed class PriceHintsDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly List<(PriceHint Hint, CheckBox Check)> _rows = new();
    private readonly TextBlock _message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _apply;
    private readonly Button _print;

    public PriceHintsDialog(IReadOnlyList<PriceHint> hints)
    {
        Title = T("Подсказки цены", "Баа кеңештери", "Price hints", "Fiyat ipuçları", "Narx maslahatlari");
        SectionCards.AsModal(this, 700);
        var panel = new StackPanel { Spacing = 6 };
        var list = new StackPanel { Spacing = 4 };
        foreach (var h in hints)
        {
            // 2026-11-05 (ревью волн 0–3, С11): рост цены больше чем на 50 % (например, закупку ввели за упаковку вместо штуки) — не отмечаем заранее и подсвечиваем.
            var big = h.CurrentSale > 0 && h.SuggestedSale > h.CurrentSale * 1.5;
            var line = new TextBlock { Text = h.Name + " — " + h.Text + $" · {T("закупка", "сатып алуу", "cost", "alış", "xarid")} {h.OldPurchase:0.##} → {h.NewPurchase:0.##}"
                + (big ? "  ⚠ " + T("рост больше 50 % — проверьте закупку (упаковка вместо штуки?)", "өсүш 50 %дан ашык — сатып алууну текшериңиз (даана ордуна таңгак?)", "rise over 50 % — check the cost (pack instead of a piece?)", "artış %50'den fazla — alışı kontrol edin (adet yerine paket mi?)", "o'sish 50 % dan ortiq — xaridni tekshiring (dona o'rniga qadoq?)") : ""),
                TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
            if (big)
                line.Foreground = Brushes.DarkOrange;
            var check = new CheckBox { IsChecked = !big, Content = line };
            Avalonia.Automation.AutomationProperties.SetName(check, h.Name + " — " + h.Text);
            list.Children.Add(check);
            _rows.Add((h, check));
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("BrushText"));
        panel.Children.Add(_message);

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        _print = SectionCards.Secondary(T("Печать ценников…", "Баа белгилерин басып чыгаруу…", "Print price tags…", "Fiyat etiketi yazdır…", "Narx belgilarini chop etish…"));
        _print.IsVisible = false;
        _print.Click += async (_, _) => await BulkPriceTagPrintDialog.Open(this).ConfigureAwait(true);
        _apply = SectionCards.Primary(T("Поднять выбранные", "Тандалгандарды көтөрүү", "Raise the selected", "Seçilenleri yükselt", "Tanlanganlarni ko'tarish"));
        _apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);
        Content = SectionCards.Shell(Title,
            T("После прихода закупка выросла, а цена продажи осталась прежней. Предлагаем цену с прежней наценкой (или целевой наценкой категории), «красиво» округлённую вверх.",
                "Кирүүдөн кийин сатып алуу баасы өстү, ал эми сатуу баасы мурункудай калды. Мурунку үстөк (же категориянын максаттуу үстөгү) менен, «кооз» жогору тегеректелген баа сунушталат.",
                "After the delivery the cost rose but the sale price stayed. We suggest the price with the old markup (or the category's target), rounded up nicely.",
                "Mal kabulünden sonra alış fiyatı yükseldi, satış fiyatı aynı kaldı. Eski kâr payıyla (veya kategori hedefiyle) «güzel» yukarı yuvarlanmış fiyat öneriyoruz.",
                "Kirimdan keyin xarid narxi oshdi, sotuv narxi esa avvalgicha qoldi. Avvalgi ustama (yoki kategoriya maqsadi) bilan «chiroyli» yuqoriga yaxlitlangan narx taklif qilamiz."),
            panel, SectionCards.Buttons(close, _print, _apply));
    }

    private async Task ApplyAsync()
    {
        var chosen = _rows.Where(r => r.Check.IsChecked == true).Select(r => r.Hint).ToList();
        if (chosen.Count == 0)
            return;
        _apply.IsEnabled = false;
        var ok = 0;
        var skipped = 0;
        var failed = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            foreach (var h in chosen)
            {
                // 2026-11-05 (ревью волн 0–3, С11): цену могли поправить после прихода (в карточке или на другой кассе) — свежую цену сверяем с той, что показали; поменяли — не затираем.
                if (ProductActions.Find(h.ProductId) is { } now && now.PriceValue > 0 && Math.Abs(now.PriceValue - h.CurrentSale) > 0.004)
                {
                    skipped++;
                    failed.Add($"{h.Name}: " + T($"цену уже поменяли (сейчас {now.PriceValue:0.##}) — пропущено", $"баа өзгөртүлгөн (азыр {now.PriceValue:0.##}) — өткөрүлдү", $"the price was already changed (now {now.PriceValue:0.##}) — skipped", $"fiyat zaten değiştirilmiş (şimdi {now.PriceValue:0.##}) — atlandı", $"narx allaqachon o'zgartirilgan (hozir {now.PriceValue:0.##}) — o'tkazib yuborildi"));
                    UncheckRow(h);
                    continue;
                }
                var result = await ProductActions.UpdateFieldsAsync(h.ProductId,
                    new Dictionary<string, object?> { ["price"] = h.SuggestedSale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) },
                    T("цена продажи", "сатуу баасы", "sale price", "satış fiyatı", "sotuv narxi"), PosApp.CurrentUserDisplayName, cts.Token).ConfigureAwait(true);
                if (result.Ok)
                {
                    ok++;
                    UncheckRow(h); // 2026-11-05 (Н6): повтор после частичной ошибки не пишет уже поднятые цены второй раз
                }
                else
                    failed.Add($"{h.Name}: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            failed.Add(T("не дождались ответа сервера", "сервердин жообу күтүлгөн жок", "no answer from the server", "sunucudan yanıt gelmedi", "server javob bermadi"));
        }
        PosLogger.Log($"Подсказки цены: поднято цен {ok} из {chosen.Count}, пропущено (цену уже поменяли) {skipped}, ошибок {failed.Count - skipped}.", "STOCK");
        _message.Text = T($"Цены подняты: {ok} из {chosen.Count}.", $"Баалар көтөрүлдү: {ok} / {chosen.Count}.", $"Prices raised: {ok} of {chosen.Count}.", $"Fiyatlar yükseltildi: {ok} / {chosen.Count}.", $"Narxlar ko'tarildi: {ok} / {chosen.Count}.")
                        + (failed.Count > 0 ? "\n" + string.Join("\n", failed.Take(4)) : "");
        _message.IsVisible = true;
        _print.IsVisible = ok > 0;
        _apply.IsEnabled = _rows.Any(r => r.Check.IsChecked == true);
    }

    private void UncheckRow(PriceHint hint)
    {
        foreach (var r in _rows.Where(r => ReferenceEquals(r.Hint, hint)))
        {
            r.Check.IsChecked = false;
            r.Check.IsEnabled = false;
        }
    }
}
