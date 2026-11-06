namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (просьба клиента магазина одежды: «для размеров нужна кнопка генерации QR»): какой код печатать на этикетке размера.
/// Только на время печати — шаблон этикетки на диске не меняется.</summary>
public enum LabelCodeMode
{
    /// <summary>Как задано в шаблоне этикетки (по умолчанию).</summary>
    AsTemplate = 0,
    /// <summary>Линейный штрихкод (если в шаблоне QR — вместо него автоматический подбор по длине кода).</summary>
    Barcode = 1,
    /// <summary>QR-код с кодом размера: сканер QR отдаёт ту же строку, касса находит размер по <see cref="VariantBarcodeIndex"/>.</summary>
    Qr = 2,
}

/// <summary>2026-11-06: переключатель «Формат кода» для этикеток размеров и проверка, что QR читаем на выбранной этикетке.</summary>
public static class LabelCodeModes
{
    /// <summary>Наименьший читаемый модуль QR, точек 203 dpi (3 точки ≈ 0,37 мм). Меньше — дешёвые сканеры QR не берут.</summary>
    public const int MinQrModuleDots = 3;

    /// <summary>Копия шаблона под выбранный формат; сам <paramref name="template"/> не меняется.</summary>
    public static LabelTemplate Apply(LabelTemplate template, LabelCodeMode mode)
    {
        var copy = template.Clone();
        switch (mode)
        {
            case LabelCodeMode.Qr:
                copy.BarcodeFormat = LabelBarcodeFormat.QrCode;
                break;
            case LabelCodeMode.Barcode when copy.BarcodeFormat == LabelBarcodeFormat.QrCode:
                copy.BarcodeFormat = LabelBarcodeFormat.Auto;
                break;
        }
        return copy;
    }

    /// <summary>Сколько точек 203 dpi занимает один модуль QR с кодом <paramref name="code"/> в блоке штрихкода шаблона
    /// (0 — не помещается вовсе или закодировать нельзя). Расчёт тот же, что при рисовании этикетки.</summary>
    public static int QrModuleDots(LabelTemplate template, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return 0;
        var matrix = BarcodeLabelService.EncodeQrModulesForCheck(code);
        if (matrix <= 0)
            return 0;
        var cells = matrix + 2 * Math.Max(template.BarcodeMargin, 0);
        var side = Math.Min(BarcodeLabelService.MmToPx(template.Barcode.WidthMm), BarcodeLabelService.MmToPx(template.Barcode.HeightMm));
        return side < cells ? 0 : (int)(side / cells);
    }

    /// <summary>Предупреждение, если QR на этой этикетке слишком мелкий; null — читаем (или блок кода выключен).</summary>
    public static string? QrSizeProblem(LabelTemplate template, string? code)
    {
        if (!template.Barcode.Enabled || string.IsNullOrWhiteSpace(code))
            return null;
        var m = QrModuleDots(template, code);
        if (m >= MinQrModuleDots)
            return null;
        return Tr.T(
            $"QR слишком мелкий для этой этикетки ({template.WidthMm:0.#}×{template.HeightMm:0.#} мм) — сканер может его не прочитать. Выберите этикетку побольше или увеличьте блок кода.",
            $"QR бул этикетка үчүн өтө майда ({template.WidthMm:0.#}×{template.HeightMm:0.#} мм) — сканер окубай калышы мүмкүн. Чоңураак этикетка тандаңыз же код блогун чоңойтуңуз.",
            $"The QR is too small for this label ({template.WidthMm:0.#}×{template.HeightMm:0.#} mm) — a scanner may not read it. Choose a larger label or enlarge the code box.",
            $"QR bu etiket için çok küçük ({template.WidthMm:0.#}×{template.HeightMm:0.#} mm) — tarayıcı okuyamayabilir. Daha büyük bir etiket seçin veya kod alanını büyütün.",
            $"QR bu yorliq uchun juda mayda ({template.WidthMm:0.#}×{template.HeightMm:0.#} mm) — skaner o'qiy olmasligi mumkin. Kattaroq yorliq tanlang yoki kod blokini kattalashtiring.");
    }
}
