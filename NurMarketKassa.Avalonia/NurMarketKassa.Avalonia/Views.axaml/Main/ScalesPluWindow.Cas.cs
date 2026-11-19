using System.Globalization;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-13: весы CAS. CL5000J и CL3000 — касса пишет карточки PLU по сети (CasProtocol, документ CAS «CL5000J Communication Protocol» 2007; НА ЖИВЫХ ВЕСАХ НЕ
// ПРОВЕРЕНО), CL5500 / CL5200 / CL7200 / LP-1.6 — только файл для CL-Works («Файл → Импорт → CSV», CasCsvWriter). Настройки — CasScaleSettings (локально).
// Касса пишет на весы ТОЛЬКО карточки PLU (команда «записать PLU»); стирание, отделы, форматы штрихкода и этикеток — никогда.
public partial class ScalesPluWindow
{
    private const string BrandCas = "cas";

    private bool IsCas => _brand == BrandCas;

    /// <summary>CAS, модель которой касса ведёт по сети (CL5000J, CL3000).</summary>
    private bool IsCasLan => IsCas && CasScaleSettings.Current.IsNetwork;

    /// <summary>CAS, модель которой — только файл для CL-Works.</summary>
    private bool IsCasFile => IsCas && !CasScaleSettings.Current.IsNetwork;

    private static string CasSaveFileText() => Tr.T("Сохранить файл для CL-Works", "CL-Works үчүн файлды сактоо", "Save file for CL-Works", "CL-Works için dosyayı kaydet", "CL-Works uchun faylni saqlash");

    private static string CasSubtitle() => CasScaleSettings.Current.IsNetwork
        ? Tr.T("Отмеченные товары уйдут прямо на весы CAS по сети карточками PLU (протокол из документа CAS; на живых весах пока не проверено — первую отправку сделайте на одном товаре). У каждого товара постоянный номер PLU (колонка PLU); в штрихкод этикетки весы печатают «Код в ШК». Запасной путь — файл для CL-Works («Сохранить файл для CL-Works»).",
            "Белгиленген товарлар CAS таразасына PLU карточкалары менен тармак аркылуу түз кетет (CAS документиндеги протокол; жандуу таразада азырынча текшерилген эмес — биринчи жөнөтүүнү бир товар менен жасаңыз). Ар бир товардын туруктуу PLU номери бар (PLU тилкеси); этикетка штрих-кодуна тараза «ШКдагы код» басат. Запас жол — CL-Works үчүн файл («CL-Works үчүн файлды сактоо»).",
            "The ticked goods go straight to the CAS scale over the network as PLU cards (protocol from the CAS document; not verified on a live scale yet — send one item first). Every product has a fixed PLU number (PLU column); the scale prints the “Code in barcode” into the label barcode. Fallback — a file for CL-Works (“Save file for CL-Works”).",
            "İşaretli ürünler PLU kartları olarak ağ üzerinden doğrudan CAS tartıya gider (CAS belgesindeki protokol; canlı tartıda henüz doğrulanmadı — ilk gönderimi tek ürünle yapın). Her ürünün sabit bir PLU numarası var (PLU sütunu); tartı etiket barkoduna «Barkoddaki kod»u basar. Yedek yol — CL-Works için dosya («CL-Works için dosyayı kaydet»).",
            "Belgilangan tovarlar PLU kartochkalari bilan tarmoq orqali to‘g‘ridan-to‘g‘ri CAS taroziga ketadi (CAS hujjatidagi protokol; jonli tarozida hali tekshirilmagan — birinchi yuborishni bitta tovar bilan qiling). Har bir tovarning doimiy PLU raqami bor (PLU ustuni); tarozi yorliq shtrix-kodiga «Shtrix-koddagi kod»ni chop etadi. Zaxira yo‘l — CL-Works uchun fayl («CL-Works uchun faylni saqlash»).")
        : Tr.T("Для этой модели CAS касса по сети не пишет: отмеченные товары сохранятся файлом для CL-Works. В CL-Works: «Файл → Импорт → CSV», проверьте товары, затем «Обмен данными → Передать данные». У каждого товара постоянный номер PLU (колонка PLU), код в штрихкоде — «Код в ШК».",
            "CAS бул модели үчүн касса тармак аркылуу жазбайт: белгиленген товарлар CL-Works үчүн файл менен сакталат. CL-Works'то: «Файл → Импорт → CSV», товарларды текшерип, андан кийин «Обмен данными → Передать данные». Ар бир товардын туруктуу PLU номери бар (PLU тилкеси), штрих-коддогу код — «ШКдагы код».",
            "The till does not write to this CAS model over the network: the ticked goods are saved as a file for CL-Works. In CL-Works: “File → Import → CSV”, check the goods, then “Data exchange → Send data”. Every product has a fixed PLU number (PLU column), the code in the barcode is “Code in barcode”.",
            "Kasa bu CAS modeline ağ üzerinden yazmaz: işaretli ürünler CL-Works için dosya olarak kaydedilir. CL-Works'te: «Dosya → İçe aktar → CSV», ürünleri kontrol edin, sonra «Veri alışverişi → Verileri gönder». Her ürünün sabit bir PLU numarası var (PLU sütunu), barkoddaki kod «Barkoddaki kod».",
            "Kassa bu CAS modeliga tarmoq orqali yozmaydi: belgilangan tovarlar CL-Works uchun fayl sifatida saqlanadi. CL-Works da: «Fayl → Import → CSV», tovarlarni tekshiring, so‘ng «Ma’lumot almashuvi → Ma’lumotlarni uzatish». Har bir tovarning doimiy PLU raqami bor (PLU ustuni), shtrix-koddagi kod — «Shtrix-koddagi kod».");

    /// <summary>Пример этикетки CAS для первого отмеченного товара: формат на весах, префикс (отдел) — из «Настроек весов CAS», код — «Код в ШК», сверка — разбором кассы.</summary>
    private void UpdateCasBarcodeExample(ScalePluRowVm? row)
    {
        var s = CasScaleSettings.Current;
        var byWeight = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        var format = CasBarcodeFormat.Recommended(WeightBarcodeParser.Layout, byWeight);
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        const int grams = 392;
        var (amount, units) = CasBarcodeFormat.SampleAmount((decimal)(row?.Price ?? 100), grams, s.PricePoint); // 2026-11-14: расчёт вынесен из окна
        var sample = CasBarcodeFormat.BuildSample(format, s.BarcodePrefix, code, byWeight ? grams : units);
        var name = row?.Name ?? "";
        var head = Tr.T($"Штрих-код на этикетке CAS для «{name}» (0,392 кг): {sample ?? "—"} — формат на весах {format}, префикс (отдел) {s.BarcodePrefix:00}. ",
            $"«{name}» үчүн CAS этикеткасындагы штрих-код (0,392 кг): {sample ?? "—"} — таразадагы формат {format}, префикс (бөлүм) {s.BarcodePrefix:00}. ",
            $"CAS label barcode for “{name}” (0.392 kg): {sample ?? "—"} — scale format {format}, prefix (department) {s.BarcodePrefix:00}. ",
            $"“{name}” için CAS etiket barkodu (0,392 kg): {sample ?? "—"} — tartı biçimi {format}, önek (reyon) {s.BarcodePrefix:00}. ",
            $"«{name}» uchun CAS yorlig‘idagi shtrix-kod (0,392 kg): {sample ?? "—"} — tarozi formati {format}, prefiks (bo‘lim) {s.BarcodePrefix:00}. ");
        if (sample is null)
        {
            BarcodeExampleText.Text = head + "⚠ " + Tr.T("код или значение не помещаются в поля формата — весы напечатают этикетку без штрихкода.",
                "код же маани форматтын талааларына батпайт — тараза этикетканы штрих-коддусуз басат.",
                "the code or value does not fit the format fields — the scale prints the label without a barcode.",
                "kod veya değer biçim alanlarına sığmıyor — tartı etiketi barkodsuz basar.",
                "kod yoki qiymat format maydonlariga sig‘maydi — tarozi yorliqni shtrix-kodsiz chop etadi.");
            return;
        }
        var (ok, verdict) = NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.VerifyWithKassa(sample, code, byWeight, grams, amount);
        BarcodeExampleText.Text = head + (ok ? "✓ " : "⚠ ") + verdict;
    }

    // ------------------------------------------------------------------ сборка карточек

    /// <summary>Собирает карточки PLU: номер — закреплённый за товаром, код в штрихкоде — колонка «Код в ШК» (проверена CheckSendBatch: не чужой товар на кассе, помещается в поле «I» формата),
    /// отдел — префикс штрихкода из настроек, цена — в наименьших единицах весов (по «Знакам в цене»). Цена выше предела весов — товар не отправляется и называется; прочие ошибки останавливают отправку целиком.</summary>
    private (List<CasPlu> Records, List<string> Ids, Dictionary<string, ScaleSentPlu> Written, List<string> Problems, string PriceNote) BuildCasRecords(List<string> selectedIds)
    {
        var s = CasScaleSettings.Current;
        var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
        var rowsById = _allRows.ToDictionary(r => r.Id);
        var numbers = PinNumbersForSend(selectedIds);
        var maxCode = string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? 999_999L : 99_999L;
        var (codes, batchProblems) = CheckSendBatch(selectedIds, numbers, maxCode);
        var records = new List<CasPlu>();
        var ids = new List<string>();
        var written = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
        var problems = new List<string>();
        var priceSkipped = new List<string>();
        foreach (var (id, text) in batchProblems)
            problems.Add($"«{(rowsById.TryGetValue(id, out var r) ? r.Name : id)}»: {text}");
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var product) || batchProblems.ContainsKey(id) || !numbers.TryGetValue(id, out var plu))
                continue;
            var price = (decimal)LocalCartService.ParsePrice(product.PriceLine);
            var record = new CasPlu
            {
                Department = s.BarcodePrefix,
                PluNumber = plu,
                ItemCode = (int)Math.Clamp(codes[id], 0, 999_999),
                Name = product.Title,
                Weighed = product.IsWeighted,
                UnitPriceUnits = CasBarcodeFormat.ToUnits(price, s.PricePoint),
                BarcodeNumber = s.BarcodeNumber,
                LabelNumber = s.LabelNumber,
            };
            var problem = CasProtocol.Validate(record);
            if (problem == CasPluProblem.PriceDoesNotFit)
            {
                var priceText = Tr.T($"цена {price:0.##} не помещается в поле цены весов (до {CasProtocol.MaxPriceUnits} в наименьших единицах при {s.PricePoint} знаках) — уменьшите «Знаков в цене» в настройках CAS, если на весах их меньше",
                    $"баа {price:0.##} тараза баасынын талаасына батпайт (эң кичине бирдикте {CasProtocol.MaxPriceUnits}ге чейин, {s.PricePoint} белгиде) — таразада аз болсо, CAS жөндөөлөрүндөгү «Баадагы белгилерди» азайтыңыз",
                    $"the price {price:0.##} does not fit the scale price field (up to {CasProtocol.MaxPriceUnits} in the smallest units with {s.PricePoint} decimals) — reduce “Price decimals” in the CAS settings if the scale has fewer",
                    $"{price:0.##} fiyatı tartı fiyat alanına sığmıyor (en küçük birimde {CasProtocol.MaxPriceUnits}'e kadar, {s.PricePoint} hanede) — tartıda daha azsa CAS ayarlarında «Fiyat ondalığı»nı azaltın",
                    $"{price:0.##} narxi tarozi narx maydoniga sig‘maydi (eng kichik birlikda {CasProtocol.MaxPriceUnits} gacha, {s.PricePoint} xonada) — tarozida kam bo‘lsa, CAS sozlamalarida «Narxdagi kasrlar»ni kamaytiring");
                priceSkipped.Add($"«{product.Title}» (PLU {plu}): {priceText}");
                if (rowsById.TryGetValue(id, out var priceRow))
                    priceRow.SetStatus("⚠ " + priceText, RowState.Warning);
                continue;
            }
            if (problem != CasPluProblem.None)
            {
                var text = CasProblemText(problem);
                problems.Add($"«{product.Title}» (PLU {plu}): {text}");
                if (rowsById.TryGetValue(id, out var badRow))
                    badRow.SetStatus("⚠ " + text, RowState.Warning);
                continue;
            }
            records.Add(record);
            ids.Add(id);
            written[id] = new ScaleSentPlu { Plu = plu, Code = record.ItemCode.ToString(CultureInfo.InvariantCulture), Name = product.Title };
        }
        var priceNote = priceSkipped.Count == 0
            ? ""
            : Tr.T("Не загружены (цена выше предела весов): ", "Жүктөлгөн жок (баа таразанын чегинен жогору): ", "Not loaded (price above the scale limit): ", "Yüklenmedi (fiyat tartı sınırının üzerinde): ", "Yuklanmadi (narx tarozi chegarasidan yuqori): ")
              + string.Join(" · ", priceSkipped.Take(3)) + (priceSkipped.Count > 3 ? $" (+{priceSkipped.Count - 3})" : "");
        return (records, ids, written, problems, priceNote);
    }

    private static string CasProblemText(CasPluProblem problem) => problem switch
    {
        CasPluProblem.BadPluNumber => Tr.T($"номер PLU должен быть от 1 до {CasProtocol.MaxPluNumber}", $"PLU номери 1ден {CasProtocol.MaxPluNumber}гө чейин болушу керек", $"the PLU number must be 1 to {CasProtocol.MaxPluNumber}", $"PLU numarası 1 ile {CasProtocol.MaxPluNumber} arasında olmalı", $"PLU raqami 1 dan {CasProtocol.MaxPluNumber} gacha bo‘lishi kerak"),
        CasPluProblem.BadItemCode => Tr.T("код в штрихкоде — до 6 цифр", "штрих-коддогу код — 6 санга чейин", "the code in the barcode is up to 6 digits", "barkoddaki kod en fazla 6 hane", "shtrix-koddagi kod — 6 raqamgacha"),
        CasPluProblem.BadDepartment => Tr.T("отдел (префикс штрихкода) — от 1 до 99", "бөлүм (штрих-код префикси) — 1ден 99го чейин", "the department (barcode prefix) must be 1 to 99", "reyon (barkod öneki) 1 ile 99 arasında olmalı", "bo‘lim (shtrix-kod prefiksi) — 1 dan 99 gacha"),
        CasPluProblem.BadBarcodeNumber => Tr.T("номер формата штрихкода — от 0 до 99", "штрих-код форматынын номери — 0дөн 99го чейин", "the barcode format number must be 0 to 99", "barkod biçimi numarası 0 ile 99 arasında olmalı", "shtrix-kod formati raqami — 0 dan 99 gacha"),
        _ => problem.ToString(),
    };

    private static string CasErrorText(CasError error, int scaleError) => error switch
    {
        CasError.ConnectFailed => Tr.T("нет подключения к весам CAS (IP, кабель, порт, обычно 20304; на весах включена сеть — меню 1913)", "CAS таразасына туташуу жок (IP, кабель, порт, адатта 20304; таразада тармак күйгүзүлгөн — 1913 меню)", "no connection to the CAS scale (IP, cable, port, usually 20304; the network is on in the scale — menu 1913)", "CAS tartıya bağlantı yok (IP, kablo, port, genellikle 20304; tartıda ağ açık — menü 1913)", "CAS taroziga ulanish yo‘q (IP, kabel, port, odatda 20304; tarozida tarmoq yoqilgan — 1913 menyu)"),
        CasError.NoReply => Tr.T("весы не ответили за 3 с", "тараза 3 с ичинде жооп берген жок", "the scale did not answer within 3 s", "tartı 3 sn içinde yanıt vermedi", "tarozi 3 s ichida javob bermadi"),
        CasError.Rejected => scaleError switch
        {
            89 => Tr.T("память PLU весов заполнена (ошибка 89)", "таразанын PLU эстутуму толгон (89 ката)", "the scale PLU memory is full (error 89)", "tartının PLU belleği dolu (hata 89)", "tarozining PLU xotirasi to‘lgan (89 xato)"),
            97 => Tr.T("весы не приняли структуру карточки (ошибка 97)", "тараза карточка түзүмүн кабыл алган жок (97 ката)", "the scale did not accept the card structure (error 97)", "tartı kart yapısını kabul etmedi (hata 97)", "tarozi kartochka tuzilmasini qabul qilmadi (97 xato)"),
            84 => Tr.T("весы сообщили об ошибке кадра или контрольной суммы (ошибка 84)", "тараза кадр же контролдук сумма катасы жөнүндө кабарлады (84 ката)", "the scale reported a frame or checksum error (error 84)", "tartı çerçeve veya sağlama toplamı hatası bildirdi (hata 84)", "tarozi kadr yoki nazorat yig‘indisi xatosini bildirdi (84 xato)"),
            82 => Tr.T("номер вне допустимого для весов (ошибка 82)", "номер таразага уруксат берилгендин сыртында (82 ката)", "the number is outside the scale's allowed range (error 82)", "numara tartının izin verdiği aralığın dışında (hata 82)", "raqam tarozi ruxsat etgan chegaradan tashqarida (82 xato)"),
            _ => Tr.T($"весы отклонили запись (ошибка {scaleError}); возможно, это другая модель CAS с другим протоколом", $"тараза жазууну четке какты ({scaleError} ката); балким, бул башка протоколдуу CAS модели", $"the scale rejected the record (error {scaleError}); it may be another CAS model with another protocol", $"tartı kaydı reddetti (hata {scaleError}); başka protokollü bir CAS modeli olabilir", $"tarozi yozuvni rad etdi ({scaleError} xato); balki boshqa protokolli CAS modeli"),
        },
        CasError.ConnectionLost => Tr.T("весы разорвали соединение", "тараза туташууну үздү", "the scale closed the connection", "tartı bağlantıyı kesti", "tarozi ulanishni uzdi"),
        CasError.SendFailed => Tr.T("не удалось отправить данные", "маалыматтарды жөнөтүү мүмкүн болгон жок", "could not send the data", "veriler gönderilemedi", "ma’lumotlarni yuborib bo‘lmadi"),
        CasError.Cancelled => Tr.T("остановлено", "токтотулду", "stopped", "durduruldu", "to‘xtatildi"),
        _ => error.ToString(),
    };

    // ------------------------------------------------------------------ отправка по сети

    private async Task SendToCasAsync()
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        var s = CasScaleSettings.Current;
        var scale = CasScaleService.TryCreate(s.Ip, s.Port);
        if (scale is null)
        {
            StatusText.Text = Tr.T("Не задан IP весов CAS — «Настройки весов CAS» → «Быстрая настройка».",
                "CAS таразасынын IP'си коюлган эмес — «CAS таразасынын жөндөөлөрү» → «Тез жөндөө».",
                "The CAS scale IP is not set — “CAS scale settings” → “Quick setup”.",
                "CAS tartı IP'si girilmemiş — «CAS tartı ayarları» → «Hızlı kurulum».",
                "CAS tarozi IP'si kiritilmagan — «CAS tarozi sozlamalari» → «Tez sozlash».");
            return;
        }

        var (records, ids, written, problems, priceNote) = BuildCasRecords(selectedIds);
        if (problems.Count > 0)
        {
            // Частичную выгрузку не делаем: иначе на весах окажется «половина» списка.
            StatusText.Text = Tr.T("Не отправлено — исправьте: ", "Жөнөтүлгөн жок — оңдоңуз: ", "Not sent — fix: ", "Gönderilmedi — düzeltin: ", "Yuborilmadi — tuzating: ")
                              + string.Join(" · ", problems.Take(3)) + (problems.Count > 3 ? $" (+{problems.Count - 3})" : "");
            return;
        }
        if (records.Count == 0)
        {
            StatusText.Text = priceNote.Length > 0
                ? priceNote
                : Tr.T("Не удалось собрать данные для выгрузки — обновите каталог.", "Жүктөө үчүн маалыматтарды чогултуу мүмкүн болгон жок — каталогду жаңыртыңыз.", "Could not prepare the data for upload — refresh the catalog.", "Tartıya gönderilecek veriler hazırlanamadı — kataloğu güncelleyin.", "Yuklash uchun ma'lumotlarni to'plab bo'lmadi — katalogni yangilang.");
            return;
        }

        // 2026-11-14: на живых весах CAS не проверено — каждый раз спрашиваем (автоотправку для CAS не включаем, ScaleAutoSendPolicy).
        var confirmed = await PosConfirmDialog.ShowModalAsync(this,
            Tr.T("Отправка на весы CAS", "CAS таразасына жөнөтүү", "Sending to the CAS scale", "CAS tartıya gönderim", "CAS taroziga yuborish"),
            Tr.T($"Протокол CAS в кассе пока НЕ ПРОВЕРЕН на живых весах. Касса запишет {records.Count} товар(ов) карточками PLU на {scale.Host}:{scale.Port} (память весов не стирается, отделы и форматы не меняются). После отправки проверьте на весах название и цену и напечатайте пробную этикетку. Отправить?",
                $"Кассадагы CAS протоколу жандуу таразада АЗЫРЫНЧА ТЕКШЕРИЛГЕН ЭМЕС. Касса {scale.Host}:{scale.Port} дарегине {records.Count} товарды PLU карточкалары менен жазат (таразанын эстутуму өчүрүлбөйт, бөлүмдөр менен форматтар өзгөрбөйт). Жөнөткөндөн кийин таразадан аталышын, баасын текшерип, сыноо этикеткасын басыңыз. Жөнөтөбүзбү?",
                $"The CAS protocol in the till is NOT YET VERIFIED on live scales. The till will write {records.Count} product(s) as PLU cards to {scale.Host}:{scale.Port} (the scale memory is not erased, departments and formats are not changed). After sending, check the name and price on the scale and print a trial label. Send?",
                $"Kasadaki CAS protokolü canlı tartıda HENÜZ DOĞRULANMADI. Kasa {records.Count} ürünü PLU kartları olarak {scale.Host}:{scale.Port} adresine yazacak (tartı belleği silinmez, reyonlar ve biçimler değişmez). Gönderdikten sonra tartıda adı ve fiyatı kontrol edip deneme etiketi basın. Gönderilsin mi?",
                $"Kassadagi CAS protokoli jonli tarozida HALI TEKSHIRILMAGAN. Kassa {records.Count} ta tovarni PLU kartochkalari sifatida {scale.Host}:{scale.Port} manziliga yozadi (tarozi xotirasi o‘chirilmaydi, bo‘limlar va formatlar o‘zgarmaydi). Yuborgandan keyin tarozida nomi va narxini tekshirib, sinov yorlig‘ini chop eting. Yuborilsinmi?"),
            Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
        if (!confirmed)
        {
            StatusText.Text = Tr.T("Отправка отменена.", "Жөнөтүү жокко чыгарылды.", "Sending cancelled.", "Gönderim iptal edildi.", "Yuborish bekor qilindi.");
            return;
        }

        _tmCts = new CancellationTokenSource();
        RongtaCheckBanner.IsVisible = false;
        SendButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");
        StatusText.Text = Tr.T($"Подключение к весам {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} таразасына туташуу…", $"Connecting to the scale {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} tartısına bağlanılıyor…", $"{scale.Host}:{scale.Port} taroziga ulanilmoqda…");
        SetProgress(0, records.Count);
        var rowsById = _allRows.ToDictionary(r => r.Id);
        try
        {
            var progress = new Progress<int>(done =>
            {
                SetProgress(done, records.Count);
                StatusText.Text = Tr.T($"Отправка на весы: {done} из {records.Count}…", $"Таразага жөнөтүү: {records.Count} ичинен {done}…", $"Sending to the scale: {done} of {records.Count}…", $"Tartıya gönderiliyor: {done} / {records.Count}…", $"Taroziga yuborilmoqda: {records.Count} dan {done}…");
            });
            var result = await scale.SendPlusAsync(records, _tmCts.Token, progress).ConfigureAwait(true);
            PosLogger.Log($"CAS: выгрузка PLU {scale.Host}:{scale.Port}: всего {records.Count}, принято {result.Sent}, ошибка {result.Error} (весы: {result.ScaleErrorNumber}) {result.Detail}", "SCALES");

            for (var i = 0; i < ids.Count; i++)
            {
                if (!rowsById.TryGetValue(ids[i], out var row))
                    continue;
                if (i < result.Sent)
                    row.SetStatus(SentText(), RowState.Ok);
                else if (!result.Ok && i == result.Sent)
                    row.SetStatus("✗ " + CasErrorText(result.Error, result.ScaleErrorNumber), RowState.Error);
                else
                    row.SetStatus(NotSentText(), RowState.Warning);
            }

            var accepted = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
            for (var i = 0; i < result.Sent && i < ids.Count; i++)
                accepted[ids[i]] = written[ids[i]];
            var plan = ScalePluPlanner.PlanMoves(LabelScaleStore.Active.SentPlus, accepted);
            RememberSent(accepted, Array.Empty<int>());
            var movedNote = plan.PluMoves.Count == 0
                ? ""
                : " " + Tr.T($"Сменился PLU у товаров: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Старые карточки остались на весах — удалять их касса не умеет.",
                    $"PLU өзгөргөн товарлар: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Эски карточкалар таразада калды — аларды касса өчүрө албайт.",
                    $"PLU changed for: {string.Join(", ", plan.PluMoves.Select(m => $"“{rowsById.GetValueOrDefault(m.ProductId)?.Name}” {m.From} → {m.To}"))}. The old cards stay on the scale — the till cannot delete them.",
                    $"PLU'su değişen ürünler: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Eski kartlar tartıda kaldı — kasa onları silemez.",
                    $"PLU o‘zgargan tovarlar: {string.Join(", ", plan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Eski kartochkalar tarozida qoldi — kassa ularni o‘chira olmaydi.");

            if (result.Ok)
            {
                StatusText.Text = Tr.T($"Принято весами: {result.Sent} из {records.Count}. На живых весах CAS отправка пока не проверялась — посмотрите товар на весах (название, цена) и напечатайте пробную этикетку, затем отсканируйте её на кассе. Журнал обмена: {CasScaleService.ExchangeLogPath}.",
                    $"Тараза кабыл алды: {records.Count} ичинен {result.Sent}. Жандуу CAS таразасында жөнөтүү азырынча текшерилген эмес — товарды таразадан караңыз (аталышы, баасы), сыноо этикеткасын басып, кассада сканерлеңиз. Алмашуу журналы: {CasScaleService.ExchangeLogPath}.",
                    $"Accepted by the scale: {result.Sent} of {records.Count}. Sending to a live CAS scale has not been verified yet — look at the item on the scale (name, price), print a trial label and scan it at the till. Exchange log: {CasScaleService.ExchangeLogPath}.",
                    $"Tartı kabul etti: {result.Sent} / {records.Count}. Canlı CAS tartıya gönderim henüz doğrulanmadı — ürüne tartıda bakın (ad, fiyat), deneme etiketi basıp kasada okutun. İletişim günlüğü: {CasScaleService.ExchangeLogPath}.",
                    $"Tarozi qabul qildi: {records.Count} dan {result.Sent}. Jonli CAS taroziga yuborish hali tekshirilmagan — tovarni tarozida ko‘ring (nomi, narxi), sinov yorlig‘ini chop etib, kassada skanerlang. Almashuv jurnali: {CasScaleService.ExchangeLogPath}.")
                    + movedNote + _codeReplaceNote;
            }
            else
            {
                StatusText.Text = Tr.T($"Отправка не завершена: {CasErrorText(result.Error, result.ScaleErrorNumber)}. Принято весами: {result.Sent} из {records.Count}",
                    $"Жөнөтүү аягына чыккан жок: {CasErrorText(result.Error, result.ScaleErrorNumber)}. Тараза кабыл алды: {records.Count} ичинен {result.Sent}",
                    $"Sending did not finish: {CasErrorText(result.Error, result.ScaleErrorNumber)}. Accepted by the scale: {result.Sent} of {records.Count}",
                    $"Gönderim tamamlanmadı: {CasErrorText(result.Error, result.ScaleErrorNumber)}. Tartının kabul ettiği: {result.Sent} / {records.Count}",
                    $"Yuborish tugamadi: {CasErrorText(result.Error, result.ScaleErrorNumber)}. Tarozi qabul qildi: {records.Count} dan {result.Sent}")
                    + Tr.T(". Журнал обмена: ", ". Алмашуу журналы: ", ". Exchange log: ", ". İletişim günlüğü: ", ". Almashuv jurnali: ") + CasScaleService.ExchangeLogPath + movedNote;
            }
            if (priceNote.Length > 0)
            {
                StatusText.Text += " ⚠ " + priceNote;
                PosLogger.Log("CAS: " + priceNote, "SCALES");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CAS: выгрузка не удалась: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            _tmCts?.Dispose();
            _tmCts = null;
            HideProgress();
            SendButton.Content = _sendButtonDefaultText;
        }
    }

    // ------------------------------------------------------------------ файл для CL-Works

    /// <summary>Файл для CL-Works («Файл → Импорт → CSV»): отмеченные товары (или все весовые, если ничего не отмечено). Кодировка и разделитель цены — из настроек CAS.</summary>
    private async Task SaveCasFileAsync()
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
            selectedIds = _allRows.Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Нечего выгружать: весовых товаров нет.", "Чыгарууга эч нерсе жок: салмактуу товарлар жок.", "Nothing to export: there are no weighed products.", "Dışa aktarılacak bir şey yok: tartılı ürün yok.", "Eksport qilish uchun hech narsa yo'q: vaznli mahsulotlar yo'q.");
            return;
        }

        var s = CasScaleSettings.Current;
        var (records, _, _, problems, priceNote) = BuildCasRecords(selectedIds);
        if (records.Count == 0)
        {
            StatusText.Text = problems.Count > 0
                ? Tr.T("Файл не сохранён — исправьте: ", "Файл сакталган жок — оңдоңуз: ", "File not saved — fix: ", "Dosya kaydedilmedi — düzeltin: ", "Fayl saqlanmadi — tuzating: ") + string.Join(" · ", problems.Take(3))
                : priceNote.Length > 0 ? priceNote
                : Tr.T("Не удалось собрать данные для файла — обновите каталог.", "Файл үчүн маалыматтарды чогултуу мүмкүн болгон жок — каталогду жаңыртыңыз.", "Could not prepare the data for the file — refresh the catalog.", "Dosya için veriler hazırlanamadı — kataloğu güncelleyin.", "Fayl uchun ma'lumotlarni to'plab bo'lmadi — katalogni yangilang.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CasSaveFileText(),
            SuggestedFileName = $"cas-cl-works-{DateTime.Now:yyyy-MM-dd}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(CasCsvWriter.Build(records, s.PricePoint, s.DecimalSeparatorChar, s.CsvUtf8));
        }
        catch (Exception ex)
        {
            StatusText.Text = Tr.T($"Не удалось сохранить файл: {ex.Message}", $"Файлды сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the file: {ex.Message}", $"Dosya kaydedilemedi: {ex.Message}", $"Faylni saqlab bo‘lmadi: {ex.Message}");
            return;
        }
        PosLogger.Log($"CAS: файл для CL-Works: {records.Count} товаров, {(s.CsvUtf8 ? "UTF-8" : "Windows-1251")}, разделитель цены «{s.DecimalSeparatorChar}»", "SCALES");
        StatusText.Text = Tr.T($"Сохранено строк: {records.Count}. В CL-Works: «Файл → Импорт → CSV», проверьте товары, затем «Обмен данными → Передать данные». Формат файла на живом CL-Works не проверялся — первый раз загрузите один-два товара.",
                $"Сакталган саптар: {records.Count}. CL-Works'то: «Файл → Импорт → CSV», товарларды текшерип, андан кийин «Обмен данными → Передать данные». Файл форматы жандуу CL-Works'то текшерилген эмес — биринчи жолу бир-эки товар жүктөңүз.",
                $"Rows saved: {records.Count}. In CL-Works: “File → Import → CSV”, check the goods, then “Data exchange → Send data”. The file format has not been verified on a live CL-Works — load one or two items the first time.",
                $"Kaydedilen satır: {records.Count}. CL-Works'te: «Dosya → İçe aktar → CSV», ürünleri kontrol edin, sonra «Veri alışverişi → Verileri gönder». Dosya biçimi canlı CL-Works'te doğrulanmadı — ilk seferde bir-iki ürün yükleyin.",
                $"Saqlangan qatorlar: {records.Count}. CL-Works da: «Fayl → Import → CSV», tovarlarni tekshiring, so‘ng «Ma’lumot almashuvi → Ma’lumotlarni uzatish». Fayl formati jonli CL-Works da tekshirilmagan — birinchi marta bir-ikki tovar yuklang.")
            + (problems.Count > 0 ? Tr.T($" Пропущено с ошибками: {problems.Count}.", $" Ката менен өткөрүлдү: {problems.Count}.", $" Skipped with errors: {problems.Count}.", $" Hatayla atlandı: {problems.Count}.", $" Xato bilan o‘tkazib yuborildi: {problems.Count}.") : "")
            + (priceNote.Length > 0 ? " ⚠ " + priceNote : "");
    }
}
