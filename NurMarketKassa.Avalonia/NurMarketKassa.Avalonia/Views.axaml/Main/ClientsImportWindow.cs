using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (Л-10, «Новичок: перенос данных»): перенос клиентов из чужой программы (Excel/CSV или таблица из буфера).
/// Клиенты заводятся ТОЛЬКО существующим запросом «Новый клиент» (IClientsApiService.CreateClientAsync); телефон, уже занятый клиентом NurCRM или повторенный в файле,
/// пропускается. Долги из файла НЕ записываются (безопасного способа завести начальный долг в API нет — вопрос Умару, docs/Вопросы Умару 13.11.md):
/// окно показывает сумму долгов, чтобы владелец мог внести их вручную. Результат ShowAsync: сколько клиентов заведено.</summary>
public sealed class ClientsImportWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _debtNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ScrollViewer _scroll;
    private readonly Button _pick, _paste, _go;
    private ClientsImportPlan? _plan;
    private bool _busy;
    private CancellationTokenSource? _cts;

    /// <summary>Сколько клиентов заведено за время работы окна.</summary>
    public int Created { get; private set; }

    private IClientsApiService? _api;
    private IClientsApiService Api => _api ??= App.GetRequiredService<IClientsApiService>();

    /// <summary>Для стенда проверки: подменить службу клиентов.</summary>
    internal void UseApi(IClientsApiService api) => _api = api;

    public static async Task<int> ShowAsync(Window? owner)
    {
        var window = new ClientsImportWindow();
        if (owner is null)
        {
            window.Show();
            return 0;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Created;
    }

    /// <summary>2026-11-20: открыть окно сразу с таблицей клиентов, найденной в папке программы (мастер загрузки товаров, «Автоподтяжка базы»).</summary>
    public static async Task<int> ShowAsync(Window? owner, ProductFileTable preloaded)
    {
        var window = new ClientsImportWindow();
        window.Opened += async (_, _) => await window.ShowResultAsync(new ProductFileReadResult { Table = preloaded }).ConfigureAwait(true);
        if (owner is null)
        {
            window.Show();
            return 0;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Created;
    }

    public ClientsImportWindow()
    {
        Title = T("Перенос клиентов", "Кардарларды которуу", "Import customers", "Müşteri aktarımı", "Mijozlarni ko'chirish");
        SectionCards.AsModal(this, 900);
        MinWidth = 560;

        _pick = SectionCards.Secondary(T("Выбрать файл…", "Файлды тандоо…", "Choose file…", "Dosya seç…", "Faylni tanlash…"));
        _paste = SectionCards.Secondary(T("Вставить из буфера", "Буферден коюу", "Paste from clipboard", "Panodan yapıştır", "Buferdan qo'yish"));
        foreach (var b in new[] { _pick, _paste })
        {
            b.MinHeight = 44;
            b.Margin = new Thickness(0, 0, 8, 6);
        }
        _pick.Click += async (_, _) => await GuardAsync(PickAsync).ConfigureAwait(true);
        _paste.Click += async (_, _) => await GuardAsync(PasteAsync).ConfigureAwait(true);

        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushText"));
        _debtNote.Bind(TextBlock.ForegroundProperty, _debtNote.GetResourceObservable("BrushTextSoft"));
        _status.Text = T("Выберите файл Excel или CSV со списком клиентов — или вставьте таблицу из буфера. Нужны столбцы «Имя» и «Телефон»; «Адрес» и «Долг» — по желанию.",
            "Кардарлардын тизмеси бар Excel же CSV файлын тандаңыз — же буфердеги таблицаны коюңуз. «Аты» жана «Телефон» мамычалары керек; «Дарек» жана «Карыз» — кааласаңыз.",
            "Choose an Excel or CSV file with the customer list — or paste a table from the clipboard. The “Name” and “Phone” columns are required; “Address” and “Debt” are optional.",
            "Müşteri listesi içeren bir Excel veya CSV dosyası seçin — ya da panodan bir tablo yapıştırın. «Ad» ve «Telefon» sütunları gerekli; «Adres» ve «Borç» isteğe bağlı.",
            "Mijozlar ro'yxati bor Excel yoki CSV faylni tanlang — yoki buferdan jadval qo'ying. «Ism» va «Telefon» ustunlari kerak; «Manzil» va «Qarz» — xohlasangiz.");

        _scroll = new ScrollViewer { Content = _rows, MaxHeight = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, IsVisible = false };

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        _go = SectionCards.Primary(T("Загрузить клиентов", "Кардарларды жүктөө", "Load customers", "Müşterileri yükle", "Mijozlarni yuklash"));
        _go.MinHeight = 44;
        _go.IsEnabled = false;
        _go.Click += async (_, _) => await GuardAsync(LoadAsync).ConfigureAwait(true);
        Closed += (_, _) => _cts?.Cancel();

        var top = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _pick, _paste } };
        Content = SectionCards.Shell(Title ?? "", T("Клиенты заводятся как кнопкой «Новый клиент». Телефон, который уже есть, пропускается.",
                "Кардарлар «Жаңы кардар» баскычындай эле түзүлөт. Бар телефон өткөрүлүп жиберилет.",
                "Customers are created just like the “New customer” button. A phone that already exists is skipped.",
                "Müşteriler «Yeni müşteri» düğmesi gibi oluşturulur. Zaten var olan telefon atlanır.",
                "Mijozlar «Yangi mijoz» tugmasi kabi yaratiladi. Allaqachon bor telefon o'tkazib yuboriladi."),
            new StackPanel { Spacing = 4, Children = { top, _status, _debtNote, _scroll } }, SectionCards.Buttons(close, _go));
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перенос клиентов: {ex.GetType().Name}: {ex.Message}", "ERROR");
            _status.Text = T($"Не удалось выполнить действие: {ex.GetBaseException().Message}", $"Аракетти аткаруу мүмкүн болгон жок: {ex.GetBaseException().Message}",
                $"Could not complete the action: {ex.GetBaseException().Message}", $"İşlem tamamlanamadı: {ex.GetBaseException().Message}", $"Amalni bajarib bo'lmadi: {ex.GetBaseException().Message}");
            _busy = false;
        }
    }

    private async Task PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Выберите файл с клиентами", "Кардарлары бар файлды тандаңыз", "Choose a file with customers", "Müşterilerin bulunduğu dosyayı seçin", "Mijozlar bor faylni tanlang"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Excel / CSV") { Patterns = ["*.xlsx", "*.csv", "*.txt"] }],
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            // Android: content:// без пути — копия во временный файл (как в мастере товаров).
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-import");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, string.Concat((files[0].Name ?? "clients.csv").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
            await using var input = await files[0].OpenReadAsync().ConfigureAwait(true);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output).ConfigureAwait(true);
        }
        await LoadFileAsync(path).ConfigureAwait(true);
    }

    private async Task PasteAsync()
    {
        string? text = null;
        try
        {
            if (Clipboard is { } clip)
                text = await clip.GetTextAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перенос клиентов: буфер обмена не прочитан: {ex.Message}", "WARNING");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = T("В буфере обмена нет таблицы. Скопируйте ячейки в Excel (Ctrl+C) и нажмите «Вставить из буфера».",
                "Буферде таблица жок. Excel'де уячейкаларды көчүрүп (Ctrl+C), «Буферден коюу» басыңыз.",
                "There is no table in the clipboard. Copy the cells in Excel (Ctrl+C) and press “Paste from clipboard”.",
                "Panoda tablo yok. Excel'de hücreleri kopyalayın (Ctrl+C) ve «Panodan yapıştır»a basın.",
                "Buferda jadval yo'q. Excel'da kataklarni nusxalang (Ctrl+C) va «Buferdan qo'yish»ni bosing.");
            return;
        }
        await ShowResultAsync(ProductFileReader.ReadClipboard(text)).ConfigureAwait(true);
    }

    /// <summary>Прочитать файл и показать итог (стенд зовёт напрямую).</summary>
    public async Task LoadFileAsync(string path)
    {
        _status.Text = T("Читаю файл…", "Файл окулууда…", "Reading the file…", "Dosya okunuyor…", "Fayl o'qilmoqda…");
        var result = await Task.Run(() => ProductFileReader.ReadFile(path)).ConfigureAwait(true);
        await ShowResultAsync(result).ConfigureAwait(true);
    }

    private async Task ShowResultAsync(ProductFileReadResult result)
    {
        _plan = null;
        _go.IsEnabled = false;
        _rows.Children.Clear();
        _scroll.IsVisible = false;
        _debtNote.IsVisible = false;
        if (!result.Ok || result.Table is null)
        {
            _status.Text = result.Error ?? "";
            return;
        }

        // Телефоны клиентов, уже заведённых в NurCRM, — чтобы не заводить второй раз. Нет связи — не грузим (иначе возможны дубли).
        HashSet<string> existing;
        _status.Text = T("Сверяю с клиентами NurCRM…", "NurCRM кардарлары менен салыштырам…", "Checking against NurCRM customers…", "NurCRM müşterileriyle karşılaştırılıyor…", "NurCRM mijozlari bilan solishtirilmoqda…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            existing = ClientsImportPlan.ExistingPhones(await Api.GetClientsAsync(null, cts.Token).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перенос клиентов: список клиентов не получен: {ex.GetType().Name}: {ex.Message}", "WARNING");
            _status.Text = T("Нет связи с NurCRM: без списка ваших клиентов нельзя исключить повторы. Проверьте интернет и выберите файл ещё раз.",
                "NurCRM менен байланыш жок: кардарлар тизмесисиз кайталанууну болтурбай болбойт. Интернетти текшерип, файлды кайра тандаңыз.",
                "No connection to NurCRM: without your customer list repeats cannot be ruled out. Check the internet and choose the file again.",
                "NurCRM bağlantısı yok: müşteri listeniz olmadan tekrarlar elenemez. İnterneti kontrol edip dosyayı yeniden seçin.",
                "NurCRM bilan aloqa yo'q: mijozlar ro'yxatisiz takrorlarni istisno qilib bo'lmaydi. Internetni tekshirib, faylni qayta tanlang.");
            return;
        }

        var plan = ClientsImportPlan.Build(result.Table, null, existing);
        _plan = plan;
        if (plan.Problem is { } problem)
        {
            _status.Text = problem;
            return;
        }
        Render(plan);
    }

    private void Render(ClientsImportPlan plan)
    {
        _rows.Children.Clear();
        foreach (var r in plan.Rows.Take(300))
        {
            var state = r.Problem ?? (r.Done ? T("заведён", "түзүлдү", "created", "oluşturuldu", "yaratildi") : r.AlreadyExists ? T("уже есть — пропущу", "бар — өткөрөм", "exists — will skip", "var — atlanacak", "bor — o'tkazaman") : "✓");
            var line = new TextBlock
            {
                FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2),
                Text = $"{r.FileRow}. {r.Name} · {r.Phone}" + (r.Address.Length > 0 ? " · " + r.Address : "") + (r.Debt > 0 ? $" · {T("долг", "карыз", "debt", "borç", "qarz")} {r.Debt:0.##}" : "") + " — " + state,
            };
            line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable(r.Problem is not null ? "BrushDanger" : r.AlreadyExists || r.Done ? "BrushTextSoft" : "BrushText"));
            _rows.Children.Add(line);
        }
        _scroll.IsVisible = plan.Rows.Count > 0;
        _status.Text = T($"Строк: {plan.Rows.Count}. Будет заведено: {plan.ReadyCount}, уже есть: {plan.ExistsCount}, с ошибкой: {plan.ErrorCount}.",
            $"Саптар: {plan.Rows.Count}. Түзүлөт: {plan.ReadyCount}, бар: {plan.ExistsCount}, катасы бар: {plan.ErrorCount}.",
            $"Rows: {plan.Rows.Count}. To be created: {plan.ReadyCount}, already exist: {plan.ExistsCount}, with errors: {plan.ErrorCount}.",
            $"Satır: {plan.Rows.Count}. Oluşturulacak: {plan.ReadyCount}, zaten var: {plan.ExistsCount}, hatalı: {plan.ErrorCount}.",
            $"Qatorlar: {plan.Rows.Count}. Yaratiladi: {plan.ReadyCount}, allaqachon bor: {plan.ExistsCount}, xatoli: {plan.ErrorCount}.");
        if (plan.DebtorsCount > 0)
        {
            _debtNote.IsVisible = true;
            _debtNote.Text = T($"В файле {plan.DebtorsCount} должников на сумму {plan.DebtTotal:N0}. Долги в кассу НЕ переносятся (безопасного способа завести начальный долг пока нет — вопрос к разработчикам сервера). "
                    + "Клиенты будут заведены без долга; список долгов оставьте в файле и внесите вручную.",
                $"Файлда {plan.DebtorsCount} карызкор, суммасы {plan.DebtTotal:N0}. Карыздар кассага которулбайт (баштапкы карызды коопсуз түзүүнүн жолу азырынча жок — сервер иштеп чыгуучуларына суроо). "
                    + "Кардарлар карызсыз түзүлөт; карыздар тизмесин файлда калтырып, өзүңүз киргизиңиз.",
                $"The file has {plan.DebtorsCount} debtors totalling {plan.DebtTotal:N0}. Debts are NOT moved to the till (there is no safe way yet to create an opening debt — a question for the server developers). "
                    + "Customers are created without debt; keep the debt list in the file and enter it by hand.",
                $"Dosyada toplam {plan.DebtTotal:N0} tutarında {plan.DebtorsCount} borçlu var. Borçlar kasaya AKTARILMAZ (açılış borcu oluşturmanın güvenli bir yolu henüz yok — sunucu geliştiricilerine soru). "
                    + "Müşteriler borçsuz oluşturulur; borç listesini dosyada tutup elle girin.",
                $"Faylda jami {plan.DebtTotal:N0} summadagi {plan.DebtorsCount} qarzdor bor. Qarzlar kassaga KO'CHIRILMAYDI (boshlang'ich qarz yaratishning xavfsiz yo'li hali yo'q — server dasturchilariga savol). "
                    + "Mijozlar qarzsiz yaratiladi; qarzlar ro'yxatini faylda qoldirib, qo'lda kiriting.");
        }
        _go.IsEnabled = plan.ReadyCount > 0;
    }

    private async Task LoadAsync()
    {
        if (_plan is null || _busy || _plan.ReadyCount == 0)
            return;
        _busy = true;
        _go.IsEnabled = false;
        _pick.IsEnabled = _paste.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var total = _plan.ReadyCount;
        var progress = new Progress<int>(n => _status.Text = T($"Заношу клиентов: {n} из {total}…", $"Кардарлар киргизилүүдө: {n} / {total}…", $"Adding customers: {n} of {total}…",
            $"Müşteriler ekleniyor: {n} / {total}…", $"Mijozlar qo'shilmoqda: {n} / {total}…"));
        try
        {
            var (created, failed) = await ClientsImportPlan.RunAsync(_plan, Api, progress, _cts.Token).ConfigureAwait(true);
            Created += created;
            Render(_plan);
            _status.Text = T($"Готово: заведено {created}" + (failed > 0 ? $", не удалось {failed} (причина — в строке; можно повторить: уже заведённые пропустятся)" : "") + ".",
                $"Даяр: түзүлдү {created}" + (failed > 0 ? $", болгон жок {failed} (себеби — сапта; кайталаса болот: түзүлгөндөр өткөрүлөт)" : "") + ".",
                $"Done: created {created}" + (failed > 0 ? $", failed {failed} (the reason is in the row; you can retry: the created ones are skipped)" : "") + ".",
                $"Tamam: oluşturulan {created}" + (failed > 0 ? $", başarısız {failed} (neden satırda; tekrar denenebilir: oluşturulanlar atlanır)" : "") + ".",
                $"Tayyor: yaratildi {created}" + (failed > 0 ? $", bo'lmadi {failed} (sababi qatorda; qayta urinish mumkin: yaratilganlari o'tkaziladi)" : "") + ".")
                + " " + T("Дальше: «Клиенты» — проверьте список; продавайте в долг тем, кому доверяете.", "Андан ары: «Кардарлар» — тизмени текшериңиз; ишенген кардарларга карызга сатыңыз.",
                    "Next: “Customers” — check the list; sell on credit to those you trust.", "Sonra: «Müşteriler» — listeyi kontrol edin; güvendiklerinize veresiye satın.", "Keyin: «Mijozlar» — ro'yxatni tekshiring; ishongan mijozlarga qarzga soting.");
            _go.IsEnabled = _plan.ReadyCount > 0;
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("Остановлено.", "Токтотулду.", "Stopped.", "Durduruldu.", "To'xtatildi.");
        }
        finally
        {
            _busy = false;
            _pick.IsEnabled = _paste.IsEnabled = true;
        }
    }
}
