using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (склад «10 из 10», К-4/К-5): окно «Партии» товара — приход, остаток, себестоимость, срок, номер партии, поставщик (GET products/{id}/batches/).
/// Партии идут по сроку (ближайший — первым), у каждой метка «Просрочена» / «Скоро истекает». Открывается из Склад → «⋯» → «Партии» ТОЛЬКО при признаке stock_batches.
/// Загрузчик передаётся снаружи — окно не знает про сеть (в проверках подставляется заглушка).</summary>
public sealed class ProductBatchesWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Колонки: № партии · Приход · Пришло · Остаток · Себестоимость · Срок · Поставщик · Состояние.
    private const string Columns = "1.1*,1.1*,0.95*,0.95*,1.2*,1.2*,1.4*,1.2*";

    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _state = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly string _unit;
    private readonly bool _showCost;
    private readonly DateTime _today = DateTime.Today;

    /// <summary>Строки списка (для проверки).</summary>
    public StackPanel RowsPanel => _rows;

    public ProductBatchesWindow(string productTitle, string unit, bool showCost, Func<CancellationToken, Task<List<StockBatch>>> loader)
    {
        _unit = unit;
        _showCost = showCost;
        var title = T("Партии", "Партиялар", "Batches", "Partiler", "Partiyalar");
        Title = title;
        Width = 912;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        _state.Bind(TextBlock.ForegroundProperty, _state.GetResourceObservable("BrushTextSoft"));
        _state.Text = T("Загружаем партии…", "Партиялар жүктөлүүдө…", "Loading batches…", "Partiler yükleniyor…", "Partiyalar yuklanmoqda…");

        var close = new Button { Name = "BatchesClose", Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), IsCancel = true, IsDefault = true };
        close.Classes.Add("modal-primary");
        close.Click += (_, _) => Close();

        var subtitle = productTitle + (ServerStock.FifoOn ? " · " + T("Себестоимость: FIFO", "Өздүк баа: FIFO", "Cost: FIFO", "Maliyet: FIFO", "Tannarx: FIFO") : "");
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = title, Subtitle = subtitle, Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            Content = new StackPanel { Spacing = 8, Children = { _state, Header(), _rows } },
        };
        Opened += async (_, _) => await LoadAsync(loader).ConfigureAwait(true);
    }

    private async Task LoadAsync(Func<CancellationToken, Task<List<StockBatch>>> loader)
    {
        try
        {
            Fill(await loader(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Партии товара: не загружены ({ex.Message}).", "WARNING");
            _state.Text = T("Не удалось загрузить партии. Проверьте связь и откройте окно снова.", "Партияларды жүктөө мүмкүн болгон жок. Байланышты текшерип, терезени кайра ачыңыз.",
                "Could not load the batches. Check the connection and open the window again.", "Partiler yüklenemedi. Bağlantıyı kontrol edip pencereyi yeniden açın.", "Partiyalarni yuklab bo'lmadi. Aloqani tekshirib, oynani qayta oching.");
        }
    }

    /// <summary>Заполнить список (публично для проверки без сети).</summary>
    public void Fill(IReadOnlyList<StockBatch> batches)
    {
        _rows.Children.Clear();
        if (batches.Count == 0)
        {
            _state.Text = T("Партий нет: товар пришёл до включения партий или остаток нулевой.", "Партиялар жок: товар партиялар күйгүзүлгөнгө чейин келген же калдык нөл.",
                "No batches: the stock arrived before batches were turned on, or the stock is zero.", "Parti yok: ürün partiler açılmadan önce gelmiş ya da stok sıfır.", "Partiyalar yo'q: tovar partiyalar yoqilgunga qadar kelgan yoki qoldiq nol.");
            return;
        }
        _state.IsVisible = false;
        foreach (var b in batches.OrderBy(x => x.ExpiresAt ?? DateTime.MaxValue).ThenBy(x => x.ReceivedAt ?? DateTime.MaxValue))
            _rows.Children.Add(Row(b));
    }

    private Control Header()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 0, 0, 2) };
        var heads = new[]
        {
            T("Партия №", "Партия №", "Batch no.", "Parti no.", "Partiya №"), T("Приход", "Кирүү", "Received", "Giriş", "Kirim"), T("Пришло", "Келди", "Qty in", "Gelen", "Kelgan"),
            T("Остаток", "Калдык", "Left", "Kalan", "Qoldiq"), T("Себестоимость", "Өздүк баа", "Unit cost", "Birim maliyet", "Tannarx"), T("Срок годности", "Жарактуулук мөөнөтү", "Expiry", "SKT", "Yaroqlilik muddati"),
            T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazuvchi"), T("Состояние", "Абалы", "Status", "Durum", "Holati"),
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    private Control Row(StockBatch b)
    {
        var state = ServerStock.BatchState(b, _today);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 6, 0, 6), Tag = "batch-row" };
        var cells = new[]
        {
            b.LotNo.Length > 0 ? b.LotNo : "—",
            b.ReceivedAt is { } r ? r.ToString("dd.MM.yyyy") : "—",
            Q(b.QtyIn),
            Q(b.QtyLeft),
            _showCost && b.UnitCost is { } c ? MoneyText.Som(c) : "—",
            b.ExpiresAt is { } e ? e.ToString("dd.MM.yyyy") : "—",
            b.Supplier.Length > 0 ? b.Supplier : "—",
            state.Length > 0 ? state : "—",
        };
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var brush = i == 7 && state.Length > 0 ? (b.IsExpired(_today) ? "BrushDanger" : "BrushWarning") : "BrushText";
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
            if (i == 7 && state.Length > 0)
                t.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
        line.Bind(Border.BorderBrushProperty, line.GetResourceObservable("BrushBorder"));
        return line;
    }

    private string Q(double v) => ServerStock.Q(v) + (_unit.Length > 0 ? " " + _unit : "");
}
