using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.ProductMerge;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21: «Журнал слияний» — когда, кто, что с чем объединил, чем кончилось и откатывали ли; у слитых — «Откатить слияние» (по бэкапу: источники возвращаются из архива со своими кодами и остатком,
/// коды убираются у главного). Откат — только владельцу/администратору и при связи с сервером. Записи — файл журнала в папке данных (MergeStore), по текущему аккаунту.</summary>
public sealed class ProductMergeJournalWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ProductMergeDeps _deps;
    private readonly IProductMergeServer? _server;
    private readonly string _modeProblem;
    private readonly ModalShell _shell = new();
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly List<string> _restored = [];

    public ProductMergeJournalWindow(ProductMergeDeps deps, IProductMergeServer? server, string modeProblem)
    {
        _deps = deps;
        _server = server;
        _modeProblem = modeProblem;
        Title = T("Журнал слияний", "Бириктирүү журналы", "Merge journal", "Birleştirme günlüğü", "Birlashtirish jurnali");
        SectionCards.AsModal(this, 760);
        Opened += (_, _) => { this.FitToKassaScreen(); Reload(); };

        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(ServiceDeskUi.Text(T("Перед каждым слиянием записывается бэкап исходных карточек. «Откатить слияние» возвращает источники из архива с их штрихкодами и остатком и убирает эти коды у главного товара.",
            "Ар бир бириктирүүдөн мурун баштапкы карточкалардын резерви жазылат. «Бириктирүүнү кайтаруу» булактарды архивден штрихкоддору жана калдыгы менен кайтарып, бул коддорду башкы товардан алып салат.",
            "A backup of the original cards is written before every merge. “Roll back” returns the sources from the archive with their barcodes and stock and removes those codes from the main product.",
            "Her birleştirmeden önce orijinal kartların yedeği yazılır. «Geri al» kaynakları arşivden barkodları ve stoklarıyla döndürür ve bu kodları ana üründen kaldırır.",
            "Har bir birlashtirishdan oldin asl kartalar zaxirasi yoziladi. «Qaytarish» manbalarni arxivdan shtrix-kodlari va qoldig'i bilan qaytaradi va bu kodlarni asosiy mahsulotdan olib tashlaydi."), 12.5, brush: "BrushTextSoft"));
        body.Children.Add(_status);
        body.Children.Add(new ScrollViewer { Name = "MergeJournalScroll", MaxHeight = 360, Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Name = "MergeJournalClose";
        close.Click += (_, _) => Close();
        var shell = SectionCards.Shell(Title ?? "", null, body, SectionCards.Buttons(close));
        shell.InitialFocus = close;
        _shell = shell;
        Content = shell;
    }

    /// <summary>true — хотя бы одно слияние откатили (окно слияния перечитывает каталог).</summary>
    public bool RolledBackAny { get; private set; }

    public IReadOnlyList<string> RestoredIds => _restored;

    public string StatusMessage => _status.Text ?? "";

    private static string StatusName(string status) => status switch
    {
        "done" => T("слито", "бириктирилди", "merged", "birleştirildi", "birlashtirildi"),
        "partial" => T("частично", "жарым-жартылай", "partly", "kısmen", "qisman"),
        "failed" => T("не вышло", "ишке ашкан жок", "failed", "başarısız", "bo'lmadi"),
        "rolled_back" => T("откатано", "кайтарылды", "rolled back", "geri alındı", "qaytarildi"),
        "check" => T("проверить", "текшерүү керек", "check it", "kontrol edin", "tekshiring"),
        "running" => T("прервано", "үзгүлтүккө учурады", "interrupted", "kesintiye uğradı", "to'xtatilgan"),
        _ => status,
    };

    private void Reload()
    {
        _list.Children.Clear();
        var entries = MergeStore.LoadJournal(_deps.Account());
        if (entries.Count == 0)
        {
            _list.Children.Add(ServiceDeskUi.Text(T("Слияний пока не было.", "Азырынча бириктирүүлөр болгон жок.", "No merges yet.", "Henüz birleştirme yapılmadı.", "Hozircha birlashtirish bo'lmagan."), 13, brush: "BrushTextSoft"));
            return;
        }
        foreach (var e in entries)
            _list.Children.Add(Row(e));
    }

    private Control Row(MergeJournalEntry e)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(ServiceDeskUi.Text(e.WhenUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + " · " + e.Who, 12, brush: "BrushTextSoft"));
        var done = e.Sources.Where(s => s.State is "done" or "rolled_back" or "check").ToList();
        info.Children.Add(ServiceDeskUi.Text("«" + (e.NewMainTitle ?? e.MainTitle) + "» ← " + string.Join(", ", (done.Count > 0 ? done : e.Sources).Take(4).Select(s => "«" + s.Title + "»"))
            + (e.Sources.Count > 4 ? $" +{e.Sources.Count - 4}" : ""), 13.5, true));
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ServiceDeskUi.Chip(StatusName(e.Status), e.Status == "done"));
        chips.Children.Add(ServiceDeskUi.Chip(MergeTexts.ModeName(e.IsServer ? "server" : "manual"), false)); // 2026-11-21: серверное слияние или запасной ручной путь
        if (e.RolledBackUtc is { } rb)
            chips.Children.Add(ServiceDeskUi.Chip(T("откат: ", "кайтаруу: ", "rollback: ", "geri alma: ", "qaytarish: ") + rb.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + (string.IsNullOrWhiteSpace(e.RolledBackBy) ? "" : " · " + e.RolledBackBy), false));
        info.Children.Add(chips);
        if (!string.IsNullOrWhiteSpace(e.Note))
            info.Children.Add(ServiceDeskUi.Text(e.Note!, 12, brush: "BrushWarning"));
        if (e.IsServer && !string.IsNullOrWhiteSpace(e.ServerMergeId))
            info.Children.Add(ServiceDeskUi.Text("merge_id " + e.ServerMergeId + " · " + T("откат на сервере до ", "серверде кайтаруу: ", "server rollback until ", "sunucuda geri alma: ", "serverda qaytarish: ") + e.WhenUtc.ToLocalTime().AddDays(30).ToString("dd.MM.yyyy"), 11.5, brush: "BrushTextSoft"));
        info.Children.Add(ServiceDeskUi.Text(T("Бэкап: ", "Резерв: ", "Backup: ", "Yedek: ", "Zaxira: ") + e.BackupFile, 11.5, brush: "BrushTextSoft"));
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);
        if (e.CanRollback)
        {
            var b = ServiceDeskUi.Action("MergeRollback_" + e.Id, T("Откатить слияние", "Бириктирүүнү кайтаруу", "Roll back", "Geri al", "Qaytarish"), () => _ = RollbackAsync(e));
            b.Margin = new Thickness(12, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            var blocked = !_deps.CanMerge() ? MergeTexts.NoRightsReason() : _server is null ? MergeTexts.OfflineReason() : "";
            b.IsEnabled = blocked.Length == 0;
            if (blocked.Length > 0)
                ToolTip.SetTip(b, blocked);
            Grid.SetColumn(b, 1);
            grid.Children.Add(b);
        }
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10), Child = grid };
        ServiceDeskUi.Use(card, Border.BorderBrushProperty, "BrushBorder");
        ServiceDeskUi.Use(card, Border.BackgroundProperty, "BrushPanel");
        return card;
    }

    private async Task RollbackAsync(MergeJournalEntry e)
    {
        if (_server is null)
            return;
        var names = string.Join(", ", e.Sources.Where(s => s.State is "done" or "check").Select(s => "«" + s.Title + "»"));
        if (!await _deps.Confirm(this, T($"Откатить слияние «{e.MainTitle}»? Вернутся из архива: {names}. Их штрихкоды и остаток вернутся им, у главного товара эти коды и остаток уберутся. Название и закупка главного вернутся прежними.",
                $"«{e.MainTitle}» бириктирүүсүн кайтаруубу? Архивден кайтарылат: {names}. Алардын штрихкоддору жана калдыгы өздөрүнө кайтат, башкы товардан бул коддор жана калдык алынат. Башкынын аталышы жана сатып алуу баасы мурунку калат.",
                $"Roll back the merge of “{e.MainTitle}”? Restored from the archive: {names}. Their barcodes and stock go back to them; the main product loses those codes and that stock. The main product's name and purchase price return to the old values.",
                $"«{e.MainTitle}» birleştirmesi geri alınsın mı? Arşivden dönecekler: {names}. Barkodları ve stokları kendilerine döner, ana üründen bu kodlar ve stok alınır. Ana ürünün adı ve alış fiyatı eski haline döner.",
                $"«{e.MainTitle}» birlashtirishi qaytarilsinmi? Arxivdan qaytadi: {names}. Ularning shtrix-kodlari va qoldig'i o'zlariga qaytadi, asosiy mahsulotdan bu kodlar va qoldiq olinadi. Asosiy mahsulot nomi va xarid narxi eskicha bo'ladi."),
                T("Откат слияния", "Бириктирүүнү кайтаруу", "Merge rollback", "Birleştirme geri alma", "Birlashtirishni qaytarish"), T("Откатить", "Кайтаруу", "Roll back", "Geri al", "Qaytarish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), true).ConfigureAwait(true))
            return;
        var runner = new ProductMergeRunner(_server, _deps.Who(), _deps.Account());
        MergeRunResult? result = null;
        await _shell.RunBusyAsync(async () =>
        {
            try
            {
                result = await runner.RollbackAsync(e.Id, CancellationToken.None, msg => _status.Text = msg).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Откат слияния {e.Id}: {ex.GetType().Name}: {ex.Message}", "ERROR");
                _status.Text = T("Откат прерван: " + ex.Message, "Кайтаруу токтотулду: " + ex.Message, "Rollback interrupted: " + ex.Message, "Geri alma kesildi: " + ex.Message, "Qaytarish to'xtatildi: " + ex.Message);
            }
        }).ConfigureAwait(true);
        if (result is not null)
        {
            _status.Text = result.Message;
            if (result.Entry is { } entry)
            {
                var back = entry.Sources.Where(s => s.State == "rolled_back").Select(s => s.Id).Append(entry.MainId).ToList();
                if (back.Count > 1)
                {
                    RolledBackAny = true;
                    _restored.AddRange(back.Where(id => !_restored.Contains(id)));
                }
            }
        }
        Reload();
    }
}
