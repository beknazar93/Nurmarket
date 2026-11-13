using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// 2026-11-13 (просьба владельца: «весы Rongta печатают сумму, при ≥ 1000 сом штрихкода нет — нужен тип 07»): запись ТИПА ШТРИХКОДА в карточки весов Rongta.
/// В «!0V» (порт 4001) такого поля нет — тип пишется «родной» командой весов «обновить PLU» (UDP 5001, <see cref="RongtaUdpProtocol"/>), как это делает RLS1000.
/// Включается только когда в «Настройках весов Rongta» → «Штрих-код» выбрана структура (UserPreferences.RongtaServerBarcodeType); по умолчанию — как раньше, ничего не шлётся.
/// Пишутся только ВЕСОВЫЕ товары (у штучных тип не трогаем — у них своя структура «ALL PCS Barcode Type» на весах).
/// </summary>
public static class RongtaBarcodeTypeWriter
{
    /// <summary>Тип, который касса пишет в карточки; null — структура не выбиралась (ничего не пишем).</summary>
    public static int? ChosenType => UserPreferences.Instance.RongtaServerBarcodeType is int t and >= 0 and <= 99 ? t : null;

    /// <summary>2026-11-13: итог последней записи типа в этой сессии (для плашки «Проверьте на весах» в окнах «Весы» и «Отправка товаров на весы»); null — ещё не писали.</summary>
    public static (int Type, bool Ok)? LastOutcome { get; private set; }

    /// <summary>2026-11-13: плашка после записи типа: что проверить на самих весах и как отсканировать пробную этикетку (5 языков).</summary>
    public static string CheckHint(int type) => Tr.T(
        $"Проверьте на весах: зажмите [PLU] → номер товара → «Bar Code Type» должен быть {type:00}. Напечатайте этикетку (лучше дороже 1000 сом) и отсканируйте её кассой.",
        $"Таразада текшериңиз: [PLU] кармап туруңуз → товардын номери → «Bar Code Type» {type:00} болушу керек. Этикетка басыңыз (1000 сомдон кымбат болсо жакшы) жана кассада сканерлеңиз.",
        $"Check on the scale: hold [PLU] → item number → “Bar Code Type” must be {type:00}. Print a label (better over 1000 som) and scan it at the till.",
        $"Tartıda kontrol edin: [PLU] basılı tutun → ürün numarası → «Bar Code Type» {type:00} olmalı. Bir etiket basın (1000 somdan pahalı olması iyi) ve kasada okutun.",
        $"Tarozida tekshiring: [PLU] ni bosib turing → tovar raqami → «Bar Code Type» {type:00} bo‘lishi kerak. Yorliq chop eting (1000 so‘mdan qimmat bo‘lgani yaxshi) va kassada skanerlang.");

    /// <summary>2026-11-13: весы не ответили по UDP 5001 — запасной путь на самих весах и где журнал обмена (5 языков).</summary>
    public static string NoReplyHint(int type) => Tr.T(
        $"Весы не ответили по порту 5001 — тип {type:00} в карточки не записан. Поставьте его на весах: [SETTING] (держать 2–3 с) → «barcode type: set all barcodes' type» → {type:00} → [PRN/ENTER]. Журнал обмена: {RongtaUdpScaleService.ExchangeLogPath}",
        $"Тараза 5001 порту боюнча жооп берген жок — {type:00} түрү карточкаларга жазылган жок. Аны таразада коюңуз: [SETTING] (2–3 с кармоо) → «barcode type: set all barcodes' type» → {type:00} → [PRN/ENTER]. Алмашуу журналы: {RongtaUdpScaleService.ExchangeLogPath}",
        $"The scale did not answer on port 5001 — type {type:00} was not written to the cards. Set it on the scale: [SETTING] (hold 2–3 s) → “barcode type: set all barcodes' type” → {type:00} → [PRN/ENTER]. Exchange log: {RongtaUdpScaleService.ExchangeLogPath}",
        $"Tartı 5001 portundan yanıt vermedi — {type:00} türü kartlara yazılmadı. Tartıda ayarlayın: [SETTING] (2–3 sn basılı) → «barcode type: set all barcodes' type» → {type:00} → [PRN/ENTER]. İletişim günlüğü: {RongtaUdpScaleService.ExchangeLogPath}",
        $"Tarozi 5001 port orqali javob bermadi — {type:00} turi kartochkalarga yozilmadi. Uni tarozida belgilang: [SETTING] (2–3 s bosib turish) → «barcode type: set all barcodes' type» → {type:00} → [PRN/ENTER]. Almashuv jurnali: {RongtaUdpScaleService.ExchangeLogPath}");

    /// <summary>Карточки «родного» формата для весовых товаров из записей «!0V».</summary>
    public static List<RongtaUdpPlu> BuildRecords(IEnumerable<DahuaTmPlu> records, int priceDecimals, int barcodeType) =>
        records.Where(r => r.WeighMode == DahuaTmWeighMode.Weighed)
            .Select(r => RongtaUdpProtocol.FromDahua(r, priceDecimals, barcodeType))
            .ToList();

    /// <summary>2026-11-13: записи «!0V» для всех товаров, уже отправленных на выбранные весы (что на весах — LabelScaleStore.Active.SentPlus),
    /// с ценой и названием из текущего каталога — для кнопки «Применить тип штрихкода ко всем товарам».</summary>
    public static List<DahuaTmPlu> FromSentGoods()
    {
        var prefs = UserPreferences.Instance;
        var byId = CatalogCacheService.Products.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<DahuaTmPlu>();
        foreach (var (id, sent) in LabelScaleStore.Active.SentPlus)
        {
            if (!byId.TryGetValue(id, out var product) || !product.IsWeighted || sent.Plu is < 1 or > DahuaTmProtocol.MaxPluNumber)
                continue;
            if (!long.TryParse(sent.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                code = sent.Plu;
            result.Add(new DahuaTmPlu
            {
                PluNumber = sent.Plu,
                ProductCode = code,
                Price = (decimal)LocalCartService.ParsePrice(product.PriceLine),
                WeighMode = DahuaTmWeighMode.Weighed,
                ShelfLifeDays = Math.Clamp(prefs.RongtaLanShelfLifeDays, 0, 999),
                BarcodePrefix = Math.Clamp(prefs.RongtaLanBarcodePrefix, 0, 99),
                Name = product.Title,
            });
        }
        return result.OrderBy(r => r.PluNumber).ToList();
    }

    /// <summary>Пишет тип в карточки и возвращает строку итога (5 языков) для окна; всё — в журнал кассы и в rongta-udp-exchange.log.</summary>
    public static async Task<(bool Ok, string Note)> WriteAsync(string? ip, IReadOnlyList<DahuaTmPlu> records, int priceDecimals, int barcodeType, CancellationToken ct)
    {
        var scale = RongtaUdpScaleService.TryCreate(ip);
        if (scale is null)
            return (false, Tr.T("Не задан IP весов Rongta.", "Rongta таразасынын IP'си коюлган эмес.", "The Rongta scale IP is not set.", "Rongta tartı IP'si girilmemiş.", "Rongta tarozi IP'si kiritilmagan."));
        List<RongtaUdpPlu> plus;
        try
        {
            plus = BuildRecords(records, priceDecimals, barcodeType);
        }
        catch (ArgumentException ex)
        {
            PosLogger.Log($"Rongta: тип штрихкода не записан — {ex.Message}", "SCALES");
            return (false, ex.Message);
        }
        if (plus.Count == 0)
            return (true, Tr.T("Весовых товаров нет — тип штрихкода писать некуда.", "Салмактуу товар жок — штрих-коддун түрүн жазууга эч нерсе жок.", "No weighed goods — nothing to write the barcode type to.", "Tartılı ürün yok — barkod türü yazılacak yer yok.", "Vaznli tovar yo‘q — shtrix-kod turini yozadigan joy yo‘q."));

        var result = await scale.UpdatePlusAsync(plus, DateTime.Now, ct).ConfigureAwait(true);
        PosLogger.Log($"Rongta: тип штрихкода {barcodeType:00} → {scale.Host}:{scale.Port} (UDP), карточек {plus.Count}, пакетов {result.Packets}, ошибка {result.Error} {result.Detail}; PLU {string.Join(",", plus.Take(30).Select(p => p.LfCode))}", "SCALES");
        LastOutcome = (barcodeType, result.Ok);
        if (result.Ok)
            return (true, Tr.T($"Тип штрихкода {barcodeType:00} записан в весовые товары: {plus.Count}.", $"Штрих-коддун {barcodeType:00} түрү салмактуу товарларга жазылды: {plus.Count}.", $"Barcode type {barcodeType:00} written to weighed goods: {plus.Count}.", $"Barkod türü {barcodeType:00} tartılı ürünlere yazıldı: {plus.Count}.", $"Shtrix-kod turi {barcodeType:00} vaznli tovarlarga yozildi: {plus.Count}."));
        return (false, Tr.T($"Тип штрихкода не записан ({ErrorText(result.Error)}).", $"Штрих-коддун түрү жазылган жок ({ErrorText(result.Error)}).", $"Barcode type not written ({ErrorText(result.Error)}).", $"Barkod türü yazılmadı ({ErrorText(result.Error)}).", $"Shtrix-kod turi yozilmadi ({ErrorText(result.Error)})."));
    }

    public static string ErrorText(RongtaUdpError error) => error switch
    {
        RongtaUdpError.NoReply => Tr.T("весы не ответили по порту 5001", "тараза 5001 порту боюнча жооп берген жок", "the scale did not answer on port 5001", "tartı 5001 portundan yanıt vermedi", "tarozi 5001 port orqali javob bermadi"),
        RongtaUdpError.Rejected => Tr.T("весы отказали", "тараза баш тартты", "the scale refused", "tartı reddetti", "tarozi rad etdi"),
        RongtaUdpError.SendFailed => Tr.T("не удалось отправить", "жөнөтүү мүмкүн болгон жок", "could not send", "gönderilemedi", "yuborib bo‘lmadi"),
        RongtaUdpError.Cancelled => Tr.T("остановлено", "токтотулду", "stopped", "durduruldu", "to‘xtatildi"),
        RongtaUdpError.BadAddress => Tr.T("неверный IP", "IP туура эмес", "bad IP", "hatalı IP", "IP noto‘g‘ri"),
        _ => error.ToString(),
    };
}
