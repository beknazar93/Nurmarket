using System.Globalization;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-13: весы Масса-К серий R/SL (АО «Масса-К», открытый протокол — MassaKProtocol, TCP 5001). Касса пишет в весы ТОЛЬКО одиночные записи
// (файл товаров 101 и файл PLU 105): справочник весов не стирается. На живых весах не проверено — отправка с подтверждением, пока владелец не отметит
// «проверено на весах» в окне «Настройки весов Масса-К». Только весовые товары: у штучных Масса-К печатает в штрихкоде количество и префикс +1.
public partial class ScalesPluWindow
{
    private const string BrandMassaK = "massak";

    private bool IsMassaK => _brand == BrandMassaK;

    /// <summary>Предел номера PLU в штрихкоде: раскладка «по PLU» (2+5+5+1) — 5 цифр, «по коду» (2+6+4+1) — 6.</summary>
    private static int MassaKMaxPlu() =>
        string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? 999_999 : 99_999;

    private static string MassaKSubtitle() => Tr.T(
        "Отмеченные весовые товары уйдут прямо на весы Масса-К (серии R, SL) по сети. Касса пишет только одиночные записи — справочник весов не стирается. У каждого товара постоянный номер PLU (колонка PLU): он же печатается в штрихкоде этикетки. Штучные товары не отправляются.",
        "Белгиленген салмактуу товарлар Масса-К (R, SL сериялары) таразасына тармак аркылуу түз кетет. Касса жеке жазууларды гана жазат — тараза справочниги өчүрүлбөйт. Ар бир товардын туруктуу PLU номери бар (PLU тилкеси): ал этикетка штрих-кодунда да басылат. Дааналык товарлар жөнөтүлбөйт.",
        "The ticked weighed goods go straight to the Massa-K scale (R, SL series) over the network. The till writes single records only — the scale's directory is not erased. Every product has a fixed PLU number (PLU column): it is also printed in the label barcode. Piece goods are not sent.",
        "İşaretli tartılı ürünler ağ üzerinden doğrudan Massa-K tartıya (R, SL serisi) gider. Kasa yalnızca tek kayıt yazar — tartının rehberi silinmez. Her ürünün sabit bir PLU numarası var (PLU sütunu): etiket barkoduna da o basılır. Adetli ürünler gönderilmez.",
        "Belgilangan vaznli tovarlar tarmoq orqali to‘g‘ridan-to‘g‘ri Massa-K tarozisiga (R, SL seriyalari) ketadi. Kassa faqat alohida yozuvlarni yozadi — tarozi ma’lumotnomasi o‘chirilmaydi. Har bir tovarning doimiy PLU raqami bor (PLU ustuni): u yorliq shtrix-kodida ham bosiladi. Donali tovarlar yuborilmaydi.");

    private static string MassaKSaveFileText() => Tr.T("Пробный пакет в журнал", "Сыноо пакетти журналга", "Trial packet to the log", "Deneme paketini günlüğe", "Sinov paketini jurnalga");

    /// <summary>Пример этикетки: префикс + PLU + вес 0,392 кг + контрольная цифра — так касса прочтёт код (раскладка компании «по PLU» / «по коду»).</summary>
    private void UpdateMassaKBarcodeExample(ScalePluRowVm? row)
    {
        var prefs = UserPreferences.Instance;
        var byCode = string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);
        var plu = row is not null && int.TryParse(row.PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
        var body = byCode
            ? $"{Math.Clamp(prefs.MassaKBarcodePrefix, 0, 99):00}{plu % 1_000_000:000000}0392"
            : $"{Math.Clamp(prefs.MassaKBarcodePrefix, 0, 99):00}{plu % 100_000:00000}00392";
        var sample = body + MassaKEan13Check(body);
        var name = row?.Name ?? "";
        BarcodeExampleText.Text = Tr.T(
            $"Штрих-код на этикетке весов для «{name}» (0,392 кг): {sample} — префикс {prefs.MassaKBarcodePrefix:00}, PLU {plu}. Формат этикетки (шаблон) задаётся на самих весах; касса читает такой код по раскладке компании «{(byCode ? "по коду" : "по PLU")}».",
            $"«{name}» үчүн тараза этикеткасындагы штрих-код (0,392 кг): {sample} — префикс {prefs.MassaKBarcodePrefix:00}, PLU {plu}. Этикетка форматы (шаблон) таразанын өзүндө коюлат; касса мындай кодду компаниянын «{(byCode ? "код боюнча" : "PLU боюнча")}» раскладкасы менен окуйт.",
            $"Scale label barcode for “{name}” (0.392 kg): {sample} — prefix {prefs.MassaKBarcodePrefix:00}, PLU {plu}. The label format (template) is set on the scale itself; the till reads such a code by the company layout “{(byCode ? "by code" : "by PLU")}”.",
            $"“{name}” için tartı etiketi barkodu (0,392 kg): {sample} — önek {prefs.MassaKBarcodePrefix:00}, PLU {plu}. Etiket biçimi (şablon) tartının kendisinde ayarlanır; kasa böyle bir kodu şirket düzeniyle «{(byCode ? "koda göre" : "PLU’ya göre")}» okur.",
            $"«{name}» uchun tarozi yorlig‘idagi shtrix-kod (0,392 kg): {sample} — prefiks {prefs.MassaKBarcodePrefix:00}, PLU {plu}. Yorliq formati (shablon) tarozining o‘zida belgilanadi; kassa bunday kodni kompaniya tartibi «{(byCode ? "kod bo‘yicha" : "PLU bo‘yicha")}» bilan o‘qiydi.");
    }

    private static int MassaKEan13Check(string twelveDigits)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (twelveDigits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    /// <summary>«Пробный пакет в журнал» (вместо «Сохранить CSV»): те же кадры, что при отправке, но в сеть не идут — только в massak-exchange.log.</summary>
    private async Task MassaKDryRunAsync()
    {
        ClearRowStatuses();
        await SendToMassaKAsync(dryRun: true).ConfigureAwait(true);
    }

    private async Task SendToMassaKAsync(bool dryRun)
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        var prefs = UserPreferences.Instance;
        var scale = MassaKScaleService.TryCreate(prefs.MassaKScaleIp, prefs.MassaKScalePort);
        if (scale is null)
        {
            StatusText.Text = Tr.T("Не задан IP весов Масса-К — «Настройки весов Масса-К» → «Быстрая настройка».",
                "Масса-К таразасынын IP'си коюлган эмес — «Масса-К таразасынын жөндөөлөрү» → «Ыкчам жөндөө».",
                "The Massa-K scale IP is not set — “Massa-K scale settings” → “Quick setup”.",
                "Massa-K tartı IP'si girilmemiş — «Massa-K tartı ayarları» → «Hızlı kurulum».",
                "Massa-K tarozi IP'si kiritilmagan — «Massa-K tarozi sozlamalari» → «Tez sozlash».");
            return;
        }

        var (items, ids, written, notes) = BuildMassaKItems(selectedIds);
        if (items.Count == 0)
        {
            StatusText.Text = notes.Count > 0
                ? Tr.T("Не отправлено — исправьте: ", "Жөнөтүлгөн жок — оңдоңуз: ", "Not sent — fix: ", "Gönderilmedi — düzeltin: ", "Yuborilmadi — tuzating: ") + string.Join(" · ", notes.Take(3)) + (notes.Count > 3 ? $" (+{notes.Count - 3})" : "")
                : Tr.T("Нечего отправлять — отметьте весовые товары.", "Жөнөтө турган эч нерсе жок — салмактуу товарларды белгилеңиз.", "Nothing to send — tick weighed goods.", "Gönderilecek bir şey yok — tartılı ürünleri işaretleyin.", "Yuboradigan narsa yo‘q — vaznli tovarlarni belgilang.");
            return;
        }
        var skippedNote = notes.Count == 0 ? "" : " " + Tr.T($"Пропущено: {notes.Count} (см. строки таблицы).", $"Өткөрүлдү: {notes.Count} (таблица саптарын караңыз).", $"Skipped: {notes.Count} (see table rows).", $"Atlandı: {notes.Count} (tablo satırlarına bakın).", $"O‘tkazib yuborildi: {notes.Count} (jadval qatorlariga qarang).");

        if (dryRun)
        {
            var dry = await scale.UploadAsync(items, prefs.MassaKLatinNames, true, null, CancellationToken.None).ConfigureAwait(true);
            PosLogger.Log($"Масса-К: пробный пакет {scale.Host}:{scale.Port}: {dry.Total} товаров — только в журнал, весам ничего не отправлено", "SCALES");
            StatusText.Text = Tr.T($"Пробный пакет: {dry.Total} товаров записаны в журнал {MassaKScaleService.ExchangeLogPath} (строки DRY-TX). Весам ничего не отправлено.",
                $"Сыноо пакет: {dry.Total} товар {MassaKScaleService.ExchangeLogPath} журналына жазылды (DRY-TX саптары). Таразага эч нерсе жөнөтүлгөн жок.",
                $"Trial packet: {dry.Total} products written to the log {MassaKScaleService.ExchangeLogPath} (DRY-TX lines). Nothing was sent to the scale.",
                $"Deneme paketi: {dry.Total} ürün {MassaKScaleService.ExchangeLogPath} günlüğüne yazıldı (DRY-TX satırları). Tartıya hiçbir şey gönderilmedi.",
                $"Sinov paketi: {dry.Total} tovar {MassaKScaleService.ExchangeLogPath} jurnaliga yozildi (DRY-TX qatorlari). Taroziga hech narsa yuborilmadi.") + skippedNote;
            return;
        }

        // Пока на живых весах не проверено — каждый раз спрашиваем (после отметки «проверено» можно отключить вопрос в настройках).
        // 2026-11-13: автоотправка (_silent) включается только при «проверено» и ничего не спрашивает.
        if (!_silent && !(prefs.MassaKVerified && prefs.MassaKSkipConfirm))
        {
            var ok = await PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Отправка на весы Масса-К", "Масса-К таразасына жөнөтүү", "Sending to the Massa-K scale", "Massa-K tartıya gönderim", "Massa-K taroziga yuborish"),
                Tr.T($"Протокол Масса-К в кассе пока НЕ ПРОВЕРЕН на живых весах. Касса запишет {items.Count} товар(ов) одиночными записями (справочник весов не стирается) на {scale.Host}:{scale.Port}. После отправки проверьте на весах название, цену и напечатайте этикетку. Отправить?",
                    $"Кассадагы Масса-К протоколу жандуу таразада АЗЫРЫНЧА ТЕКШЕРИЛГЕН ЭМЕС. Касса {scale.Host}:{scale.Port} дарегине {items.Count} товарды жеке жазуулар менен жазат (тараза справочниги өчүрүлбөйт). Жөнөткөндөн кийин таразадан аталышын, баасын текшерип, этикетка басыңыз. Жөнөтөбүзбү?",
                    $"The Massa-K protocol in the till is NOT YET VERIFIED on live scales. The till will write {items.Count} product(s) as single records (the scale's directory is not erased) to {scale.Host}:{scale.Port}. After sending, check the name and price on the scale and print a label. Send?",
                    $"Kasadaki Massa-K protokolü canlı tartıda HENÜZ DOĞRULANMADI. Kasa {items.Count} ürünü tek kayıtlar halinde {scale.Host}:{scale.Port} adresine yazacak (tartının rehberi silinmez). Gönderdikten sonra tartıda adı ve fiyatı kontrol edip etiket basın. Gönderilsin mi?",
                    $"Kassadagi Massa-K protokoli jonli tarozida HALI TEKSHIRILMAGAN. Kassa {items.Count} ta tovarni alohida yozuvlar bilan {scale.Host}:{scale.Port} manziliga yozadi (tarozi ma’lumotnomasi o‘chirilmaydi). Yuborgandan keyin tarozida nomi va narxini tekshirib, yorliq chop eting. Yuborilsinmi?"),
                Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
            if (!ok)
            {
                StatusText.Text = Tr.T("Отправка отменена.", "Жөнөтүү жокко чыгарылды.", "Sending cancelled.", "Gönderim iptal edildi.", "Yuborish bekor qilindi.");
                return;
            }
        }

        _tmCts = new CancellationTokenSource();
        RongtaCheckBanner.IsVisible = false;
        SendButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");
        StatusText.Text = Tr.T($"Подключение к весам {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} таразасына туташуу…", $"Connecting to the scale {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} tartısına bağlanılıyor…", $"{scale.Host}:{scale.Port} tarozisiga ulanilmoqda…");
        SetProgress(0, items.Count);
        var rowsById = _allRows.ToDictionary(r => r.Id);
        try
        {
            var progress = new Progress<MassaKUploadProgress>(p =>
            {
                SetProgress(p.Done, p.Total);
                StatusText.Text = Tr.T($"Отправка на весы: {p.Done} из {p.Total}…", $"Таразага жөнөтүү: {p.Total} ичинен {p.Done}…", $"Sending to the scale: {p.Done} of {p.Total}…", $"Tartıya gönderim: {p.Done} / {p.Total}…", $"Taroziga yuborish: {p.Total} dan {p.Done}…");
            });
            var result = await scale.UploadAsync(items, prefs.MassaKLatinNames, false, progress, _tmCts.Token).ConfigureAwait(true);
            PosLogger.Log("Масса-К: товары переотправлены на весы вручную (кнопка «Отправить на весы»)", "SCALES");
            PosLogger.Log($"Масса-К: выгрузка {scale.Host}:{scale.Port}: всего {result.Total}, принято {result.Accepted}, ошибка {result.Error} {result.Detail}", "SCALES");

            for (var i = 0; i < ids.Count; i++)
            {
                if (!rowsById.TryGetValue(ids[i], out var row))
                    continue;
                if (i < result.Accepted)
                    row.SetStatus(SentText(), RowState.Ok);
                else if (!result.Ok && items[i].Goods.Id == result.FailedId)
                    row.SetStatus("✗ " + MassaKErrorText(result.Error), RowState.Error);
                else
                    row.SetStatus(NotSentText(), RowState.Warning);
            }

            var accepted = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
            for (var i = 0; i < result.Accepted && i < ids.Count; i++)
                accepted[ids[i]] = written[ids[i]];
            var plan = ScalePluPlanner.PlanMoves(LabelScaleStore.Active.SentPlus, accepted);
            RememberSent(accepted, Array.Empty<int>());
            var movedNote = plan.PluMoves.Count == 0
                ? ""
                : " " + Tr.T($"Сменился PLU у товаров: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Записи со старыми номерами остались на весах — удалите их на самих весах.",
                    $"PLU өзгөргөн товарлар: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Эски номерлүү жазуулар таразада калды — аларды таразанын өзүндө өчүрүңүз.",
                    $"PLU changed for: {string.Join(", ", plan.PluMoves.Select(m => $"“{rowsById.GetValueOrDefault(m.ProductId)?.Name}” {m.From} → {m.To}"))}. The records with the old numbers stay on the scale — delete them on the scale itself.",
                    $"PLU'su değişen ürünler: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Eski numaralı kayıtlar tartıda kaldı — onları tartının kendisinde silin.",
                    $"PLU o‘zgargan tovarlar: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Eski raqamli yozuvlar tarozida qoldi — ularni tarozining o‘zida o‘chiring.");

            if (result.Ok)
            {
                StatusText.Text = Tr.T($"Принято весами: {result.Accepted} из {result.Total}. Проверьте товар на весах (название, цена) и напечатайте этикетку; если всё верно — отметьте «проверено» в «Настройки весов Масса-К».",
                    $"Тараза кабыл алды: {result.Total} ичинен {result.Accepted}. Товарды таразадан текшериңиз (аталышы, баасы), этикетка басыңыз; баары туура болсо — «Масса-К таразасынын жөндөөлөрүндө» «текшерилди» деп белгилеңиз.",
                    $"Accepted by the scale: {result.Accepted} of {result.Total}. Check the product on the scale (name, price) and print a label; if all is right, tick “verified” in “Massa-K scale settings”.",
                    $"Tartı kabul etti: {result.Accepted} / {result.Total}. Ürünü tartıda kontrol edin (ad, fiyat) ve etiket basın; her şey doğruysa «Massa-K tartı ayarları»nda «doğrulandı» işaretleyin.",
                    $"Tarozi qabul qildi: {result.Total} dan {result.Accepted}. Tovarni tarozida tekshiring (nomi, narxi) va yorliq chop eting; hammasi to‘g‘ri bo‘lsa — «Massa-K tarozi sozlamalari»da «tekshirildi» deb belgilang.") + skippedNote + movedNote;
            }
            else
            {
                StatusText.Text = Tr.T($"Не отправлено до конца: принято {result.Accepted} из {result.Total}. ", $"Аягына чейин жөнөтүлгөн жок: {result.Total} ичинен {result.Accepted} кабыл алынды. ", $"Not sent to the end: {result.Accepted} of {result.Total} accepted. ", $"Sonuna kadar gönderilmedi: {result.Accepted} / {result.Total} kabul edildi. ", $"Oxirigacha yuborilmadi: {result.Total} dan {result.Accepted} ta qabul qilindi. ")
                                  + MassaKErrorText(result.Error) + " " + Tr.T("Подробности — в журнале обмена (massak-exchange.log).", "Чоо-жайы — алмашуу журналында (massak-exchange.log).", "Details are in the exchange log (massak-exchange.log).", "Ayrıntılar iletişim günlüğünde (massak-exchange.log).", "Batafsil — almashuv jurnalida (massak-exchange.log).") + movedNote;
            }
        }
        finally
        {
            _tmCts.Dispose();
            _tmCts = null;
            HideProgress();
            SendButton.Content = _sendButtonDefaultText;
        }
    }

    /// <summary>Собирает записи для весов. Пропускает (с пометкой в строке): штучные, товары без номера PLU, PLU больше предела штрихкода, повторы PLU,
    /// PLU, который на кассе открыл бы ДРУГОЙ товар, цену и данные вне пределов протокола.</summary>
    private (List<(MassaKGoods Goods, long Plu)> Items, List<string> Ids, Dictionary<string, ScaleSentPlu> Written, List<string> Notes) BuildMassaKItems(List<string> selectedIds)
    {
        var prefs = UserPreferences.Instance;
        var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
        var rowsById = _allRows.ToDictionary(r => r.Id);
        var numbers = PinNumbersForSend(selectedIds);
        var maxPlu = MassaKMaxPlu();
        var items = new List<(MassaKGoods, long)>();
        var ids = new List<string>();
        var written = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
        var notes = new List<string>();
        var usedPlu = new Dictionary<int, string>();
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var product))
                continue;
            rowsById.TryGetValue(id, out var row);
            void Skip(string text)
            {
                row?.SetStatus("⚠ " + text, RowState.Warning);
                notes.Add($"«{product.Title}»: {text}");
            }

            if (!product.IsWeighted)
            {
                Skip(Tr.T("штучный товар на Масса-К пока не отправляется", "дааналык товар Масса-К'га азырынча жөнөтүлбөйт", "piece goods are not sent to Massa-K yet", "adetli ürün henüz Massa-K'ya gönderilmiyor", "donali tovar Massa-K ga hozircha yuborilmaydi"));
                continue;
            }
            if (!numbers.TryGetValue(id, out var plu))
            {
                Skip(Tr.T("нет номера PLU", "PLU номери жок", "no PLU number", "PLU numarası yok", "PLU raqami yo‘q"));
                continue;
            }
            if (plu < 1 || plu > maxPlu)
            {
                Skip(Tr.T($"PLU {plu} не помещается в штрихкод (до {maxPlu})", $"PLU {plu} штрих-кодго батпайт ({maxPlu}ге чейин)", $"PLU {plu} does not fit the barcode (up to {maxPlu})", $"PLU {plu} barkoda sığmıyor (en fazla {maxPlu})", $"PLU {plu} shtrix-kodga sig‘maydi ({maxPlu} gacha)"));
                continue;
            }
            if (usedPlu.TryGetValue(plu, out var other))
            {
                Skip(Tr.T($"PLU {plu} уже у «{other}»", $"PLU {plu} «{other}» товарында бар", $"PLU {plu} is already used by “{other}”", $"PLU {plu} zaten «{other}» ürününde", $"PLU {plu} allaqachon «{other}» da"));
                continue;
            }
            usedPlu[plu] = product.Title;
            // Этикетка Масса-К печатает в штрихкоде именно PLU. Если на кассе такой код открыл бы другой товар — продажа по этикетке пойдёт не туда.
            var hit = LocalCartService.FindByEmbeddedCodeInCatalog(plu.ToString(CultureInfo.InvariantCulture));
            if (hit is not null && !string.Equals(hit.Id, id, StringComparison.Ordinal))
            {
                Skip(Tr.T($"PLU {plu} на кассе открывает другой товар «{hit.Title}» — смените PLU товара", $"PLU {plu} кассада башка «{hit.Title}» товарын ачат — товардын PLU'сун алмаштырыңыз", $"PLU {plu} opens another product “{hit.Title}” at the till — change the product's PLU", $"PLU {plu} kasada başka bir ürünü «{hit.Title}» açar — ürünün PLU'sunu değiştirin", $"PLU {plu} kassada boshqa tovarni «{hit.Title}» ochadi — tovar PLU'sini almashtiring"));
                continue;
            }
            var goods = new MassaKGoods
            {
                Id = plu,
                Code = plu.ToString(CultureInfo.InvariantCulture),
                Price = (decimal)LocalCartService.ParsePrice(product.PriceLine),
                Piece = false,
                Name = product.Title,
                BarcodePrefix = Math.Clamp(prefs.MassaKBarcodePrefix, 0, 99),
                ShelfLifeMinutes = Math.Clamp(prefs.MassaKShelfLifeDays, 0, 999) * 1440,
            };
            var problem = MassaKProtocol.Validate(goods);
            if (problem != MassaKGoodsProblem.None)
            {
                Skip(MassaKProblemText(problem));
                continue;
            }
            items.Add((goods, plu));
            ids.Add(id);
            written[id] = new ScaleSentPlu { Plu = plu, Code = plu.ToString(CultureInfo.InvariantCulture), Name = product.Title };
        }
        return (items, ids, written, notes);
    }

    private static string MassaKProblemText(MassaKGoodsProblem problem) => problem switch
    {
        MassaKGoodsProblem.BadId => Tr.T("номер PLU вне пределов весов", "PLU номери тараза чегинен тышкары", "the PLU number is out of the scale's range", "PLU numarası tartının sınırları dışında", "PLU raqami tarozi chegarasidan tashqarida"),
        MassaKGoodsProblem.BadCode => Tr.T("код товара не подходит весам", "товар коду таразага туура келбейт", "the product code does not suit the scale", "ürün kodu tartıya uymuyor", "tovar kodi tarozi uchun mos emas"),
        MassaKGoodsProblem.BadPrice => Tr.T("цена выше предела весов (до 999 999,99)", "баа тараза чегинен жогору (999 999,99го чейин)", "the price is above the scale limit (up to 999,999.99)", "fiyat tartı sınırının üstünde (999.999,99'a kadar)", "narx tarozi chegarasidan yuqori (999 999,99 gacha)"),
        MassaKGoodsProblem.EmptyName => Tr.T("пустое название", "аталышы бош", "empty name", "ad boş", "nom bo‘sh"),
        MassaKGoodsProblem.BadPrefix => Tr.T("префикс штрихкода — от 00 до 99", "штрих-код префикси — 00дөн 99го чейин", "the barcode prefix must be 00 to 99", "barkod öneki 00 ile 99 arasında olmalı", "shtrix-kod prefiksi — 00 dan 99 gacha"),
        _ => "",
    };

    private static string MassaKErrorText(MassaKError error) => error switch
    {
        MassaKError.ConnectFailed => Tr.T("нет подключения к весам (проверьте адрес, порт 5001 и кабель)", "таразага туташуу жок (даректи, 5001 портту жана кабелди текшериңиз)", "cannot connect to the scale (check the address, port 5001 and the cable)", "tartıya bağlanılamadı (adresi, 5001 portunu ve kabloyu kontrol edin)", "taroziga ulanib bo‘lmadi (manzil, 5001 port va kabelni tekshiring)"),
        MassaKError.NoReply => Tr.T("весы не ответили", "тараза жооп берген жок", "the scale did not answer", "tartı yanıt vermedi", "tarozi javob bermadi"),
        MassaKError.Nack => Tr.T("весы ответили «ошибка / неизвестная команда»", "тараза «ката / белгисиз буйрук» деп жооп берди", "the scale answered “error / unknown command”", "tartı «hata / bilinmeyen komut» yanıtı verdi", "tarozi «xato / noma’lum buyruq» deb javob berdi"),
        MassaKError.Rejected => Tr.T("весы отклонили запись (неверный формат или номер файла)", "тараза жазууну четке какты (формат же файл номери туура эмес)", "the scale rejected the record (wrong format or file number)", "tartı kaydı reddetti (yanlış biçim veya dosya numarası)", "tarozi yozuvni rad etdi (format yoki fayl raqami noto‘g‘ri)"),
        MassaKError.SendFailed => Tr.T("связь с весами оборвалась", "тараза менен байланыш үзүлдү", "the connection to the scale dropped", "tartıyla bağlantı koptu", "tarozi bilan aloqa uzildi"),
        MassaKError.Cancelled => Tr.T("остановлено", "токтотулду", "stopped", "durduruldu", "to‘xtatildi"),
        _ => "",
    };
}
