using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): история изменений возвратного документа (журнал аудита сервера,
/// GET …/returns/{id}/history/ — только руководителю). Каждая версия: что сделано (создан / изменён / аннулирован), когда, кто, кто подтвердил PIN, причина,
/// сумма; «Показать состав» — строки документа на этой версии (так смотрят исходную версию v1). Общее для возврата поставщику и от клиента.</summary>
public sealed class ReturnHistoryDialog : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Func<CancellationToken, Task<List<ReturnHistoryEntry>>> _load;
    private readonly HashSet<int> _open = new();
    private List<ReturnHistoryEntry> _entries = new();

    public ReturnHistoryDialog(string subtitle, Func<CancellationToken, Task<List<ReturnHistoryEntry>>> load)
    {
        _load = load;
        Title = T("История изменений", "Өзгөртүүлөрдүн тарыхы", "Change history", "Değişiklik geçmişi", "O'zgarishlar tarixi");
        SectionCards.AsModal(this, 640);
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));
        _status.Text = T("Загружаю историю…", "Тарых жүктөлүүдө…", "Loading history…", "Geçmiş yükleniyor…", "Tarix yuklanmoqda…");
        var panel = new StackPanel { Spacing = 8, Children = { _status, _list } };
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title, subtitle, panel, SectionCards.Buttons(close));
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Показать готовый список (для снимков окна без сервера).</summary>
    public void ShowEntries(List<ReturnHistoryEntry> entries)
    {
        _entries = entries;
        Render();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            ShowEntries(await _load(cts.Token).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _list.Children.Clear();
            _status.Text = ReturnEditErrors.Text(ReturnEditErrors.Classify(ex));
        }
    }

    internal static string ActionName(ReturnHistoryEntry e) => e.Action switch
    {
        "created" => T("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"),
        "voided" => T("Аннулирован", "Жокко чыгарылды", "Voided", "İptal edildi", "Bekor qilindi"),
        _ => T("Изменён", "Өзгөртүлдү", "Edited", "Düzenlendi", "O'zgartirildi"),
    };

    private void Render()
    {
        _list.Children.Clear();
        _status.IsVisible = _entries.Count == 0;
        if (_entries.Count == 0)
        {
            _status.Text = T("Сервер не прислал историю изменений.", "Сервер өзгөртүүлөрдүн тарыхын жөнөткөн жок.", "The server sent no change history.", "Sunucu değişiklik geçmişi göndermedi.", "Server o'zgarishlar tarixini yubormadi.");
            return;
        }
        foreach (var e in _entries)
            _list.Children.Add(Card(e));
    }

    private Control Card(ReturnHistoryEntry e)
    {
        var body = new StackPanel { Spacing = 4 };
        var head = new WrapPanel();
        var title = new TextBlock
        {
            Text = T($"Версия {e.Version}", $"{e.Version}-версия", $"Version {e.Version}", $"Sürüm {e.Version}", $"{e.Version}-versiya") + " · " + ActionName(e),
            FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        head.Children.Add(title);
        if (e.IsCreated)
            head.Children.Add(SectionCards.Pill(this, T("исходная", "баштапкы", "original", "ilk", "dastlabki")));
        if (e.IsVoided)
            head.Children.Add(SectionCards.Pill(this, ReturnEditErrors.VoidedMark(), "BrushDangerSoft", "BrushDangerOnSoft"));
        body.Children.Add(head);
        var meta = string.Join(" · ", new[]
        {
            e.At?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru) ?? "",
            e.User,
            e.ApprovedBy.Length > 0 ? T("PIN: ", "PIN: ", "PIN: ", "PIN: ", "PIN: ") + e.ApprovedBy : "",
            e.Total > 0 || !e.IsVoided ? MoneyText.Som(e.Total) : "",
        }.Where(x => x.Length > 0));
        body.Children.Add(Soft(meta));
        if (e.Reason.Length > 0)
            body.Children.Add(Soft(T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + e.Reason));
        var lines = e.SnapshotLines();
        if (lines.Count > 0)
        {
            var open = _open.Contains(e.Version);
            var toggle = new Button
            {
                Content = open ? T("▴ Скрыть состав", "▴ Курамды жашыруу", "▴ Hide contents", "▴ İçeriği gizle", "▴ Tarkibni yashirish")
                    : T($"▾ Показать состав ({lines.Count})", $"▾ Курамын көрсөтүү ({lines.Count})", $"▾ Show contents ({lines.Count})", $"▾ İçeriği göster ({lines.Count})", $"▾ Tarkibni ko'rsatish ({lines.Count})"),
                MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Left,
            };
            toggle.Classes.Add("modal-ghost");
            toggle.Click += (_, _) =>
            {
                if (!_open.Remove(e.Version))
                    _open.Add(e.Version);
                Render();
            };
            body.Children.Add(toggle);
            if (open)
                body.Children.Add(SectionCards.LinesTable(this,
                    lines.Select(l => (l.Name.Length > 0 ? l.Name : "—", $"{l.Qty.ToString("0.###", Ru)} {l.Unit}".Trim(),
                        MoneyText.Som(l.Price) + (l.Discount > 0 ? " −" + MoneyText.Som(l.Discount) : ""), MoneyText.Som(l.Total))).ToList(),
                    T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"), T("Цена", "Баасы", "Price", "Fiyat", "Narx"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa")));
        }
        return SectionCards.Group(this, body);
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
