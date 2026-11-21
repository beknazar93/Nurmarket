using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21 (просьба владельца «добавь возможность старые ячейки стирать!»): кнопка «Стереть старые ячейки…» у весов Rongta напрямую. Итог отправки «Сменился PLU у товаров»
/// раньше говорил «касса удалять ячейки пока не умеет»; теперь старые ячейки запоминаются (<see cref="LabelScaleProfile.StaleSlots"/>) и стираются по кнопке — только после явного
/// подтверждения в окне (<see cref="RongtaEraseSlotsDialog"/>), автоматически — никогда. Как именно стирается ячейка и что в этом не проверено на весах — <see cref="RongtaSlotEraser"/>.</summary>
public partial class ScalesPluWindow
{
    private CancellationTokenSource? _eraseCts;

    /// <summary>Подпись и видимость кнопки (вызывается из ApplyBrandVisibility): только Rongta напрямую по сети.</summary>
    private void UpdateEraseSlotsButton()
    {
        EraseSlotsButton.Content = Tr.T("Стереть старые ячейки…", "Эски уячаларды өчүрүү…", "Erase old slots…", "Eski hücreleri sil…", "Eski kataklarni o‘chirish…");
        Avalonia.Controls.ToolTip.SetTip(EraseSlotsButton, Tr.T(
            "Очистить ячейки PLU на весах Rongta: старые (товар сменил номер или его нет в каталоге), номера вручную или все. Перед стиранием покажем список и спросим подтверждение.",
            "Rongta таразасындагы PLU уячаларын тазалоо: эски (товар номерин алмаштырды же каталогто жок), номерлерди колу менен же баарын. Өчүрүүдөн мурун тизмени көрсөтүп, ырастоо сурайбыз.",
            "Clear PLU slots on the Rongta scale: old ones (the product changed its number or is not in the catalog), numbers by hand, or all. We show the list and ask for confirmation first.",
            "Rongta tartıdaki PLU hücrelerini temizle: eskiler (ürün numara değiştirdi veya katalogda yok), elle numaralar veya hepsi. Silmeden önce listeyi gösterip onay isteriz.",
            "Rongta tarozisidagi PLU kataklarini tozalash: eskilari (tovar raqamini almashtirgan yoki katalogda yo‘q), qo‘lda raqamlar yoki hammasi. O‘chirishdan oldin ro‘yxatni ko‘rsatib, tasdiq so‘raymiz."));
        EraseSlotsButton.IsVisible = IsRongtaLan;
    }

    /// <summary>После отправки Rongta: ячейки, из которых товары переехали, — в «старые» (по ним окно предложит стереть); записанные сейчас ячейки старыми не считаются.
    /// Вызывается ДО RememberSent: там «что на весах» обновляется, а прежние названия ещё нужны.</summary>
    private void RememberRongtaStaleSlots(ScalePluMovePlan plan, IReadOnlyDictionary<string, ScaleSentPlu> written)
    {
        var profile = LabelScaleStore.Active;
        RongtaSlotEraser.RememberStale(profile.StaleSlots, plan.SlotsToClear, profile.SentPlus);
        RongtaSlotEraser.Forget(profile.StaleSlots, written.Values.Select(v => v.Plu));
        LabelScaleStore.Save();
        if (plan.SlotsToClear.Count > 0)
            PosLogger.Log("Rongta: запомнены старые ячейки для стирания: " + string.Join(", ", plan.SlotsToClear.Select(m => $"PLU {m.From} (товар теперь в {m.To})")), "SCALES");
    }

    private async void EraseSlots_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsRongtaLan)
            return;
        if (_tmCts is not null || _eraseCts is not null)
        {
            StatusText.Text = Tr.T("Подождите: идёт отправка или стирание.", "Күтө туруңуз: жөнөтүү же өчүрүү жүрүп жатат.", "Please wait: sending or erasing is in progress.", "Lütfen bekleyin: gönderim veya silme sürüyor.", "Kuting: yuborish yoki o‘chirish davom etmoqda.");
            return;
        }

        var prefs = UserPreferences.Instance;
        var scale = DahuaTmScaleService.TryCreate(prefs.RongtaScaleIp, DahuaTmProtocol.DefaultPort, DahuaTmNameCodec.Rongta);
        if (scale is null)
        {
            StatusText.Text = Tr.T("Не задан IP весов Rongta — «Настройки весов Rongta» → «Подключение».",
                "Rongta таразасынын IP'си коюлган эмес — «Rongta таразасынын жөндөөлөрү» → «Туташуу».",
                "The Rongta scale IP is not set — “Rongta scale settings” → “Connection”.",
                "Rongta tartı IP'si girilmemiş — «Rongta tartı ayarları» → «Bağlantı».",
                "Rongta tarozi IP'si kiritilmagan — «Rongta tarozi sozlamalari» → «Ulanish».");
            return;
        }

        var profile = LabelScaleStore.Active;
        var isLive = LiveFilter(LiveProductIds()); // null — каталог не загружен: «нет в каталоге» не определяем, все отправленные товары считаем живыми
        var stale = RongtaSlotEraser.StaleCandidates(profile.StaleSlots, profile.SentPlus, isLive);

        // Защита: ячейки отмеченных сейчас товаров (в том числе ещё не отправленных — их номер уже показан в колонке PLU) и ячейки живых товаров, отправленных кассой.
        var ticked = new HashSet<int>();
        foreach (var row in _allRows.Where(r => r.IsSelected))
            if (int.TryParse((row.PluText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var plu) && plu > 0)
                ticked.Add(plu);
        var sentProtected = profile.SentPlus.Where(kv => isLive is null || isLive(kv.Key)).Select(kv => kv.Value.Plu).Where(p => p > 0).ToHashSet();

        var names = new Dictionary<int, string>();
        foreach (var (id, sent) in profile.SentPlus)
            if (sent.Plu > 0 && !string.IsNullOrWhiteSpace(sent.Name))
                names[sent.Plu] = sent.Name!;
        foreach (var (slot, name) in stale)
            if (!string.IsNullOrWhiteSpace(name))
                names[slot] = name;

        var dialog = new RongtaEraseSlotsDialog(new RongtaEraseContext(scale.Host, stale, ticked, sentProtected, names));
        if (await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true) != true || dialog.Plan is not { } plan || plan.Slots.Count == 0)
            return;
        await EraseRongtaSlotsAsync(scale, plan, dialog.Mode).ConfigureAwait(true);
    }

    private async Task EraseRongtaSlotsAsync(DahuaTmScaleService scale, RongtaErasePlan plan, RongtaEraseMode mode)
    {
        var count = plan.Slots.Count;
        var records = plan.Slots.Select(RongtaSlotEraser.BlankRecord).ToList();
        _eraseCts = new CancellationTokenSource();
        EraseSlotsButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        RongtaCheckBanner.IsVisible = false;
        SetProgress(0, count);
        StatusText.Text = Tr.T($"Стираю ячейки на весах {scale.Host}: 0 из {count}…", $"{scale.Host} таразасында уячалар өчүрүлүүдө: {count} ичинен 0…", $"Erasing slots on the scale {scale.Host}: 0 of {count}…",
            $"{scale.Host} tartısında hücreler siliniyor: 0 / {count}…", $"{scale.Host} tarozisida kataklar o‘chirilmoqda: {count} dan 0…");
        PosLogger.Log($"Rongta: стирание ячеек (режим {mode}) на {scale.Host}:{scale.Port}: {count} шт., PLU {RongtaSlotEraser.FormatSlots(plan.Slots, 80)}; пропущено защищённых {plan.SkippedProtected.Count}"
            + (plan.SkippedProtected.Count > 0 ? $" (PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 40)})" : ""), "SCALES");
        try
        {
            var progress = new Progress<DahuaTmUploadProgress>(p =>
            {
                SetProgress(p.Done, p.Total);
                StatusText.Text = Tr.T($"Стираю ячейки: {p.Done} из {p.Total}…", $"Уячалар өчүрүлүүдө: {p.Total} ичинен {p.Done}…", $"Erasing slots: {p.Done} of {p.Total}…",
                    $"Hücreler siliniyor: {p.Done} / {p.Total}…", $"Kataklar o‘chirilmoqda: {p.Total} dan {p.Done}…");
            });
            var result = await scale.UploadPlusAsync(records, DahuaPricePoint, DahuaTmSendMode.LineByLine, progress, _eraseCts.Token).ConfigureAwait(true);
            PosLogger.Log($"Rongta: стирание ячеек {scale.Host}:{scale.Port}: всего {result.Total}, отправлено {result.Sent}, ответов {result.Acknowledged}, без маркера {result.UnframedReplies}, повторов {result.Retries}, ошибка {result.Error} {result.Detail}", "SCALES");

            // Строки уходят по порядку: принятые весами — первые N (как у обычной отправки).
            var accepted = result.Ok ? count : Math.Min(count, result.Acknowledged + result.UnframedReplies);
            var cleared = plan.Slots.Take(accepted).ToList();
            MarkErasedSlots(cleared);

            var skippedNote = plan.SkippedProtected.Count > 0
                ? Tr.T($" Пропущено (товары в работе): {plan.SkippedProtected.Count}.", $" Өткөрүлдү (иштеп жаткан товарлар): {plan.SkippedProtected.Count}.", $" Skipped (goods in use): {plan.SkippedProtected.Count}.",
                    $" Atlandı (kullanımdaki ürünler): {plan.SkippedProtected.Count}.", $" O‘tkazib yuborildi (ishlatilayotgan tovarlar): {plan.SkippedProtected.Count}.")
                : "";
            if (result.Ok)
            {
                StatusText.Text = Tr.T($"Стёрто ячеек Rongta: {accepted} (PLU {RongtaSlotEraser.FormatSlots(cleared, 25)}). Ячейки очищены пустым товаром — проверьте на весах: зажмите [PLU] и введите номер.",
                                       $"Rongta уячалары өчүрүлдү: {accepted} (PLU {RongtaSlotEraser.FormatSlots(cleared, 25)}). Уячалар бош товар менен тазаланды — таразадан текшериңиз: [PLU] кармап, номерди териңиз.",
                                       $"Rongta slots erased: {accepted} (PLU {RongtaSlotEraser.FormatSlots(cleared, 25)}). The slots were cleared with an empty item — check on the scale: hold [PLU] and enter the number.",
                                       $"Rongta hücreleri silindi: {accepted} (PLU {RongtaSlotEraser.FormatSlots(cleared, 25)}). Hücreler boş bir ürünle temizlendi — tartıdan kontrol edin: [PLU] basılı tutup numarayı girin.",
                                       $"Rongta kataklari o‘chirildi: {accepted} (PLU {RongtaSlotEraser.FormatSlots(cleared, 25)}). Kataklar bo‘sh tovar bilan tozalandi — tarozidan tekshiring: [PLU] ni bosib turib raqamni kiriting.")
                                  + skippedNote;
            }
            else
            {
                var errorText = TmErrorText(result.Error, true);
                StatusText.Text = Tr.T($"Стирание не завершено: {errorText}. Стёрто: {accepted} из {count}",
                                       $"Өчүрүү аягына чыккан жок: {errorText}. Өчүрүлдү: {count} ичинен {accepted}",
                                       $"Erasing did not finish: {errorText}. Erased: {accepted} of {count}",
                                       $"Silme tamamlanmadı: {errorText}. Silinen: {accepted} / {count}",
                                       $"O‘chirish tugamadi: {errorText}. O‘chirildi: {count} dan {accepted}")
                                  + (result.FailedPlu > 0 ? $" (PLU {result.FailedPlu})" : "")
                                  + Tr.T(". Журнал обмена: ", ". Алмашуу журналы: ", ". Exchange log: ", ". İletişim günlüğü: ", ". Almashuv jurnali: ") + DahuaTmScaleService.ExchangeLogPath
                                  + skippedNote;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta: стирание ячеек не удалось: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка стирания: ", "Өчүрүү катасы: ", "Erase error: ", "Silme hatası: ", "O‘chirish xatosi: ") + ex.Message;
        }
        finally
        {
            _eraseCts?.Dispose();
            _eraseCts = null;
            HideProgress();
            EraseSlotsButton.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>Ячейки стёрты: они больше не «старые» и не «что на весах»; строкам товаров, чья ячейка стёрта (ручной список / «все» без защиты), — пометка «отправьте заново».</summary>
    private void MarkErasedSlots(IReadOnlyList<int> cleared)
    {
        if (cleared.Count == 0)
            return;
        var profile = LabelScaleStore.Active;
        var slots = cleared.ToHashSet();
        var rowsById = _allRows.ToDictionary(r => r.Id);
        foreach (var (id, sent) in profile.SentPlus.Where(kv => slots.Contains(kv.Value.Plu)).ToList())
            if (rowsById.TryGetValue(id, out var row))
                row.SetStatus(Tr.T($"⚠ ячейка {sent.Plu} стёрта — отправьте товар на весы заново", $"⚠ {sent.Plu}-уяча өчүрүлдү — товарды таразага кайра жөнөтүңүз",
                    $"⚠ slot {sent.Plu} erased — send the product to the scale again", $"⚠ {sent.Plu} hücresi silindi — ürünü tartıya yeniden gönderin", $"⚠ {sent.Plu}-katak o‘chirildi — tovarni taroziga qayta yuboring"), RowState.Warning);
        RongtaSlotEraser.Forget(profile.StaleSlots, cleared);
        ScalePluPlanner.ApplySent(profile.SentPlus, new Dictionary<string, ScaleSentPlu>(), cleared);
        LabelScaleStore.Save();
        LabelScaleStore.PublishLabelCodes();
    }
}
