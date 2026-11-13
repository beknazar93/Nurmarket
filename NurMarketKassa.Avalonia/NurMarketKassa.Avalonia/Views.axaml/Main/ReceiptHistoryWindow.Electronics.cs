using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Services; // 2026-11-12: ReceiptHistoryEntry
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-12 (ТЗ «Сфера Электроника», ЭЛ3–ЭЛ4): «История чеков» в сфере «Электроника» — поиск чека по серийному номеру / IMEI
/// (местный журнал этой кассы, <see cref="SerialJournal"/>) и кнопка «Гарантийный талон» у чека с серийными номерами.
/// 2026-11-13 (сервер sn1311): при флаге компании electronics_2026_11 номер ищется ещё и на сервере (GET serials/?serial=…) — со всех касс; над списком —
/// статус «продан / возвращён / гарантия действует / повтор номера». Талон берёт номера и «гарантия до» из карточки чека сервера, журнал кассы — запасной путь.
/// В других сферах ничего не меняется: кнопка скрыта, поиск идёт как раньше.</summary>
public partial class ReceiptHistoryWindow
{
    private Button? _warrantyButton;
    private string? _noMatchDefaultText;
    // 2026-11-13: ответ сервера на поиск номера (для запроса _serverHitsQuery) и плашка со статусом.
    private List<ServerSerialHit> _serverHits = new();
    private string _serverHitsQuery = "";
    private bool _serverHitsAnswered;
    private CancellationTokenSource? _serialSearchCts;
    private Border? _serialStatusBorder;
    private TextBlock? _serialStatusText;

    /// <summary>Кнопка под панелью действий чека. Вызывается из конструктора один раз; сама скрыта, пока не выбран чек с номерами.</summary>
    private void InitElectronics(Panel actions)
    {
        _warrantyButton = new Button
        {
            Name = "WarrantyCardButton",
            Classes = { "sec-outline" },
            MinHeight = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
            Content = new TextBlock
            {
                Text = Tr.T("Гарантийный талон", "Кепилдик талону", "Warranty card", "Garanti belgesi", "Kafolat taloni"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            },
        };
        Avalonia.Automation.AutomationProperties.SetName(_warrantyButton, Tr.T("Гарантийный талон", "Кепилдик талону", "Warranty card", "Garanti belgesi", "Kafolat taloni"));
        _warrantyButton.Click += async (_, _) => await PrintWarrantyCardAsync().ConfigureAwait(true);
        actions.Children.Insert(Math.Min(actions.Children.Count, actions.Children.IndexOf(ActionBarHost) + 1), _warrantyButton);

        // 2026-11-13: плашка статуса номера (ответ сервера) — под строкой загрузки; пока пусто — скрыта.
        _serialStatusText = new TextBlock { Name = "SerialStatusText", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 };
        _serialStatusText.Bind(TextBlock.ForegroundProperty, _serialStatusText.GetResourceObservable("BrushText"));
        _serialStatusBorder = new Border { Name = "SerialStatusBorder", IsVisible = false, CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(12, 8), Child = _serialStatusText };
        _serialStatusBorder.Bind(Border.BackgroundProperty, _serialStatusBorder.GetResourceObservable("BrushAccentSoft"));
        if (NoticeBorder.Parent is Panel notices)
            notices.Children.Add(_serialStatusBorder);
    }

    /// <summary>Номера выбранного чека (для кнопки талона): карточка чека с сервера (serial_numbers / warranty_until), если она уже загружена и номера в ней есть, иначе журнал кассы.
    /// В других сферах — пусто.</summary>
    private List<SerialRecord> SerialsOf(RowVm? row)
    {
        if (row is null || !ElectronicsProfile.IsActive)
            return new List<SerialRecord>();
        if (_selectedDetail is { } detail && string.Equals(_shownDetailId, row.Entry.Id, StringComparison.OrdinalIgnoreCase)
            && ServerSerials.RecordsFromSale(detail, row.Entry.ReceiptNumber) is { Count: > 0 } server)
            return server;
        return SerialJournal.ForSale(row.Entry.Id);
    }

    private void UpdateWarrantyButton(RowVm? row)
    {
        if (_warrantyButton is null)
            return;
        _warrantyButton.IsVisible = SerialsOf(row).Count > 0;
    }

    /// <summary>Печать гарантийных талонов выбранного чека (по одному на единицу с номером) тем же принтером, что и чеки.</summary>
    private async Task PrintWarrantyCardAsync()
    {
        if (_selected is not { } row)
            return;
        var records = SerialsOf(row);
        if (records.Count == 0)
            return;
        try
        {
            var text = WarrantyCard.BuildCards(records);
            var error = await ReceiptHistoryService.PrintAsync(text).ConfigureAwait(true);
            ShowAction(error ?? Tr.T("Гарантийный талон напечатан.", "Кепилдик талону басылып чыкты.", "The warranty card has been printed.", "Garanti belgesi yazdırıldı.", "Kafolat taloni chop etildi."),
                ok: error == null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: гарантийный талон не напечатан: {ex}", "PRINTER");
            ShowAction(Tr.T("Не удалось напечатать талон: ", "Талонду басып чыгаруу мүмкүн болгон жок: ", "Could not print the warranty card: ", "Garanti belgesi yazdırılamadı: ", "Talonni chop etib bo'lmadi: ") + ex.Message, ok: false);
        }
    }

    /// <summary>Чек найден по серийному номеру: id продажи (или номер чека) есть среди записей журнала, подходящих запросу.</summary>
    internal static bool SerialHit(IReadOnlyCollection<SerialRecord> hits, ReceiptHistoryEntry entry) =>
        hits.Count > 0 && hits.Any(h =>
            (h.SaleId.Length > 0 && string.Equals(h.SaleId, entry.Id, StringComparison.OrdinalIgnoreCase))
            || (h.ReceiptNo.Length > 0 && !string.IsNullOrWhiteSpace(entry.ReceiptNumber)
                && string.Equals(h.ReceiptNo.TrimStart('№').Trim(), entry.ReceiptNumber.TrimStart('№').Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>Поиск по номеру — только в сфере «Электроника»; иначе пусто (прежний поиск). Журнал кассы (по части номера) + ответ сервера (весь номер, со всех касс).</summary>
    private List<SerialRecord> SerialHitsFor(string query)
    {
        if (!ElectronicsProfile.IsActive)
            return new List<SerialRecord>();
        var hits = SerialJournal.Find(query);
        ScheduleServerSerialSearch(query);
        if (_serverHitsAnswered && string.Equals(_serverHitsQuery, query, StringComparison.Ordinal))
            hits.AddRange(_serverHits.Select(h => h.ToRecord()));
        return hits;
    }

    private static bool LooksLikeSerial(string query) =>
        query.Length >= SerialJournal.MinQueryLength && !query.Contains(' ') && query.Any(char.IsDigit);

    /// <summary>2026-11-13: запрос серверу — через полсекунды после последней буквы, один на запрос; сервер выключен/недоступен — остаётся журнал кассы.</summary>
    private void ScheduleServerSerialSearch(string query)
    {
        if (!ElectronicsProfile.ServerEnabled || !LooksLikeSerial(query) || App.SalesApi is not IPosSerialsApi api)
        {
            _serialSearchCts?.Cancel();
            ShowSerialStatus(null);
            return;
        }
        if (string.Equals(_serverHitsQuery, query, StringComparison.Ordinal))
            return;
        _serialSearchCts?.Cancel();
        var cts = _serialSearchCts = new CancellationTokenSource();
        _ = SearchServerSerialAsync(api, query, cts);
    }

    private async Task SearchServerSerialAsync(IPosSerialsApi api, string query, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(500, cts.Token).ConfigureAwait(true);
            var found = await api.SerialLookupAsync(query, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            _serverHits = found.Results.ToList();
            _serverHitsQuery = query;
            _serverHitsAnswered = true;
            ShowSerialStatus(found);
            ApplyFilter(); // найденные сервером чеки — в список (запрос уже запомнен, повторно не уходит)
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: поиск номера на сервере не удался ({ex.GetType().Name}): {ex.Message}. Остаётся журнал этой кассы.", "WARNING");
            _serverHitsAnswered = false;
            ShowSerialStatus(null);
        }
    }

    /// <summary>Плашка над списком: что сервер знает про номер. null — скрыть.</summary>
    private void ShowSerialStatus(ServerSerialLookup? found)
    {
        if (_serialStatusBorder is null || _serialStatusText is null)
            return;
        if (found is null)
        {
            _serialStatusBorder.IsVisible = false;
            return;
        }
        _serialStatusText.Text = found.Results.Count == 0
            ? Tr.T($"Номер {found.Serial}: на сервере продаж не найдено.", $"{found.Serial} номери: серверде сатуу табылган жок.", $"Number {found.Serial}: no sales found on the server.", $"{found.Serial} numarası: sunucuda satış bulunamadı.", $"{found.Serial} raqami: serverda sotuv topilmadi.")
            : string.Join("\n", found.Results.Take(5).Select(h => $"{(h.Serial.Length > 0 ? h.Serial : found.Serial)}{(h.Name.Length > 0 ? " — " + h.Name : "")}: {h.StatusText()}"));
        _serialStatusBorder.IsVisible = true;
    }

    /// <summary>Текст «ничего не найдено»: у электроники и запроса, похожего на номер, — с пояснением, что номера ищутся только на этой кассе.</summary>
    private void UpdateNoMatchText(string query, int visibleCount)
    {
        if (!ElectronicsProfile.IsActive)
            return;
        _noMatchDefaultText ??= NoMatchText.Text;
        var serialLike = LooksLikeSerial(query);
        if (visibleCount == 0 && serialLike && ElectronicsProfile.ServerEnabled)
        {
            // 2026-11-13: сервер ищет номера со всех касс — подсказка «только эта касса» была бы неправдой.
            NoMatchText.Text = Tr.T("Не найдено среди загруженных чеков. Номер ищется и на сервере — его ответ показан над списком.",
                "Жүктөлгөн чектердин арасынан табылган жок. Номер серверден да изделет — анын жообу тизменин үстүндө көрсөтүлөт.",
                "Not found among the loaded receipts. The number is also searched on the server — its answer is shown above the list.",
                "Yüklenen fişler arasında bulunamadı. Numara sunucuda da aranır — yanıtı listenin üstünde gösterilir.",
                "Yuklangan cheklar orasidan topilmadi. Raqam serverda ham qidiriladi — uning javobi ro'yxat ustida ko'rsatiladi.");
            return;
        }
        NoMatchText.Text = visibleCount == 0 && serialLike
            ? Tr.T("Не найдено на этой кассе. Серийные номера и IMEI ищутся только среди чеков, проведённых на этой кассе (на других кассах номера пока не видны).",
                "Бул кассада табылган жок. Сериялык номерлер жана IMEI ушул кассада өткөн чектерден гана изделет (башка кассалардагы номерлер азырынча көрүнбөйт).",
                "Not found on this till. Serial numbers and IMEI are searched only among receipts made on this till (numbers from other tills are not visible yet).",
                "Bu kasada bulunamadı. Seri numaraları ve IMEI yalnızca bu kasada yapılan fişlerde aranır (diğer kasalardaki numaralar henüz görünmez).",
                "Bu kassada topilmadi. Seriya raqamlari va IMEI faqat shu kassada o'tkazilgan cheklar orasidan qidiriladi (boshqa kassalardagi raqamlar hozircha ko'rinmaydi).")
            : _noMatchDefaultText;
    }
}
