using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Данные «карго» накладной (на весь документ): перевозчик, город / адрес получателя, трек-номер, число мешков.</summary>
public sealed record CargoInfo(bool Enabled, int Bags, string Carrier, string City, string Track)
{
    public static readonly CargoInfo Off = new(false, 0, "", "", "");
}

/// <summary>
/// 2026-11-21 («Базар», опт, модуль <c>invoice_cargo</c>, ТЗ Контейнер-базар 3.6 / 3.11 п. 5): «Накладная A4 + карго». Накладная опту печатается с колонками «Мест» и «Вес брутто, кг» по каждой строке
/// и блоком «Карго» под итогами: всего мест, брутто, мешков, перевозчик, город / адрес получателя, трек-номер. Данные лежат в самом документе клиента (ClientDoc.Extra["cargo"] и поля строк
/// <c>cg_places</c> / <c>cg_gross</c>) — формат документа и его синхронизация с телефоном и другими ПК не меняются (незнакомые поля сохраняются как есть). Включает галочка «Карго» в выписке накладной.
/// Номер накладной и серверная нумерация — отдельно (ответ сервера по накладным ещё нужен, см. отчёт). Чистая логика — проверяется тестами.
/// </summary>
public static class BazaarCargo
{
    public const string DocKey = "cargo";
    public const string LinePlaces = "cg_places";
    public const string LineGross = "cg_gross";

    /// <summary>Накладная с включённым «карго» (только накладная; у КП и счёта колонок карго нет).</summary>
    public static bool IsOn(ClientDoc d) => d.Kind == ClientDocKind.Waybill && Read(d).Enabled;

    public static CargoInfo Read(ClientDoc d)
    {
        if (d.Extra is null || !d.Extra.TryGetValue(DocKey, out var el) || el.ValueKind != JsonValueKind.Object)
            return CargoInfo.Off;
        string S(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
        var enabled = el.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
        var bags = el.TryGetProperty("bags", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n) && n >= 0 ? n : 0;
        return new CargoInfo(enabled, bags, S("carrier"), S("city"), S("track"));
    }

    /// <summary>Записать «карго» в документ. Выключено и пусто — поле убирается (документ как раньше).</summary>
    public static void Write(ClientDoc d, CargoInfo info)
    {
        d.Extra ??= new Dictionary<string, JsonElement>();
        var empty = !info.Enabled && info.Bags == 0 && info.Carrier.Length == 0 && info.City.Length == 0 && info.Track.Length == 0;
        if (empty)
        {
            d.Extra.Remove(DocKey);
            return;
        }
        var obj = new JsonObject
        {
            ["on"] = info.Enabled,
            ["bags"] = Math.Max(0, info.Bags),
            ["carrier"] = info.Carrier.Trim(),
            ["city"] = info.City.Trim(),
            ["track"] = info.Track.Trim(),
        };
        d.Extra[DocKey] = JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }

    /// <summary>Мест у строки (0 — не задано).</summary>
    public static int Places(ClientDocLine line) =>
        line.Extra is not null && line.Extra.TryGetValue(LinePlaces, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : 0;

    /// <summary>Вес брутто строки, кг (0 — не задан).</summary>
    public static double Gross(ClientDocLine line) =>
        line.Extra is not null && line.Extra.TryGetValue(LineGross, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var g) && g > 0 ? g : 0;

    /// <summary>Поставить места и вес брутто строки (0 — убрать поле).</summary>
    public static void SetLine(ClientDocLine line, int places, double gross)
    {
        line.Extra ??= new Dictionary<string, JsonElement>();
        if (places > 0)
            line.Extra[LinePlaces] = JsonDocument.Parse(places.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
        else
            line.Extra.Remove(LinePlaces);
        if (gross > 0)
            line.Extra[LineGross] = JsonDocument.Parse(Math.Round(gross, 3).ToString("0.###", CultureInfo.InvariantCulture)).RootElement.Clone();
        else
            line.Extra.Remove(LineGross);
        if (line.Extra.Count == 0)
            line.Extra = null;
    }

    public static int TotalPlaces(ClientDoc d) => d.Lines.Sum(Places);

    public static double TotalGross(ClientDoc d) => Math.Round(d.Lines.Sum(Gross), 3);

    /// <summary>Разбор ввода мест: целое от 0 (пусто — 0). Не число — null.</summary>
    public static int? ParsePlaces(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 99999 ? n : null;

    /// <summary>Разбор веса брутто, кг: запятая / точка (пусто — 0). Не число или отрицательное — null.</summary>
    public static double? ParseGross(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g >= 0 && g < 1_000_000 ? g : null;
    }
}
