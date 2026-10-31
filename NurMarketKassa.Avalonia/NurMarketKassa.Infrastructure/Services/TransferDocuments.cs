using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

// 2026-10-31, просьба владельца (карточка перемещения, вкладка «Документы»): «автоматическое создание документов с примерами как в
// накладной КР». Касса сама формирует документ из данных перемещения — накладную на внутреннее перемещение, акт приёма-передачи и
// транспортный документ. Здесь — данные документа и их сборка; рисование — TransferDocumentRenderer (PDF, печать), Word и Excel —
// TransferDocumentOffice. Деньги, остатки и сам процесс перемещения не затрагиваются: документ только читает данные перемещения.

/// <summary>Вид создаваемого документа перемещения. Метки (<see cref="TransferDocuments.KindTag"/>) совпадают с видами вложений
/// карточки: invoice / act / transport. «Фото повреждений» создать нельзя — только прикрепить.</summary>
public enum TransferDocKind { Waybill, Act, Transport }

/// <summary>Строка состава документа. Цена — закупочная (0 — не известна или не показывается).</summary>
public sealed record TransferDocLine(string Name, string Article, string Barcode, string Unit, double Qty, double Price)
{
    public double Sum => Math.Round(Qty * Price, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Всё, что нужно для рисования документа. Хранится рядом с файлом (снимок .json): печать, Word и Excel созданного документа
/// берутся из снимка, а не из текущего состояния перемещения — документ не меняется задним числом. Язык (ru/ky/en/tr/uz) тоже в снимке.</summary>
public sealed class TransferDocData
{
    public TransferDocKind Kind { get; set; }
    public string Number { get; set; } = "";
    /// <summary>Дата документа (день отправки/создания перемещения).</summary>
    public DateTime Date { get; set; }
    /// <summary>Когда документ сформирован (с поясом «UTC+06» в подвале).</summary>
    public DateTime CreatedAt { get; set; }
    public string TransferNumber { get; set; } = "";
    public string Lang { get; set; } = "ru";
    public string OrgName { get; set; } = "";
    public string OrgInn { get; set; } = "";
    public string OrgAddress { get; set; } = "";
    public string OrgPhone { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Responsible { get; set; } = "";
    public string Carrier { get; set; } = "";
    public string Tracking { get; set; } = "";
    public double Weight { get; set; }
    public string Note { get; set; } = "";
    public string IssuedBy { get; set; } = "";
    /// <summary>Показывать ли закупочные цены и суммы (нужно право на закупочные цены — решает окно).</summary>
    public bool ShowPrices { get; set; }
    public List<TransferDocLine> Lines { get; set; } = new();

    public double TotalQty => Lines.Sum(l => l.Qty);
    public double TotalSum => Math.Round(Lines.Sum(l => l.Sum), 2, MidpointRounding.AwayFromZero);
}

public static class TransferDocuments
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string KindTag(TransferDocKind kind) => kind switch { TransferDocKind.Act => "act", TransferDocKind.Transport => "transport", _ => "invoice" };

    public static TransferDocKind? KindFromTag(string? tag) => tag switch
    {
        "invoice" => TransferDocKind.Waybill,
        "act" => TransferDocKind.Act,
        "transport" => TransferDocKind.Transport,
        _ => null,
    };

    /// <summary>Код языка документа по языку интерфейса.</summary>
    public static string CurrentLang() => UserPreferences.Instance.Language switch
    {
        AppLanguage.Kyrgyz => "ky",
        AppLanguage.English => "en",
        AppLanguage.Turkish => "tr",
        AppLanguage.Uzbek => "uz",
        _ => "ru",
    };

    /// <summary>Название вида на языке документа (ru/ky/en/tr/uz) — заголовок документа.</summary>
    public static string Title(TransferDocKind kind, string lang)
    {
        string T(string ru, string ky, string en, string tr, string uz) => Pick(lang, ru, ky, en, tr, uz);
        return kind switch
        {
            TransferDocKind.Act => T("Акт приёма-передачи товара", "Товарды кабыл алуу-өткөрүп берүү акты", "Goods handover certificate", "Mal teslim tutanağı", "Tovarni qabul qilish-topshirish dalolatnomasi"),
            TransferDocKind.Transport => T("Транспортный документ", "Транспорттук документ", "Transport document", "Taşıma belgesi", "Transport hujjati"),
            _ => T("Накладная на внутреннее перемещение", "Ички жылдыруу боюнча накладной", "Internal stock transfer waybill", "İç stok transfer irsaliyesi", "Ichki ko'chirish yuk xati"),
        };
    }

    /// <summary>Короткое название вида для списка («Накладная», «Акт приёма-передачи», «Транспортный документ»).</summary>
    public static string ShortName(TransferDocKind kind, string lang) => kind switch
    {
        TransferDocKind.Act => Pick(lang, "Акт приёма-передачи", "Кабыл алуу-өткөрүп берүү акты", "Handover certificate", "Teslim tutanağı", "Qabul-topshirish akti"),
        TransferDocKind.Transport => Pick(lang, "Транспортный документ", "Транспорттук документ", "Transport document", "Taşıma belgesi", "Transport hujjati"),
        _ => Pick(lang, "Накладная", "Накладная", "Waybill", "İrsaliye", "Yuk xati"),
    };

    internal static string Pick(string lang, string ru, string ky, string en, string tr, string uz) => lang switch
    {
        "ky" => ky,
        "en" => en,
        "tr" => tr,
        "uz" => uz,
        _ => ru,
    };

    private static string Prefix(TransferDocKind kind) => kind switch { TransferDocKind.Act => "АПП", TransferDocKind.Transport => "ТД", _ => "ПМ" };

    private static readonly Regex ServerIdRx = new(@"\s*\[[0-9a-fA-F-]{8,}\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TransferNumberRx = new(@"^\s*(\d{4})-(\d{1,9})\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Номер документа по нумерации проекта (DocumentNumbering): «ПМ-2026-000001» — префикс вида, год и порядковый номер
    /// перемещения («2026-001» → 1), ширина — из настроек нумерации. Один номер на перемещение и вид: повторное создание того же вида
    /// не плодит новых номеров. Акт — «АПП-…», транспортный документ — «ТД-…».</summary>
    public static string NumberFor(StockTransferService.Transfer transfer, TransferDocKind kind)
    {
        var year = transfer.CreatedAt.Year;
        var seq = 1;
        if (TransferNumberRx.Match(transfer.Number) is { Success: true } m)
        {
            year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            seq = Math.Max(1, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        else if (Regex.Match(transfer.Number ?? "", @"(\d{1,9})\s*$") is { Success: true } tail)
            seq = Math.Max(1, int.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture));

        DocNumberingSettings settings;
        try
        {
            settings = DocumentNumbering.Settings;
        }
        catch (Exception)
        {
            settings = new DocNumberingSettings();
        }
        // Серия «приход» здесь только носитель формата; префикс вида задан явно и от настроек серий не зависит.
        return DocumentNumberingCore.Format(settings, new DocNumber(DocSeries.Receipt, year, seq, false, Prefix(kind)));
    }

    /// <summary>Собрать документ из данных перемещения. <paramref name="priceOf"/> — закупочная цена строки (null — цены не показываем:
    /// у роли нет права на закупочные цены). Нет данных реквизитов организации — поля остаются пустыми и печатаются как «____».</summary>
    public static TransferDocData Build(TransferDocKind kind, StockTransferService.Transfer transfer,
        IReadOnlyList<StockTransferService.TransferItem> items, Func<StockTransferService.TransferItem, double>? priceOf, string? issuedBy, DateTime? now = null)
    {
        var profile = ClientDocsStore.Profile;
        var company = CompanyInfoService.LastCompany;
        var prefs = UserPreferences.Instance;
        string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        var showPrices = priceOf is not null;
        var data = new TransferDocData
        {
            Kind = kind,
            Number = NumberFor(transfer, kind),
            Date = (now ?? DateTime.Now).Date, // дата документа — день его создания (при «Отправить» — день отправки)
            CreatedAt = now ?? DateTime.Now,
            TransferNumber = transfer.Number,
            Lang = CurrentLang(),
            OrgName = First(profile.Name, company?.Name, prefs.StoreName),
            OrgInn = First(profile.Inn, company?.Inn, prefs.StoreInn),
            OrgAddress = First(profile.Address, company?.Address, prefs.StoreAddress),
            OrgPhone = First(profile.Phone),
            From = First(transfer.FromPlaceName),
            To = First(transfer.ToPlaceName),
            Responsible = First(transfer.Responsible),
            Carrier = First(transfer.Carrier),
            Tracking = First(transfer.TrackingNumber),
            Weight = transfer.TotalWeight > 0 ? transfer.TotalWeight : items.Sum(i => i.Weight),
            Note = ServerIdRx.Replace(First(transfer.Note), "").Trim(), // «NurCRM №77 [id]» — внутренний id сервера в документ не нужен
            IssuedBy = First(issuedBy),
            ShowPrices = showPrices,
        };
        foreach (var item in items)
            data.Lines.Add(new TransferDocLine(item.ProductName, item.Article ?? "", item.Barcode ?? "", string.IsNullOrWhiteSpace(item.Unit) ? Pick(data.Lang, "шт", "даана", "pcs", "adet", "dona") : item.Unit!.Trim(),
                item.Quantity, showPrices ? Math.Max(0, priceOf!(item)) : 0));
        return data;
    }

    /// <summary>Имя файла: «Накладная на перемещение ПМ-2026-000001.pdf».</summary>
    public static string FileName(TransferDocData data, string extension) =>
        ExportFileName.Build(Title(data.Kind, data.Lang), data.Number, null, extension);

    /// <summary>Пояс документа в подвале: «31.10.2026 08:04 (UTC+06)». Пояс — компьютера кассы (Бишкек, Алматы — +06).</summary>
    public static string Stamp(DateTime when)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(when);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var zone = "UTC" + sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture) + (abs.Minutes != 0 ? ":" + abs.Minutes.ToString("00", CultureInfo.InvariantCulture) : "");
        return when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " (" + zone + ")";
    }

    public static string SnapshotPath(string documentPath) => documentPath + ".json";

    public static void SaveSnapshot(TransferDocData data, string documentPath) =>
        File.WriteAllText(SnapshotPath(documentPath), JsonSerializer.Serialize(data, Json), new System.Text.UTF8Encoding(false));

    /// <summary>Снимок созданного документа; null — файл прикреплён вручную (снимка нет) или повреждён.</summary>
    public static TransferDocData? LoadSnapshot(string documentPath)
    {
        try
        {
            var path = SnapshotPath(documentPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<TransferDocData>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ перемещения: снимок не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Создать документ: PDF + снимок во временной папке, затем прикрепить к перемещению (тем же способом, что «Прикрепить»:
    /// копия в папку данных кассы + запись вида и даты в список). Возвращает путь прикреплённого PDF. Пишет в журнал кассы.</summary>
    public static string CreateAndAttach(string transferId, TransferDocData data)
    {
        var temp = Path.Combine(Path.GetTempPath(), "nurmarket-transfer-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, FileName(data, "pdf"));
            TransferDocumentRenderer.SavePdf(data, file);
            var target = StockTransferService.Instance.AttachFile(transferId, file, KindTag(data.Kind));
            SaveSnapshot(data, target);
            PosLogger.Log($"Перемещение {data.TransferNumber}: создан документ «{Title(data.Kind, "ru")}» {data.Number} ({data.Lines.Count} поз.).", "INFO");
            return target;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (Exception)
            {
                // временная папка — не страшно, если осталась
            }
        }
    }
}
