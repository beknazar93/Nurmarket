using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q5): окно «Приходы в фоне» — список задач очереди
/// <see cref="BackgroundReceivingQueue"/>: откуда, время, поставщик, строк, сумма, статус (в очереди / отправляется / повтор через N с /
/// ждёт связь / оприходован / частично / ошибка) и кнопки «Повторить», «Вернуть строки в приёмку», «Подробнее», «Убрать из списка».
/// Не модальное: открыто сколько нужно, кассир работает дальше. Одно окно на программу (<see cref="ShowOrActivate"/>).
/// Строки списка создаются один раз и обновляются на месте (кнопки не пересоздаются под пальцем): полное обновление — по событию
/// очереди не чаще раза в 250 мс, секундный таймер двигает только «повтор через N с».</summary>
public sealed class BackgroundReceiptsWindow : Window
{
    private static BackgroundReceiptsWindow? _instance;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Открыть окно или вывести уже открытое на передний план.</summary>
    public static void ShowOrActivate(Window? owner)
    {
        if (_instance is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new BackgroundReceiptsWindow();
        _instance = window;
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }

    private sealed class Row
    {
        public string Id = "";
        public ReceivingJob Job = new();
        public Border Card = new();
        public TextBlock Head = new();
        public TextBlock Info = new();
        public TextBlock Status = new();
        public TextBlock Notice = new();
        public TextBlock Detail = new();
        public Button Retry = null!;
        public Button TakeBack = null!;
        public Button Details = null!;
        public Button Dismiss = null!;
        public string StatusBrush = "";
        public string NoticeText = "";
        public bool DetailsOpen;
    }

    private readonly Dictionary<string, Row> _rows = new();
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24) };
    private readonly TextBlock _closedNote = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ModalShell _shell;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshPending;
    private readonly Button _returnButton;

    private void UpdateReturnButton() => _returnButton.IsVisible = BackgroundReceiptsHost.CanReturnToProgram();

    private BackgroundReceiptsWindow()
    {
        Title = T("Приходы в фоне", "Фондогу кирүүлөр", "Background receivings", "Arka plandaki mal kabulleri", "Fondagi kirimlar");
        // Ширина по сетке окон — M = 672 (карточка 640 + поля под тень).
        Width = 672;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };

        _empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _closedNote.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(_closedNote);
        root.Children.Add(_list);
        root.Children.Add(_empty);

        var close = UiKit.Ghost(this, T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), isCancel: true);
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        // 2026-10-26 (приёмка, S-5): после «Подождать» при выходе главного окна уже нет — кнопка открывает программу заново.
        // Видна только пока нет окна кассы/владельца/входа (обновляется по секундному таймеру и при открытии).
        _returnButton = UiKit.Primary(this, T("Вернуться в программу", "Программага кайтуу", "Back to the program", "Programa dön", "Dasturga qaytish"));
        _returnButton.IsVisible = false;
        _returnButton.Click += async (_, _) =>
        {
            _returnButton.IsEnabled = false;
            try
            {
                await BackgroundReceiptsHost.ReturnToProgramAsync().ConfigureAwait(true);
            }
            finally
            {
                _returnButton.IsEnabled = true;
                UpdateReturnButton();
            }
        };
        footer.Children.Add(_returnButton);
        footer.Children.Add(close);
        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = Title,
            Subtitle = T("Приходы оформляются сами — окно можно закрыть", "Кирүүлөр өзү жасалат — терезени жабууга болот",
                "Receivings are processed automatically — you can close this window", "Mal kabuller kendiliğinden işlenir — pencereyi kapatabilirsiniz",
                "Kirimlar o'zi rasmiylashtiriladi — oynani yopishingiz mumkin"),
            Footer = footer,
            Content = root,
        };
        Content = _shell;

        Opened += (_, _) =>
        {
            BackgroundReceivingQueue.Instance.JobChanged += OnJobChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            _tick.Tick += (_, _) => { TickCountdown(); UpdateReturnButton(); };
            _tick.Start();
            UpdateReturnButton();
            Refresh();
        };
        Closed += (_, _) =>
        {
            BackgroundReceivingQueue.Instance.JobChanged -= OnJobChanged;
            Tr.LanguageChanged -= OnLanguageChanged;
            _tick.Stop();
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };
    }

    // ------------------------------------------------------------------ обновление

    private void OnJobChanged(ReceivingJob _) => Dispatcher.UIThread.Post(Schedule);

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        Title = T("Приходы в фоне", "Фондогу кирүүлөр", "Background receivings", "Arka plandaki mal kabulleri", "Fondagi kirimlar");
        _shell.Title = Title;
        _returnButton.Content = T("Вернуться в программу", "Программага кайтуу", "Back to the program", "Programa dön", "Dasturga qaytish");
        // Подписи кнопок строк зависят от языка — строки пересоздаются.
        _rows.Clear();
        _list.Children.Clear();
        Refresh();
    });

    private void Schedule()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        DispatcherTimer.RunOnce(() =>
        {
            _refreshPending = false;
            Refresh();
        }, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Только «повтор через N с» — без снимка очереди.</summary>
    private void TickCountdown()
    {
        foreach (var row in _rows.Values)
            if (row.Job.Status == ReceivingJobStatus.RetryWait && !row.Job.WaitingForServer)
                row.Status.Text = row.Job.StatusText;
    }

    private void Refresh()
    {
        if (!IsVisible)
            return;
        IReadOnlyList<ReceivingJob> jobs;
        try
        {
            jobs = BackgroundReceivingQueue.Instance.Snapshot(allCompanies: true);
            _closedNote.IsVisible = BackgroundReceivingQueue.Instance.IsClosed;
            _closedNote.Text = _closedNote.IsVisible ? BackgroundReceivingQueue.Instance.ClosedText : "";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Приходы в фоне»: не удалось прочитать очередь: {ex.Message}", "WARNING");
            return;
        }

        var ids = new HashSet<string>(jobs.Select(j => j.Id));
        foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _list.Children.Remove(_rows[gone].Card);
            _rows.Remove(gone);
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!_rows.TryGetValue(job.Id, out var row))
            {
                row = BuildRow(job.Id);
                _rows[job.Id] = row;
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row.Card);
            }
            else if (!ReferenceEquals(_list.Children.ElementAtOrDefault(i), row.Card))
            {
                _list.Children.Remove(row.Card);
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row.Card);
            }
            Apply(row, job);
        }

        _empty.Text = T("Приходов в фоне нет", "Фондо кирүүлөр жок", "No background receivings", "Arka planda mal kabul yok", "Fonda kirimlar yo'q");
        _empty.IsVisible = jobs.Count == 0;
    }

    // ------------------------------------------------------------------ строка списка

    private Row BuildRow(string id)
    {
        var row = new Row { Id = id };
        row.Head.FontSize = 15;
        row.Head.FontWeight = FontWeight.SemiBold;
        row.Head.TextWrapping = TextWrapping.Wrap;
        row.Head.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        row.Info.FontSize = 13;
        row.Info.TextWrapping = TextWrapping.Wrap;
        row.Info.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        row.Status.FontSize = 13;
        row.Status.FontWeight = FontWeight.SemiBold;
        row.Status.TextWrapping = TextWrapping.Wrap;
        row.Notice.FontSize = 13;
        row.Notice.TextWrapping = TextWrapping.Wrap;
        row.Notice.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushWarning"));
        row.Detail.FontSize = 12;
        row.Detail.TextWrapping = TextWrapping.Wrap;
        row.Detail.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        row.Retry = SmallButton(UiKit.Primary(this, T("Повторить", "Кайра аракет", "Retry", "Tekrar dene", "Qayta urinish")));
        row.TakeBack = SmallButton(UiKit.Ghost(this, T("Вернуть строки в приёмку", "Саптарды кабыл алууга кайтаруу", "Return lines to receiving", "Satırları mal kabule geri al", "Qatorlarni qabulga qaytarish")));
        row.Details = SmallButton(UiKit.Ghost(this, T("Подробнее", "Кененирээк", "Details", "Ayrıntılar", "Batafsil")));
        row.Dismiss = SmallButton(UiKit.Ghost(this, T("Убрать из списка", "Тизмеден алып салуу", "Remove from list", "Listeden kaldır", "Ro'yxatdan olib tashlash")));
        row.Retry.Click += (_, _) => OnRetry(row);
        row.TakeBack.Click += (_, _) => OnTakeBack(row);
        row.Details.Click += (_, _) => { row.DetailsOpen = !row.DetailsOpen; Apply(row, row.Job); };
        row.Dismiss.Click += async (_, _) => await OnDismissAsync(row).ConfigureAwait(true);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { row.Retry, row.TakeBack, row.Details, row.Dismiss })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(row.Head);
        body.Children.Add(row.Info);
        body.Children.Add(row.Status);
        body.Children.Add(row.Notice);
        body.Children.Add(row.Detail);
        body.Children.Add(new Border { Height = 4 });
        body.Children.Add(buttons);

        row.Card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 4), BorderThickness = new Thickness(1), Child = body };
        row.Card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushSurfaceSubtle"));
        row.Card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        return row;
    }

    private static Button SmallButton(Button b)
    {
        b.Height = 40;
        b.FontSize = 13;
        b.Padding = new Thickness(14, 0);
        return b;
    }

    private void Apply(Row row, ReceivingJob job)
    {
        row.Job = job;
        var today = DateTime.Today;
        var when = job.CreatedAt.Date == today ? job.CreatedAt.ToString("HH:mm") : job.CreatedAt.ToString("dd.MM HH:mm");
        row.Head.Text = job.SourceText + " · " + when + (job.Supplier is { Name.Length: > 0 } s ? " · " + s.Name : "");
        var n = job.LineCount;
        row.Info.Text = T($"{n} поз.", $"{n} сап", $"{n} items", $"{n} kalem", $"{n} ta pozitsiya") + " · " + MoneyText.Som(job.Total)
            + (job.Supplier is not null && !job.PaidNow ? " · " + T("в долг", "карызга", "on credit", "veresiye", "qarzga") : "");
        row.Status.Text = job.StatusText;
        var brush = job.ForeignCompany && !job.IsFinal ? "BrushTextSoft" : job.Status switch
        {
            ReceivingJobStatus.Done => "BrushSuccess",
            ReceivingJobStatus.Failed or ReceivingJobStatus.NeedsCheck => "BrushDanger",
            ReceivingJobStatus.Partial or ReceivingJobStatus.RetryWait => "BrushWarning",
            _ => "BrushText",
        };
        if (!string.Equals(row.StatusBrush, brush, StringComparison.Ordinal))
        {
            row.StatusBrush = brush;
            row.Status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        }

        // 2026-10-29: отказ сервера по существу (4xx с JSON) — «Повторить» не поможет, вместо кнопки подсказка «исправьте данные».
        var rejectedHint = !job.ForeignCompany && job.IsRejectedByServer
            ? T("Исправьте данные: вернуть строки в приёмку.", "Маалыматтарды оңдоңуз: саптарды кабыл алууга кайтарыңыз.",
                "Fix the data: return the lines to receiving.", "Verileri düzeltin: satırları mal kabule geri alın.", "Ma'lumotlarni tuzating: qatorlarni qabulga qaytaring.")
            : "";
        var noticeText = row.NoticeText.Length > 0 ? row.NoticeText : rejectedHint;
        row.Notice.Text = noticeText;
        row.Notice.IsVisible = noticeText.Length > 0;

        var hasDetails = job.HasWarnings || job.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Error));
        row.Details.IsVisible = hasDetails;
        row.Detail.IsVisible = hasDetails && row.DetailsOpen;
        if (row.Detail.IsVisible)
        {
            var parts = new List<string>(job.Errors);
            parts.AddRange(job.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Error)).Select(l => l.ProductName + ": " + l.Error));
            row.Detail.Text = string.Join("\n", parts.Select(p => ReceivingServerMessages.Humanize(p) ?? p).Distinct());
        }

        var own = !job.ForeignCompany;
        var needsAttention = job.Status is ReceivingJobStatus.Failed or ReceivingJobStatus.Partial or ReceivingJobStatus.NeedsCheck;
        row.Retry.IsVisible = own && needsAttention && !job.IsRejectedByServer; // 2026-10-29: «Повторить» — только для сети/5xx
        row.TakeBack.IsVisible = own && needsAttention
            && job.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Rejected or ReceivingLineOutcome.Pending);
        row.Dismiss.IsVisible = own && job.IsFinal;
        // 2026-10-26 (ТЗ «Склад — массовое добавление», М4): строки пачки «Массовое добавление» возвращаются во вкладку «Массовое добавление».
        row.TakeBack.Content = string.Equals(job.Source, ReceivingJobSource.Bulk, StringComparison.Ordinal)
            ? T("Вернуть строки в массовое добавление", "Саптарды массалык кошууга кайтаруу", "Return lines to bulk add", "Satırları toplu eklemeye geri al", "Qatorlarni ommaviy qo'shishga qaytarish")
            : T("Вернуть строки в приёмку", "Саптарды кабыл алууга кайтаруу", "Return lines to receiving", "Satırları mal kabule geri al", "Qatorlarni qabulga qaytarish");
    }

    private void SetNotice(Row row, string text)
    {
        row.NoticeText = text;
        Apply(row, row.Job);
    }

    // ------------------------------------------------------------------ действия

    private void OnRetry(Row row)
    {
        try
        {
            row.NoticeText = "";
            if (!BackgroundReceivingQueue.Instance.Retry(row.Id))
                SetNotice(row, T("Повторять нечего: приход уже проведён. Верните оставшиеся строки в приёмку или уберите задачу.",
                    "Кайталоого эч нерсе жок: кирүү өткөрүлгөн. Калган саптарды кабыл алууга кайтарыңыз же тапшырманы алып салыңыз.",
                    "Nothing to retry: the receiving is already posted. Return the remaining lines to receiving or remove the task.",
                    "Tekrar edilecek bir şey yok: mal kabul zaten işlendi. Kalan satırları mal kabule geri alın veya görevi kaldırın.",
                    "Qayta urinadigan narsa yo'q: kirim allaqachon o'tkazilgan. Qolgan qatorlarni qabulga qaytaring yoki vazifani olib tashlang."));
            else
                Refresh();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: «Повторить» не удалось: {ex.Message}", "WARNING");
            SetNotice(row, ex.Message);
        }
    }

    private void OnTakeBack(Row row)
    {
        try
        {
            row.NoticeText = "";
            // Окно приёмки готовим ДО того, как строки уйдут из очереди: не получилось — строки остаются в задаче.
            var warehouse = FindOrOpenWarehouse();
            if (warehouse?.DataContext is not WarehouseViewModel vm)
            {
                SetNotice(row, T("Не удалось открыть «Склад → Приёмка». Откройте склад вручную и повторите.",
                    "«Кампа → Кабыл алуу» ачылган жок. Кампаны өзүңүз ачып, кайра аракет кылыңыз.",
                    "Could not open Warehouse → Receiving. Open the warehouse manually and try again.",
                    "Depo → Mal kabul açılamadı. Depoyu elle açıp tekrar deneyin.",
                    "Ombor → Qabul ochilmadi. Omborni qo'lda ochib, qayta urining."));
                return;
            }

            var job = row.Job;
            var lines = BackgroundReceivingQueue.Instance.TakeBackLines(row.Id);
            if (lines.Count == 0)
            {
                SetNotice(row, T("Нечего возвращать: все строки уже проведены или требуют проверки остатка.",
                    "Кайтарууга эч нерсе жок: бардык саптар өткөрүлгөн же калдыкты текшерүү керек.",
                    "Nothing to return: all lines are already posted or need a manual stock check.",
                    "Geri alınacak bir şey yok: tüm satırlar işlendi veya stok kontrolü gerekiyor.",
                    "Qaytariladigan narsa yo'q: barcha qatorlar o'tkazilgan yoki qoldiqni tekshirish kerak."));
                Refresh();
                return;
            }

            // 2026-10-26: возврат строк пачки «Массовое добавление» в колонки вкладки — пакет М4 (экран трёх колонок), в 1.17.61 его нет:
            // строки возвращаются в «Приёмку», строки уже сняты с задачи, терять их нельзя.

            if (!vm.HasReceivingSupplier && job.Supplier is not null)
            {
                vm.SelectOrAddReceivingSupplier(job.Supplier.ToSupplier());
                vm.ReceivingPaidNow = job.PaidNow;
            }
            foreach (var line in lines)
                vm.ReceivingLines.Insert(0, line);

            try
            {
                var tabs = warehouse.FindControl<TabControl>("WarehouseTabs");
                if (tabs is not null && warehouse.FindControl<TabItem>("ReceivingTabItem") is { } tab)
                    tabs.SelectedItem = tab;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приходы в фоне: вкладка «Приёмка» не выбрана: {ex.Message}", "WARNING");
            }
            warehouse.Activate();
            PosLogger.Log($"Приходы в фоне: строки задачи {row.Id} возвращены в приёмку ({lines.Count}).", "STOCK");
            Refresh();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: «Вернуть строки» не удалось: {ex}", "WARNING");
            SetNotice(row, ex.Message);
        }
    }

    private async Task OnDismissAsync(Row row)
    {
        try
        {
            var job = row.Job;
            if (job.Status != ReceivingJobStatus.Done)
            {
                var needsCheck = job.Status == ReceivingJobStatus.NeedsCheck;
                var message = needsCheck
                    ? T("Приход мог пройти на сервере. Уберите задачу, только если вы проверили остаток вручную.",
                        "Кирүү серверде өтүп кетиши мүмкүн. Калдыкты өзүңүз текшергенде гана тапшырманы алып салыңыз.",
                        "The receiving may have gone through on the server. Remove the task only if you have checked the stock manually.",
                        "Mal kabul sunucuda işlenmiş olabilir. Görevi yalnızca stoğu elle kontrol ettiyseniz kaldırın.",
                        "Kirim serverda o'tgan bo'lishi mumkin. Vazifani faqat qoldiqni qo'lda tekshirgan bo'lsangiz olib tashlang.")
                    : T("Убрать задачу из списка? Строки, которые не вошли в приход, пропадут — их можно сначала вернуть в приёмку.",
                        "Тапшырманы тизмеден алып салабызбы? Кирүүгө кирбеген саптар жоголот — аларды адегенде кабыл алууга кайтарсаңыз болот.",
                        "Remove the task from the list? Lines that were not received will be lost — you can return them to receiving first.",
                        "Görev listeden kaldırılsın mı? Mal kabule girmeyen satırlar kaybolur — önce mal kabule geri alabilirsiniz.",
                        "Vazifa ro'yxatdan olib tashlansinmi? Kirimga kirmagan qatorlar yo'qoladi — avval ularni qabulga qaytarishingiz mumkin.");
                var ok = await PosConfirmDialog.ShowModalAsync(this,
                    T("Убрать из списка", "Тизмеден алып салуу", "Remove from list", "Listeden kaldır", "Ro'yxatdan olib tashlash"),
                    message,
                    needsCheck ? T("Я проверил остаток вручную", "Калдыкты өзүм текшердим", "I checked the stock manually", "Stoğu elle kontrol ettim", "Qoldiqni qo'lda tekshirdim")
                               : T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"),
                    T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                    PosConfirmAccent.Danger).ConfigureAwait(true);
                if (!ok)
                    return;
            }
            BackgroundReceivingQueue.Instance.Dismiss(row.Id);
            Refresh();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: «Убрать из списка» не удалось: {ex.Message}", "WARNING");
            SetNotice(row, ex.Message);
        }
    }

    /// <summary>Открытое окно склада или новое (Склад → Приёмка). Окно приёмки лежит в общем списке окон программы.</summary>
    private static WarehouseWindow? FindOrOpenWarehouse()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var open = desktop?.Windows.OfType<WarehouseWindow>().FirstOrDefault(w => w.IsVisible);
        if (open is not null)
            return open;
        var window = App.GetRequiredService<WarehouseWindow>();
        var owner = desktop?.MainWindow is { IsVisible: true } main ? main : null;
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
        return window;
    }
}
