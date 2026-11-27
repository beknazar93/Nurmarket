using System.Text.Json;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Куда прижимать элемент (кнопки выравнивания в редакторе ценников).</summary>
public enum LabelAlignTo { Left, CenterH, Right, Top, MiddleV, Bottom }

/// <summary>2026-11-21 («Редактор ценников»): расчёты холста без интерфейса — привязка к сетке, границы, выравнивание, сдвиг стрелками,
/// список элементов шаблона, размеры. Отдельно от окна, чтобы это проверяли юнит-тесты.</summary>
public static class LabelDesignerMath
{
    /// <summary>Размеры этикеток в редакторе (мм). Лист A4 с сеткой — режим «Печать на A4» вкладки.</summary>
    public static readonly (double W, double H)[] Sizes = { (30, 20), (40, 20), (40, 30), (58, 30), (58, 40), (100, 70) };

    public const double MinSizeMm = 15;
    public const double MaxSizeMm = 150;
    public const double MinElementMm = 0.3;

    /// <summary>Шаги сетки в редакторе, мм (0 — привязки нет).</summary>
    public static readonly double[] GridSteps = { 0, 0.5, 1, 2 };

    public static double Snap(double value, double step) => step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>Элемент целиком внутри этикетки; ширина и высота не меньше минимума.</summary>
    public static void ClampInside(LabelElementLayout e, LabelTemplate t)
    {
        e.WidthMm = Math.Clamp(e.WidthMm, MinElementMm, Math.Max(t.WidthMm, MinElementMm));
        e.HeightMm = Math.Clamp(e.HeightMm, MinElementMm, Math.Max(t.HeightMm, MinElementMm));
        e.XMm = Math.Clamp(e.XMm, 0, Math.Max(0, t.WidthMm - e.WidthMm));
        e.YMm = Math.Clamp(e.YMm, 0, Math.Max(0, t.HeightMm - e.HeightMm));
    }

    public static void Align(LabelElementLayout e, LabelTemplate t, LabelAlignTo to)
    {
        switch (to)
        {
            case LabelAlignTo.Left: e.XMm = 0; break;
            case LabelAlignTo.CenterH: e.XMm = (t.WidthMm - e.WidthMm) / 2; break;
            case LabelAlignTo.Right: e.XMm = t.WidthMm - e.WidthMm; break;
            case LabelAlignTo.Top: e.YMm = 0; break;
            case LabelAlignTo.MiddleV: e.YMm = (t.HeightMm - e.HeightMm) / 2; break;
            case LabelAlignTo.Bottom: e.YMm = t.HeightMm - e.HeightMm; break;
        }
        ClampInside(e, t);
        e.XMm = Math.Round(e.XMm, 2);
        e.YMm = Math.Round(e.YMm, 2);
    }

    /// <summary>Сдвиг стрелками на dx, dy мм (с привязкой к сетке, если шаг задан).</summary>
    public static void Nudge(LabelElementLayout e, LabelTemplate t, double dx, double dy, double gridStep)
    {
        e.XMm = Snap(e.XMm + dx, gridStep);
        e.YMm = Snap(e.YMm + dy, gridStep);
        ClampInside(e, t);
        e.XMm = Math.Round(e.XMm, 2);
        e.YMm = Math.Round(e.YMm, 2);
    }

    /// <summary>Перемещение мышью: новая позиция от начальной + смещение, с привязкой и границами.</summary>
    public static void Move(LabelElementLayout e, LabelTemplate t, double startX, double startY, double dxMm, double dyMm, double gridStep)
    {
        e.XMm = Snap(startX + dxMm, gridStep);
        e.YMm = Snap(startY + dyMm, gridStep);
        ClampInside(e, t);
        e.XMm = Math.Round(e.XMm, 2);
        e.YMm = Math.Round(e.YMm, 2);
    }

    /// <summary>Изменение размера за правый нижний угол.</summary>
    public static void Resize(LabelElementLayout e, LabelTemplate t, double startW, double startH, double dxMm, double dyMm, double gridStep)
    {
        e.WidthMm = Math.Max(Snap(startW + dxMm, gridStep), MinElementMm);
        e.HeightMm = Math.Max(Snap(startH + dyMm, gridStep), MinElementMm);
        ClampInside(e, t);
        e.WidthMm = Math.Round(e.WidthMm, 2);
        e.HeightMm = Math.Round(e.HeightMm, 2);
    }

    /// <summary>Допустимый размер этикетки: 15–150 × 15–150 мм.</summary>
    public static double ClampSize(double mm) => Math.Clamp(mm, MinSizeMm, MaxSizeMm);

    /// <summary>Встроенные элементы шаблона по ключам (barcode, name, price, sku, unit, store, variant).</summary>
    public static IReadOnlyList<(string Key, LabelElementLayout Layout)> BuiltIns(LabelTemplate t) => new (string, LabelElementLayout)[]
    {
        ("barcode", t.Barcode), ("name", t.ProductName), ("price", t.Price), ("sku", t.Sku), ("unit", t.Unit), ("store", t.StoreName),
        ("variant", t.Variant ??= new LabelElementLayout { XMm = 2, YMm = 0, WidthMm = 36, HeightMm = 4, Enabled = false }),
    };

    /// <summary>Все видимые на этикетке элементы (встроенные включённые и свои включённые) — для проверки наложений.</summary>
    public static IEnumerable<LabelElementLayout> Visible(LabelTemplate t) =>
        BuiltIns(t).Select(b => b.Layout).Concat(t.Extras ?? new()).Where(e => e.Enabled);

    /// <summary>2026-11-27: заходит ли элемент на полосу рамки (отступ + толщина) — рамка заденет текст или штрихкод. Без рамки — false.</summary>
    public static bool TouchesFrame(LabelElementLayout e, LabelTemplate t)
    {
        if (!t.Frame)
            return false;
        var spec = LabelFrameSpec.From(t);
        var band = spec.Inset + spec.Thickness;
        return e.XMm < band - 0.01 || e.YMm < band - 0.01 || e.XMm + e.WidthMm > t.WidthMm - band + 0.01 || e.YMm + e.HeightMm > t.HeightMm - band + 0.01;
    }

    /// <summary>Заходят ли два элемента друг на друга по площади больше 0,5 мм² (подсказка редактора, не запрет).</summary>
    public static bool Overlaps(LabelElementLayout a, LabelElementLayout b)
    {
        var w = Math.Min(a.XMm + a.WidthMm, b.XMm + b.WidthMm) - Math.Max(a.XMm, b.XMm);
        var h = Math.Min(a.YMm + a.HeightMm, b.YMm + b.HeightMm) - Math.Max(a.YMm, b.YMm);
        return w > 0 && h > 0 && w * h > 0.5;
    }
}

/// <summary>Готовый шаблон-пресет: быстрый старт («Ценник с акцией» и т.п.).</summary>
public sealed record LabelDesignPreset(string Id, Func<string> Name, Func<LabelTemplate> Build);

/// <summary>2026-11-21: быстрые пресеты редактора ценников. Каждый — полноценный шаблон, который дальше правится руками.</summary>
public static class LabelDesignPresets
{
    private static LabelCustomElement X(LabelExtraKind kind, double x, double y, double w, double h, Action<LabelCustomElement>? tune = null)
    {
        var e = new LabelCustomElement { Kind = kind, XMm = x, YMm = y, WidthMm = w, HeightMm = h };
        tune?.Invoke(e);
        return e;
    }

    private static void Lay(LabelElementLayout e, double x, double y, double w, double h, bool on = true)
    {
        e.XMm = x; e.YMm = y; e.WidthMm = w; e.HeightMm = h; e.Enabled = on;
    }

    public static readonly IReadOnlyList<LabelDesignPreset> All = new[]
    {
        new LabelDesignPreset("simple", () => Tr.T("Простой 40×30", "Жөнөкөй 40×30", "Simple 40×30", "Basit 40×30", "Oddiy 40×30"), () => LabelTemplate.CreateDefault()),
        new LabelDesignPreset("mini", () => Tr.T("Мини 30×20", "Мини 30×20", "Mini 30×20", "Mini 30×20", "Mini 30×20"), () =>
        {
            var t = new LabelTemplate { WidthMm = 30, HeightMm = 20 };
            Lay(t.Barcode, 1.5, 1, 27, 9);
            Lay(t.ProductName, 1.5, 10.5, 27, 4);
            Lay(t.Price, 1.5, 14.8, 27, 4.2);
            return t;
        }),
        new LabelDesignPreset("shelf", () => Tr.T("Полка 58×40 (бренд, ячейка)", "Текче 58×40 (бренд, уяча)", "Shelf 58×40 (brand, cell)", "Raf 58×40 (marka, hücre)", "Javon 58×40 (brend, katak)"), () =>
        {
            var t = new LabelTemplate { WidthMm = 58, HeightMm = 40 };
            Lay(t.ProductName, 2, 1.5, 54, 8);
            Lay(t.Barcode, 2, 19, 36, 15);
            Lay(t.Price, 2, 10, 36, 8);
            t.Price.Align = 0;
            t.ProductName.Align = 0;
            t.Extras.Add(X(LabelExtraKind.Brand, 39, 12, 17, 4.5, e => e.Align = 2));
            t.Extras.Add(X(LabelExtraKind.Cell, 39, 20, 17, 5, e => { e.Bold = true; e.Align = 2; }));
            t.Extras.Add(X(LabelExtraKind.Date, 39, 34.5, 17, 3.5, e => e.Align = 2));
            return t;
        }),
        new LabelDesignPreset("promo", () => Tr.T("Ценник с акцией 58×40", "Акциялуу баа белгиси 58×40", "Promo price tag 58×40", "Promosyonlu etiket 58×40", "Aksiyali narx yorlig'i 58×40"), () =>
        {
            var t = new LabelTemplate { WidthMm = 58, HeightMm = 40, Frame = true };
            Lay(t.ProductName, 2, 2, 54, 9);
            t.ProductName.Bold = true;
            Lay(t.Price, 2, 16, 36, 12);
            t.Price.Align = 0;
            t.Price.FontSizePx = 30;
            Lay(t.Barcode, 2, 29.5, 54, 9);
            t.Extras.Add(X(LabelExtraKind.OldPrice, 2, 11.5, 28, 4.5, e => e.Align = 0));
            t.Extras.Add(X(LabelExtraKind.Discount, 40, 14, 16, 8, e => { e.FontSizePx = 18; }));
            return t;
        }),
        new LabelDesignPreset("price58x30", () => Tr.T("Только цена 58×30", "Баасы гана 58×30", "Price only 58×30", "Sadece fiyat 58×30", "Faqat narx 58×30"), () =>
        {
            var t = new LabelTemplate { WidthMm = 58, HeightMm = 30 };
            Lay(t.ProductName, 2, 2, 54, 9);
            Lay(t.Price, 2, 12, 54, 14);
            t.Price.FontSizePx = 36;
            Lay(t.Barcode, 2, 0, 20, 5, false);
            t.Extras.Add(X(LabelExtraKind.OldPrice, 2, 26, 54, 3.5));
            return t;
        }),
        new LabelDesignPreset("clothes", () => Tr.T("Одежда 40×30 (размер и цвет)", "Кийим 40×30 (өлчөм жана түс)", "Clothes 40×30 (size and colour)", "Giyim 40×30 (beden ve renk)", "Kiyim 40×30 (o'lcham va rang)"), () =>
        {
            var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
            Lay(t.ProductName, 2, 1, 36, 5);
            Lay(t.Variant, 2, 6.2, 36, 4.5);
            Lay(t.Barcode, 2, 11, 36, 11);
            Lay(t.Price, 2, 23, 36, 6);
            return t;
        }),
    };

    public static LabelDesignPreset? ById(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>2026-11-21: «Мои шаблоны» — общий список сохранённых шаблонов ценника/этикетки (BarcodePrintSettings.Presets). Один живой список
/// на программу: им пользуются и режим «Печать» вкладки (выпадающий список), и «Редактор», и «Шаблоны». Каждое сохранение зеркалится в общие
/// настройки магазина (<see cref="UserPreferences.LabelDesignsJson"/>, через SettingsCloudSync — как КП и настройки сфер), поэтому шаблоны
/// видны на всех компьютерах магазина. Свой эндпоинт не нужен.</summary>
public static class LabelDesignLibrary
{
    public const int MaxCount = 30;
    public const int MaxJsonBytes = 256 * 1024;

    private static readonly object Sync = new();
    private static List<BarcodeLabelPreset>? _shared;
    private static string? _lastJson;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Шаблоны изменились (сохранён, удалён, пришли с сервера).</summary>
    public static event Action? Changed;

    /// <summary>Живой общий список. Не копируйте — правьте и вызывайте <see cref="Commit"/>.</summary>
    public static List<BarcodeLabelPreset> Shared
    {
        get
        {
            lock (Sync)
                return Resolve(null);
        }
    }

    /// <summary>Вызывается при загрузке файла настроек печати: <paramref name="fromFile"/> — шаблоны из файла. Возвращает общий список.</summary>
    internal static List<BarcodeLabelPreset> Resolve(List<BarcodeLabelPreset>? fromFile)
    {
        lock (Sync)
        {
            var cloud = UserPreferences.Instance.LabelDesignsJson;
            if (_shared is null)
            {
                _shared = new List<BarcodeLabelPreset>();
                if (!string.IsNullOrWhiteSpace(cloud) && TryParse(cloud, out var fromCloud))
                {
                    _shared.AddRange(fromCloud);
                    _lastJson = cloud;
                }
                else if (fromFile is not null)
                    _shared.AddRange(fromFile);
            }
            else if (!string.IsNullOrWhiteSpace(cloud) && cloud != _lastJson && TryParse(cloud, out var changed))
            {
                // Пришло с сервера (другой компьютер магазина): общий список заменяется, ссылка остаётся прежней.
                _shared.Clear();
                _shared.AddRange(changed);
                _lastJson = cloud;
            }
            return _shared;
        }
    }

    private static bool TryParse(string json, out List<BarcodeLabelPreset> list)
    {
        list = new List<BarcodeLabelPreset>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<BarcodeLabelPreset>>(json, Json);
            if (parsed is null)
                return false;
            list = parsed.Where(x => x is { Template: not null } && !string.IsNullOrWhiteSpace(x.Name)).Take(MaxCount).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Записать список в файл настроек печати и в общие настройки магазина.</summary>
    public static void Commit()
    {
        List<BarcodeLabelPreset> list;
        lock (Sync)
            list = Resolve(null);
        PushToCloud(list);
        var settings = BarcodePrintSettingsStore.Load();
        BarcodePrintSettingsStore.Save(settings); // Presets у него — этот же общий список
        Changed?.Invoke();
    }

    /// <summary>Зеркало в общие настройки (UserPreferences → SettingsCloudSync). Слишком большой список не отправляется.</summary>
    internal static void PushToCloud(IReadOnlyList<BarcodeLabelPreset> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(list, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            {
                PosLogger.Log("Label designs: список слишком большой для общих настроек, остаётся только на этом компьютере.", "WARNING");
                return;
            }
            lock (Sync)
            {
                if (json == _lastJson)
                    return;
                _lastJson = json;
            }
            UserPreferences.Instance.LabelDesignsJson = json;
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label designs push failed: {ex.GetType().Name}", "WARNING");
        }
    }

    /// <summary>Сохранить шаблон под именем (тот же — перезаписать). null — достигнут предел 30.</summary>
    public static BarcodeLabelPreset? Upsert(string name, LabelTemplate template, int copies = 1, string copiesMode = "default")
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return null;
        if (name.Length > 40)
            name = name[..40];
        var stored = template.Clone();
        stored.Item = null;
        var preset = new BarcodeLabelPreset { Name = name, Template = stored, DefaultCopies = Math.Clamp(copies, 1, 99), CopiesMode = copiesMode };
        var list = Shared;
        var idx = list.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (idx >= 0)
            list[idx] = preset;
        else if (list.Count >= MaxCount)
            return null;
        else
            list.Add(preset);
        Commit();
        return preset;
    }

    public static bool Delete(string name)
    {
        var list = Shared;
        var n = list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (n > 0)
            Commit();
        return n > 0;
    }

    public static bool Rename(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        var list = Shared;
        var p = list.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.CurrentCultureIgnoreCase));
        if (p is null || newName.Length == 0 || list.Any(x => !ReferenceEquals(x, p) && string.Equals(x.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            return false;
        p.Name = newName.Length > 40 ? newName[..40] : newName;
        Commit();
        return true;
    }

    /// <summary>Для тестов: забыть общий список (следующее чтение берёт файл и общие настройки заново).</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _shared = null;
            _lastJson = null;
        }
    }
}
