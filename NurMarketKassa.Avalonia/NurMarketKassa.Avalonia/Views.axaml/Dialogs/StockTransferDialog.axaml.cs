using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Карточка перемещения: маршрут и сопровождение, состав партии, хронология статусов,
/// прикреплённые документы и выгрузка.
///
/// Документ правится прямо здесь и сохраняется по ходу — отдельной кнопки «Сохранить» нет
/// намеренно: перемещение живёт долго (создали утром, отправили днём, приняли вечером), и
/// потерять правки из-за незакрытого окна нельзя.</summary>
public partial class StockTransferDialog : Window
{
    private readonly string _transferId;
    private StockTransferService.Transfer? _transfer;

    public StockTransferDialog() : this(string.Empty) { }

    public StockTransferDialog(string transferId)
    {
        _transferId = transferId;
        InitializeComponent();
        InitializeFileKinds();
        Opened += (_, _) => Reload();
    }

    private sealed class ItemRow
    {
        public long Id { get; init; }
        public string ProductName { get; init; } = "";
        public string Article { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string QuantityText { get; init; } = "";
    }

    private sealed class LogRow
    {
        public string WhenText { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string EmployeeText { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private sealed class FileRow
    {
        public string FilePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public string KindText { get; init; } = "";
        public string WhenText { get; init; } = "";
    }

    private static string DescribeStatus(string status) => status switch
    {
        StockTransferService.StatusInTransit => Tr.T("В пути", "Жолдо", "In transit", "Yolda", "Yo'lda"),
        StockTransferService.StatusDelivered => Tr.T("Доставлено", "Жеткирилди", "Delivered", "Teslim edildi", "Yetkazildi"),
        StockTransferService.StatusCancelled => Tr.T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"),
        _ => Tr.T("Создано", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"),
    };

    private void InitializeFileKinds()
    {
        FileKindBox.Items.Clear();
        FileKindBox.Items.Add(new ComboBoxItem { Content = Tr.T("Накладная", "Накладная", "Invoice", "İrsaliye", "Yuk xati"), Tag = "invoice" });
        FileKindBox.Items.Add(new ComboBoxItem { Content = Tr.T("Акт приёма-передачи", "Кабыл алуу акты", "Handover act", "Teslim tutanağı", "Qabul akti"), Tag = "act" });
        FileKindBox.Items.Add(new ComboBoxItem { Content = Tr.T("Транспортный документ", "Транспорт документи", "Transport document", "Taşıma belgesi", "Transport hujjati"), Tag = "transport" });
        FileKindBox.Items.Add(new ComboBoxItem { Content = Tr.T("Фото повреждений", "Бузулуу сүрөтү", "Damage photo", "Hasar fotoğrafı", "Shikast surati"), Tag = "damage" });
        FileKindBox.SelectedIndex = 0;
    }

    private static string FileKindText(string? kind) => kind switch
    {
        "invoice" => Tr.T("Накладная", "Накладная", "Invoice", "İrsaliye", "Yuk xati"),
        "act" => Tr.T("Акт приёма-передачи", "Кабыл алуу акты", "Handover act", "Teslim tutanağı", "Qabul akti"),
        "transport" => Tr.T("Транспортный документ", "Транспорт документи", "Transport document", "Taşıma belgesi", "Transport hujjati"),
        "damage" => Tr.T("Фото повреждений", "Бузулуу сүрөтү", "Damage photo", "Hasar fotoğrafı", "Shikast surati"),
        _ => Tr.T("Документ", "Документ", "Document", "Belge", "Hujjat"),
    };

    private void Reload()
    {
        _transfer = StockTransferService.Instance.LoadTransfers(limit: 1000)
            .FirstOrDefault(t => t.Id == _transferId);
        if (_transfer is null)
            return;

        NumberText.Text = _transfer.Number;
        StatusText_Set(_transfer.Status);
        CreatedText.Text = _transfer.CreatedAt.ToString("dd.MM.yyyy HH:mm");

        FromBox.Text = _transfer.FromPlaceName;
        ToBox.Text = _transfer.ToPlaceName;
        ResponsibleBox.Text = _transfer.Responsible ?? "";
        CarrierBox.Text = _transfer.Carrier ?? "";
        TrackingBox.Text = _transfer.TrackingNumber ?? "";

        var items = StockTransferService.Instance.LoadItems(_transferId);
        ItemsGrid.ItemsSource = items
            .Select(i => new ItemRow
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Article = i.Article ?? "",
                Barcode = i.Barcode ?? "",
                QuantityText = i.Quantity.ToString("0.###") + (string.IsNullOrWhiteSpace(i.Unit) ? "" : " " + i.Unit),
            })
            .ToList();

        WeightBox.Text = _transfer.TotalWeight > 0
            ? _transfer.TotalWeight.ToString("0.###", CultureInfo.InvariantCulture)
            : "";

        LogGrid.ItemsSource = StockTransferService.Instance.LoadLog(_transferId)
            .Select(l => new LogRow
            {
                WhenText = l.At.ToString("dd.MM.yyyy HH:mm"),
                StatusText = DescribeStatus(l.Status),
                EmployeeText = l.Employee ?? "—",
                Note = l.Note ?? "",
            })
            .ToList();

        FilesGrid.ItemsSource = StockTransferService.Instance.LoadAttachments(_transferId)
            .Select(f => new FileRow
            {
                FilePath = f.FilePath,
                FileName = f.FileName,
                KindText = FileKindText(f.Kind),
                WhenText = f.AddedAt.ToString("dd.MM.yyyy HH:mm"),
            })
            .ToList();

        var closed = _transfer.Status is StockTransferService.StatusDelivered or StockTransferService.StatusCancelled;
        ShipButton.IsEnabled = _transfer.Status == StockTransferService.StatusCreated;
        DeliverButton.IsEnabled = _transfer.Status == StockTransferService.StatusInTransit;
        CancelTransferButton.IsEnabled = !closed;
        // Состав принятой или отменённой партии уже не меняют (2026-09-25: добавлять и убирать
        // товары можно было и после «Принять»).
        ItemEditPanel.IsEnabled = !closed;
        if (closed)
            SuggestionsBox.IsVisible = false;
    }

    private void StatusText_Set(string status)
    {
        StatusText.Text = DescribeStatus(status);
        StatusBadge.Classes.Set("transit", status == StockTransferService.StatusInTransit);
        StatusBadge.Classes.Set("done", status == StockTransferService.StatusDelivered);
        StatusBadge.Classes.Set("cancelled", status == StockTransferService.StatusCancelled);
    }

    /// <summary>Поля шапки сохраняются, как только из них уходит курсор. Перемещение заполняют
    /// в несколько заходов, и кнопка «Сохранить» здесь только создавала бы риск потерять
    /// введённое.</summary>
    private void Field_Changed(object? sender, RoutedEventArgs e) => SaveHeader();

    private void SaveHeader()
    {
        if (_transfer is null)
            return;

        var from = EnsurePlace(FromBox.Text);
        var to = EnsurePlace(ToBox.Text);

        // Пусто или не число — ноль, то есть «вес не указан»: запятую принимаем наравне с точкой.
        if (!double.TryParse(WeightBox.Text?.Replace(',', '.').Trim(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var weight) || weight < 0)
            weight = 0;

        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE StockTransfers SET from_place_id = $from, to_place_id = $to, "
                + "responsible = $responsible, carrier = $carrier, tracking_number = $tracking, "
                + "total_weight = $weight WHERE id = $id;";
            command.Parameters.AddWithValue("$weight", weight);
            command.Parameters.AddWithValue("$from", (object?)from ?? System.DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)to ?? System.DBNull.Value);
            command.Parameters.AddWithValue("$responsible", (object?)NullIfEmpty(ResponsibleBox.Text) ?? System.DBNull.Value);
            command.Parameters.AddWithValue("$carrier", (object?)NullIfEmpty(CarrierBox.Text) ?? System.DBNull.Value);
            command.Parameters.AddWithValue("$tracking", (object?)NullIfEmpty(TrackingBox.Text) ?? System.DBNull.Value);
            command.Parameters.AddWithValue("$id", _transferId);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Склад-отправитель и склад-получатель вводятся текстом: своего справочника складов
    /// в кассе нет, а заставлять кладовщика сначала заводить справочник — верный способ, чтобы
    /// разделом не пользовались. Новое название заводится в справочнике само, при первом вводе.</summary>
    private static string? EnsurePlace(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var existing = StockTransferService.Instance.LoadPlaces()
            .FirstOrDefault(p => string.Equals(p.Name, trimmed, System.StringComparison.OrdinalIgnoreCase));
        return existing?.Id ?? StockTransferService.Instance.AddPlace(trimmed, "warehouse", null);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Живой поиск. Показывается с двух букв: с одной в каталоге на сотню товаров
    /// совпадает почти всё, и список подсказок только мешает. Сканер в это поле пишет быстро и
    /// заканчивает переводом строки — подсказки при этом мелькнут и уступят место обычному
    /// добавлению по Enter.</summary>
    private void ItemSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = ItemSearchBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            SuggestionsBox.IsVisible = false;
            SuggestionsPanel.ItemsSource = null;
            return;
        }

        var matches = CatalogCacheService.Products
            .Where(p => p.Title.Contains(query, System.StringComparison.CurrentCultureIgnoreCase)
                        || (p.Barcode ?? "").Contains(query, System.StringComparison.OrdinalIgnoreCase)
                        || (p.Article ?? "").Contains(query, System.StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Title, System.StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();

        SuggestionsPanel.ItemsSource = matches;
        SuggestionsBox.IsVisible = matches.Count > 0;
    }

    private void Suggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NurMarketKassa.Models.Pos.CatalogProductTileVm product })
            AddProductToTransfer(product, ReadQuantity());
    }

    /// <summary>Выбор из всего каталога списком — тем же окном, что собирает комплекты. Нужен,
    /// когда штрихкода нет под рукой, а название вспоминается с трудом: список показывает всё
    /// сразу и позволяет отметить несколько товаров за раз.</summary>
    private async void PickFromList_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new BundleItemPickerDialog(CatalogCacheService.Products, []);
        var confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || dialog.Result.Count == 0)
            return;

        var quantity = ReadQuantity();
        foreach (var product in dialog.Result)
            AddProductToTransfer(product, quantity, reloadAfter: false);

        Reload();
    }

    private double ReadQuantity()
    {
        if (!double.TryParse(ItemQuantityBox.Text?.Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;
        return quantity;
    }

    private void AddProductToTransfer(NurMarketKassa.Models.Pos.CatalogProductTileVm product, double quantity, bool reloadAfter = true)
    {
        StockTransferService.Instance.AddItem(_transferId, product.Id, product.Title,
            product.Article, product.Barcode, quantity, product.Unit, 0);

        ItemSearchBox.Text = "";
        ItemQuantityBox.Text = "1";
        SuggestionsBox.IsVisible = false;
        SuggestionsPanel.ItemsSource = null;

        if (reloadAfter)
            Reload();
    }

    private void ItemSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddItem_Click(sender, new RoutedEventArgs());
    }

    private void AddItem_Click(object? sender, RoutedEventArgs e)
    {
        var query = ItemSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
            return;

        if (!double.TryParse(ItemQuantityBox.Text?.Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        // Ищем сначала по штрихкоду — кладовщик чаще сканирует, чем печатает название.
        var product = LocalProductRepository.Instance.TryGetTileByBarcode(query)
                      ?? CatalogCacheService.Products.FirstOrDefault(p =>
                          p.Title.Contains(query, System.StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            StockTransferService.Instance.AddItem(_transferId, null, query, null, null, quantity, null, 0);
        }
        else
        {
            StockTransferService.Instance.AddItem(_transferId, product.Id, product.Title,
                product.Article, product.Barcode, quantity, product.Unit, 0);
        }

        ItemSearchBox.Text = "";
        ItemQuantityBox.Text = "1";
        Reload();
    }

    private void RemoveItem_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is ItemRow row)
        {
            StockTransferService.Instance.RemoveItem(row.Id, _transferId);
            Reload();
        }
    }

    private void Ship_Click(object? sender, RoutedEventArgs e) => ChangeStatus(StockTransferService.StatusInTransit);

    private void Deliver_Click(object? sender, RoutedEventArgs e) => ChangeStatus(StockTransferService.StatusDelivered);

    private void CancelTransfer_Click(object? sender, RoutedEventArgs e) => ChangeStatus(StockTransferService.StatusCancelled);

    private void ChangeStatus(string status)
    {
        StockTransferService.Instance.ChangeStatus(_transferId, status,
            App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName, null);
        Reload();
    }

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Выберите документ", "Документти тандаңыз", "Choose a document", "Belge seçin", "Hujjatni tanlang"),
            AllowMultiple = true,
        });

        if (files.Count == 0)
            return;

        var kind = (FileKindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "other";
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                StockTransferService.Instance.AttachFile(_transferId, path, kind);
        }
        Reload();
    }

    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow row || !File.Exists(row.FilePath))
            return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = row.FilePath,
            UseShellExecute = true,
        });
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e) => await ExportAsync("csv");

    /// <summary>Настоящий .xlsx — через ту же библиотеку OpenXml, что и отчёты аналитики: Excel
    /// на кассе обычно не установлен, но файл должен открываться у бухгалтера без вопросов.
    /// Отдельными листами идут шапка документа, состав партии и хронология статусов.</summary>
    private async void ExportExcel_Click(object? sender, RoutedEventArgs e) => await ExportOfficeAsync(toWord: false);

    /// <summary>Word — это бумажная накладная: маршрут, таблица состава, итоги и строки для
    /// подписей отправителя и получателя. Её печатают и отдают водителю.</summary>
    private async void ExportWord_Click(object? sender, RoutedEventArgs e) => await ExportOfficeAsync(toWord: true);

    private async System.Threading.Tasks.Task ExportOfficeAsync(bool toWord)
    {
        if (_transfer is null)
            return;

        var extension = toWord ? "docx" : "xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = toWord
                ? Tr.T("Сохранить накладную в Word", "Накладнойду Word форматында сактоо", "Save the waybill to Word", "İrsaliyeyi Word olarak kaydet", "Yuk xatini Word formatida saqlash")
                : Tr.T("Сохранить перемещение в Excel", "Жылышууну Excel форматында сактоо", "Save the transfer to Excel", "Transferi Excel olarak kaydet", "Ko'chirishni Excel formatida saqlash"),
            SuggestedFileName = $"transfer-{_transfer.Number}.{extension}",
            FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var items = StockTransferService.Instance.LoadItems(_transferId);
            var log = StockTransferService.Instance.LoadLog(_transferId);
            var status = DescribeStatus(_transfer.Status);
            var shop = UserPreferences.Instance.StoreName;

            await System.Threading.Tasks.Task.Run(() =>
            {
                if (toWord)
                    StockTransferExportService.ExportTransferToWord(path!, _transfer, items, status, shop);
                else
                    StockTransferExportService.ExportTransferToExcel(path!, _transfer, items, log, status, DescribeStatus, shop);
            }).ConfigureAwait(true);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Выгрузка перемещения не удалась: {ex}", "WARNING");
        }
    }

    /// <summary>Выгрузка состава партии. Excel-файл — это тот же разделённый табуляцией текст с
    /// расширением .xls: Excel такой открывает без вопросов, а тянуть ради одной таблицы
    /// библиотеку для настоящего xlsx не стоит.</summary>
    private async System.Threading.Tasks.Task ExportAsync(string format)
    {
        if (_transfer is null)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить выгрузку", "Жүктөөнү сактоо", "Save export", "Dışa aktarımı kaydet", "Eksportni saqlash"),
            SuggestedFileName = $"transfer-{_transfer.Number}.{format}",
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var separator = format == "csv" ? ";" : "\t";
        var text = new StringBuilder();
        text.AppendLine(string.Join(separator,
            Tr.T("Документ", "Документ", "Document", "Belge", "Hujjat"), _transfer.Number));
        text.AppendLine(string.Join(separator,
            Tr.T("Маршрут", "Багыты", "Route", "Güzergah", "Yo'nalish"),
            _transfer.FromPlaceName, "→", _transfer.ToPlaceName));
        text.AppendLine(string.Join(separator,
            Tr.T("Статус", "Абалы", "Status", "Durum", "Holat"), DescribeStatus(_transfer.Status)));
        text.AppendLine();
        text.AppendLine(string.Join(separator,
            Tr.T("Название", "Аты", "Name", "Ad", "Nomi"),
            Tr.T("Код", "Коду", "Code", "Kod", "Kod"),
            Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"),
            Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor")));

        foreach (var item in StockTransferService.Instance.LoadItems(_transferId))
        {
            text.AppendLine(string.Join(separator,
                item.ProductName, item.Article ?? "", item.Barcode ?? "",
                item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        // UTF-8 с меткой порядка байтов: без неё Excel открывает кириллицу знаками вопроса.
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
