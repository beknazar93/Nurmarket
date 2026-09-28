using System;
using System.Threading;
using System.Threading.Tasks;
using F = NurMarketKassa.Services.Hardware.ShtrikhLabelFormat;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: формат этикетки (A0h..A7h), символы валют (C1h/C2h) и курс (2Bh) —
/// для «Редактора валюты» и «Макета этикетки» в окне настроек весов. Отдельным файлом, чтобы
/// не трогать основной драйвер. Все команды — с паролем администратора; при ошибке пароля
/// (122/170) исключение летит сразу, окно на этом останавливает обмен.</summary>
public sealed partial class ShtrikhPrintLanScaleService
{
    /// <summary>Читает формат этикетки целиком: A0h (координаты), A2h (доп. координаты),
    /// A4h (шрифты). Если весы не знают A2h/A4h (код 120/121 — протокол ниже 1.4), эти части
    /// остаются пустыми и потом не записываются.</summary>
    public async Task<ShtrikhLabelLayout> GetLabelLayoutAsync(int format, CancellationToken ct = default)
    {
        if (format is < 0 or > F.MaxFormat)
            throw new ArgumentOutOfRangeException(nameof(format), format, "Формат этикетки — 0..14.");

        var main = await SendCheckedAsync(F.CmdGetLabelParams, WithPassword((byte)format), false, ct).ConfigureAwait(false);
        if (main.Payload.Length < F.MainLength)
            throw new ShtrikhScaleException("Весы вернули короткий ответ на запрос параметров этикетки (A0h).");

        var ex = await TryGetOptionalAsync(F.CmdGetLabelParamsEx, (byte)format, F.ExLength, ct).ConfigureAwait(false);
        var fonts = await TryGetOptionalAsync(F.CmdGetFonts, (byte)format, F.FontsLength, ct).ConfigureAwait(false);
        return new ShtrikhLabelLayout(format, main.Payload, ex, fonts);
    }

    private async Task<byte[]?> TryGetOptionalAsync(byte command, byte format, int length, CancellationToken ct)
    {
        var response = await SendAsync(command, WithPassword(format), false, ct).ConfigureAwait(false);
        if (response.ErrorCode is 120 or 121)
            return null; // команда неизвестна этой прошивке
        if (!response.Ok)
            throw new ShtrikhScaleException($"Весы отклонили команду {command:X2}h: {response.ErrorText}.", response.ErrorCode);
        return response.Payload.Length >= length ? response.Payload : null;
    }

    /// <summary>Записывает формат в весы: A1h, затем A3h и A5h (если весы их отдавали при
    /// чтении). Только пользовательские форматы 10..14.</summary>
    public async Task WriteLabelLayoutAsync(ShtrikhLabelLayout layout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!F.IsUserFormat(layout.Format))
            throw new ShtrikhScaleException($"Формат {layout.Format} — стандартный; весы разрешают записывать только «Формат 1..5» (10..14).");

        await SendCheckedAsync(F.CmdSetLabelParams, WithPassword(layout.EncodeMainWrite()), false, ct).ConfigureAwait(false);
        if (layout.HasEx)
            await SendCheckedAsync(F.CmdSetLabelParamsEx, WithPassword(layout.EncodeExWrite()), false, ct).ConfigureAwait(false);
        if (layout.HasFonts)
            await SendCheckedAsync(F.CmdSetFonts, WithPassword(layout.EncodeFontsWrite()), false, ct).ConfigureAwait(false);
    }

    /// <summary>A7h «Получить длины строчных элементов этикетки» — 31 байт (число знаков).
    /// null — прошивка команду не знает (тогда предпросмотр берёт типовые длины).</summary>
    public async Task<byte[]?> GetStringLengthsAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(F.CmdGetStringLengths, WithPassword(), false, ct).ConfigureAwait(false);
        if (response.ErrorCode is 120 or 121)
            return null;
        if (!response.Ok)
            throw new ShtrikhScaleException($"Весы отклонили команду A7h: {response.ErrorText}.", response.ErrorCode);
        return response.Payload.Length >= F.StringLengthsLength ? response.Payload[..F.StringLengthsLength] : null;
    }

    /// <summary>A6h «Получить список доступных шрифтов» — битовая маска шрифтов 0..6.</summary>
    public async Task<int> GetAvailableFontsAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(F.CmdGetAvailableFonts, WithPassword(), false, ct).ConfigureAwait(false);
        if (response.ErrorCode is 120 or 121)
            return 0x7F;
        if (!response.Ok)
            throw new ShtrikhScaleException($"Весы отклонили команду A6h: {response.ErrorText}.", response.ErrorCode);
        return response.Payload.Length < 1 ? 0x7F : response.Payload[0] & 0x7F;
    }

    /// <summary>C2h «Загрузка символов валюты для печати»: номер 1 — основная валюта (печатается
    /// справа от стоимости), 2 — дополнительная (справа от валютного эквивалента).</summary>
    public Task LoadPrintSymbolAsync(int number, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != F.PrintSymbolSize)
            throw new ArgumentException($"Символ для печати — ровно {F.PrintSymbolSize} байт.", nameof(data));
        var tail = new byte[2 + data.Length];
        tail[0] = (byte)Math.Clamp(number, 1, 2);
        tail[1] = F.PrintSymbolSize;
        data.CopyTo(tail, 2);
        return SendCheckedAsync(F.CmdLoadPrintSymbol, WithPassword(tail), false, ct);
    }

    /// <summary>C1h «Загрузка символов валюты для экрана» (дисплей весов, 5×7 = 7 байт).</summary>
    public Task LoadDisplaySymbolAsync(int number, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != F.DisplaySymbolSize)
            throw new ArgumentException($"Символ для экрана — ровно {F.DisplaySymbolSize} байт.", nameof(data));
        var tail = new byte[2 + data.Length];
        tail[0] = (byte)Math.Clamp(number, 1, 2);
        tail[1] = F.DisplaySymbolSize;
        data.CopyTo(tail, 2);
        return SendCheckedAsync(F.CmdLoadDisplaySymbol, WithPassword(tail), false, ct);
    }

    /// <summary>2Bh «Записать параметр "Курс"» — сколько основной валюты за единицу
    /// дополнительной (для валютного эквивалента стоимости).</summary>
    public Task SetCurrencyRateAsync(decimal rate, CancellationToken ct = default) =>
        SendCheckedAsync(F.CmdSetCurrencyRate, WithPassword(F.EncodeRate(rate)), false, ct);

    /// <summary>Курс и признак подсчёта валютного эквивалента из «Запроса состояния» 11h
    /// (байт 53 — признак, 54..57 — курс, «дробное»). Без пароля.</summary>
    public async Task<(bool EquivalentOn, decimal Rate)> GetCurrencyInfoAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(ShtrikhPrintProtocol.CmdGetStatus, null, false, ct).ConfigureAwait(false);
        if (!response.HasStatusPayload || response.Payload.Length < 58)
            throw new ShtrikhScaleException($"Весы не отдали состояние: {response.ErrorText}.", response.ErrorCode);
        return (response.Payload[53] == 1, F.DecodeRate(response.Payload.AsSpan(54, 4)));
    }
}
