using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-09-30, просьба владельца: «PLU автоматически привяжи к кнопкам; поменяли PLU 10 на 1 — товар
/// должен привязаться к PLU 1 и к кнопке 1; поиск и фильтр весовых, показывай только весовые;
/// печать на A4 номеров кнопок с описанием и сохранение в Word».
///
/// • Кнопка весов = номер PLU (у весов «!0L» — TM-30F, Rongta напрямую — так и по умолчанию:
///   кнопка N вызывает ячейку N). Колонка «Клавиша» — исключение: вписанная кнопка важнее; пустая —
///   кнопка = PLU (показана серым). Сменили PLU — сменилась и кнопка, при отправке касса пишет обе.
/// • «Показать»: весовые на сайте (is_weight, по умолчанию) / все товары в кг / без PLU / с кнопкой.
/// • «Лист кнопок»: кнопка, PLU, товар, цена, код в ШК — печать на A4 или Word.
/// </summary>
public partial class ScalesPluWindow
{
    private enum ShowMode { SiteWeighted, AllKg, NoPlu, WithKey }

    private ShowMode _showMode = ShowMode.SiteWeighted;
    private bool _fillingShowCombo;

    /// <summary>Подписи и пункты «Показать» — до первой загрузки строк.</summary>
    private void InitKeySheetControls()
    {
        KeysButton.Content = Tr.T("Кнопки весов…", "Тараза баскычтары…", "Scale keys…", "Tartı tuşları…", "Tarozi tugmalari…");
        ToolTip.SetTip(KeysButton, Tr.T("Какой товар на какой кнопке весов: нажмите кнопку и выберите товар.",
            "Тараза баскычында кайсы товар: баскычты басып товарды тандаңыз.",
            "Which product is on which scale key: press a key and choose a product.",
            "Hangi ürün hangi tartı tuşunda: tuşa basıp ürün seçin.",
            "Qaysi tovar qaysi tarozi tugmasida: tugmani bosing va tovarni tanlang."));
        KeySheetButton.Content = Tr.T("Лист кнопок ▾", "Баскычтар барагы ▾", "Key sheet ▾", "Tuş listesi ▾", "Tugmalar varag‘i ▾");
        ToolTip.SetTip(KeySheetButton, Tr.T("Какая кнопка весов какой товар вызывает — печать на A4 или Word.",
            "Тараза баскычы кайсы товарды чакырат — A4 басып чыгаруу же Word.",
            "Which scale key calls which product — print on A4 or Word.",
            "Hangi tartı tuşu hangi ürünü çağırır — A4 yazdırma veya Word.",
            "Qaysi tarozi tugmasi qaysi tovarni chaqiradi — A4 chop etish yoki Word."));
        _fillingShowCombo = true;
        try
        {
            ShowCombo.ItemsSource = new List<ComboBoxItem>
            {
                new() { Content = Tr.T("Весовые на сайте", "Сайттагы салмактуулар", "Weighed on the website", "Sitede tartılı", "Saytda vaznli"), Tag = ShowMode.SiteWeighted },
                new() { Content = Tr.T("Все товары в кг", "Кг'дагы бардык товарлар", "All goods in kg", "Kg'daki tüm ürünler", "Kg dagi barcha tovarlar"), Tag = ShowMode.AllKg },
                new() { Content = Tr.T("Без PLU", "PLU'суз", "Without PLU", "PLU'suz", "PLU'siz"), Tag = ShowMode.NoPlu },
                new() { Content = Tr.T("С кнопкой на весах", "Таразада баскычы бар", "With a scale key", "Tartı tuşu olan", "Tarozida tugmasi bor"), Tag = ShowMode.WithKey },
            };
            ShowCombo.SelectedIndex = 0;
        }
        finally
        {
            _fillingShowCombo = false;
        }
    }

    /// <summary>Какие товары грузятся в список: «весовые на сайте» (галочка «Весовой» в карточке) —
    /// если в каталоге таких нет совсем, то все в кг, как раньше; «все в кг» — прежний список.</summary>
    private bool LoadsProduct(NurMarketKassa.Models.Pos.CatalogProductTileVm p, bool anyMustWeigh) =>
        _showMode == ShowMode.AllKg || !anyMustWeigh ? p.IsWeighted : p.MustWeigh;

    private void ShowCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingShowCombo || ShowCombo.SelectedItem is not ComboBoxItem { Tag: ShowMode mode })
            return;
        var reload = (mode == ShowMode.AllKg) != (_showMode == ShowMode.AllKg);
        _showMode = mode;
        if (reload)
        {
            // Другой набор товаров: отмеченные и скрытые не должны уйти на весы — список заново.
            RememberProfileSelection();
            LoadRows();
        }
        else
        {
            ApplySearch();
        }
    }

    /// <summary>Фильтр «без PLU» / «с кнопкой» поверх поиска и категории.</summary>
    private bool PassesShowFilter(ScalePluRowVm row) => _showMode switch
    {
        ShowMode.NoPlu => !int.TryParse(row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0,
        ShowMode.WithKey => EffectiveKey(row) is not null,
        _ => true,
    };

    /// <summary>Кнопка весов товара: вписанная в «Клавиша», иначе у весов «!0L» — номер PLU (если
    /// такая кнопка бывает). У Штрих-ПРИНТ — только вписанная (клавиши там задают сами).</summary>
    private int? EffectiveKey(ScalePluRowVm row)
    {
        // Rongta: кнопка = PLU всегда (раскладку «!0L» весы не принимают).
        if (!IsRongtaLan && int.TryParse((row.HotkeyText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var key) && key is >= 1 and <= MaxHotkey)
            return key;
        if (IsDahuaWire && int.TryParse(row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var plu) && plu is >= 1 and <= MaxHotkey)
            return plu;
        return null;
    }

    /// <summary>Серая подсказка в пустой «Клавише»: кнопка = PLU.</summary>
    private void UpdateHotkeyAuto()
    {
        foreach (var row in _allRows)
        {
            row.HotkeyAuto = IsDahuaWire && int.TryParse(row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var plu) && plu is >= 1 and <= MaxHotkey
                ? plu.ToString(CultureInfo.InvariantCulture)
                : "";
        }
    }

    /// <summary>Кнопки для записи на весы «!0L»: сначала вписанные вручную, затем кнопка = PLU.</summary>
    private Dictionary<int, int> BuildDahuaKeyMap(IReadOnlyList<string> recordIds, IReadOnlyList<int> pluByRecord)
    {
        var rowsById = _allRows.ToDictionary(r => r.Id);
        var map = new Dictionary<int, int>();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < recordIds.Count; i++)
            {
                if (!rowsById.TryGetValue(recordIds[i], out var row))
                    continue;
                var explicitKey = int.TryParse((row.HotkeyText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k is >= 1 and <= MaxHotkey;
                if (pass == 0 && explicitKey)
                    map.TryAdd(k, pluByRecord[i]);
                else if (pass == 1 && !explicitKey && pluByRecord[i] is >= 1 and <= MaxHotkey)
                    map.TryAdd(pluByRecord[i], pluByRecord[i]);
            }
        }
        return map;
    }

    private List<ScaleKeySheetRow> KeySheetRows() =>
        _allRows.Where(r => r.IsSelected)
            .Select(r => (Row: r, Key: EffectiveKey(r)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!.Value)
            .Select(g => g.First())
            .OrderBy(x => x.Key)
            .Select(x => new ScaleKeySheetRow(
                x.Key!.Value,
                int.TryParse(x.Row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var plu) ? plu : 0,
                x.Row.Name ?? "",
                x.Row.PriceLine ?? "",
                (x.Row.BarcodeCode ?? "").Trim()))
            .ToList();

    private string ScaleTitle => $"{LabelScaleStore.Active.Name} · {ScaleUi.LabelBrandTitle(_brand)}";

    private void KeySheet_Click(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        var print = new MenuItem { Header = Tr.T("Печать на A4", "A4 басып чыгаруу", "Print on A4", "A4 yazdır", "A4 chop etish") };
        foreach (var printer in ScaleKeySheetService.InstalledPrinters())
        {
            var item = new MenuItem { Header = printer };
            item.Click += async (_, _) => await PrintKeySheetAsync(printer).ConfigureAwait(true);
            print.Items.Add(item);
        }
        if (print.Items.Count == 0)
            print.IsEnabled = false;
        var word = new MenuItem { Header = Tr.T("Сохранить в Word…", "Word'го сактоо…", "Save to Word…", "Word'e kaydet…", "Word'ga saqlash…") };
        word.Click += async (_, _) => await SaveKeySheetWordAsync().ConfigureAwait(true);
        flyout.Items.Add(print);
        flyout.Items.Add(word);
        flyout.ShowAt(KeySheetButton);
    }

    private bool CheckKeySheetRows(List<ScaleKeySheetRow> rows)
    {
        if (rows.Count > 0)
            return true;
        StatusText.Text = Tr.T("Нет товаров с кнопками: отметьте товары — кнопка берётся из колонки «Клавиша» или равна PLU.",
            "Баскычы бар товар жок: товарларды белгилеңиз — баскыч «Баскыч» тилкесинен алынат же PLU'га барабар.",
            "No products with keys: tick products — the key comes from the “Key” column or equals the PLU.",
            "Tuşlu ürün yok: ürünleri işaretleyin — tuş «Tuş» sütunundan gelir veya PLU'ya eşittir.",
            "Tugmali tovar yo‘q: tovarlarni belgilang — tugma «Tugma» ustunidan olinadi yoki PLU'ga teng.");
        return false;
    }

    private async Task PrintKeySheetAsync(string printer)
    {
        var rows = KeySheetRows();
        if (!CheckKeySheetRows(rows))
            return;
        var title = ScaleTitle;
        var shop = UserPreferences.Instance.StoreName;
        StatusText.Text = Tr.T("Печать листа кнопок…", "Баскычтар барагы басылууда…", "Printing the key sheet…", "Tuş listesi yazdırılıyor…", "Tugmalar varag‘i chop etilmoqda…");
        var error = await Task.Run(() => ScaleKeySheetService.Print(printer, title, shop, rows)).ConfigureAwait(true);
        StatusText.Text = error is null
            ? Tr.T($"Лист кнопок отправлен на печать ({printer}): {rows.Count} кнопок.", $"Баскычтар барагы басууга жөнөтүлдү ({printer}): {rows.Count} баскыч.",
                   $"Key sheet sent to the printer ({printer}): {rows.Count} keys.", $"Tuş listesi yazıcıya gönderildi ({printer}): {rows.Count} tuş.",
                   $"Tugmalar varag‘i chop etishga yuborildi ({printer}): {rows.Count} tugma.")
            : Tr.T("Печать не удалась: ", "Басып чыгаруу болбоду: ", "Printing failed: ", "Yazdırma başarısız: ", "Chop etish bo‘lmadi: ") + error;
    }

    private async Task SaveKeySheetWordAsync()
    {
        var rows = KeySheetRows();
        if (!CheckKeySheetRows(rows))
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Лист кнопок весов (Word)", "Тараза баскычтарынын барагы (Word)", "Scale key sheet (Word)", "Tartı tuş listesi (Word)", "Tarozi tugmalari varag‘i (Word)"),
            SuggestedFileName = $"scale-keys-{System.DateTime.Now:yyyy-MM-dd}.docx",
            FileTypeChoices = [new FilePickerFileType("Word") { Patterns = ["*.docx"] }],
        }).ConfigureAwait(true);
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        var title = ScaleTitle;
        var shop = UserPreferences.Instance.StoreName;
        try
        {
            await Task.Run(() => ScaleKeySheetService.SaveWord(path!, title, shop, rows)).ConfigureAwait(true);
            StatusText.Text = Tr.T($"Лист кнопок сохранён: {path}", $"Баскычтар барагы сакталды: {path}", $"Key sheet saved: {path}", $"Tuş listesi kaydedildi: {path}", $"Tugmalar varag‘i saqlandi: {path}");
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Лист кнопок весов: Word не сохранён: {ex}", "SCALES");
            StatusText.Text = Tr.T("Не удалось сохранить: ", "Сактоо болбоду: ", "Could not save: ", "Kaydedilemedi: ", "Saqlab bo‘lmadi: ") + ex.Message;
        }
    }
}
