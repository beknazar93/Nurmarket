using System;

namespace NurMarketKassa.ViewModels
{
    /// <summary>2026-11-06 (план «десктоп 10 из 10», Д4-16, решение владельца): правка текста поля суммы кнопками цифрового блока окна оплаты.
    /// Чистая функция без окон и денег: что было в поле, где курсор/выделение, какая кнопка → новый текст и положение курсора. Расчёт суммы
    /// и сдачи делает CheckoutViewModel по тексту поля, как при вводе с клавиатуры. Это не экранная клавиатура: только цифры, запятая и «стереть».</summary>
    public static class CheckoutKeypad
    {
        /// <summary>Не больше стольких цифр в сумме (чек в десятки миллиардов — ошибка ввода).</summary>
        public const int MaxDigits = 12;

        /// <summary>Знаков после запятой — не больше двух (так же, как проверяет поле суммы).</summary>
        public const int MaxDecimals = 2;

        /// <param name="text">Текст поля сейчас.</param>
        /// <param name="selectionStart">Начало выделения (или положение курсора).</param>
        /// <param name="selectionEnd">Конец выделения (равен началу, если выделения нет).</param>
        /// <param name="key">"0"…"9", "," или "back".</param>
        /// <returns>Новый текст и положение курсора. Недопустимое нажатие (вторая запятая, третий знак после запятой, слишком длинное число) — текст не меняется.</returns>
        public static (string Text, int Caret) Apply(string? text, int selectionStart, int selectionEnd, string key)
        {
            text ??= "";
            var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
            var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);

            if (key == "back")
            {
                if (end > start)
                    return (text.Remove(start, end - start), start);
                return start > 0 ? (text.Remove(start - 1, 1), start - 1) : (text, start);
            }

            string insert;
            if (key.Length == 1 && key[0] is >= '0' and <= '9')
                insert = key;
            else if (key == ",")
                insert = ",";
            else
                return (text, end);

            var rest = text.Remove(start, end - start);
            if (insert == ",")
            {
                if (rest.Contains(',') || rest.Contains('.'))
                    return (text, end);
                // Запятая в начале — «0,»; запятая посреди числа не должна оставлять «хвост» из более чем двух знаков.
                var head = rest[..start];
                var tail = rest[start..];
                if (tail.Length > MaxDecimals)
                    return (text, end);
                if (head.Length == 0)
                    head = "0";
                var withComma = head + "," + tail;
                return (withComma, head.Length + 1);
            }

            var result = rest.Insert(start, insert);
            var caret = start + 1;
            var commaIndex = result.IndexOfAny(new[] { ',', '.' });
            if (commaIndex >= 0 && result.Length - commaIndex - 1 > MaxDecimals)
                return (text, end);
            if (CountDigits(result) > MaxDigits)
                return (text, end);

            // Ведущие нули у целого числа: «05» → «5» (но «0,5» остаётся).
            if (commaIndex < 0 && result.Length > 1 && result[0] == '0')
            {
                var trimmed = result.TrimStart('0');
                if (trimmed.Length == 0)
                    trimmed = "0";
                caret = Math.Max(0, caret - (result.Length - trimmed.Length));
                result = trimmed;
            }

            return (result, Math.Min(caret, result.Length));
        }

        private static int CountDigits(string text)
        {
            var n = 0;
            foreach (var ch in text)
                if (ch is >= '0' and <= '9')
                    n++;
            return n;
        }
    }
}
