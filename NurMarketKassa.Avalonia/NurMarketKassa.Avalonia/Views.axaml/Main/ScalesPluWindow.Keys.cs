using NurMarketKassa.Core.Application;
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
        // 2026-10-22 (модалки, пакет 3): окно без рамки ОС в общей оболочке ModalShell (L = 912, было своё 1100×760):
        // «Готово» в закреплённом подвале (Esc, Enter и ✕ закрывают), кнопки весов прокручиваются между шапкой и подвалом.
        var window = new Window
        {
            Title = Tr.T("Кнопки весов", "Тараза баскычтары", "Scale keys", "Tartı tuşları", "Tarozi tugmalari") + " — " + ScaleTitle,
        };
        SectionCards.AsModal(window, 912);
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
        var countBox = new NumericUpDown { Minimum = 1, Maximum = KeyIsPlu ? Math.Min(240, MaxPlu) : MaxHotkey, Increment = 1, FormatString = "0", Value = KeyCount, Width = 140, Classes = { "modal-field" } };
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
                // 2026-10-22 (модалки, пакет 3): фон плитки — привязкой к теме (BrushSurfaceSubtle в теме нет — в тёмной теме
                // пустые кнопки были светлыми WhiteSmoke).
                tile.Bind(Button.BackgroundProperty, this.GetResourceObservable(row is null ? "BrushPanelSoft" : "BrushAccentSoft"));
                tile.Bind(Button.ForegroundProperty, this.GetResourceObservable("BrushText"));
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
        var close = SectionCards.Primary(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        close.IsCancel = true;
        close.Click += (_, _) => window.Close();

        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(hint);
        top.Children.Add(ScaleUi.Row(Tr.T("Кнопок на весах", "Таразадагы баскычтар", "Keys on the scale", "Tartıdaki tuşlar", "Tarozidagi tugmalar"), countBox, 200));
        top.Children.Add(status);
        top.Children.Add(grid);
        window.Content = SectionCards.Shell(window.Title ?? "", null, top, SectionCards.Buttons(close));
        Rebuild();
        await window.ShowDialog(this).ConfigureAwait(true);
        ApplySearch();
    }

    /// <summary>Выбор товара для кнопки: поиск по названию/PLU, двойной щелчок или «Выбрать»; «Убрать с
    /// кнопки» — если на ней уже товар. null — отмена.</summary>
    private async Task<(ScalePluRowVm Row, bool Remove)?> PickProductAsync(Window owner, int key, ScalePluRowVm? current)
    {
        (ScalePluRowVm Row, bool Remove)? result = null;
        // 2026-10-22 (модалки, пакет 3): окно без рамки ОС в общей оболочке ModalShell (M = 672): поиск сверху, список на всю
        // высоту, «Отмена» (Esc = ✕) и «Выбрать» (Enter) в закреплённом подвале. Выбор — касанием строки и «Выбрать»
        // (двойное касание тоже работает, но больше не единственный способ на сенсорном экране); Enter в поиске выбирает
        // выделенный, а если ничего не выделено — первый найденный. «Убрать с кнопки» — красной обводкой слева в подвале.
        var dialog = new Window
        {
            Title = Tr.T($"Кнопка {key}: товар", $"{key}-баскыч: товар", $"Key {key}: product", $"{key}. tuş: ürün", $"{key}-tugma: tovar"),
        };
        SectionCards.AsModal(dialog, 672);
        var search = SectionCards.Field(new TextBox { Watermark = Tr.T("Поиск: название или PLU", "Издөө: аталышы же PLU", "Search: name or PLU", "Ara: ad veya PLU", "Qidirish: nomi yoki PLU") });
        var list = new ListBox { MinHeight = 220 };
        List<ScalePluRowVm> Filtered()
        {
            var plu = (search.Text ?? "").Trim();
            return SearchMatch.Filter(_allRows, plu, r => SearchMatch.Key(r.Name, r.PluText)) // 2026-10-29: единый поиск (слова в любом порядке, ё=е, раскладка)
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        void Fill()
        {
            list.ItemsSource = Filtered().Select(r => new ListBoxItem
            {
                Content = $"{r.Name}   ·   PLU {(string.IsNullOrEmpty(r.PluText) ? "—" : r.PluText)}   ·   {r.PriceLine}",
                Tag = r,
                MinHeight = 44,
                VerticalContentAlignment = VerticalAlignment.Center,
            }).ToList();
        }
        search.TextChanged += (_, _) => Fill();
        void Choose()
        {
            var item = list.SelectedItem as ListBoxItem ?? (list.ItemsSource as List<ListBoxItem>)?.FirstOrDefault();
            if (item is { Tag: ScalePluRowVm row })
            {
                result = (row, false);
                dialog.Close();
            }
        }
        list.DoubleTapped += (_, _) => Choose();
        var ok = SectionCards.Primary(Tr.T("Выбрать", "Тандоо", "Choose", "Seç", "Tanlash"), isDefault: true);
        ok.Click += (_, _) => Choose();
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => dialog.Close();
        Button? remove = null;
        if (current is not null)
        {
            remove = SectionCards.Secondary(Tr.T("Убрать с кнопки", "Баскычтан алуу", "Remove from the key", "Tuştan kaldır", "Tugmadan olish"));
            remove.Classes.Add("danger");
            remove.Click += (_, _) =>
            {
                result = (current, true);
                dialog.Close();
            };
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(search);
        Grid.SetRow(list, 1);
        list.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(list);
        var shell = SectionCards.Shell(dialog.Title ?? "",
            current is null ? null : Tr.T($"Сейчас на кнопке: {current.Name}", $"Азыр баскычта: {current.Name}", $"On the key now: {current.Name}", $"Şu an tuşta: {current.Name}", $"Hozir tugmada: {current.Name}"),
            root, SectionCards.Buttons(cancel, ok), remove);
        shell.ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        shell.InitialFocus = search;
        dialog.Content = shell;
        Fill();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}
