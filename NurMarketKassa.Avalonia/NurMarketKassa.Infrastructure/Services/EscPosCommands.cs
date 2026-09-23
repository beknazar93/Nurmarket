using System.IO;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Общие ESC/POS-команды для текстовой и графической печати.</summary>
internal static class EscPosCommands
{
    public static void WriteInitialize(Stream s)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x40);
    }

    /// <summary>FS . (1C 2E) — выключить китайский/Kanji двухбайтовый режим. Многие дешёвые
    /// ESC/POS-принтеры китайского происхождения загружаются в этом режиме по умолчанию — тогда
    /// любая команда выбора кодовой страницы (ESC t) игнорируется, а кириллица печатается как
    /// случайные китайские иероглифы, потому что принтер трактует пары байт как код Kanji вместо
    /// одного байта из выбранной таблицы. На принтерах без поддержки Kanji эта команда — безопасный
    /// no-op. См. память "Thermal printer Cyrillic encoding 2026-09-04": смена кодировки/таблицы
    /// сама по себе не помогла — печать вышла китайской даже при верных настройках (wpc1251+Авто).</summary>
    public static void WriteCancelKanjiMode(Stream s)
    {
        s.WriteByte(0x1C);
        s.WriteByte(0x2E);
    }

    public static void WriteCodePage(Stream s, int tableByte, int? escRByte, bool noEscPct)
    {
        if (!noEscPct)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x25);
            s.WriteByte(0x00);
        }

        if (escRByte is >= 0 and <= 255)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x52);
            s.WriteByte((byte)(escRByte.Value & 0xFF));
        }

        s.WriteByte(0x1B);
        s.WriteByte(0x74);
        s.WriteByte((byte)(tableByte & 0xFF));
    }

    public static void WriteDefaultLineSpacing(Stream s)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x32);
    }

    /// <summary>ESC 3 n (1B 33 n) — межстрочный интервал ровно n точек. Нужна для растровой
    /// печати полосами (см. GraphicReceiptGenerator.ConvertToEscPosRaster): каждая полоса
    /// растра высотой 24 точки отделяется от следующей обычным LF, а LF без этой команды
    /// продвигает бумагу на межстрочный интервал ПО УМОЛЧАНИЮ принтера (обычно 1/6 дюйма —
    /// не 24 точки), из-за чего полосы съезжают и накладываются друг на друга ("двоение"/
    /// полосы на чеке). Явно выставленный интервал в 24 точки делает LF между полосами
    /// продвижением ровно на высоту полосы — без наложения и без зазора.</summary>
    public static void WriteLineSpacing(Stream s, byte dots)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x33);
        s.WriteByte(dots);
    }

    /// <summary>GS ! n — размер символов. 2026-09-08: раньше настройка "Размер шрифта (px)" в
    /// Настройки → Печать влияла только на графический режим печати (растровое изображение) —
    /// в текстовом ESC/POS-режиме размер шрифта в принципе никак не задавался (принтер печатал
    /// только своим встроенным шрифтом), хотя элемент управления был виден и в текстовом режиме
    /// тоже, что вводило владельца в заблуждение (напечатанный чек не менялся). Теперь и текстовый
    /// режим отправляет эту команду. Множитель ШИРИНЫ намеренно всегда 1 (не увеличивается) —
    /// весь текст чека уже выровнен под фиксированную ширину в символах (32/48, см. ReceiptLayout),
    /// и увеличение ширины символов физически уменьшило бы, сколько символов помещается на
    /// бумаге, ломая выравнивание сумм по правому краю; растёт только высота — чек читается
    /// крупнее, но колонки не съезжают.</summary>
    public static void WriteCharacterSize(Stream s, int widthMultiplier, int heightMultiplier)
    {
        var w = (byte)System.Math.Clamp(widthMultiplier - 1, 0, 7);
        var h = (byte)System.Math.Clamp(heightMultiplier - 1, 0, 7);
        s.WriteByte(0x1D);
        s.WriteByte(0x21);
        s.WriteByte((byte)((h << 4) | w));
    }

    /// <summary>LF — перевод строки для ESC/POS текстового режима.</summary>
    public static void WriteLineFeed(Stream s) => s.WriteByte(0x0A);

    /// <summary>ESC d n — прокрутка на n строк.</summary>
    public static void WriteFeedLines(Stream s, byte lines)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x64);
        s.WriteByte(lines);
    }

    /// <summary>ESC p m t1 t2 (1B 70 …) — импульс на разъём денежного ящика. Ящик подключается
    /// НЕ к компьютеру, а к чековому принтеру (разъём RJ-11/RJ-12 "DK"), поэтому команда идёт в
    /// тот же порт, что и чек. <paramref name="pin"/>: 0 — контакт 2, 1 — контакт 5; какой именно
    /// используется, зависит от распайки кабеля конкретного ящика, поэтому вынесено в настройки
    /// (ящик, подключённый к контакту 5, на команду с m=0 просто не реагирует — «ничего не
    /// происходит», а не ошибка). Длительности импульса заданы в единицах по 2 мс: 25 → 50 мс
    /// включено, 250 → 500 мс пауза; это значения из спецификации ESC/POS, их принимают
    /// практически все соленоидные ящики.</summary>
    public static void WriteOpenCashDrawer(Stream s, int pin)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x70);
        s.WriteByte((byte)(pin == 1 ? 1 : 0));
        s.WriteByte(25);
        s.WriteByte(250);
    }

    /// <summary>Прокрутка на 3 строки и отрез (GS V B 0).</summary>
    public static void WriteFeedAndCut(Stream s, byte feedLines = 3)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x64);
        s.WriteByte(feedLines);

        s.WriteByte(0x1D);
        s.WriteByte(0x56);
        s.WriteByte(0x42);
        s.WriteByte(0x00);
    }
}
