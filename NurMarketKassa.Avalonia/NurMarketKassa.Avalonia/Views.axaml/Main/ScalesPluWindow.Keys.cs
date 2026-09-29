using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-09-30, владелец: «где настройка горячих кнопок и привязка товара к этой кнопке?».
/// Окно «Кнопки весов»: сетка кнопок 1…N, на каждой — товар; нажали кнопку → выбрали товар → привязан.
/// • Rongta (напрямую): весы принимают «!0L», но раскладку не меняют — кнопка K всегда вызывает ячейку
///   PLU K. Поэтому «товар на кнопку K» = «PLU товара = K»: касса ставит товару PLU K (и на сайте), а
///   товар, который занимал PLU K, переезжает на первый свободный номер за кнопками.
/// • TM-30F, Штрих-ПРИНТ напрямую: кнопка пишется отдельно (колонка «Клавиша»).
/// Записываются на весы при «Отправить на весы».
/// </summary>
public partial class ScalesPluWindow
{
    /// <summary>У этих весов кнопка = номер PLU (раскладку кнопок они не принимают).</summary>
    private bool KeyIsPlu => IsRongtaLan;

    private int KeyCount => Math.Clamp(UserPreferences.Instance.ScaleKeyCount, 1, KeyIsPlu ? Math.Min(240, MaxPlu) : MaxHotkey);

    private static int? RowPlu(ScalePluRowVm row) =>
        int.TryParse(row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var plu) && plu > 0 ? plu : null;

    /// <summary>Ставит товару номер ячейки <paramref name="value"/> (закреплённый и PLU на сайте). Товар,
    /// который держит этот номер, переезжает на первый свободный номер после кнопок. Возвращает текст
    /// итога или ошибки.</summary>
    private async Task<string> MoveRowToPluAsync(ScalePluRowVm row, int value)
    {
        if (value < 1 || value > MaxPlu)
            return Tr.T($"PLU — число от 1 до {MaxPlu}.", $"PLU — 1ден {MaxPlu}гө чейинки сан.", $"PLU must be 1 to {MaxPlu}.", $"PLU 1 ile {MaxPlu} arasında olmalı.", $"PLU — 1 dan {MaxPlu} gacha.");
        if (RowPlu(row) == value && row.CatalogPlu == value)
            return "";
        var profile = LabelScaleStore.Active;
        var online = !OfflineModeHelper.UseLocalOperations;

        // Кто держит номер: закреплённый за другим товаром, PLU другого товара на сайте.
        var holders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in profile.PluNumbers.Where(kv => kv.Value == value && kv.Key != row.Id))
            holders.Add(kv.Key);
        foreach (var p in NurMarketKassa.Services.CatalogCacheService.Products.Where(p => p.Plu == value && p.Id != row.Id))
            holders.Add(p.Id);

        var note = "";
        if (holders.Count > 0)
        {
            var used = new HashSet<int>(profile.PluNumbers.Values);
            used.UnionWith(NurMarketKassa.Services.CatalogCacheService.Products.Where(p => p.Plu is > 0).Select(p => p.Plu!.Value));
            var next = KeyCount + 1;
            foreach (var holderId in holders)
            {
                while (next <= MaxPlu && used.Contains(next))
                    next++;
                if (next > MaxPlu)
                    return Tr.T("Нет свободного номера PLU, чтобы освободить кнопку.", "Баскычты бошотууга бош PLU номери жок.", "No free PLU number to free the key.", "Tuşu boşaltmak için boş PLU numarası yok.", "Tugmani bo‘shatish uchun bo‘sh PLU raqami yo‘q.");
                var holderRow = _allRows.FirstOrDefault(r => r.Id == holderId);
                var holderCached = NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == holderId);
                if (online && holderCached?.Plu == value)
                {
                    try
                    {
                        await App.CatalogApi.SetProductPluAsync(holderId, next).ConfigureAwait(true);
                        holderCached.Plu = next;
                        if (holderRow is not null)
                            holderRow.CatalogPlu = next;
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"Кнопки весов: PLU на сайте у «{holderCached.Title}» не изменён: {ex.Message}", "SCALES");
                        return Tr.T("Не удалось освободить PLU на сайте: ", "Сайтта PLU бошотулган жок: ", "Could not free the PLU on the website: ", "Sitede PLU boşaltılamadı: ", "Saytda PLU bo‘shatilmadi: ") + ex.Message;
                    }
                }
                if (profile.PluNumbers.ContainsKey(holderId) || holderRow is not null)
                    profile.PluNumbers[holderId] = next;
                used.Add(next);
                var holderName = holderRow?.Name ?? holderCached?.Title ?? holderId;
                PosLogger.Log($"Кнопки весов «{profile.Name}»: «{holderName}» PLU {value} → {next} (освобождена кнопка {value})", "SCALES");
                note += Tr.T($" «{holderName}» переехал на PLU {next}.", $" «{holderName}» PLU {next}ге көчтү.", $" “{holderName}” moved to PLU {next}.", $" «{holderName}» PLU {next}'e taşındı.", $" «{holderName}» PLU {next} ga ko‘chdi.");
            }
        }

        profile.PluNumbers[row.Id] = value;
        if (online && row.CatalogPlu != value)
        {
            try
            {
                await App.CatalogApi.SetProductPluAsync(row.Id, value).ConfigureAwait(true);
                row.CatalogPlu = value;
                var cached = NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == row.Id);
                if (cached is not null)
                    cached.Plu = value;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Кнопки весов: PLU на сайте у «{row.Name}» не изменён: {ex.Message}", "SCALES");
                note += Tr.T(" На сайте PLU не изменён: ", " Сайтта PLU өзгөргөн жок: ", " The website PLU was not changed: ", " Sitede PLU değişmedi: ", " Saytda PLU o‘zgarmadi: ") + ex.Message;
            }
        }
        LabelScaleStore.Save();
        PosLogger.Log($"Кнопки весов «{profile.Name}»: «{row.Name}» → PLU {value}", "SCALES");
        RefreshPluNumbers();
        row.SetStatus(Tr.T($"PLU {value} — отправьте на весы", $"PLU {value} — таразага жөнөтүңүз", $"PLU {value} — send to the scale", $"PLU {value} — tartıya gönderin", $"PLU {value} — taroziga yuboring"), RowState.None);
        return Tr.T($"«{row.Name}» — PLU {value}.", $"«{row.Name}» — PLU {value}.", $"“{row.Name}” — PLU {value}.", $"«{row.Name}» — PLU {value}.", $"«{row.Name}» — PLU {value}.") + note;
    }

    /// <summary>Товар на кнопку: у Rongta — PLU = кнопка; у остальных — «Клавиша» (у прежнего хозяина
    /// кнопки она снимается).</summary>
    private async Task<string> AssignKeyAsync(ScalePluRowVm row, int key)
    {
        row.IsSelected = true;
        if (KeyIsPlu)
        {
            row.HotkeyText = "";
            var result = await MoveRowToPluAsync(row, key).ConfigureAwait(true);
            RememberProfileSelection();
            return result;
        }
        foreach (var other in _allRows.Where(r => !ReferenceEquals(r, row) && (r.HotkeyText ?? "").Trim() == key.ToString(CultureInfo.InvariantCulture)))
            other.HotkeyText = "";
        row.HotkeyText = key.ToString(CultureInfo.InvariantCulture);
        RememberProfileSelection();
        UpdateHotkeyAuto();
        return Tr.T($"«{row.Name}» — кнопка {key}.", $"«{row.Name}» — {key}-баскыч.", $"“{row.Name}” — key {key}.", $"«{row.Name}» — {key}. tuş.", $"«{row.Name}» — {key}-tugma.");
    }

    /// <summary>Снять товар с кнопки: у Rongta — перенести на первый свободный PLU за кнопками.</summary>
    private async Task<string> UnassignKeyAsync(ScalePluRowVm row)
    {
        if (!KeyIsPlu)
        {
            row.HotkeyText = "";
            RememberProfileSelection();
            UpdateHotkeyAuto();
            return Tr.T($"«{row.Name}» снят с кнопки.", $"«{row.Name}» баскычтан алынды.", $"“{row.Name}” removed from the key.", $"«{row.Name}» tuştan kaldırıldı.", $"«{row.Name}» tugmadan olindi.");
        }
        var used = new HashSet<int>(LabelScaleStore.Active.PluNumbers.Values);
        used.UnionWith(NurMarketKassa.Services.CatalogCacheService.Products.Where(p => p.Plu is > 0).Select(p => p.Plu!.Value));
        var next = KeyCount + 1;
        while (next <= MaxPlu && used.Contains(next))
            next++;
        return await MoveRowToPluAsync(row, next).ConfigureAwait(true);
    }

    /// <summary>Колонка «Клавиша» у Rongta: вписали K → товар на кнопку K (PLU = K).</summary>
    private async Task<bool> HotkeyEditAsPluAsync(ScalePluRowVm row)
    {
        if (!KeyIsPlu)
            return false;
        var text = (row.HotkeyText ?? "").Trim();
        if (text.Length == 0 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key == RowPlu(row))
        {
            row.HotkeyText = "";
            return true;
        }
        StatusText.Text = await AssignKeyAsync(row, key).ConfigureAwait(true);
        return true;
    }

    // ------------------------------------------------------------------ окно «Кнопки весов»

    private async void KeysWindow_Click(object? sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = Tr.T("Кнопки весов", "Тараза баскычтары", "Scale keys", "Tartı tuşları", "Tarozi tugmalari") + " — " + ScaleTitle,
            Width = 1100,
            Height = 760,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Text = KeyIsPlu
                ? Tr.T("Нажмите кнопку и выберите товар. У весов Rongta кнопка вызывает ячейку PLU с тем же номером, поэтому касса поставит товару PLU = номер кнопки (и на сайте); товар, который был на этой кнопке, переедет на свободный номер. Потом — «Отправить на весы».",
                       "Баскычты басып товарды тандаңыз. Rongta таразасында баскыч ошол эле номердеги PLU уячасын чакырат, ошондуктан касса товарга PLU = баскычтын номери коёт (сайтта да); бул баскычта болгон товар бош номерге көчөт. Андан кийин — «Таразага жөнөтүү».",
                       "Press a key and choose a product. On a Rongta scale a key calls the PLU slot with the same number, so the till gives the product PLU = key number (on the website too); the product that was on this key moves to a free number. Then — “Send to scale”.",
                       "Bir tuşa basıp ürün seçin. Rongta tartıda tuş aynı numaralı PLU hücresini çağırır; kasa ürüne PLU = tuş numarası verir (sitede de); bu tuştaki ürün boş bir numaraya taşınır. Sonra — «Tartıya gönder».",
                       "Tugmani bosing va tovarni tanlang. Rongta tarozisida tugma shu raqamli PLU katagini chaqiradi, shuning uchun kassa tovarga PLU = tugma raqami beradi (saytda ham); bu tugmadagi tovar bo‘sh raqamga ko‘chadi. So‘ng — «Taroziga yuborish».")
                : Tr.T("Нажмите кнопку и выберите товар — касса запишет кнопку на весы при «Отправить на весы».",
                       "Баскычты басып товарды тандаңыз — касса баскычты «Таразага жөнөтүү» учурунда жазат.",
                       "Press a key and choose a product — the till writes the key on “Send to scale”.",
                       "Bir tuşa basıp ürün seçin — kasa tuşu «Tartıya gönder» sırasında yazar.",
                       "Tugmani bosing va tovarni tanlang — kassa tugmani «Taroziga yuborish» paytida yozadi."),
        };
        var countBox = new NumericUpDown { Minimum = 1, Maximum = KeyIsPlu ? Math.Min(240, MaxPlu) : MaxHotkey, Increment = 1, FormatString = "0", Value = KeyCount, Width = 140 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };

        void Rebuild()
        {
            grid.Children.Clear();
            var byKey = new Dictionary<int, ScalePluRowVm>();
            foreach (var row in _allRows)
            {
                if (EffectiveKey(row) is { } k && k <= KeyCount)
                    byKey.TryAdd(k, row);
            }
            for (var key = 1; key <= KeyCount; key++)
            {
                var k = key;
                byKey.TryGetValue(k, out var row);
                var tile = new Button
                {
                    Width = 150,
                    Height = 74,
                    Margin = new Thickness(0, 0, 8, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Background = row is null ? ScaleUi.ThemeBrush(this, "BrushSurfaceSubtle", Brushes.WhiteSmoke) : ScaleUi.ThemeBrush(this, "BrushAccentSoft", Brushes.LightYellow),
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = k.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeight.Bold, FontSize = 16 },
                            new TextBlock { Text = row?.Name ?? "—", FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                };
                tile.Click += async (_, _) =>
                {
                    var picked = await PickProductAsync(window, k, row).ConfigureAwait(true);
                    if (picked is null)
                        return;
                    status.Text = picked.Value.Remove
                        ? await UnassignKeyAsync(picked.Value.Row).ConfigureAwait(true)
                        : await AssignKeyAsync(picked.Value.Row, k).ConfigureAwait(true);
                    StatusText.Text = status.Text;
                    Rebuild();
                };
                grid.Children.Add(tile);
            }
        }

        countBox.ValueChanged += (_, _) =>
        {
            UserPreferences.Instance.ScaleKeyCount = (int)(countBox.Value ?? 70);
            UserPreferences.Instance.SaveToDisk();
            Rebuild();
        };
        var close = new Button { Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), Classes = { "btn-primary" }, MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();

        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(hint);
        top.Children.Add(ScaleUi.Row(Tr.T("Кнопок на весах", "Таразадагы баскычтар", "Keys on the scale", "Tartıdaki tuşlar", "Tarozidagi tugmalar"), countBox, 200));
        top.Children.Add(status);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        root.Children.Add(top);
        var scroll = new ScrollViewer { Content = grid, Margin = new Thickness(0, 10, 0, 10) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        window.Content = root;
        window.Opened += (_, _) => window.FitToKassaScreen();
        Rebuild();
        await window.ShowDialog(this).ConfigureAwait(true);
        ApplySearch();
    }

    /// <summary>Выбор товара для кнопки: поиск по названию/PLU, двойной щелчок или «Выбрать»; «Убрать с
    /// кнопки» — если на ней уже товар. null — отмена.</summary>
    private async Task<(ScalePluRowVm Row, bool Remove)?> PickProductAsync(Window owner, int key, ScalePluRowVm? current)
    {
        (ScalePluRowVm Row, bool Remove)? result = null;
        var dialog = new Window
        {
            Title = Tr.T($"Кнопка {key}: товар", $"{key}-баскыч: товар", $"Key {key}: product", $"{key}. tuş: ürün", $"{key}-tugma: tovar"),
            Width = 560,
            Height = 620,
            MinWidth = 380,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var search = new TextBox { Watermark = Tr.T("Поиск: название или PLU", "Издөө: аталышы же PLU", "Search: name or PLU", "Ara: ad veya PLU", "Qidirish: nomi yoki PLU") };
        var list = new ListBox();
        List<ScalePluRowVm> Filtered()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return _allRows.Where(r => words.All(w => (r.Name ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase) || r.PluText == w))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        void Fill()
        {
            list.ItemsSource = Filtered().Select(r => new ListBoxItem
            {
                Content = $"{r.Name}   ·   PLU {(string.IsNullOrEmpty(r.PluText) ? "—" : r.PluText)}   ·   {r.PriceLine}",
                Tag = r,
            }).ToList();
        }
        search.TextChanged += (_, _) => Fill();
        void Choose()
        {
            if (list.SelectedItem is ListBoxItem { Tag: ScalePluRowVm row })
            {
                result = (row, false);
                dialog.Close();
            }
        }
        list.DoubleTapped += (_, _) => Choose();
        var ok = new Button { Content = Tr.T("Выбрать", "Тандоо", "Choose", "Seç", "Tanlash"), Classes = { "btn-primary" }, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Choose();
        var cancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (current is not null)
        {
            var remove = new Button { Content = Tr.T($"Убрать «{current.Name}» с кнопки", $"«{current.Name}» баскычтан алуу", $"Remove “{current.Name}” from the key", $"«{current.Name}» tuştan kaldır", $"«{current.Name}» ni tugmadan olish") };
            remove.Click += (_, _) =>
            {
                result = (current, true);
                dialog.Close();
            };
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
        root.Children.Add(search);
        Grid.SetRow(list, 1);
        list.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(list);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.Opened += (_, _) => search.Focus();
        Fill();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}
