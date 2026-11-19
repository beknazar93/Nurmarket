using System.Text.Json;
using NurMarketKassa.Core.Application;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-11-13: настройки весов CAS одной записью (IP, порт, модель, префикс, знаки цены, форматы весов, параметры файла для CL-Works).
/// Хранятся строкой JSON в <see cref="UserPreferences.CasScaleConfig"/> (по образцу <see cref="ShtrikhPcSettings"/>), на сервер не уходят.
/// Модель определяет путь: CL5000J и CL3000 — по сети (<see cref="CasProtocol"/>, НЕ проверено на весах), CL5500/CL5200/CL7200/LP-1.6 — только файл для CL-Works.
/// </summary>
public sealed class CasScaleSettings
{
    public const string ModelCl5000J = "cl5000j";
    public const string ModelCl3000 = "cl3000";
    public const string ModelCl5500 = "cl5500";
    public const string ModelCl5200 = "cl5200";
    public const string ModelCl7200 = "cl7200";
    public const string ModelLp16 = "lp16";

    public static readonly string[] Models = { ModelCl5000J, ModelCl3000, ModelCl5500, ModelCl5200, ModelCl7200, ModelLp16 };

    /// <summary>Модели, которым касса пишет товары по сети. Остальным — только файл CL-Works.</summary>
    public static bool IsNetworkModel(string? model) => model is ModelCl5000J or ModelCl3000;

    public static string ModelTitle(string model) => model switch
    {
        ModelCl3000 => "CAS CL3000",
        ModelCl5500 => "CAS CL5500",
        ModelCl5200 => "CAS CL5200",
        ModelCl7200 => "CAS CL7200",
        ModelLp16 => "CAS LP-1.6",
        _ => "CAS CL5000J",
    };

    public string Ip { get; set; } = "";
    public int Port { get; set; } = CasProtocol.DefaultPort;
    public string Model { get; set; } = ModelCl5000J;

    /// <summary>Префикс штрихкода 20…29 — уходит на весы номером отдела (формат штрихкода «DD…»). Для кассы: 20 — вес (режим «авто»), 25 — сумма.</summary>
    public int BarcodePrefix { get; set; } = 20;

    /// <summary>Знаков после запятой в цене на весах (весы показывают их сами; читается командой «Проверить связь»). По умолчанию 2 — как в CL-Works.</summary>
    public int PricePoint { get; set; } = 2;

    /// <summary>Номер формата штрихкода в таблице весов (0 — как настроено на весах, глобальный формат).</summary>
    public int BarcodeNumber { get; set; }

    /// <summary>Номер формата этикетки на весах (0 — формат по умолчанию).</summary>
    public int LabelNumber { get; set; }

    /// <summary>Файл для CL-Works: кодировка UTF-8 вместо Windows-1251.</summary>
    public bool CsvUtf8 { get; set; }

    /// <summary>Файл для CL-Works: десятичный разделитель цены («.» или «,»).</summary>
    public string CsvDecimalSeparator { get; set; } = ".";

    public bool IsNetwork => IsNetworkModel(Model);

    public char DecimalSeparatorChar => CsvDecimalSeparator == "," ? ',' : '.';

    public CasScaleSettings Normalize()
    {
        Ip = (Ip ?? "").Trim();
        Port = Port is > 0 and <= 65535 ? Port : CasProtocol.DefaultPort;
        if (!Models.Contains(Model))
            Model = ModelCl5000J;
        BarcodePrefix = Math.Clamp(BarcodePrefix, 20, 29);
        PricePoint = Math.Clamp(PricePoint, 0, 3);
        BarcodeNumber = Math.Clamp(BarcodeNumber, 0, 99);
        LabelNumber = Math.Clamp(LabelNumber, 0, 999);
        CsvDecimalSeparator = CsvDecimalSeparator == "," ? "," : ".";
        return this;
    }

    // ------------------------------------------------------------------ хранение

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static CasScaleSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CasScaleSettings();
        try
        {
            return (JsonSerializer.Deserialize<CasScaleSettings>(json, Json) ?? new CasScaleSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new CasScaleSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalize(), Json);

    /// <summary>Текущие настройки этой кассы.</summary>
    public static CasScaleSettings Current => Parse(UserPreferences.Instance.CasScaleConfig);

    /// <summary>Записывает настройки в память (на диск — <c>UserPreferences.Instance.SaveToDisk()</c> вызывающим).</summary>
    public static void Save(CasScaleSettings settings) => UserPreferences.Instance.CasScaleConfig = settings.ToJson();
}
