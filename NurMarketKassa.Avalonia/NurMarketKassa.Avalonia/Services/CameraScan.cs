using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, владелец: «доступ к камере добавь в Android, чтобы его как сканер тоже можно было
/// использовать в кассе, и ещё при создании товаров, приёмке товаров», «и в админке тоже».
/// Камера аппарата как сканер штрихкодов. Сам сканер ставит платформа (Android: экран камеры + ZXing,
/// см. AndroidCameraScanner); считанный код уходит в кассу как скан USB-сканера
/// (AvaloniaKeyboardWedgeBarcodeService.Inject) — его ловят те же обработчики: чек, карточка товара,
/// склад (приёмка, ревизия, списание) в кассе и в программе владельца. В Windows кнопки камеры нет.</summary>
public static class CameraScan
{
    /// <summary>Открыть камеру и вернуть код (null — закрыли без кода). Ставит Android-часть.</summary>
    public static Func<Task<string?>>? Scanner { get; set; }

    public static bool IsAvailable => Scanner is not null;

    /// <summary>Считать код камерой и отдать его кассе как скан.</summary>
    public static async Task ScanIntoKassaAsync()
    {
        if (Scanner is not { } scanner)
            return;
        string? code;
        try
        {
            code = await scanner().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Камера-сканер: {ex.Message}", "WARNING");
            return;
        }
        if (string.IsNullOrWhiteSpace(code))
            return;
        PosLogger.Log($"Камера-сканер: считан код ({code.Length} симв.).", "CART");
        if (App.GetRequiredService<IBarcodeInputService>() is AvaloniaKeyboardWedgeBarcodeService wedge)
            wedge.Inject(code);
    }
}
