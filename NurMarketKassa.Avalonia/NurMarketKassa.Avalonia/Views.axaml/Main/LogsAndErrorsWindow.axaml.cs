using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Логи и ошибки» (этап 3 бэклога «Доработки») — читает существующий текстовый лог-файл
/// (см. SafeFileLoggerProvider, NurMarketKassa.Infrastructure) напрямую с диска и показывает
/// только Warning/Error/Critical записи, отфильтровывая обычный информационный шум (синхронизация
/// каталога и т.п.) — цель экрана в том, чтобы владелец магазина/поддержка NurMarket быстро
/// поняли, что пошло не так, а не читали полный технический журнал.
/// </summary>
public partial class LogsAndErrorsWindow : Window
{
    private static readonly Regex LineRegex = new(
        @"^\d{4}-\d{2}-\d{2} (?<time>\d{2}:\d{2}:\d{2})\.\d{3} [+\-]\d{2}:\d{2} \[(?<level>\w+)\] (?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex DatePrefixRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) ", RegexOptions.Compiled);

    private static readonly Regex CategoryPrefixRegex = new(
        @"^\[NurMarketKassa\.POS\]\s*", RegexOptions.Compiled);

    // Записи PosLogger.Log всегда начинаются с "[КАТЕГОРИЯ] " (см. PosLogger.cs) — это дублирует
    // колонку "Статус" в этой же таблице, поэтому убирается перед показом для ВСЕХ строк.
    private static readonly Regex LeadingCategoryBracketRegex = new(
        @"^\[[^\]]+\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Часть записей в лог-файле пишется на английском (внутренние разработческие сообщения) —
    /// экран "Логи и ошибки" рассчитан на владельца магазина, поэтому самые частые из них
    /// переводятся здесь на русский/кыргызский (через Tr.T, тот же язык, что выбран в
    /// Настройки → Экран). Полный охват всех возможных PosLogger.Log(...) по всему коду
    /// нецелесообразен (их сотни, в основном редкие технические случаи) — при отсутствии
    /// совпадения показывается исходный текст как есть, это лучше, чем ничего.
    /// </summary>
    private static readonly (Regex Pattern, Func<Match, string> Translate)[] MessageTranslations =
    {
        (new Regex(@"^Subscription expired: end_date=(?<date>\S+), daysRemaining=(?<days>-?\d+)$", RegexOptions.Compiled),
            m => Tr.T(
                $"Подписка истекла (дата окончания: {m.Groups["date"].Value}, дней просрочено: {-int.Parse(m.Groups["days"].Value)}).",
                $"Жазылуу мөөнөтү бүттү (аяктоо күнү: {m.Groups["date"].Value}, {-int.Parse(m.Groups["days"].Value)} күн өттү).")),

        (new Regex(@"^Subscription near expiry: end_date=(?<date>\S+), daysRemaining=(?<days>\d+)$", RegexOptions.Compiled),
            m => Tr.T(
                $"Подписка скоро истекает (дата окончания: {m.Groups["date"].Value}, осталось дней: {m.Groups["days"].Value}).",
                $"Жазылуу мөөнөтү жакында бүтөт (аяктоо күнү: {m.Groups["date"].Value}, {m.Groups["days"].Value} күн калды).")),

        (new Regex(@"^Subscription expired during active session.*$", RegexOptions.Compiled),
            _ => Tr.T(
                "Подписка истекла во время работы — чек сохранён автоматически, выполнен выход из кассы.",
                "Жазылуу мөөнөтү иштеп жатканда бүттү — чек автоматтык түрдө сакталды, кассадан чыгуу аткарылды.")),

        (new Regex(@"^Receipt printer returned failure\.?$", RegexOptions.Compiled),
            _ => Tr.T("Чековый принтер вернул ошибку печати.", "Чек принтери басып чыгаруу катасын кайтарды.", "The receipt printer returned a printing error.", "Fiş yazıcısı bir baskı hatası döndürdü.", "Chek printeri chop etish xatosini qaytardi.")),

        (new Regex(@"^Receipt printed\.?$", RegexOptions.Compiled),
            _ => Tr.T("Чек напечатан.", "Чек басылып чыкты.", "Receipt printed.", "Fiş yazdırıldı.", "Chek chop etildi.")),

        (new Regex(@"^Permission denied: (?<perm>\S+)$", RegexOptions.Compiled),
            m => Tr.T(
                $"Недостаточно прав для действия ({m.Groups["perm"].Value}).",
                $"Аракет үчүн укук жетишсиз ({m.Groups["perm"].Value}).")),

        (new Regex(@"^Нет связи с сервером\.?$", RegexOptions.Compiled),
            _ => Tr.T("Нет связи с сервером.", "Сервер менен байланыш жок.", "No connection to the server.", "Sunucuyla bağlantı yok.", "Server bilan aloqa yo'q.")),

        (new Regex(@"^Связь с сервером восстановлена\.?$", RegexOptions.Compiled),
            _ => Tr.T("Связь с сервером восстановлена.", "Сервер менен байланыш калыбына келди.", "Connection to the server restored.", "Sunucu bağlantısı yeniden kuruldu.", "Server bilan aloqa tiklandi.")),
    };

    private static string TranslateMessage(string message)
    {
        var stripped = LeadingCategoryBracketRegex.Replace(message, "");
        foreach (var (pattern, translate) in MessageTranslations)
        {
            var match = pattern.Match(stripped);
            if (match.Success)
                return translate(match);
        }

        return stripped;
    }

    public LogsAndErrorsWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e) => LoadRows();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => LoadRows();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось открыть папку с логами: {ex}", "WARNING");
        }
    }

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NurMarketKassa",
        "Logs");

    private void LoadRows()
    {
        var rows = new List<RowVm>();

        foreach (var fileName in new[] { "nurmarket-kassa.log.1", "nurmarket-kassa.log" })
        {
            var path = Path.Combine(LogDirectory, fileName);
            if (!File.Exists(path))
                continue;

            IEnumerable<string> lines;
            try
            {
                // FileShare.ReadWrite: файл открыт на дозапись активным логгером приложения.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                continue;
            }

            string? currentDate = null;
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                var dateMatch = DatePrefixRegex.Match(line);
                if (dateMatch.Success)
                    currentDate = dateMatch.Groups["date"].Value;

                var match = LineRegex.Match(line);
                if (!match.Success || currentDate is null)
                    continue;

                var level = match.Groups["level"].Value;
                if (!IsWarningOrAbove(level))
                    continue;

                var message = CategoryPrefixRegex.Replace(match.Groups["rest"].Value, "");
                var isDeferred = message.Contains("[Отложено кассиром]", StringComparison.Ordinal);

                rows.Add(new RowVm
                {
                    DateText = FormatDate(currentDate),
                    TimeText = match.Groups["time"].Value,
                    LevelText = LevelDisplayText(level, isDeferred),
                    IsWarning = string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) && !isDeferred,
                    IsError = (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(level, "Critical", StringComparison.OrdinalIgnoreCase)) && !isDeferred,
                    IsDeferred = isDeferred,
                    Message = TranslateMessage(message),
                    SortKey = $"{currentDate} {match.Groups["time"].Value}",
                });
            }
        }

        LogGrid.ItemsSource = rows
            .OrderByDescending(r => r.SortKey)
            .Take(500)
            .ToList();
        EmptyText.IsVisible = rows.Count == 0;
    }

    private static string FormatDate(string isoDate) =>
        DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM", CultureInfo.InvariantCulture)
            : isoDate;

    private static bool IsWarningOrAbove(string level) =>
        string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase)
        || string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(level, "Critical", StringComparison.OrdinalIgnoreCase);

    private static string LevelDisplayText(string level, bool isDeferred)
    {
        if (isDeferred)
            return Tr.T("Отложено кассиром", "Кассир кийинкиге калтырды", "Held by cashier", "Kasiyer tarafından beklemeye alındı", "Kassir tomonidan kutishga qo'yildi");

        return level switch
        {
            "Critical" => Tr.T("Критическая ошибка", "Олуттуу ката", "Critical error", "Kritik hata", "Kritik xato"),
            "Error" => Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
            "Warning" => Tr.T("Предупреждение", "Эскертүү", "Warning", "Uyarı", "Ogohlantirish"),
            _ => level,
        };
    }

    private sealed class RowVm
    {
        public string DateText { get; set; } = "";
        public string TimeText { get; set; } = "";
        public string LevelText { get; set; } = "";
        public bool IsWarning { get; set; }
        public bool IsError { get; set; }
        public bool IsDeferred { get; set; }
        public string Message { get; set; } = "";
        public string SortKey { get; set; } = "";
    }
}
