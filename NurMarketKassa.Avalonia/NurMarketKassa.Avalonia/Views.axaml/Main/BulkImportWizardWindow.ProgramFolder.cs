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
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-20 (просьба владельца «автоматическое подтягивание базы, если указать установленную папку программы, и с подсказкой, где по стандарту база»):
// часть мастера загрузки товаров на шаге «Файл»: «Указать папку программы…», «Найти на этом компьютере», путь вручную, подсказка «Обычно здесь: …».
// Вся работа с диском — в NurMarketKassa.Services.ProgramFolderImporter (только чтение, копия базы во временную папку); здесь — только окно.
// Прочитанная таблица идёт в тот же LoadResultAsync, что и выбранный файл, поэтому дальше всё как у обычного файла: «Далее» → «Проверьте».
public partial class BulkImportWizardWindow
{
    private readonly Border _folderPanel = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10) };
    private readonly TextBlock _folderNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _folderUsual = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _folderStatus = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _folderPath = new() { MinHeight = 36, MinWidth = 120 };
    private Button _folderPick = null!, _folderFind = null!, _folderOpen = null!, _folderPaste = null!, _folderRead = null!, _folderClients = null!;
    private StackPanel _folderUsualRow = null!;
    private Control _folderControls = null!;
    private bool _folderBusy;
    private ProductFileTable? _folderClientsTable;
    private string? _folderProgramId;
    private CancellationTokenSource? _folderCts;

    /// <summary>Панель на шаге «Файл»: на Android кнопок нет — только пояснение.</summary>
    private Control BuildFolderPanel()
    {
        var title = Caption(T("Или подтяните базу из папки программы", "Же программанын папкасынан базаны алыңыз", "Or pull the database from the program folder", "Veya veritabanını program klasöründen alın", "Yoki bazani dastur papkasidan oling"));
        Bind(_folderPanel, Border.BackgroundProperty, "BrushPanel");
        Bind(_folderPanel, Border.BorderBrushProperty, "BrushBorder");
        Bind(_folderNote, TextBlock.ForegroundProperty, "BrushTextSoft");
        Bind(_folderUsual, TextBlock.ForegroundProperty, "BrushTextSoft");
        Bind(_folderStatus, TextBlock.ForegroundProperty, "BrushText");

        if (OperatingSystem.IsAndroid())
        {
            _folderNote.Text = T("Чтение папок программ работает только на компьютере (Windows, Linux): на телефоне и планшете выгрузите файл из программы и выберите его кнопкой «Выбрать файл…».",
                "Программалардын папкаларын окуу компьютерде гана иштейт (Windows, Linux): телефондо жана планшетте файлды программадан чыгарып, «Файлды тандоо…» баскычы менен тандаңыз.",
                "Reading program folders works only on a computer (Windows, Linux): on a phone or tablet, export a file from the program and choose it with “Choose file…”.",
                "Program klasörlerini okuma yalnızca bilgisayarda çalışır (Windows, Linux): telefon ve tablette programdan dosya dışa aktarıp «Dosya seç…» ile seçin.",
                "Dastur papkalarini o'qish faqat kompyuterda ishlaydi (Windows, Linux): telefon va planshetda dasturdan fayl chiqarib, «Faylni tanlash…» bilan tanlang.");
            _folderPanel.Child = new StackPanel { Spacing = 4, Children = { title, _folderNote } };
            _folderControls = _folderPanel;
            return _folderPanel;
        }

        _folderPick = SectionCards.Secondary(T("Указать папку программы…", "Программанын папкасын көрсөтүү…", "Choose the program folder…", "Program klasörünü seç…", "Dastur papkasini ko'rsatish…"));
        _folderFind = SectionCards.Secondary(T("Найти на этом компьютере", "Бул компьютерден табуу", "Find on this computer", "Bu bilgisayarda bul", "Shu kompyuterdan topish"));
        _folderOpen = SectionCards.Ghost(T("Открыть папку", "Папканы ачуу", "Open the folder", "Klasörü aç", "Papkani ochish"));
        _folderPaste = SectionCards.Ghost(T("Вставить путь", "Жолду коюу", "Paste the path", "Yolu yapıştır", "Yo'lni qo'yish"));
        _folderRead = SectionCards.Secondary(T("Прочитать", "Окуу", "Read", "Oku", "O'qish"));
        _folderClients = SectionCards.Secondary("");
        _folderClients.IsVisible = false;
        foreach (var b in new[] { _folderPick, _folderFind, _folderRead })
            b.MinHeight = 36;
        foreach (var b in new[] { _folderOpen, _folderPaste, _folderClients })
            b.MinHeight = 36;
        _folderPath.Watermark = T("C:\\Программа\\Данные", "C:\\Программа\\Маалымат", "C:\\Program\\Data", "C:\\Program\\Veri", "C:\\Dastur\\Ma'lumot");

        _folderPick.Click += async (_, _) => await GuardAsync(PickFolderAsync, "выбор папки программы").ConfigureAwait(true);
        _folderFind.Click += async (_, _) => await GuardAsync(FindFolderAsync, "поиск папки программы").ConfigureAwait(true);
        _folderRead.Click += async (_, _) => await GuardAsync(() => ReadFolderAsync(_folderPath.Text ?? ""), "чтение папки программы").ConfigureAwait(true);
        _folderPath.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                await GuardAsync(() => ReadFolderAsync(_folderPath.Text ?? ""), "чтение папки программы").ConfigureAwait(true);
            }
        };
        _folderPaste.Click += async (_, _) => await GuardAsync(async () =>
        {
            var text = Clipboard is { } clip ? await clip.GetTextAsync().ConfigureAwait(true) : null;
            if (!string.IsNullOrWhiteSpace(text))
                _folderPath.Text = text.Trim().Trim('"');
        }, "вставка пути").ConfigureAwait(true);
        _folderOpen.Click += (_, _) =>
        {
            var dir = ProgramFolderHints.For(_folderProgramId)?.ExistingStandardFolders().FirstOrDefault() ?? _folderPath.Text;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                PlatformLauncher.OpenFile(dir);
        };
        _folderClients.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (_folderClientsTable is { } t)
                await ClientsImportWindow.ShowAsync(this, t).ConfigureAwait(true);
        }, "клиенты из папки").ConfigureAwait(true);

        // ряд 1: две крупные кнопки; ряд 2: подпись «Путь к папке» над полем, поле | «Вставить путь» | «Прочитать»; ряд 3: «Обычно здесь» и «Открыть папку»
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _folderPick, _folderFind })
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            row1.Children.Add(b);
        }
        var pathCaption = Soft(T("Путь к папке (можно вставить)", "Папканын жолу (коюуга болот)", "Folder path (you can paste it)", "Klasör yolu (yapıştırabilirsiniz)", "Papka yo'li (qo'yish mumkin)"), 12.5);
        pathCaption.Margin = new Thickness(0, 4, 0, 4);
        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _folderPaste.Margin = new Thickness(8, 0, 0, 0);
        _folderRead.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_folderPaste, 1);
        Grid.SetColumn(_folderRead, 2);
        row2.Children.Add(_folderPath);
        row2.Children.Add(_folderPaste);
        row2.Children.Add(_folderRead);
        _folderOpen.Margin = new Thickness(8, 0, 0, 0);
        // узкое окно: «Обычно здесь» — своя строка, кнопка под ней (StackPanel горизонтальный только пока влезает)
        var usualWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { _folderUsual, _folderOpen } };
        _folderUsual.MaxWidth = 560;
        _folderUsualRow = new StackPanel { Children = { usualWrap } };
        _folderClients.Margin = new Thickness(0, 6, 0, 0);
        _folderClients.HorizontalAlignment = HorizontalAlignment.Left;
        _folderStatus.Margin = new Thickness(0, 6, 0, 0);

        _folderNote.Margin = new Thickness(0, 2, 0, 8);
        _folderControls = new StackPanel { Children = { row1, pathCaption, row2, _folderUsualRow, _folderStatus, _folderClients } };
        _folderPanel.Child = new StackPanel { Children = { title, _folderNote, _folderControls } };
        UpdateFolderPanel(0);
        return _folderPanel;
    }

    /// <summary>Плитка программы выбрана (индекс как в <see cref="_sourceCombo"/>): подсказка «Обычно здесь» и пояснение «облако / закрытая база».</summary>
    private void UpdateFolderPanel(int comboIndex)
    {
        _folderProgramId = comboIndex >= 1 && comboIndex <= ImportSourceProfiles.All.Count ? ImportSourceProfiles.All[comboIndex - 1].Id : null;
        // прайс поставщика (автозапчасти) — не про папки программ
        _folderPanel.IsVisible = comboIndex <= ImportSourceProfiles.All.Count;
        if (OperatingSystem.IsAndroid() || _folderOpen is null)
            return;
        var hint = ProgramFolderHints.For(_folderProgramId);
        _folderNote.Text = hint?.Note ?? ProgramFolderHints.GenericNote();
        var usual = hint?.UsuallyHereText() ?? "";
        _folderUsual.Text = usual;
        _folderUsualRow.IsVisible = usual.Length > 0;
        _folderOpen.IsVisible = hint?.ExistingStandardFolders().Any() == true;
        _folderStatus.IsVisible = false;
        _folderClients.IsVisible = false;
        _folderClientsTable = null;
    }

    private async Task PickFolderAsync()
    {
        if (_folderBusy || _started)
            return;
        IStorageFolder? start = null;
        try
        {
            var first = ProgramFolderHints.For(_folderProgramId)?.ExistingStandardFolders().FirstOrDefault();
            if (first is not null)
                start = await StorageProvider.TryGetFolderFromPathAsync(first).ConfigureAwait(true);
        }
        catch
        {
            // стартовая папка — только удобство
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Укажите папку программы", "Программанын папкасын көрсөтүңүз", "Choose the program folder", "Program klasörünü seçin", "Dastur papkasini ko'rsating"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 0)
            return;
        var path = folders[0].TryGetLocalPath();
        if (path is null)
        {
            ShowFolderStatus(T("Эту папку нельзя прочитать напрямую. Выберите папку на диске компьютера.", "Бул папканы түз окуу мүмкүн эмес. Компьютердин дискиндеги папканы тандаңыз.",
                "This folder cannot be read directly. Choose a folder on the computer's disk.", "Bu klasör doğrudan okunamaz. Bilgisayarın diskinden bir klasör seçin.", "Bu papkani to'g'ridan-to'g'ri o'qib bo'lmaydi. Kompyuter diskidagi papkani tanlang."), false);
            return;
        }
        _folderPath.Text = path;
        await ReadFolderAsync(path).ConfigureAwait(true);
    }

    /// <summary>«Найти на этом компьютере»: только проверка стандартных мест из справочника (без обхода диска) и списка баз 1С (ibases.v8i).</summary>
    private async Task FindFolderAsync()
    {
        if (_folderBusy || _started)
            return;
        var found = await Task.Run(() =>
        {
            var list = new List<string>();
            var hints = ProgramFolderHints.For(_folderProgramId) is { } one ? [one] : ProgramFolderHints.All.ToList();
            foreach (var h in hints)
                list.AddRange(h.ExistingStandardFolders());
            if (_folderProgramId is null or "1c")
                list.AddRange(ProgramFolderHints.FindOneCFileBases());
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }).ConfigureAwait(true);
        if (found.Count == 0)
        {
            ShowFolderStatus(T("В стандартных местах папка программы не найдена. Нажмите «Указать папку программы…» или вставьте путь.",
                "Стандарттуу жерлерден программанын папкасы табылган жок. «Программанын папкасын көрсөтүү…» басыңыз же жолду коюңуз.",
                "The program folder was not found in the standard places. Press “Choose the program folder…” or paste the path.",
                "Program klasörü standart yerlerde bulunamadı. «Program klasörünü seç…»e basın veya yolu yapıştırın.",
                "Dastur papkasi standart joylarda topilmadi. «Dastur papkasini ko'rsatish…»ni bosing yoki yo'lni qo'ying."), false);
            return;
        }
        _folderPath.Text = found[0];
        if (found.Count > 1)
            ShowFolderStatus(T($"Найдено папок: {found.Count}. Читаю первую; остальные можно указать вручную: ", $"Табылган папкалар: {found.Count}. Биринчисин окуйм; калгандарын кол менен көрсөтүңүз: ",
                $"Folders found: {found.Count}. Reading the first; point to the others by hand: ", $"Bulunan klasör: {found.Count}. İlki okunuyor; diğerlerini elle gösterin: ", $"Topilgan papkalar: {found.Count}. Birinchisini o'qiyman; qolganlarini qo'lda ko'rsating: ")
                + string.Join("  ·  ", found.Skip(1).Take(3)), false);
        await ReadFolderAsync(found[0]).ConfigureAwait(true);
    }

    private void ShowFolderStatus(string text, bool error)
    {
        _folderStatus.Text = text;
        _folderStatus.IsVisible = text.Length > 0;
        Bind(_folderStatus, TextBlock.ForegroundProperty, error ? "BrushDanger" : "BrushText");
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        if (error && text.Length > 0) // ошибка внизу страницы на низком окне не должна прятаться за прокруткой
            Dispatcher.UIThread.Post(() => _folderStatus.BringIntoView(), DispatcherPriority.Background);
    }

    private void SetFolderBusy(bool busy)
    {
        _folderBusy = busy;
        foreach (var b in new[] { _folderPick, _folderFind, _folderRead, _folderPaste, _folderOpen, _folderClients })
            b.IsEnabled = !busy;
        _folderPath.IsEnabled = !busy;
    }

    /// <summary>Прочитать папку и отдать таблицу мастеру как обычный файл. Ничего не отправляется на сервер: запись — только после «Загрузить» на шаге «Проверьте».</summary>
    public async Task ReadFolderAsync(string path)
    {
        if (_folderBusy || _started)
            return;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowFolderStatus(T("Укажите папку программы или вставьте путь.", "Программанын папкасын көрсөтүңүз же жолду коюңуз.", "Choose the program folder or paste the path.",
                "Program klasörünü seçin veya yolu yapıştırın.", "Dastur papkasini ko'rsating yoki yo'lni qo'ying."), true);
            return;
        }
        SetFolderBusy(true);
        ShowFolderStatus(T("Ищу базу в папке и читаю её…", "Папкадан базаны издеп, окуп жатам…", "Looking for the database in the folder and reading it…", "Klasörde veritabanı aranıyor ve okunuyor…", "Papkada bazani qidirib, o'qiyapman…"), false);
        ProgramFolderResult res;
        _folderCts?.Cancel();
        _folderCts = new CancellationTokenSource();
        var ct = _folderCts.Token;
        var id = _folderProgramId;
        try
        {
            res = await Task.Run(() => ProgramFolderImporter.Read(path, id, ct), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetFolderBusy(false);
            return;
        }
        finally
        {
            SetFolderBusy(false);
        }

        _folderClientsTable = res.Clients;
        _folderClients.IsVisible = res.Clients is { DataRowCount: > 0 };
        if (res.Clients is { } ct2)
            _folderClients.Content = T($"Клиенты из этой базы ({ct2.DataRowCount})…", $"Бул базадагы кардарлар ({ct2.DataRowCount})…", $"Customers from this database ({ct2.DataRowCount})…",
                $"Bu veritabanındaki müşteriler ({ct2.DataRowCount})…", $"Bu bazadagi mijozlar ({ct2.DataRowCount})…");
        if (!res.Ok)
        {
            // закрытый формат: ошибка + короткая инструкция «Как выгрузить» именно для выбранной/узнанной программы
            var text = res.Error ?? "";
            if (res.Problem is FolderProblem.UnsupportedFormat or FolderProblem.Encrypted or FolderProblem.NothingReadable or FolderProblem.TooBig or FolderProblem.TooManyRows)
                text += "\n" + FolderHowToExport(res.Program?.ProfileId ?? id);
            ShowFolderStatus(text, true);
            return;
        }
        var table = res.Read.Table!;
        var program = res.Program;
        var confidence = res.Confidence switch
        {
            FolderConfidence.High => T("высокая", "жогору", "high", "yüksek", "yuqori"),
            FolderConfidence.Medium => T("средняя", "орточо", "medium", "orta", "o'rtacha"),
            _ => T("низкая", "төмөн", "low", "düşük", "past"),
        };
        var who = program is null
            ? T("Программа не узнана", "Программа аныкталган жок", "The program was not recognised", "Program tanınmadı", "Dastur aniqlanmadi")
            : T($"Похоже на «{ProgramTitle(program.ProfileId)}»", $"«{ProgramTitle(program.ProfileId)}» окшойт", $"Looks like “{ProgramTitle(program.ProfileId)}”", $"«{ProgramTitle(program.ProfileId)}» gibi görünüyor", $"«{ProgramTitle(program.ProfileId)}» ga o'xshaydi");
        var line = T($"Прочитано: {res.Source} «{res.SourceFileName}», товаров: {table.DataRowCount}. {who} (уверенность: {confidence}). Проверьте столбцы и нажмите «Далее».",
            $"Окулду: {res.Source} «{res.SourceFileName}», товарлар: {table.DataRowCount}. {who} (ишенимдүүлүк: {confidence}). Мамычаларды текшерип, «Андан ары» басыңыз.",
            $"Read: {res.Source} “{res.SourceFileName}”, products: {table.DataRowCount}. {who} (confidence: {confidence}). Check the columns and press “Next”.",
            $"Okundu: {res.Source} «{res.SourceFileName}», ürün: {table.DataRowCount}. {who} (güven: {confidence}). Sütunları kontrol edip «İleri»ye basın.",
            $"O'qildi: {res.Source} «{res.SourceFileName}», mahsulotlar: {table.DataRowCount}. {who} (ishonch: {confidence}). Ustunlarni tekshirib, «Keyingi»ni bosing.");
        if (program is not null && id is not null && program.ProfileId != id && res.Confidence == FolderConfidence.High)
            line += " " + T($"Внимание: на плитке выбрана другая программа («{ProgramTitle(id)}»).", $"Көңүл буруңуз: плитканы башка программага тандадыңыз («{ProgramTitle(id)}»).",
                $"Note: a different program is chosen on the tile (“{ProgramTitle(id)}”).", $"Dikkat: karoda başka bir program seçili («{ProgramTitle(id)}»).", $"Diqqat: plitkada boshqa dastur tanlangan («{ProgramTitle(id)}»).");
        _fileSize = 0;
        await LoadResultAsync(res.Read, T("из папки: ", "папкадан: ", "from folder: ", "klasörden: ", "papkadan: ") + res.SourceFileName, advance: false).ConfigureAwait(true);
        ShowFolderStatus(line, false);
    }

    private static string ProgramTitle(string id) => id == "1c" ? "1С" : ImportSourceProfiles.ById(id)?.Name ?? (id == "ctmax" ? "CT MAX" : id);

    /// <summary>Короткая инструкция «Как выгрузить» для программы: из готового профиля (как на плитке) или общая.</summary>
    private static string FolderHowToExport(string? programId)
    {
        if (ImportSourceProfiles.ById(programId) is { } p)
            return HowToSteps(p.Id == "1c" ? "1С" : p.Name, p.HowToExport);
        if (programId == "ctmax")
            return T("Как выгрузить товары из CT MAX:\n1) Откройте в CT MAX список товаров (номенклатуру).\n2) Выгрузите его в Excel (.xlsx): нужны столбцы «Штрих Код», «Наименование», «Цена», «Закупочная».\n3) Выберите файл кнопкой «Выбрать файл…».",
                "CT MAX программасынан товарларды кантип чыгаруу:\n1) CT MAX'те товарлар тизмесин (номенклатура) ачыңыз.\n2) Аны Excel'ге (.xlsx) чыгарыңыз: «Штрих Код», «Наименование», «Цена», «Закупочная» мамычалары керек.\n3) Файлды «Файлды тандоо…» баскычы менен тандаңыз.",
                "How to export products from CT MAX:\n1) Open the product list (nomenclature) in CT MAX.\n2) Export it to Excel (.xlsx): the columns “Штрих Код”, “Наименование”, “Цена”, “Закупочная” are needed.\n3) Choose the file with “Choose file…”.",
                "CT MAX'ten ürünler nasıl alınır:\n1) CT MAX'te ürün listesini (isimlendirme) açın.\n2) Excel'e (.xlsx) aktarın: «Штрих Код», «Наименование», «Цена», «Закупочная» sütunları gerekir.\n3) Dosyayı «Dosya seç…» ile seçin.",
                "CT MAX dan mahsulotlarni qanday chiqarish:\n1) CT MAX da mahsulotlar ro'yxatini (nomenklatura) oching.\n2) Uni Excel (.xlsx) ga chiqaring: «Штрих Код», «Наименование», «Цена», «Закупочная» ustunlari kerak.\n3) Faylni «Faylni tanlash…» bilan tanlang.");
        return T("Выгрузите товары из программы в Excel (.xlsx) или CSV: нужны столбцы «Штрихкод», «Наименование», «Цена» (остаток и закупка — по желанию). Затем выберите файл кнопкой «Выбрать файл…».",
            "Товарларды программадан Excel (.xlsx) же CSV'ге чыгарыңыз: «Штрих-код», «Аталышы», «Баа» мамычалары керек (калдык жана сатып алуу — каалоо боюнча). Анан файлды «Файлды тандоо…» баскычы менен тандаңыз.",
            "Export the products from the program to Excel (.xlsx) or CSV: the columns “Barcode”, “Name”, “Price” are needed (stock and purchase are optional). Then choose the file with “Choose file…”.",
            "Ürünleri programdan Excel (.xlsx) veya CSV'ye aktarın: «Barkod», «Ürün adı», «Fiyat» sütunları gerekir (stok ve alış isteğe bağlı). Sonra dosyayı «Dosya seç…» ile seçin.",
            "Mahsulotlarni dasturdan Excel (.xlsx) yoki CSV ga chiqaring: «Shtrix-kod», «Nomi», «Narx» ustunlari kerak (qoldiq va xarid ixtiyoriy). Keyin faylni «Faylni tanlash…» bilan tanlang.");
    }
}
