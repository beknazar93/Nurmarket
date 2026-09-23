using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Сборка платёжного QR ELQR (межбанковский стандарт КР, оператор — Межбанковский
/// процессинговый центр) с УЖЕ ВПИСАННОЙ суммой чека, чтобы покупателю не набирать её руками
/// в приложении банка.
///
/// Формат — EMVCo Merchant Presented QR, TLV: тег (2 цифры) + длина (2 цифры) + значение.
/// Доставляется ссылкой вида `https://app.mbank.kg/qr/#&lt;payload&gt;`, где payload
/// percent-кодирован (пробел в имени получателя приходит как %20).
///
/// ВАЖНО про разбор: декодировать из percent-кодирования надо ДО разбора TLV — длины считают
/// ДЕКОДИРОВАННЫЕ символы, и разбор сырой строки ломается ровно на имени получателя.
///
/// КОНТРОЛЬНАЯ СУММА (тег 63) — главное, из-за чего фича была заморожена 2026-09-03:
///   SHA-256(декодированный поток БЕЗ объекта 63) → hex → последние 4 символа.
/// Проверено 2026-09-22 на ДВУХ реальных QR владельца: без суммы → 919c, на 200 сом → 4498.
/// Прошлая попытка не сходилась, потому что в хеш включали "6304" (тег и длину) по конвенции
/// EMVCo CRC — объект 63 нужно отбрасывать целиком.
///
/// Регистр: MBank пишет контрольную сумму СТРОЧНЫМИ буквами (919c), хотя спецификация EMVCo
/// требует прописных. Повторяем то, что реально делает MBank, — так собранный QR побайтово
/// совпадает с настоящим.
///
/// ЧЕГО НЕ ЗНАЕМ (и почему это не мешает): у тега 32 есть подполя 12/13, значение которых не
/// разгадано; они приходят из статического QR владельца и переносятся КАК ЕСТЬ — мы их не
/// сочиняем. Сумма живёт в теге 54, он от этих подполей не зависит.
///
/// НЕ ПРОВЕРЕНО: примут ли собранный QR приложения ДРУГИХ банков — оба образца были от MBank.
/// Перед выпуском отсканировать двумя-тремя приложениями разных банков.</summary>
public static class ElqrQrBuilder
{
    /// <summary>Тег суммы операции. Целое число в тыйынах, без точки и без ведущих нулей.</summary>
    public const string TagAmount = "54";

    /// <summary>Тег контрольной суммы — всегда последний и всегда длиной 4.</summary>
    public const string TagChecksum = "63";

    /// <summary>Один элемент TLV верхнего уровня.</summary>
    public readonly record struct TlvField(string Tag, string Value);

    /// <summary>Разбирает payload (уже декодированный) в плоский список тегов верхнего уровня.
    /// Во вложенные шаблоны (26–51) не лезем: сумма живёт на верхнем уровне, а содержимое
    /// шаблонов переносится без изменений.</summary>
    public static List<TlvField> Parse(string payload)
    {
        var fields = new List<TlvField>();
        if (string.IsNullOrEmpty(payload))
            return fields;

        var i = 0;
        while (i + 4 <= payload.Length)
        {
            var tag = payload.Substring(i, 2);
            if (!int.TryParse(payload.AsSpan(i + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var len))
                break;

            var valueStart = i + 4;
            if (valueStart + len > payload.Length)
                break;

            fields.Add(new TlvField(tag, payload.Substring(valueStart, len)));
            i = valueStart + len;
        }

        return fields;
    }

    /// <summary>Собирает TLV обратно в строку.</summary>
    public static string Serialize(IEnumerable<TlvField> fields) =>
        string.Concat(fields.Select(f =>
            f.Tag + f.Value.Length.ToString("00", CultureInfo.InvariantCulture) + f.Value));

    /// <summary>Контрольная сумма тега 63 по потоку БЕЗ самого объекта 63.</summary>
    public static string ComputeChecksum(string payloadWithoutChecksumObject)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadWithoutChecksumObject));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[^4..];
    }

    /// <summary>Пересобирает payload, проставляя корректный тег 63 в конец.</summary>
    public static string FinalizePayload(IEnumerable<TlvField> fields)
    {
        var body = Serialize(fields.Where(f => f.Tag != TagChecksum));
        return body + TagChecksum + "04" + ComputeChecksum(body);
    }

    /// <summary>Сумма в тыйынах из суммы в сомах. 200.00 сом → "20000".</summary>
    public static string AmountToTiyin(decimal amountSom)
    {
        var tiyin = Math.Round(amountSom * 100m, MidpointRounding.AwayFromZero);
        if (tiyin < 0)
            tiyin = 0;
        return ((long)tiyin).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Главный метод: берёт СТАТИЧЕСКИЙ QR владельца (ссылку или голый payload) и
    /// возвращает такую же ссылку, но с суммой чека внутри.
    ///
    /// Реквизиты получателя не сочиняются — они целиком переносятся из статического QR.
    /// Возвращает null, если вход не похож на ELQR-payload (тогда вызывающий код должен
    /// показать обычный статический QR, как раньше).</summary>
    public static string? BuildWithAmount(string staticQrTextOrLink, decimal amountSom)
    {
        if (string.IsNullOrWhiteSpace(staticQrTextOrLink))
            return null;

        var (prefix, rawPayload) = SplitLink(staticQrTextOrLink.Trim());
        var decoded = Uri.UnescapeDataString(rawPayload);

        var fields = Parse(decoded);
        // Минимальная вменяемость: обязаны быть формат (00) и контрольная сумма (63),
        // иначе это не ELQR и трогать его нельзя.
        if (fields.Count < 3 || !fields.Any(f => f.Tag == "00") || !fields.Any(f => f.Tag == TagChecksum))
            return null;

        var amount = AmountToTiyin(amountSom);
        if (amount == "0")
            return null; // нулевой чек — смысла в динамическом QR нет

        var rebuilt = new List<TlvField>();
        var inserted = false;
        foreach (var field in fields)
        {
            if (field.Tag == TagChecksum)
                continue; // пересчитаем в конце
            if (field.Tag == TagAmount)
                continue; // старую сумму выбрасываем, вставим свою в правильном месте

            // Теги идут по возрастанию; сумма должна встать перед первым тегом больше 54.
            if (!inserted
                && int.TryParse(field.Tag, NumberStyles.None, CultureInfo.InvariantCulture, out var tagNumber)
                && tagNumber > 54)
            {
                rebuilt.Add(new TlvField(TagAmount, amount));
                inserted = true;
            }

            rebuilt.Add(field);
        }

        if (!inserted)
            rebuilt.Add(new TlvField(TagAmount, amount));

        var finalPayload = FinalizePayload(rebuilt);

        // Возвращаем в том же виде, в каком пришло: percent-кодируем обратно, чтобы ссылка
        // была байт в байт такой же формы, как настоящая от банка.
        return prefix + EncodePayloadForLink(finalPayload);
    }

    /// <summary>Отделяет `https://…/#` от самого payload. Если пришёл голый payload — префикс пуст.</summary>
    private static (string Prefix, string Payload) SplitLink(string text)
    {
        var hash = text.IndexOf('#');
        return hash >= 0
            ? (text[..(hash + 1)], text[(hash + 1)..])
            : ("", text);
    }

    /// <summary>Кодирует payload для ссылки так же, как это делает банк: спецсимволы percent-
    /// кодируются, цифры и точки остаются как есть.</summary>
    private static string EncodePayloadForLink(string payload)
    {
        var sb = new StringBuilder(payload.Length + 8);
        foreach (var ch in payload)
        {
            var safe = (ch >= '0' && ch <= '9')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= 'a' && ch <= 'z')
                || ch is '.' or '-' or '_' or '~';
            if (safe)
                sb.Append(ch);
            else
                foreach (var b in Encoding.UTF8.GetBytes(ch.ToString()))
                    sb.Append('%').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
