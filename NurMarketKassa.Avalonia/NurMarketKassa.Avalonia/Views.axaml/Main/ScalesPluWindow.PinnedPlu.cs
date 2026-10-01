using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-09-29: закреплённые номера PLU (живой баг клиента «Алтымыш ата», ШТРИХ-ПРИНТ М по
/// сети: «весовой товар таразага отправка кылган сайын ПЛУ алмашып кетти»; владелец: «надо сделать
/// так, чтобы товар фиксировался на определённый плу»; и второй баг «если менять ПЛУ, назначения
/// клавиш не работают в весах»).
///
/// Было: при каждой отправке номера раздавались подряд по текущему порядку отмеченных строк —
/// новый товар, другой фильтр или отправка части списка сдвигали всех ниже на другие ячейки. Клавиши
/// весов вызывают ячейку по номеру, поэтому под клавишей оказывался чужой товар; клавиши, которые
/// были запрограммированы на самих весах (или раньше, другой раскладкой), касса не трогала вовсе, а
/// старые ячейки оставались на весах со старыми товарами и ценами.
///
/// Стало: номер закрепляется за товаром один раз (LabelScaleProfile.PluNumbers, у каждых весов свой
/// список, файл label-scales.json вне папки программы) и показывается в колонке PLU; владелец может
/// его исправить. При отправке касса пишет товары в их ячейки, переводит на новые номера клавиши,
/// которые вызывали старый номер переехавшего товара (в том числе запрограммированные на весах), и
/// очищает старую ячейку. Правила — ScalePluPlanner (проверены отдельно, без окна).</summary>
public partial class ScalesPluWindow
{
    /// <summary>Номера ячеек раздаёт касса: прямая отправка на ШТРИХ-ПРИНТ и TM-30F. Сервер NurCRM,
    /// Rongta и AI-весы нумеруют сами (там колонка PLU — номер из карточки товара).</summary>
    private bool KassaNumbering => (_brand == BrandShtrikh && ShtrikhDirect) || IsDahuaWire;

    /// <summary>Галочка «Постоянный PLU за товаром» снята — номер ячейки = PLU из карточки товара
    /// (у товара без PLU в карточке — закреплённый номер).</summary>
    private bool UseCatalogPlu => SequentialPluCheck.IsChecked != true;

    /// <summary>Размер таблицы товаров весов — узнаём при отправке (11h); до этого проверяем только
    /// двухбайтовое поле номера ПЛУ протокола.</summary>
    private int _shtrikhTableSize;

    private int MaxPlu => IsDahuaWire ? DahuaTmProtocol.MaxPluNumber : (_shtrikhTableSize > 0 ? _shtrikhTableSize : 65535);

    private int PluStart =>
        int.TryParse((PluStartBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) && start > 0 ? start : 1;

    /// <summary>Товары, которые есть в каталоге. 2026-10-01, «кнопки весов слетают»: брались только
    /// из каталога в памяти — пока он загружен не полностью, закреплённый номер «пропавшего»
    /// товара считался свободным и уходил другому товару (у Rongta кнопка = PLU — кнопки
    /// переезжали). Теперь — каталог в памяти + вся локальная база товаров.</summary>
    private static HashSet<string> LiveProductIds()
    {
        var ids = NurMarketKassa.Services.CatalogCacheService.Products.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var tile in NurMarketKassa.Services.LocalProductRepository.Instance.LoadAllTiles())
                ids.Add(tile.Id);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы: локальный каталог не прочитан: {ex.Message}", "WARNING");
        }
        return ids;
    }

    /// <summary>Пустой каталог (ещё не загружен) — не знаем, кто «пропал»: считаем живыми всех,
    /// чтобы не отдать чужой закреплённый номер.</summary>
    private static Func<string, bool>? LiveFilter(HashSet<string> live) => live.Count == 0 ? null : live.Contains;

    private bool _pluRefreshQueued;

    /// <summary>Пересчёт колонки PLU после щелчков по галочкам — одним проходом, а не на каждую строку
    /// («Выбрать все» отмечает сотни строк подряд).</summary>
    private void QueuePluRefresh()
    {
        if (_pluRefreshQueued)
            return;
        _pluRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pluRefreshQueued = false;
            RefreshPluNumbers();
        }, DispatcherPriority.Background);
    }

    /// <summary>Колонка PLU: закреплённый номер (правится); у отмеченного товара без номера — номер,
    /// который он получит при отправке (курсивом); в режиме «PLU из карточки» и у марок, которые
    /// нумеруют сами, — PLU из карточки (только чтение).</summary>
    private void RefreshPluNumbers()
    {
        if (_allRows.Count == 0)
            return;

        if (!KassaNumbering)
        {
            foreach (var row in _allRows)
                row.SetPlu(CatalogPluText(row), tentative: false, readOnly: true, CatalogPluHint());
            UpdateHotkeyAuto();
            return;
        }

        var profile = LabelScaleStore.Active;
        var catalogMode = UseCatalogPlu;
        var live = LiveProductIds();
        var reserved = catalogMode
            ? _allRows.Where(r => r.IsSelected && r.CatalogPlu is > 0).Select(r => r.CatalogPlu!.Value).ToList()
            : null;
        var tentative = ScalePluPlanner.AssignMissing(
            profile.PluNumbers,
            _allRows.Where(r => r.IsSelected && !(catalogMode && r.CatalogPlu is > 0)).Select(r => r.Id),
            PluStart,
            MaxPlu,
            LiveFilter(live),
            reserved);

        foreach (var row in _allRows)
        {
            if (catalogMode && row.CatalogPlu is > 0)
                // 2026-09-30: PLU с сайта можно исправить здесь — правка уходит и на сайт (PluText_LostFocus).
                row.SetPlu(CatalogPluText(row), tentative: false, readOnly: false, CatalogPluHint());
            else if (profile.PluNumbers.TryGetValue(row.Id, out var pinned))
                row.SetPlu(pinned.ToString(CultureInfo.InvariantCulture), tentative: false, readOnly: false,
                    Tr.T("Постоянный номер ячейки на весах. Можно исправить — при отправке касса перенесёт товар и клавиши.",
                         "Таразадагы уячанын туруктуу номери. Оңдосо болот — жөнөткөндө касса товарды жана баскычтарды көчүрөт.",
                         "The product's fixed slot number on the scale. You can change it — on sending the till moves the product and the keys.",
                         "Ürünün tartıdaki sabit hücre numarası. Değiştirebilirsiniz — gönderimde kasa ürünü ve tuşları taşır.",
                         "Tovarning tarozidagi doimiy katak raqami. Tuzatish mumkin — yuborishda kassa tovarni va tugmalarni ko‘chiradi."));
            else if (tentative.TryGetValue(row.Id, out var next))
                row.SetPlu(next.ToString(CultureInfo.InvariantCulture), tentative: true, readOnly: false,
                    Tr.T("Новый товар: этот номер закрепится за ним при первой отправке на весы.",
                         "Жаңы товар: бул номер ага таразага биринчи жөнөткөндө бекитилет.",
                         "New product: this number is fixed to it on the first send to the scale.",
                         "Yeni ürün: bu numara tartıya ilk gönderimde ona sabitlenir.",
                         "Yangi tovar: bu raqam taroziga birinchi yuborishda unga biriktiriladi."));
            else
                row.SetPlu("", tentative: false, readOnly: false,
                    Tr.T("Номер ещё не выдан — отметьте товар или впишите номер сами.",
                         "Номер азырынча берилген жок — товарды белгилеңиз же номерди өзүңүз жазыңыз.",
                         "No number yet — tick the product or type a number.",
                         "Henüz numara yok — ürünü işaretleyin veya numarayı kendiniz yazın.",
                         "Raqam hali berilmagan — tovarni belgilang yoki raqamni o‘zingiz yozing."));
        }
        UpdateHotkeyAuto();
    }

    private static string CatalogPluText(ScalePluRowVm row) =>
        row.CatalogPlu is > 0 ? row.CatalogPlu.Value.ToString(CultureInfo.InvariantCulture) : "—";

    private static string CatalogPluHint() =>
        Tr.T("PLU из карточки товара на сайте.", "Сайттагы товар карточкасындагы PLU.", "PLU from the product card on the website.",
             "Sitedeki ürün kartındaki PLU.", "Saytdagi tovar kartochkasidagi PLU.");

    private void SequentialPluCheck_Click(object? sender, RoutedEventArgs e)
    {
        var profile = LabelScaleStore.Active;
        profile.PluFromCatalog = SequentialPluCheck.IsChecked != true;
        LabelScaleStore.Save();
        RefreshPluNumbers();
    }

    private void PluStartBox_LostFocus(object? sender, RoutedEventArgs e) => RefreshPluNumbers();

    /// <summary>2026-09-30, просьба владельца «на сайте PLU даётся автоматически — сделай синхронизацию с
    /// вебом»: номер ячейки на весах = PLU товара на сайте (галочка «Постоянный PLU» снимается), а
    /// отмеченным весовым товарам без PLU касса присваивает на сайте наименьшие свободные номера
    /// (PATCH plu; сервер сам отказывает в занятом номере — тогда берём следующий). Номера не больше
    /// таблицы весов (у TM-30F/Rongta — 4000), чтобы PLU с сайта годился как номер ячейки.</summary>
    private async void WebPlu_Click(object? sender, RoutedEventArgs e) => await SyncWebPluAsync(ask: true).ConfigureAwait(true);

    /// <summary>2026-09-30 (владелец: «сделай синхронизацию PLU с сайтом, если нет PLU — назначь их»):
    /// то же, что кнопка, но без вопроса — вызывается перед каждой прямой отправкой на весы.
    /// Возвращает текст итога для строки состояния («» — присваивать было нечего).</summary>
    private async Task<string> SyncWebPluAsync(bool ask)
    {
        var missing = _allRows.Where(r => r.IsSelected && r.CatalogPlu is not > 0).ToList();
        if (missing.Count > 0 && ask)
        {
            var ok = await PosDialogHost.ShowAsync(new PosConfirmDialog(
                Tr.T("PLU как на сайте", "Сайттагыдай PLU", "PLU as on the website", "Sitedeki gibi PLU", "Saytdagidek PLU"),
                Tr.T($"У {missing.Count} отмеченных товаров нет PLU на сайте. Касса присвоит им на сайте свободные номера PLU (как сайт делает при создании весового товара). Номер ячейки на весах будет равен PLU с сайта.",
                     $"Белгиленген {missing.Count} товардын сайтта PLU'су жок. Касса аларга сайтта бош PLU номерлерин берет (сайт салмактуу товарды түзгөндөгүдөй). Таразадагы уячанын номери сайттагы PLU'га барабар болот.",
                     $"{missing.Count} ticked products have no PLU on the website. The till will give them free PLU numbers on the website (as the website does when a weighed product is created). The scale slot number will equal the website PLU.",
                     $"İşaretli {missing.Count} ürünün sitede PLU'su yok. Kasa onlara sitede boş PLU numaraları verir (site tartılı ürün oluştururken yaptığı gibi). Tartıdaki hücre numarası sitedeki PLU'ya eşit olur.",
                     $"Belgilangan {missing.Count} ta tovarning saytda PLU'si yo‘q. Kassa ularga saytda bo‘sh PLU raqamlarini beradi (sayt vaznli tovar yaratganda qilganidek). Tarozidagi katak raqami saytdagi PLU'ga teng bo‘ladi."),
                Tr.T("Присвоить", "Берүү", "Assign", "Ata", "Berish")), this).ConfigureAwait(true) == true;
            if (!ok)
                return "";
        }

        WebPluButton.IsEnabled = false;
        var assigned = 0;
        var failed = new List<string>();
        try
        {
            // Все PLU компании (не только весовых) — сервер требует уникальности по компании.
            var used = NurMarketKassa.Services.CatalogCacheService.Products
                .Where(p => p.Plu is > 0).Select(p => p.Plu!.Value).ToHashSet();
            var next = 1;
            for (var i = 0; i < missing.Count; i++)
            {
                var row = missing[i];
                StatusText.Text = Tr.T($"PLU на сайте: {i + 1} из {missing.Count}…", $"Сайттагы PLU: {missing.Count} ичинен {i + 1}…",
                    $"PLU on the website: {i + 1} of {missing.Count}…", $"Sitede PLU: {i + 1} / {missing.Count}…", $"Saytdagi PLU: {missing.Count} dan {i + 1}…");
                var done = false;
                for (var attempt = 0; attempt < 20 && !done; attempt++)
                {
                    while (next <= MaxPlu && used.Contains(next))
                        next++;
                    if (next > MaxPlu)
                        break;
                    try
                    {
                        await App.CatalogApi.SetProductPluAsync(row.Id, next).ConfigureAwait(true);
                        used.Add(next);
                        row.CatalogPlu = next;
                        var cached = NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == row.Id);
                        if (cached is not null)
                            cached.Plu = next;
                        PosLogger.Log($"Весы: PLU на сайте «{row.Name}» = {next}", "SCALES");
                        assigned++;
                        done = true;
                    }
                    catch (ApiException ex) when (ex.Message.Contains("PLU", StringComparison.OrdinalIgnoreCase))
                    {
                        // Номер занят товаром, которого нет в кэше кассы (удалён/скрыт) — следующий.
                        used.Add(next);
                    }
                }
                if (!done)
                    failed.Add(row.Name);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы: PLU на сайте не присвоены: {ex}", "SCALES");
            failed.Add(ex.Message);
        }
        finally
        {
            WebPluButton.IsEnabled = true;
        }

        // Номер ячейки = PLU с сайта. 2026-09-30 (владелец: «постоянный PLU за товаром не сохраняется»):
        // галочку «Постоянный PLU» больше НЕ снимаем — вместо этого закреплённый номер товара
        // становится равен его PLU с сайта (если номер помещается в таблицу весов и не занят другим
        // закреплённым товаром) и сохраняется. Перенос со старой ячейки — как при ручной правке PLU.
        var profile = LabelScaleStore.Active;
        var repinned = 0;
        foreach (var row in _allRows.Where(r => r.IsSelected && r.CatalogPlu is > 0 && r.CatalogPlu <= MaxPlu))
        {
            var web = row.CatalogPlu!.Value;
            if (profile.PluNumbers.TryGetValue(row.Id, out var pinned) && pinned == web)
                continue;
            // Номер держит другой товар: если тот тоже есть на сайте со своим PLU — он переедет на
            // свой в этом же проходе; иначе номер освобождаем только у товаров, которых нет в каталоге.
            var holder = profile.PluNumbers.FirstOrDefault(kv => kv.Value == web && kv.Key != row.Id).Key;
            if (holder is not null)
            {
                var holderRow = _allRows.FirstOrDefault(r => r.Id == holder);
                var holderWeb = holderRow?.CatalogPlu ?? NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == holder)?.Plu;
                if (holderWeb is > 0 && holderWeb != web)
                    profile.PluNumbers.Remove(holder); // получит свой PLU с сайта (или новый номер при отправке)
                else if (holderRow is not null || NurMarketKassa.Services.CatalogCacheService.Products.Any(p => p.Id == holder))
                    continue; // живой товар без своего PLU держит этот номер — не отнимаем
                else
                    profile.PluNumbers.Remove(holder);
            }
            profile.PluNumbers[row.Id] = web;
            repinned++;
        }
        if (repinned > 0)
            PosLogger.Log($"Весы «{profile.Name}»: закреплённые номера = PLU с сайта у {repinned} товаров", "SCALES");
        LabelScaleStore.Save();
        RefreshPluNumbers();
        UpdateBarcodeExample();

        var summary = Tr.T($"Номер ячейки на весах = PLU с сайта. Присвоено на сайте: {assigned}.", $"Таразадагы уячанын номери = сайттагы PLU. Сайтта берилди: {assigned}.",
                              $"Scale slot number = website PLU. Assigned on the website: {assigned}.", $"Tartı hücre numarası = sitedeki PLU. Sitede atanan: {assigned}.",
                              $"Tarozi katak raqami = saytdagi PLU. Saytda berildi: {assigned}.")
                          + (failed.Count > 0
                              ? Tr.T($" Не удалось: {failed.Count} — ", $" Болбоду: {failed.Count} — ", $" Failed: {failed.Count} — ", $" Başarısız: {failed.Count} — ", $" Bo‘lmadi: {failed.Count} — ") + string.Join(", ", failed.Take(3))
                              : "");
        StatusText.Text = summary;
        return assigned > 0 || failed.Count > 0 ? summary : "";
    }

    /// <summary>Владелец вписал номер в колонку PLU: проверяем диапазон и что номер свободен, и
    /// закрепляем. Сам перенос на весах (новая ячейка, клавиши, очистка старой) — при отправке.</summary>
    private async void PluText_LostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ScalePluRowVm row || row.PluReadOnly || !KassaNumbering)
            return;

        var text = (row.PluText ?? "").Trim();
        if (text == row.PluShown)
            return; // не меняли
        if (text.Length == 0)
        {
            // Номер не снимается: товар без номера при следующей отправке получил бы чужой.
            RefreshPluNumbers();
            return;
        }

        var profile = LabelScaleStore.Active;
        var live = LiveProductIds();
        var problem = ScalePluPlanner.CheckEdit(profile.PluNumbers, row.Id, text, MaxPlu, live.Contains, out var value, out var takenBy);
        string? takenName = takenBy is null ? null : _allRows.FirstOrDefault(r => r.Id == takenBy)?.Name
                                                     ?? NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == takenBy)?.Title;
        if (problem == ScalePluPlanner.EditProblem.None && UseCatalogPlu)
        {
            // В режиме «PLU из карточки» номер может быть занят PLU другого товара с сайта.
            var holder = _allRows.FirstOrDefault(r => r.Id != row.Id && r.IsSelected && r.CatalogPlu == value);
            if (holder is not null)
            {
                problem = ScalePluPlanner.EditProblem.Taken;
                takenName = holder.Name;
            }
        }

        if (problem != ScalePluPlanner.EditProblem.None)
        {
            var message = problem switch
            {
                ScalePluPlanner.EditProblem.Taken => Tr.T($"PLU {value} уже у товара «{takenName}».", $"PLU {value} «{takenName}» товарында бар.",
                    $"PLU {value} already belongs to “{takenName}”.", $"PLU {value} zaten «{takenName}» ürününde.", $"PLU {value} allaqachon «{takenName}» tovarida."),
                _ => Tr.T($"PLU — число от 1 до {MaxPlu}.", $"PLU — 1ден {MaxPlu}гө чейинки сан.", $"PLU must be a number from 1 to {MaxPlu}.",
                    $"PLU 1 ile {MaxPlu} arasında bir sayı olmalı.", $"PLU — 1 dan {MaxPlu} gacha son."),
            };
            row.SetStatus("⚠ " + message, RowState.Warning);
            StatusText.Text = $"«{row.Name}»: " + message;
            RefreshPluNumbers();
            return;
        }

        // Номер товара, которого в каталоге уже нет (удалён, другой аккаунт), освобождается.
        foreach (var stale in profile.PluNumbers.Where(kv => kv.Value == value && kv.Key != row.Id).Select(kv => kv.Key).ToList())
            profile.PluNumbers.Remove(stale);
        profile.PluNumbers.TryGetValue(row.Id, out var old);
        profile.PluNumbers[row.Id] = value;
        LabelScaleStore.Save();
        PosLogger.Log($"Весы «{profile.Name}»: PLU товара «{row.Name}» {(old > 0 ? old.ToString(CultureInfo.InvariantCulture) : "—")} → {value} (вручную)", "SCALES");

        var onScale = profile.SentPlus.TryGetValue(row.Id, out var sent) && sent.Plu > 0 && sent.Plu != value;
        row.SetStatus(Tr.T($"PLU {value} закреплён — отправьте на весы", $"PLU {value} бекитилди — таразага жөнөтүңүз",
            $"PLU {value} fixed — send to the scale", $"PLU {value} sabitlendi — tartıya gönderin", $"PLU {value} biriktirildi — taroziga yuboring"), RowState.None);
        StatusText.Text = onScale
            ? Tr.T($"«{row.Name}»: PLU {sent!.Plu} → {value}. При отправке касса запишет товар в ячейку {value}, очистит ячейку {sent.Plu} и переведёт на {value} клавиши весов, которые вызывали {sent.Plu}.",
                   $"«{row.Name}»: PLU {sent!.Plu} → {value}. Жөнөткөндө касса товарды {value} уячасына жазат, {sent.Plu} уячасын тазалайт жана {sent.Plu} чакырган тараза баскычтарын {value}гө которот.",
                   $"“{row.Name}”: PLU {sent!.Plu} → {value}. On sending, the till writes the product to slot {value}, clears slot {sent.Plu} and moves the scale keys that called {sent.Plu} to {value}.",
                   $"«{row.Name}»: PLU {sent!.Plu} → {value}. Gönderimde kasa ürünü {value} hücresine yazar, {sent.Plu} hücresini temizler ve {sent.Plu} numarasını çağıran tartı tuşlarını {value} numarasına taşır.",
                   $"«{row.Name}»: PLU {sent!.Plu} → {value}. Yuborishda kassa tovarni {value}-katakka yozadi, {sent.Plu}-katakni tozalaydi va {sent.Plu} ni chaqirgan tarozi tugmalarini {value} ga o‘tkazadi.")
            : Tr.T($"«{row.Name}»: PLU {value} закреплён.", $"«{row.Name}»: PLU {value} бекитилди.", $"“{row.Name}”: PLU {value} fixed.",
                   $"«{row.Name}»: PLU {value} sabitlendi.", $"«{row.Name}»: PLU {value} biriktirildi.");
        RefreshPluNumbers();

        // 2026-09-30 (владелец: «добавь возможность изменения PLU»): исправленный номер — и PLU товара на
        // сайте, чтобы сайт, касса и весы не разошлись. Сервер сам откажет, если номер у другого товара.
        if (!OfflineModeHelper.UseLocalOperations && row.CatalogPlu != value)
        {
            try
            {
                await App.CatalogApi.SetProductPluAsync(row.Id, value).ConfigureAwait(true);
                row.CatalogPlu = value;
                var cached = NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == row.Id);
                if (cached is not null)
                    cached.Plu = value;
                PosLogger.Log($"Весы: PLU на сайте «{row.Name}» = {value} (правка в окне «Весы»)", "SCALES");
                StatusText.Text += Tr.T($" На сайте PLU тоже {value}.", $" Сайтта да PLU {value}.", $" The website PLU is {value} too.", $" Sitedeki PLU da {value}.", $" Saytda ham PLU {value}.");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы: PLU на сайте «{row.Name}» не изменён: {ex.Message}", "SCALES");
                StatusText.Text += Tr.T(" На сайте PLU не изменён: ", " Сайтта PLU өзгөргөн жок: ", " The website PLU was not changed: ", " Sitedeki PLU değişmedi: ", " Saytda PLU o‘zgarmadi: ") + ex.Message;
            }
            RefreshPluNumbers();
        }
    }

    /// <summary>2026-09-29: клавиша проверяется сразу при вводе (число 1–120, не у другого товара), а
    /// не только при отправке, и запоминается.</summary>
    private void Hotkey_LostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ScalePluRowVm row)
            return;
        // 2026-09-30: у Rongta кнопка = PLU — вписанная кнопка переставляет PLU товара.
        if (KeyIsPlu)
        {
            _ = HotkeyEditAsPluAsync(row);
            return;
        }
        var text = (row.HotkeyText ?? "").Trim();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key is < 1 or > MaxHotkey)
            {
                row.SetStatus(Tr.T($"⚠ клавиша — число 1–{MaxHotkey}", $"⚠ баскыч — 1–{MaxHotkey} сан", $"⚠ key must be 1–{MaxHotkey}", $"⚠ tuş 1–{MaxHotkey} olmalı", $"⚠ tugma — 1–{MaxHotkey} son"), RowState.Warning);
                return;
            }
            var other = _allRows.FirstOrDefault(r => !ReferenceEquals(r, row) && (r.HotkeyText ?? "").Trim() == text);
            if (other is not null)
            {
                row.SetStatus(Tr.T($"⚠ клавиша {key} уже у «{other.Name}»", $"⚠ {key}-баскыч «{other.Name}» товарында", $"⚠ key {key} already belongs to “{other.Name}”",
                    $"⚠ {key}. tuş zaten «{other.Name}» ürününde", $"⚠ {key}-tugma allaqachon «{other.Name}» tovarida"), RowState.Warning);
                return;
            }
        }
        if (row.StatusWarn)
            row.SetStatus("", RowState.None);
        RememberProfileSelection();
    }

    /// <summary>2026-09-29: «Код в ШК», исправленный владельцем, запоминается для этих весов (раньше
    /// правка терялась при следующем открытии окна, а этикетки с этим кодом касса не находила).
    /// Пустое поле — вернуть код по умолчанию.</summary>
    private void RememberBarcodeCode(ScalePluRowVm row)
    {
        var profile = LabelScaleStore.Active;
        var text = (row.BarcodeCode ?? "").Trim();
        if (text.Length == 0 || text == row.DefaultCode)
        {
            if (text.Length == 0)
                row.BarcodeCode = row.DefaultCode;
            if (!profile.BarcodeCodes.Remove(row.Id))
                return;
        }
        else
        {
            if (profile.BarcodeCodes.TryGetValue(row.Id, out var saved) && saved == text)
                return;
            profile.BarcodeCodes[row.Id] = text;
        }
        LabelScaleStore.Save();
    }

    // ------------------------------------------------------------------ отправка: номера и коды

    /// <summary>Номера для отправки: у каждого отмеченного товара — закреплённый (новым выдаётся
    /// наименьший свободный и сразу сохраняется), в режиме «PLU из карточки» — PLU карточки.
    /// Товар без номера (таблица весов заполнена) в результат не попадает.</summary>
    private Dictionary<string, int> PinNumbersForSend(IReadOnlyCollection<string> selectedIds)
    {
        var profile = LabelScaleStore.Active;
        var live = LiveProductIds();
        var selected = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        var catalogMode = UseCatalogPlu;
        var rows = _allRows.Where(r => selected.Contains(r.Id)).ToList();
        var reserved = catalogMode ? rows.Where(r => r.CatalogPlu is > 0).Select(r => r.CatalogPlu!.Value).ToList() : null;
        var fresh = ScalePluPlanner.AssignMissing(
            profile.PluNumbers,
            rows.Where(r => !(catalogMode && r.CatalogPlu is > 0)).Select(r => r.Id),
            PluStart,
            MaxPlu,
            LiveFilter(live),
            reserved);
        if (fresh.Count > 0)
        {
            var taken = fresh.Values.ToHashSet();
            foreach (var stale in profile.PluNumbers.Where(kv => live.Count > 0 && !live.Contains(kv.Key) && taken.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                profile.PluNumbers.Remove(stale);
            foreach (var (id, plu) in fresh)
                profile.PluNumbers[id] = plu;
            LabelScaleStore.Save();
            PosLogger.Log($"Весы «{profile.Name}»: закреплены PLU новых товаров: "
                          + string.Join("; ", fresh.OrderBy(kv => kv.Value).Select(kv => $"{kv.Value} — {rows.FirstOrDefault(r => r.Id == kv.Key)?.Name}")), "SCALES");
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (catalogMode && row.CatalogPlu is > 0)
                result[row.Id] = row.CatalogPlu.Value;
            else if (profile.PluNumbers.TryGetValue(row.Id, out var plu))
                result[row.Id] = plu;
        }
        RefreshPluNumbers();
        return result;
    }

    /// <summary>Проверка пачки перед записью на весы: у товара есть номер, номер не повторяется, а
    /// «Код в ШК» на кассе откроет именно этот товар (по карточке или по списку кодов весов, но не
    /// чужой). Раньше такие совпадения уходили на весы молча, и на кассе по этикетке пробивался
    /// другой товар. Возвращает коды товаров (id → код) и проблемы (id → текст для строки).</summary>
    private (Dictionary<string, long> Codes, Dictionary<string, string> Problems) CheckSendBatch(
        IReadOnlyList<string> selectedIds, IReadOnlyDictionary<string, int> numbers, long maxCode)
    {
        var rowsById = _allRows.ToDictionary(r => r.Id);
        var codes = new Dictionary<string, long>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        var autoCodeIds = new List<string>();
        var explicitIds = new HashSet<string>(StringComparer.Ordinal);
        var savedCodes = LabelScaleStore.Active.BarcodeCodes;
        _codeReplaceNote = "";
        foreach (var id in selectedIds)
        {
            if (!rowsById.TryGetValue(id, out var row))
                continue;
            if (!numbers.TryGetValue(id, out var plu))
            {
                problems[id] = Tr.T($"нет свободного номера PLU (таблица весов 1–{MaxPlu})", $"бош PLU номери жок (тараза таблицасы 1–{MaxPlu})",
                    $"no free PLU number (scale table 1–{MaxPlu})", $"boş PLU numarası yok (tartı tablosu 1–{MaxPlu})", $"bo‘sh PLU raqami yo‘q (tarozi jadvali 1–{MaxPlu})");
                continue;
            }
            // Как и раньше: пустой или неверный «Код в ШК» — в код идёт номер ячейки.
            var typed = long.TryParse((row.BarcodeCode ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                        && code >= 1 && code <= maxCode;
            // 2026-09-30 (владелец перенёс «Ак Кант» с PLU 66 на 1 — в «Код в ШК» осталось 66, а 66 уже
            // получил другой товар, отправка встала): у весов «!0L» (Rongta, TM-30F) код, который владелец
            // сам не вписывал, идёт за номером ячейки, а не за тем, что было в колонке при открытии окна.
            var saved = savedCodes.TryGetValue(id, out var savedText)
                        && long.TryParse(savedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var savedCode)
                        && savedCode >= 1 && savedCode <= maxCode;
            if (IsDahuaWire && !saved)
                typed = false;
            codes[id] = typed ? code : plu;
            if (!typed)
                autoCodeIds.Add(id);
            else
                explicitIds.Add(id);
        }

        // 2026-09-30, живой случай владельца (весы Rongta, 219 весовых товаров): у товаров без «Кода
        // в ШК» код = номер ячейки (98, 99…), а такие числа уже были PLU/кодами ДРУГИХ товаров
        // каталога — касса отказывалась отправлять весь список. Теперь пустой «Код в ШК» получает
        // свободный код (не PLU/артикул/код другого весового товара, не код с этикеток других весов,
        // в пределах цифр кода в штрих-коде кассы) и сразу записывается в колонку. Вписанный руками
        // код проверяется как раньше.
        // Вписанный код, который на кассе открыл бы ЧУЖОЙ товар, тоже заменяется (номером ячейки или
        // свободным кодом) — с пометкой, а не остановкой всей отправки.
        AssignFreeCodes(autoCodeIds, codes, rowsById, maxCode);
        ResolveExplicitCodeConflicts(explicitIds, numbers, codes, rowsById, maxCode);

        // Один номер PLU у двух товаров (режим «PLU из карточки»: одинаковый PLU на сайте).
        foreach (var group in numbers.Where(kv => codes.ContainsKey(kv.Key)).GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ", group.Select(kv => $"«{rowsById[kv.Key].Name}»"));
            foreach (var kv in group)
                problems[kv.Key] = Tr.T($"PLU {group.Key} у нескольких товаров: {names}", $"PLU {group.Key} бир нече товарда: {names}",
                    $"PLU {group.Key} is used by several products: {names}", $"PLU {group.Key} birden fazla üründe: {names}", $"PLU {group.Key} bir nechta tovarda: {names}");
        }

        // «Код в ШК» → какой товар откроет касса по этикетке.
        var notInCatalog = new List<string>();
        foreach (var (id, code) in codes)
        {
            if (problems.ContainsKey(id))
                continue;
            var hit = LocalCartService.FindByEmbeddedCodeInCatalog(code.ToString(CultureInfo.InvariantCulture));
            if (hit is null)
                notInCatalog.Add(id);
            else if (!string.Equals(hit.Id, id, StringComparison.Ordinal))
                problems[id] = CodeTakenText(code, hit.Title);
        }
        // Код не из карточки — касса найдёт этикетку по списку кодов, записанных на весы. Два товара с
        // таким кодом она не различит.
        foreach (var group in notInCatalog.GroupBy(id => codes[id]))
        {
            if (group.Count() > 1)
            {
                var names = string.Join(", ", group.Select(id => $"«{rowsById[id].Name}»"));
                foreach (var id in group)
                    problems[id] = CodeTakenText(group.Key, names, quoted: false);
                continue;
            }
            var single = group.First();
            var registered = ScaleLabelCodeRegistry.ProductIdFor(group.Key.ToString(CultureInfo.InvariantCulture));
            if (registered is not null && !string.Equals(registered, single, StringComparison.Ordinal) && !codes.ContainsKey(registered))
            {
                var title = NurMarketKassa.Services.CatalogCacheService.Products.FirstOrDefault(p => p.Id == registered)?.Title;
                if (title is not null)
                    problems[single] = CodeTakenText(group.Key, title);
            }
        }

        foreach (var (id, text) in problems)
        {
            if (rowsById.TryGetValue(id, out var row))
                row.SetStatus("⚠ " + text, RowState.Warning);
        }
        return (codes, problems);
    }

    /// <summary>Пустой «Код в ШК»: оставляем номер ячейки, если по нему касса откроет этот же товар
    /// (или никакой), иначе берём наименьший свободный код. Свободный — не PLU/артикул/код другого
    /// весового товара каталога, не код этикеток других весов (ScaleLabelCodeRegistry), не код
    /// другого товара этой отправки и помещается в цифры кода штрих-кода кассы (5 — «по PLU», 6 —
    /// «по коду»). Найденный код пишется в колонку и запоминается для этих весов.</summary>
    private void AssignFreeCodes(List<string> autoIds, Dictionary<string, long> codes,
        Dictionary<string, ScalePluRowVm> rowsById, long maxCode)
    {
        if (autoIds.Count == 0)
            return;
        var (codeLength, _) = ScaleBarcodeRules.Layout();
        long limit = 1;
        for (var i = 0; i < codeLength; i++)
            limit *= 10;
        limit = Math.Min(maxCode, limit - 1);

        // Коды, которые касса по этикетке отдаст какому-то весовому товару каталога.
        var taken = new Dictionary<long, string>();
        void Take(string? text, string productId)
        {
            var digits = (text ?? "").Trim().TrimStart('0');
            if (digits.Length > 0 && digits.Length <= 9 && digits.All(char.IsDigit))
                taken.TryAdd(long.Parse(digits, CultureInfo.InvariantCulture), productId);
        }
        // Порядок — как у поиска кассы (LocalCartService.FindByEmbeddedCodeInCatalog): при раскладке «по
        // PLU» сначала PLU весовых товаров, потом артикул/код товара; «по коду» — наоборот.
        var weighed = NurMarketKassa.Services.CatalogCacheService.Products.Where(p => p.MustWeigh).ToList();
        var codeLayout = string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);
        if (!codeLayout)
            foreach (var p in weighed.Where(p => p.Plu is > 0))
                taken.TryAdd(p.Plu!.Value, p.Id);
        foreach (var p in weighed)
        {
            Take(p.Article, p.Id);
            Take(p.ProductCode, p.Id);
        }
        if (codeLayout)
            foreach (var p in weighed.Where(p => p.Plu is > 0))
                taken.TryAdd(p.Plu!.Value, p.Id);
        var autoSet = autoIds.ToHashSet(StringComparer.Ordinal);
        var used = codes.Where(kv => !autoSet.Contains(kv.Key)).Select(kv => kv.Value).ToHashSet();

        bool FreeFor(long candidate, string id)
        {
            if (candidate < 1 || candidate > limit || used.Contains(candidate))
                return false;
            if (taken.TryGetValue(candidate, out var owner) && !string.Equals(owner, id, StringComparison.Ordinal))
                return false;
            var registered = ScaleLabelCodeRegistry.ProductIdFor(candidate.ToString(CultureInfo.InvariantCulture));
            // Код товара из этой же отправки в списке кодов весов перезапишется ею — не помеха.
            return registered is null || string.Equals(registered, id, StringComparison.Ordinal) || codes.ContainsKey(registered);
        }

        long next = 1;
        foreach (var id in autoIds)
        {
            var code = codes[id];
            if (!FreeFor(code, id))
            {
                while (next <= limit && !FreeFor(next, id))
                    next++;
                if (next > limit)
                    continue; // свободных нет — сработает обычная проверка и покажет, чей это код
                code = next;
            }
            codes[id] = code;
            used.Add(code);
            if (rowsById.TryGetValue(id, out var row))
            {
                row.BarcodeCode = code.ToString(CultureInfo.InvariantCulture);
                // Подобранный кассой код не «вписан владельцем»: при смене PLU он пойдёт за номером.
                if (!IsDahuaWire)
                    RememberBarcodeCode(row);
            }
        }
    }

    /// <summary>Итог замен кодов последней проверки — дописывается к строке состояния после отправки.</summary>
    private string _codeReplaceNote = "";

    /// <summary>Вписанный «Код в ШК», который открыл бы на кассе другой товар: берём номер ячейки товара,
    /// если он свободен, иначе наименьший свободный код; новый код запоминается для этих весов.
    /// Проверка — тем же поиском, что у кассы при скане этикетки.</summary>
    private void ResolveExplicitCodeConflicts(HashSet<string> explicitIds, IReadOnlyDictionary<string, int> numbers,
        Dictionary<string, long> codes, Dictionary<string, ScalePluRowVm> rowsById, long maxCode)
    {
        if (explicitIds.Count == 0)
            return;
        var (codeLength, _) = ScaleBarcodeRules.Layout();
        long limit = 1;
        for (var i = 0; i < codeLength; i++)
            limit *= 10;
        limit = Math.Min(maxCode, limit - 1);
        bool Free(long candidate, string id)
        {
            if (candidate < 1 || candidate > limit)
                return false;
            if (codes.Any(kv => kv.Value == candidate && !string.Equals(kv.Key, id, StringComparison.Ordinal)))
                return false;
            var hit = LocalCartService.FindByEmbeddedCodeInCatalog(candidate.ToString(CultureInfo.InvariantCulture));
            if (hit is not null)
                return string.Equals(hit.Id, id, StringComparison.Ordinal);
            var registered = ScaleLabelCodeRegistry.ProductIdFor(candidate.ToString(CultureInfo.InvariantCulture));
            return registered is null || string.Equals(registered, id, StringComparison.Ordinal) || codes.ContainsKey(registered);
        }
        var notes = new List<string>();
        foreach (var id in explicitIds)
        {
            var code = codes[id];
            if (Free(code, id))
                continue;
            long replacement = numbers.TryGetValue(id, out var plu) && Free(plu, id) ? plu : 0;
            for (long c = 1; replacement == 0 && c <= limit; c++)
            {
                if (Free(c, id))
                    replacement = c;
            }
            if (replacement == 0)
                continue; // свободных нет — обычная проверка покажет, чей это код
            codes[id] = replacement;
            if (rowsById.TryGetValue(id, out var row))
            {
                row.BarcodeCode = replacement.ToString(CultureInfo.InvariantCulture);
                RememberBarcodeCode(row);
                notes.Add($"«{row.Name}» {code} → {replacement}");
            }
        }
        if (notes.Count > 0)
        {
            PosLogger.Log("Весы: «Код в ШК» заменён (открывал чужой товар): " + string.Join("; ", notes), "SCALES");
            _codeReplaceNote = Tr.T(" «Код в ШК» заменён (открывал бы чужой товар): ", " «ШКдагы код» алмашты (башка товарды ачмак): ", " “Barcode code” replaced (it would open another product): ", " «Barkod kodu» değiştirildi (başka ürünü açardı): ", " «ShKdagi kod» almashtirildi (boshqa tovarni ochardi): ")
                                + string.Join(", ", notes.Take(5)) + (notes.Count > 5 ? $" (+{notes.Count - 5})" : "") + ".";
        }
    }

    private static string CodeTakenText(long code, string owner, bool quoted = true)
    {
        var who = quoted ? $"«{owner}»" : owner;
        return Tr.T($"код в ШК {code} на кассе откроет {who} — впишите другой «Код в ШК» или исправьте PLU/код в карточке",
                    $"ШКдагы {code} коду кассада {who} ачат — башка «ШКдагы код» жазыңыз же карточкадагы PLU/кодду оңдоңуз",
                    $"barcode code {code} opens {who} at the till — enter another “Barcode code” or fix the PLU/code in the product card",
                    $"barkod kodu {code} kasada {who} açar — başka bir «Barkod kodu» girin veya karttaki PLU/kodu düzeltin",
                    $"shtrix-koddagi {code} kodi kassada {who} ni ochadi — boshqa «ShKdagi kod» yozing yoki kartochkadagi PLU/kodni tuzating");
    }

    /// <summary>После отправки: что записано — в «что на весах» этих весов, коды — в поиск кассы.</summary>
    private static void RememberSent(IReadOnlyDictionary<string, ScaleSentPlu> written, IEnumerable<int> clearedSlots)
    {
        if (written.Count == 0)
            return;
        var profile = LabelScaleStore.Active;
        ScalePluPlanner.ApplySent(profile.SentPlus, written, clearedSlots);
        LabelScaleStore.Save();
        LabelScaleStore.PublishLabelCodes();
    }

    // ------------------------------------------------------------------ ШТРИХ-ПРИНТ: клавиши и старые ячейки

    /// <summary>Клавиши, запрограммированные на самих весах (с клавиатуры весов, «Загрузчиком», в окне
    /// настроек), которые вызывали старый номер или код переехавшего товара, — на новый. Клавиши из
    /// колонки «Клавиша» (<paramref name="tableKeys"/>) уже записаны и не трогаются. Возвращает
    /// (переведено, с ошибкой).</summary>
    private async Task<(int Moved, int Failed)> RemapScaleHotkeysAsync(ShtrikhPrintLanScaleService scale, int keyCount,
        ScalePluMovePlan plan, HashSet<int> tableKeys, Dictionary<string, ScalePluRowVm> rowsById)
    {
        if (plan.IsEmpty)
            return (0, 0);
        int moved = 0, failed = 0;
        for (var key = 1; key <= keyCount; key++)
        {
            if (tableKeys.Contains(key))
                continue;
            StatusText.Text = Tr.T($"Проверяю клавиши весов: {key} из {keyCount}…", $"Тараза баскычтары текшерилүүдө: {keyCount} ичинен {key}…",
                $"Checking the scale keys: {key} of {keyCount}…", $"Tartı tuşları kontrol ediliyor: {key} / {keyCount}…", $"Tarozi tugmalari tekshirilmoqda: {keyCount} dan {key}…");
            ShtrikhHotkey hk;
            try
            {
                hk = await scale.GetHotkeyAsync(key, CancellationToken.None).ConfigureAwait(true);
            }
            catch (ShtrikhScaleException ex)
            {
                // Код ошибки — клавиш у этих весов меньше (конец списка); без кода — нет связи.
                PosLogger.Log($"Весы: чтение клавиши {key} — {ex.Message}; проверка клавиш остановлена.", "SCALES");
                if (ex.ErrorCode == 0)
                    failed++;
                break;
            }

            var remap = ScalePluPlanner.RemapHotkey(hk.FunctionCode, hk.Value, plan);
            if (remap is not { } target)
                continue;

            var productId = hk.FunctionCode == ShtrikhPrintProtocol.HotkeyPluNumber
                ? plan.PluMoves.First(m => m.From == hk.Value).ProductId
                : plan.CodeMoves.First(m => m.From == hk.Value).ProductId;
            rowsById.TryGetValue(productId, out var row);
            try
            {
                await scale.SetHotkeyAsync(key, target.Function, target.Value, CancellationToken.None).ConfigureAwait(true);
                moved++;
                PosLogger.Log($"Весы: клавиша {key} ({hk.FunctionCode:X2}h {hk.Value} → {target.Value}) — «{row?.Name}»", "SCALES");
                row?.AppendStatus(target.Function == ShtrikhPrintProtocol.HotkeyPluNumber
                    ? Tr.T($" · клавиша {key} → PLU {target.Value}", $" · {key}-баскыч → PLU {target.Value}", $" · key {key} → PLU {target.Value}", $" · {key}. tuş → PLU {target.Value}", $" · {key}-tugma → PLU {target.Value}")
                    : Tr.T($" · клавиша {key} → код {target.Value}", $" · {key}-баскыч → код {target.Value}", $" · key {key} → code {target.Value}", $" · {key}. tuş → kod {target.Value}", $" · {key}-tugma → kod {target.Value}"),
                    RowState.Ok);
            }
            catch (ShtrikhScaleException ex)
            {
                failed++;
                PosLogger.Log($"Весы: клавиша {key} не переведена на {target.Value}: {ex.Message}", "SCALES");
                row?.AppendStatus(" · ✗ " + Tr.T($"клавиша {key}: ", $"{key}-баскыч: ", $"key {key}: ", $"{key}. tuş: ", $"{key}-tugma: ") + ex.Message, RowState.Error);
            }
        }
        return (moved, failed);
    }

    /// <summary>Старые ячейки переехавших товаров (54h «Очистить ПЛУ»). Чистим, только если в ячейке
    /// по-прежнему этот товар (58h: то же название или код) — чужую запись не трогаем.</summary>
    private async Task<List<int>> ClearOldShtrikhSlotsAsync(ShtrikhPrintLanScaleService scale, ScalePluMovePlan plan,
        IReadOnlyDictionary<string, ScaleSentPlu> lastSent, Dictionary<string, ScalePluRowVm> rowsById)
    {
        var cleared = new List<int>();
        foreach (var move in plan.SlotsToClear)
        {
            rowsById.TryGetValue(move.ProductId, out var row);
            lastSent.TryGetValue(move.ProductId, out var before);
            try
            {
                var existing = await scale.ReadPluAsync(move.From, CancellationToken.None).ConfigureAwait(true);
                if (existing is null)
                {
                    cleared.Add(move.From); // уже пусто
                    continue;
                }
                if (!IsSameShtrikhRecord(existing, before, row?.Name))
                {
                    PosLogger.Log($"Весы: старая ячейка {move.From} товара «{row?.Name}» не очищена — в ней «{existing.Name1}».", "SCALES");
                    row?.AppendStatus(Tr.T($" · ячейка {move.From} не очищена: там «{existing.Name1}»", $" · {move.From}-уяча тазаланган жок: анда «{existing.Name1}»",
                        $" · slot {move.From} not cleared: it holds “{existing.Name1}”", $" · {move.From} hücresi temizlenmedi: içinde «{existing.Name1}»",
                        $" · {move.From}-katak tozalanmadi: unda «{existing.Name1}»"), RowState.Warning);
                    continue;
                }
                await scale.ClearPluAsync(move.From, CancellationToken.None).ConfigureAwait(true);
                cleared.Add(move.From);
                PosLogger.Log($"Весы: старая ячейка {move.From} товара «{row?.Name}» очищена (товар теперь в {move.To}).", "SCALES");
                row?.AppendStatus(Tr.T($" · старый PLU {move.From} очищен", $" · эски PLU {move.From} тазаланды", $" · old PLU {move.From} cleared",
                    $" · eski PLU {move.From} temizlendi", $" · eski PLU {move.From} tozalandi"), RowState.Ok);
            }
            catch (ShtrikhScaleException ex)
            {
                PosLogger.Log($"Весы: старая ячейка {move.From} не очищена: {ex.Message}", "SCALES");
                row?.AppendStatus(" · ✗ " + Tr.T($"ячейка {move.From} не очищена: ", $"{move.From}-уяча тазаланган жок: ", $"slot {move.From} not cleared: ",
                    $"{move.From} hücresi temizlenmedi: ", $"{move.From}-katak tozalanmadi: ") + ex.Message, RowState.Warning);
            }
        }
        return cleared;
    }

    /// <summary>В ячейке тот же товар, что касса записала туда в прошлый раз: по названию (так, как
    /// весы его хранят — CP1251, 28 байт), а если название неизвестно — по коду товара.</summary>
    private static bool IsSameShtrikhRecord(ShtrikhPluRecord existing, ScaleSentPlu? before, string? currentName)
    {
        var name = before?.Name ?? currentName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var expected = ShtrikhPrintLanScaleService.CreateRecord(1, 1, name, 0m, 0).Name1;
            var stored = ShtrikhPrintProtocol.DecodeText(ShtrikhPrintProtocol.EncodeFixedText(expected, ShtrikhPrintProtocol.NameFieldLength));
            return string.Equals(stored.Trim(), (existing.Name1 ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return before?.Code is { } code
               && long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
               && existing.ProductCode == c;
    }

    // ------------------------------------------------------------------ первый запуск: закрепить прежние номера

    /// <summary>Первый запуск после обновления: у клиентов весы уже загружены прежней нумерацией
    /// «подряд». Закрепляем номера такими, какими они были при последней успешной отправке (строка
    /// журнала «раскладка»), а если её нет — такими, какими их дала бы прежняя нумерация сейчас
    /// (порядок списка). Один раз показываем владельцу, какой PLU у какого товара.</summary>
    private async Task PinExistingNumbersOnceAsync()
    {
        try
        {
            var profile = LabelScaleStore.Active;
            if (!KassaNumbering || profile.PluPinnedFrom is not null || _allRows.Count == 0)
                return;
            if (profile.PluNumbers.Count > 0)
            {
                profile.PluPinnedFrom = "—";
                LabelScaleStore.Save();
                return;
            }

            var marker = IsTm ? ScalePluPlanner.TmLayoutMarker
                : IsRongtaLan ? ScalePluPlanner.RongtaLayoutMarker
                : ScalePluPlanner.ShtrikhLayoutMarker;
            var selected = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var products = _allRows.Select(r => (r.Id, r.Name)).ToList();
            // В строке журнала не записано, на какие весы шла отправка: при нескольких весах одной
            // марки журналу не доверяем — номера по порядку списка.
            var onlyScaleOfBrand = LabelScaleStore.All.Count(p => p.Brand == profile.Brand) == 1;
            var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppMode.DataFolderName, "Logs");
            var fromLog = onlyScaleOfBrand
                ? await Task.Run(() => FindLastLayoutInLog(logFolder, marker, products, selected)).ConfigureAwait(true)
                : null;

            // Пока читали журнал, окно могли закрыть или переключить весы.
            if (!IsVisible || !ReferenceEquals(profile, LabelScaleStore.Active) || profile.PluPinnedFrom is not null || profile.PluNumbers.Count > 0)
                return;

            string source;
            string sourceTag;
            if (fromLog is { Pins.Count: > 0 } log)
            {
                sourceTag = "log " + log.When;
                foreach (var (id, plu) in log.Pins)
                {
                    profile.PluNumbers[id] = plu;
                    // Это то, что сейчас на весах: по нему при смене номера найдутся старая ячейка и клавиши.
                    profile.SentPlus[id] = new ScaleSentPlu { Plu = plu, Name = _allRows.First(r => r.Id == id).Name };
                }
                source = Tr.T($"из последней отправки на весы ({log.When})", $"таразага акыркы жөнөтүүдөн ({log.When})", $"from the last send to the scale ({log.When})",
                    $"tartıya son gönderimden ({log.When})", $"taroziga oxirgi yuborishdan ({log.When})");
            }
            else
            {
                sourceTag = "order";
                var order = _allRows.Where(r => r.IsSelected).Select(r => r.Id);
                foreach (var (id, plu) in ScalePluPlanner.AssignMissing(new Dictionary<string, int>(), order, 1, MaxPlu))
                    profile.PluNumbers[id] = plu;
                source = Tr.T("по порядку списка (как их нумеровала прошлая версия кассы)", "тизменин тартиби боюнча (кассанын мурунку версиясы номерлегендей)",
                    "in list order (as the previous version of the till numbered them)", "liste sırasına göre (kasanın önceki sürümünün numaralandırdığı gibi)",
                    "ro‘yxat tartibida (kassaning oldingi versiyasi raqamlagandek)");
            }
            if (profile.PluNumbers.Count == 0)
                return;

            profile.PluPinnedFrom = sourceTag + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            LabelScaleStore.Save();
            LabelScaleStore.PublishLabelCodes();
            RefreshPluNumbers();

            var names = _allRows.ToDictionary(r => r.Id, r => r.Name);
            var list = profile.PluNumbers
                .Where(kv => names.ContainsKey(kv.Key))
                .OrderBy(kv => kv.Value)
                .Select(kv => $"{kv.Value} — {names[kv.Key]}")
                .ToList();
            PosLogger.Log($"Весы «{profile.Name}»: номера PLU закреплены ({profile.PluPinnedFrom}): " + string.Join("; ", list), "SCALES");

            var text = new StringBuilder();
            text.Append(Tr.T(
                $"Теперь у каждого весового товара свой постоянный номер PLU: он больше не меняется при отправке, добавлении товаров, поиске или сортировке. Номера взяты {source}. Исправить номер можно в колонке PLU.",
                $"Эми ар бир салмактуу товардын өзүнүн туруктуу PLU номери бар: ал жөнөтүүдө, товар кошууда, издөөдө же иреттөөдө өзгөрбөйт. Номерлер {source} алынды. Номерди PLU тилкесинде оңдосо болот.",
                $"Every weighed product now has its own fixed PLU number: it no longer changes when sending, adding products, searching or sorting. The numbers were taken {source}. You can correct a number in the PLU column.",
                $"Artık her tartılı ürünün kendi sabit PLU numarası var: gönderimde, ürün eklerken, aramada veya sıralamada değişmez. Numaralar {source} alındı. Numarayı PLU sütununda düzeltebilirsiniz.",
                $"Endi har bir vaznli tovarning o‘z doimiy PLU raqami bor: u yuborishda, tovar qo‘shishda, qidiruvda yoki saralashda o‘zgarmaydi. Raqamlar {source} olindi. Raqamni PLU ustunida tuzatish mumkin."));
            text.Append("\n\n");
            text.Append(string.Join("\n", list.Take(150)));
            if (list.Count > 150)
                text.Append($"\n… (+{list.Count - 150})");
            await PosAlertDialog.ShowAsync(this,
                Tr.T("Номера PLU закреплены", "PLU номерлери бекитилди", "PLU numbers are now fixed", "PLU numaraları sabitlendi", "PLU raqamlari biriktirildi"),
                text.ToString()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы: закрепление прежних номеров PLU не удалось: {ex}", "SCALES");
        }
    }

    private sealed record LayoutFromLog(string When, Dictionary<string, int> Pins);

    /// <summary>Последняя строка «раскладка» из журнала кассы (nurmarket-kassa.log и .log.1), которая
    /// относится к этим весам: больше половины её товаров отмечены для них. null — такой нет.</summary>
    private static LayoutFromLog? FindLastLayoutInLog(string folder, string marker, IReadOnlyList<(string Id, string Name)> products, HashSet<string> selected)
    {
        var lines = new List<string>();
        foreach (var file in new[] { "nurmarket-kassa.log.1", "nurmarket-kassa.log" })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
                continue;
            try
            {
                // Журнал открыт логгером на дозапись — читаем с общим доступом.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Contains(marker, StringComparison.Ordinal))
                        lines.Add(line);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы: журнал {file} не прочитан: {ex.Message}", "SCALES");
            }
        }

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var layout = ScalePluPlanner.ParseLayout(lines[i], marker);
            if (layout is null || layout.Count == 0)
                continue;
            var pins = ScalePluPlanner.MatchLayout(layout, products);
            if (pins.Count == 0)
                continue;
            if (selected.Count > 0 && pins.Keys.Count(selected.Contains) * 2 < pins.Count)
                continue; // раскладка других весов
            var when = lines[i].Length >= 16 && DateTime.TryParseExact(lines[i][..16], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                : "?";
            return new LayoutFromLog(when, pins);
        }
        return null;
    }

    // ------------------------------------------------------------------ «Обновить с сервера»

    /// <summary>2026-09-29 (владелец: «непонятно, обновляет ли с сервера»): раньше кнопка только
    /// перечитывала уже загруженный в память каталог. Теперь — полная загрузка каталога с сервера
    /// NurCRM тем же сервисом, что и фоновая синхронизация, с ходом и понятным итогом.</summary>
    private async Task RefreshFromServerAsync()
    {
        // Правки в таблице (галочки, клавиши) не теряются при пересборке строк.
        RememberProfileSelection();
        RefreshButton.IsEnabled = false;
        SendProgress.IsIndeterminate = true;
        SendProgress.IsVisible = true;
        StatusText.Text = Tr.T("Загружаю товары с сервера NurCRM…", "Товарлар NurCRM серверинен жүктөлүүдө…", "Downloading products from the NurCRM server…",
            "Ürünler NurCRM sunucusundan indiriliyor…", "Tovarlar NurCRM serveridan yuklanmoqda…");
        CatalogSyncResult result;
        try
        {
            result = await App.GetRequiredService<ICatalogCacheService>().SyncCatalogFullAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = CatalogSyncResult.Failed(ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            SendProgress.IsIndeterminate = false;
            HideProgress();
        }

        // Список товаров в памяти обновляется через очередь UI-потока — даём ей отработать, иначе
        // таблица соберётся из прежнего каталога.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        LoadRows();

        var total = NurMarketKassa.Services.CatalogCacheService.Products.Count;
        var weighed = _allRows.Count;
        var lastSync = LocalProductRepository.Instance.GetLastSyncTime()?.ToLocalTime();
        if (result.Success)
        {
            var at = (lastSync ?? DateTime.Now).ToString("HH:mm", CultureInfo.InvariantCulture);
            var changes = result.Added + result.Changed + result.Deleted > 0
                ? Tr.T($" Изменения: новых {result.Added}, изменено {result.Changed}, удалено {result.Deleted}.",
                       $" Өзгөрүүлөр: жаңы {result.Added}, өзгөргөн {result.Changed}, өчүрүлгөн {result.Deleted}.",
                       $" Changes: {result.Added} new, {result.Changed} changed, {result.Deleted} deleted.",
                       $" Değişiklikler: {result.Added} yeni, {result.Changed} değişti, {result.Deleted} silindi.",
                       $" O‘zgarishlar: yangi {result.Added}, o‘zgargan {result.Changed}, o‘chirilgan {result.Deleted}.")
                : Tr.T(" Изменений нет.", " Өзгөрүү жок.", " No changes.", " Değişiklik yok.", " O‘zgarish yo‘q.");
            StatusText.Text = Tr.T($"Обновлено с сервера в {at}: {total} товаров, весовых {weighed}.",
                                   $"Серверден {at} жаңыланды: {total} товар, салмактуу {weighed}.",
                                   $"Refreshed from the server at {at}: {total} products, {weighed} weighed.",
                                   $"Sunucudan {at} saatinde yenilendi: {total} ürün, {weighed} tartılı.",
                                   $"Serverdan {at} da yangilandi: {total} ta tovar, vaznli {weighed}.") + changes;
            PosLogger.Log($"Весы: каталог обновлён с сервера по кнопке — товаров {total}, весовых {weighed}.", "SCALES");
        }
        else
        {
            var from = lastSync is { } t
                ? t.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                : Tr.T("неизвестной даты", "белгисиз күндөн", "an unknown date", "bilinmeyen bir tarihten", "noma’lum sanadan");
            StatusText.Text = Tr.T($"Нет связи с сервером — показан сохранённый каталог от {from} ({total} товаров, весовых {weighed}).",
                                   $"Сервер менен байланыш жок — {from} сакталган каталог көрсөтүлдү ({total} товар, салмактуу {weighed}).",
                                   $"No connection to the server — showing the saved catalog from {from} ({total} products, {weighed} weighed).",
                                   $"Sunucuyla bağlantı yok — {from} tarihli kayıtlı katalog gösteriliyor ({total} ürün, {weighed} tartılı).",
                                   $"Server bilan aloqa yo‘q — {from} dagi saqlangan katalog ko‘rsatildi ({total} ta tovar, vaznli {weighed}).")
                              + (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "" : " " + result.ErrorMessage);
            PosLogger.Log($"Весы: обновление каталога с сервера не удалось: {result.ErrorMessage}", "SCALES");
        }
    }
}
