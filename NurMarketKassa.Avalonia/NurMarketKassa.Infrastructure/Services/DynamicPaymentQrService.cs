using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using ZXing;

namespace NurMarketKassa.Services;

/// <summary>Показ на экране покупателя платёжного QR С УЖЕ ВПИСАННОЙ суммой чека.
///
/// Как это работает: владелец, как и раньше, загружает в Настройках → Операции картинку
/// статического QR своего банка. Мы её ОДИН РАЗ распознаём (ZXing), достаём оттуда реквизиты
/// получателя и дальше на каждый чек собираем новый QR с суммой — см. <see cref="ElqrQrBuilder"/>.
/// Реквизиты не сочиняются и не хранятся отдельно: источник истины — та же картинка, которую
/// владелец уже загрузил.
///
/// ЕСЛИ ЧТО-ТО НЕ ТАК — молча возвращаем null, и экран покупателя показывает статический QR
/// ровно как раньше. Не распознали картинку, это не ELQR, нулевой чек, нет прав на запись
/// временного файла — во всех случаях касса продолжает работать как до этой функции.
///
/// НЕ ПРОВЕРЕНО НА ЖИВЫХ ПЛАТЕЖАХ: формула контрольной суммы сверена с двумя настоящими QR
/// MBank, но примут ли собранный QR приложения ДРУГИХ банков — неизвестно, образцов не было.
/// Поэтому функция по умолчанию ВЫКЛЮЧЕНА (UserPreferences.DynamicPaymentQrEnabled) — владелец
/// включает её сам после того, как отсканирует тестовый QR парой банковских приложений.
///
/// И ещё одно ограничение, которое надо понимать: без API банка касса НЕ УЗНАЕТ, что платёж
/// прошёл. Кассир подтверждает оплату вручную, ровно как и сейчас со статическим QR. Этот
/// код экономит покупателю ввод суммы, но не заменяет подтверждение.</summary>
[SupportedOSPlatform("windows")]
public static class DynamicPaymentQrService
{
    /// <summary>Распознанные payload'ы статических QR: ключ — путь к картинке плюс отметка
    /// времени файла, чтобы замена картинки в настройках подхватилась сама.</summary>
    private static readonly ConcurrentDictionary<string, string?> DecodedCache = new();

    /// <summary>Последний сгенерированный файл — чтобы не плодить мусор в temp на каждый чек.</summary>
    private static string? _lastGeneratedPath;
    private static string? _lastGeneratedKey;

    private static string OutputDirectory => Path.Combine(Path.GetTempPath(), "NurMarketKassa", "qr");

    /// <summary>Достаёт текст из картинки статического QR. null — распознать не удалось.</summary>
    public static string? TryDecodeStaticQr(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        string cacheKey;
        try
        {
            cacheKey = imagePath + "|" + File.GetLastWriteTimeUtc(imagePath).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }

        return DecodedCache.GetOrAdd(cacheKey, _ => DecodeFile(imagePath));
    }

    private static string? DecodeFile(string imagePath)
    {
        try
        {
            using var bitmap = new Bitmap(imagePath);
            var luminance = ToLuminanceSource(bitmap);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                },
            };

            return reader.Decode(luminance)?.Text;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось распознать статический QR «{Path.GetFileName(imagePath)}»: {ex.Message}", "QR");
            return null;
        }
    }

    /// <summary>Переводит картинку в источник яркости для ZXing. Делаем через 24-битную копию:
    /// исходный PNG может быть с палитрой или альфа-каналом, и читать его байты «как есть»
    /// нельзя — разложение по каналам будет другим.</summary>
    private static RGBLuminanceSource ToLuminanceSource(Bitmap source)
    {
        using var rgb = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(rgb))
            g.DrawImage(source, 0, 0, source.Width, source.Height);

        var rect = new Rectangle(0, 0, rgb.Width, rgb.Height);
        var data = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[rgb.Width * rgb.Height * 3];
            // Копируем построчно: у Bitmap строки выровнены по 4 байта (Stride), сплошного
            // массива в памяти нет, и копирование одним куском дало бы сдвиг пикселей.
            for (var y = 0; y < rgb.Height; y++)
            {
                var sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(
                    sourceRow, bytes, y * rgb.Width * 3, rgb.Width * 3);
            }

            return new RGBLuminanceSource(bytes, rgb.Width, rgb.Height, RGBLuminanceSource.BitmapFormat.BGR24);
        }
        finally
        {
            rgb.UnlockBits(data);
        }
    }

    /// <summary>Главный метод для экрана покупателя: по картинке статического QR владельца и
    /// сумме чека возвращает путь к PNG с динамическим QR. null — работаем как раньше.</summary>
    public static string? TryBuildDynamicQrImage(string staticQrImagePath, decimal totalSom, int sizePx = 420)
    {
        if (totalSom <= 0m)
            return null;

        var staticPayload = TryDecodeStaticQr(staticQrImagePath);
        if (string.IsNullOrWhiteSpace(staticPayload))
            return null;

        var dynamicPayload = ElqrQrBuilder.BuildWithAmount(staticPayload, totalSom);
        if (string.IsNullOrWhiteSpace(dynamicPayload))
            return null;

        // Один и тот же чек перерисовывается много раз (экран обновляется на каждое изменение
        // корзины) — если сумма и банк не поменялись, отдаём уже готовый файл.
        var key = staticQrImagePath + "|" + dynamicPayload;
        if (key == _lastGeneratedKey && _lastGeneratedPath is { } cached && File.Exists(cached))
            return cached;

        try
        {
            Directory.CreateDirectory(OutputDirectory);
            var target = Path.Combine(OutputDirectory, $"dynamic_{Guid.NewGuid():N}.png");

            using (var bitmap = BarcodeLabelService.RenderBarcode(
                       dynamicPayload, sizePx, sizePx, LabelBarcodeFormat.QrCode, margin: 1))
            {
                bitmap.Save(target, ImageFormat.Png);
            }

            DeleteQuietly(_lastGeneratedPath);
            _lastGeneratedPath = target;
            _lastGeneratedKey = key;
            return target;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось собрать динамический QR: {ex.GetType().Name}: {ex.Message}", "QR");
            return null;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Файл ещё показывается на экране покупателя — удалится при следующей попытке.
        }
    }
}
