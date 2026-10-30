using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Логи и ошибки» (этап 3 бэклога «Доработки») — читает существующий текстовый лог-файл
/// (см. SafeFileLoggerProvider, NurMarketKassa.Infrastructure) напрямую с диска и показывает
/// только Warning/Error/Critical записи, отфильтровывая обычный информационный шум (синхронизация
/// каталога и т.п.) — цель экрана в том, чтобы владелец магазина/поддержка NurMarket быстро
/// поняли, что пошло не так, а не читали полный технический журнал.
/// </summary>
public partial class LogsAndErrorsWindow : Window, IOwnerSection
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
                $"Жазылуу мөөнөтү бүттү (аяктоо күнү: {m.Groups["date"].Value}, {-int.Parse(m.Groups["days"].Value)} күн өттү).", $"Subscription has expired (end date: {m.Groups["date"].Value}, days overdue: {-int.Parse(m.Groups["days"].Value)}).", $"Aboneliğin süresi doldu (bitiş tarihi: {m.Groups["date"].Value}, geciken gün: {-int.Parse(m.Groups["days"].Value)}).", $"Obuna muddati tugagan (tugash sanasi: {m.Groups["date"].Value}, kechikkan kunlar: {-int.Parse(m.Groups["days"].Value)}).")),

        (new Regex(@"^Subscription near expiry: end_date=(?<date>\S+), daysRemaining=(?<days>\d+)$", RegexOptions.Compiled),
            m => Tr.T(
                $"Подписка скоро истекает (дата окончания: {m.Groups["date"].Value}, осталось дней: {m.Groups["days"].Value}).",
                $"Жазылуу мөөнөтү жакында бүтөт (аяктоо күнү: {m.Groups["date"].Value}, {m.Groups["days"].Value} күн калды).", $"Subscription expires soon (end date: {m.Groups["date"].Value}, days left: {m.Groups["days"].Value}).", $"Aboneliğin süresi yakında doluyor (bitiş tarihi: {m.Groups["date"].Value}, kalan gün: {m.Groups["days"].Value}).", $"Obuna muddati tez orada tugaydi (tugash sanasi: {m.Groups["date"].Value}, qolgan kunlar: {m.Groups["days"].Value}).")),

        (new Regex(@"^Subscription expired during active session.*$", RegexOptions.Compiled),
            _ => Tr.T(
                "Подписка истекла во время работы — чек сохранён автоматически, выполнен выход из кассы.",
                "Жазылуу мөөнөтү иштеп жатканда бүттү — чек автоматтык түрдө сакталды, кассадан чыгуу аткарылды.", "The subscription expired during work — the receipt was saved automatically and you were signed out of the till.", "Çalışma sırasında aboneliğin süresi doldu — fiş otomatik olarak kaydedildi, kasadan çıkış yapıldı.", "Ish vaqtida obuna muddati tugadi — chek avtomatik saqlandi, kassadan chiqildi.")),

        (new Regex(@"^Receipt printer returned failure\.?$", RegexOptions.Compiled),
            _ => Tr.T("Чековый принтер вернул ошибку печати.", "Чек принтери басып чыгарууда ката берди.", "The receipt printer returned a printing error.", "Fiş yazıcısı yazdırma hatası verdi.", "Chek printeri chop etish xatosini qaytardi.")),

        (new Regex(@"^Receipt printed\.?$", RegexOptions.Compiled),
            _ => Tr.T("Чек напечатан.", "Чек басылып чыкты.", "Receipt printed.", "Fiş yazdırıldı.", "Chek chop etildi.")),

        (new Regex(@"^Permission denied: (?<perm>\S+)$", RegexOptions.Compiled),
            m => Tr.T(
                $"Недостаточно прав для действия ({m.Groups["perm"].Value}).",
                $"Аракет үчүн укук жетишсиз ({m.Groups["perm"].Value}).", $"Insufficient permissions for this action ({m.Groups["perm"].Value}).", $"Bu işlem için yetkiniz yetersiz ({m.Groups["perm"].Value}).", $"Bu amal uchun huquq yetarli emas ({m.Groups["perm"].Value}).")),

        // 2026-10-31 (живой обход, В-9): технические английские строки журнала — человеческие подписи; исходный текст остаётся в подсказке («Подробнее»).
        (new Regex(@"^(?:Owner app:\s*)?permission denied:\s*(?<perm>\S+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            m => Tr.T(
                $"Не хватило прав для действия ({m.Groups["perm"].Value}).",
                $"Аракет үчүн укук жетишпеди ({m.Groups["perm"].Value}).", $"Not enough rights for the action ({m.Groups["perm"].Value}).", $"İşlem için yetki yetmedi ({m.Groups["perm"].Value}).", $"Amal uchun huquq yetmadi ({m.Groups["perm"].Value}).")),

        (new Regex(@"^Unobserved task exception\b.*$", RegexOptions.Compiled | RegexOptions.Singleline),
            _ => Tr.T("Фоновая операция завершилась с ошибкой — на работу кассы это не повлияло. Подробности — в подсказке.",
                "Фондук аракет ката менен аяктады — кассанын ишине таасир эткен жок. Кеңири маалымат — кеңеште.",
                "A background task ended with an error — it did not affect the till. Details are in the tooltip.",
                "Arka plan işlemi hatayla bitti — kasanın çalışmasını etkilemedi. Ayrıntılar ipucunda.",
                "Fon amali xato bilan tugadi — kassa ishiga ta'sir qilmadi. Tafsilotlar maslahatda.")),

        (new Regex(@"^Product categories/brands load failed\b.*$", RegexOptions.Compiled | RegexOptions.Singleline),
            _ => Tr.T("Не удалось загрузить категории и бренды товаров — сервер NurCRM не ответил.",
                "Товар категорияларын жана бренддерин жүктөө мүмкүн болгон жок — NurCRM сервери жооп берген жок.",
                "Could not load product categories and brands — the NurCRM server did not respond.",
                "Ürün kategorileri ve markaları yüklenemedi — NurCRM sunucusu yanıt vermedi.",
                "Mahsulot kategoriyalari va brendlarini yuklab bo'lmadi — NurCRM serveri javob bermadi.")),

        (new Regex(@"^Нет связи с сервером\.?$", RegexOptions.Compiled),
            _ => Tr.T("Нет связи с сервером.", "Сервер менен байланыш жок.", "No connection to the server.", "Sunucuyla bağlantı yok.", "Server bilan aloqa yo'q.")),

        (new Regex(@"^Связь с сервером восстановлена\.?$", RegexOptions.Compiled),
            _ => Tr.T("Связь с сервером восстановлена.", "Сервер менен байланыш калыбына келди.", "Connection to the server restored.", "Sunucu bağlantısı yeniden kuruldu.", "Server bilan aloqa tiklandi.")),
    };

    // 2026-10-27, аудит владельца Н-7: владельцу показывались названия .NET-исключений («TaskCanceledException», «HttpRequestException») —
    // заменяем их понятными словами; сам журнал на диске не меняется.
    private static readonly Regex TechnicalNameRegex = new(@"\b(TaskCanceledException|OperationCanceledException|HttpRequestException|ApiException)\b", RegexOptions.Compiled);

    private static string PlainWords(string text) => TechnicalNameRegex.Replace(text, m => m.Value switch
    {
        "HttpRequestException" => Tr.T("нет связи с сервером", "сервер менен байланыш жок", "no connection to the server", "sunucuyla bağlantı yok", "server bilan aloqa yo'q"),
        "ApiException" => Tr.T("сервер NurCRM вернул ошибку", "NurCRM сервери ката кайтарды", "the NurCRM server returned an error", "NurCRM sunucusu hata döndürdü", "NurCRM serveri xato qaytardi"),
        _ => Tr.T("сервер не ответил вовремя", "сервер убагында жооп берген жок", "the server did not respond in time", "sunucu zamanında yanıt vermedi", "server o'z vaqtida javob bermadi"),
    });

    private static string TranslateMessage(string message)
    {
        var stripped = LeadingCategoryBracketRegex.Replace(message, "");
        foreach (var (pattern, translate) in MessageTranslations)
        {
            var match = pattern.Match(stripped);
            if (match.Success)
                return PlainWords(translate(match));
        }

        return PlainWords(stripped);
    }

    public LogsAndErrorsWindow()
    {
        InitializeComponent();
        // 2026-10-20 (живой аудит кассы, К18): в кассе Esc закрывает окно (раньше не закрывал). В программе владельца окно —
        // раздел оболочки, там Esc не трогаем.
        if (!AppMode.IsOwner)
        {
            EscapeKey.Attach(this);
            ApplyKassaShell();
        }
    }

    /// <summary>2026-10-22 (аудит новой сборки, П2): в кассе окно было в системной рамке Windows с обрезанными заголовками
    /// («Врем», «Дата»). Теперь — общая оболочка ModalShell: шапка и кнопки закреплены, таблица прокручивается сама. В программе
    /// владельца окно — раздел оболочки (<see cref="AsOwnerSection"/>), там ничего не меняется.</summary>
    private void ApplyKassaShell()
    {
        SystemDecorations = Avalonia.Controls.SystemDecorations.None;
        CanResize = false;
        Width = 1112;
        Height = 680;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();

        var footer = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        foreach (var button in new[] { RefreshButton, OpenFolderButton, SendSupportButton, CloseButton })
        {
            ((Panel)button.Parent!).Children.Remove(button);
            button.Classes.Clear();
            button.Classes.Add("modal-cancel");
            button.Height = double.NaN;
            button.Margin = new Avalonia.Thickness(0);
            button.MaxWidth = 240;
            footer.Children.Add(button);
        }
        CloseButton.IsVisible = true;
        HeaderGrid.IsVisible = false;
        SubtitleText.IsVisible = false;
        RootGrid.Margin = new Avalonia.Thickness(0);
        RootGrid.Background = null;
        RootGrid.Children.Remove(HeaderGrid);
        RootGrid.Children.Remove(SubtitleText);
        RootGrid.RowDefinitions = new RowDefinitions("*");
        Grid.SetRow(LogGrid.Parent as Control ?? LogGrid, 0);

        var shell = new Dialogs.ModalShell
        {
            Margin = new Avalonia.Thickness(16),
            ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Footer = footer,
        };
        shell[!Dialogs.ModalShell.TitleProperty] = this.GetResourceObservable("csh.menu.errorLogs").ToBinding();
        shell[!Dialogs.ModalShell.SubtitleProperty] = this.GetResourceObservable("logs.subtitle").ToBinding();
        Content = null;
        shell.Content = RootGrid;
        Content = shell;
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        SendSupportButton.Content = Tr.T("Отправить в поддержку", "Колдоого жөнөтүү", "Send to support", "Desteğe gönder", "Yordamga yuborish");
        LoadRows();
    }

    /// <summary>2026-10-01: журнал и отчёты о сбоях — в Telegram-бот поддержки с названием компании
    /// и логином (SupportLogService). Пока бот не подключён — подсказка отправить файлы вручную.</summary>
    private async void SendSupport_Click(object? sender, RoutedEventArgs e)
    {
        var title = Tr.T("Отправить в поддержку", "Колдоого жөнөтүү", "Send to support", "Desteğe gönder", "Yordamga yuborish");
        if (!SupportLogService.IsConfigured)
        {
            PosMessageBox.Show(this, Tr.T(
                "Бот поддержки ещё не подключён. Нажмите «Открыть папку» и отправьте файлы журнала в поддержку NurCRM вручную.",
                "Колдоо боту азырынча туташкан эмес. «Папканы ачуу» баскычын басып, журнал файлдарын NurCRM колдоосуна кол менен жөнөтүңүз.",
                "The support bot isn't connected yet. Click “Open folder” and send the log files to NurCRM support manually.",
                "Destek botu henüz bağlı değil. «Klasörü aç»a tıklayın ve günlük dosyalarını NurCRM desteğine elle gönderin.",
                "Yordam boti hali ulanmagan. «Papkani ochish» tugmasini bosing va jurnal fayllarini NurCRM yordamiga qo'lda yuboring."),
                title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        SendSupportButton.IsEnabled = false;
        try
        {
            var error = await SupportLogService.SendLogsAsync(null).ConfigureAwait(true);
            PosMessageBox.Show(this, error is null
                    ? Tr.T("Журнал отправлен в поддержку.", "Журнал колдоого жөнөтүлдү.", "The log was sent to support.", "Günlük desteğe gönderildi.", "Jurnal yordamga yuborildi.")
                    : Tr.T("Журнал не отправлен: ", "Журнал жөнөтүлгөн жок: ", "The log was not sent: ", "Günlük gönderilmedi: ", "Jurnal yuborilmadi: ") + error,
                title, System.Windows.MessageBoxButton.OK,
                error is null ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            SendSupportButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => LoadRows();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): название уже над
    /// разделом, «Закрыть» не нужна. Пояснение, что попадает в журнал, встаёт на место названия —
    /// слева в строке с «Обновить» и «Открыть папку», отдельная строка под ним не нужна.</summary>
    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        OpenFolderButton.Margin = new Avalonia.Thickness(0);

        RootGrid.Children.Remove(SubtitleText);
        Grid.SetRow(SubtitleText, 0);
        Grid.SetColumn(SubtitleText, 0);
        SubtitleText.Margin = new Avalonia.Thickness(0, 0, 16, 0);
        SubtitleText.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        HeaderGrid.Children.Add(SubtitleText);
        HeaderGrid.Margin = new Avalonia.Thickness(0, 0, 0, 12);
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

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
        NurMarketKassa.Services.AppMode.DataFolderName,
        "Logs");

    private bool _loadingRows;

    /// <summary>2026-10-30 (стресс-тест «Управление и Помощь»): журнал (два файла по ≤5 МБ) читался и разбирался на потоке интерфейса —
    /// окно открывалось с зависанием 0,3–1,5 с. Теперь разбор — в фоне, на экран строки попадают одним присвоением.</summary>
    private async void LoadRows()
    {
        if (_loadingRows)
            return;
        _loadingRows = true;
        try
        {
            var rows = await System.Threading.Tasks.Task.Run(BuildRows).ConfigureAwait(true);
            LogGrid.ItemsSource = rows
                .OrderByDescending(r => r.SortKey)
                .Take(500)
                .ToList();
            EmptyText.IsVisible = rows.Count == 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал ошибок не прочитан: {ex.Message}", "WARNING");
        }
        finally
        {
            _loadingRows = false;
        }
    }

    private List<RowVm> BuildRows()
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
                    Details = LeadingCategoryBracketRegex.Replace(message, ""), // 2026-10-31: исходный текст записи — в подсказке («Подробнее»)
                    SortKey = $"{currentDate} {match.Groups["time"].Value}",
                });
            }
        }

        return rows;
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
            return Tr.T("Отложено кассиром", "Кассир кийинкиге калтырды", "Deferred by the cashier", "Kasiyer tarafından ertelendi", "Kassir tomonidan kutishga qo'yildi");

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
        public string Details { get; set; } = "";
        public string SortKey { get; set; } = "";
    }
}
