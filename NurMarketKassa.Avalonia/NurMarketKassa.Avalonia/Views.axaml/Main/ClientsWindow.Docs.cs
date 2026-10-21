using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, владелец: «КП и товарную накладную, выставление счёта … всё это должно быть во вкладке Клиенты». Только в
/// программе владельца (окно «Клиенты» общее с кассой — в кассе вида не меняем): сверху вкладки «Клиенты / Документы», в карточке
/// клиента — блок «Документы» (сколько КП, счетов, накладных, «Все документы клиента», «+ КП», «+ Счёт», «+ Накладная»).
/// Права — те же, что у раздела «Клиенты» (OwnerShellWindow открывает его только с правом на клиентов).</summary>
public partial class ClientsWindow
{
    private ClientDocsView? _docsView;
    private bool _docsTab;

    private static string Td(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void InitDocs()
    {
        if (!AppMode.IsOwner)
            return;
        ClientsTabs.IsVisible = true;
        TabClientsButton.Content = Td("Клиенты", "Кардарлар", "Clients", "Müşteriler", "Mijozlar");
        TabDocsButton.Content = Td("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar");
        Avalonia.Automation.AutomationProperties.SetName(TabClientsButton, Td("Вкладка «Клиенты»", "«Кардарлар» өтмөгү", "Clients tab", "Müşteriler sekmesi", "Mijozlar yorlig'i"));
        Avalonia.Automation.AutomationProperties.SetName(TabDocsButton, Td("Вкладка «Документы»: КП, счета, накладные", "«Документтер» өтмөгү: КС, эсептер, накладнойлор",
            "Documents tab: quotes, invoices, waybills", "Belgeler sekmesi: teklifler, faturalar, irsaliyeler", "Hujjatlar yorlig'i: TT, hisoblar, yuk xatlari"));
        ToolTip.SetTip(TabDocsButton, Td("КП, счета на оплату, накладные", "КС, төлөм эсептери, накладнойлор", "Quotes, invoices, waybills", "Teklifler, faturalar, irsaliyeler", "TT, hisoblar, yuk xatlari"));
        TabClientsButton.Click += (_, _) => ShowDocsTab(false);
        TabDocsButton.Click += (_, _) => ShowDocsTab(true);
        ClientDocsStore.Changed += OnClientDocsChanged;
        Closed += (_, _) => ClientDocsStore.Changed -= OnClientDocsChanged;
    }

    private void OnClientDocsChanged() => UpdateCardDocs();

    /// <summary>Вкладка «Документы» (true) или список клиентов; с клиентом — только его документы.</summary>
    public void ShowDocsTab(bool docs, string? clientId = null, string? clientName = null)
    {
        if (!AppMode.IsOwner)
            return;
        _docsTab = docs;
        UiKit.SetActive(TabClientsButton, !docs);
        UiKit.SetActive(TabDocsButton, docs);
        ClientsKpis.IsVisible = !docs;
        ClientsFilterRow.IsVisible = !docs;
        ClientsListCard.IsVisible = !docs;
        ClientsHint.IsVisible = !docs;
        ClientsActions.IsVisible = !docs;
        if (docs)
        {
            _docsView ??= new ClientDocsView { CurrentBuyer = FilteredBuyer };
            DocsHost.Content ??= _docsView;
            _docsView.SetClient(clientId, clientName);
        }
        DocsHost.IsVisible = docs;
    }

    /// <summary>Вкладка документов открыта (для стенда снимков).</summary>
    public ClientDocsView? DocsView => _docsTab ? _docsView : null;

    private ClientDocsUi.Buyer? _docsFilterBuyer;

    private ClientDocsUi.Buyer? FilteredBuyer() => _docsFilterBuyer;

    private ClientDocsUi.Buyer BuyerOf(ClientRow c)
    {
        // ИНН и компания — из списка клиентов NurCRM (если уже загружен), иначе только имя, телефон и адрес.
        var known = ClientDocsUi.BuyersCached().FirstOrDefault(b => b.Id == c.Id);
        return known ?? new ClientDocsUi.Buyer(c.Id, c.FullName, c.Phone, c.Address, "", "");
    }

    private void UpdateCardDocs()
    {
        if (!AppMode.IsOwner || CardDocsBox == null)
            return;
        var c = SelectedClient;
        CardDocsBox.IsVisible = c != null;
        if (c == null)
            return;
        var docs = ClientDocsStore.All().Where(d => d.ClientId == c.Id).ToList();
        CardDocsLabel.Text = Td("Документы: КП, счета, накладные", "Документтер: КС, эсептер, накладнойлор", "Documents: quotes, invoices, waybills", "Belgeler: teklifler, faturalar, irsaliyeler", "Hujjatlar: TT, hisoblar, yuk xatlari");
        if (docs.Count == 0)
            CardDocsDetail.Text = Td("Документов для этого клиента пока нет.", "Бул кардар үчүн азырынча документтер жок.", "No documents for this client yet.", "Bu müşteri için henüz belge yok.", "Bu mijoz uchun hali hujjatlar yo'q.");
        else
        {
            var q = docs.Count(d => d.Kind == ClientDocKind.Quote);
            var inv = docs.Where(d => d.Kind == ClientDocKind.Invoice).ToList();
            var w = docs.Count(d => d.Kind == ClientDocKind.Waybill);
            var unpaid = inv.Where(d => d.Status != "paid").Sum(d => d.Left);
            var overdue = docs.Count(d => d.Overdue);
            var parts = new List<string>
            {
                Td($"КП: {q}", $"КС: {q}", $"Quotes: {q}", $"Teklif: {q}", $"TT: {q}"),
                Td($"счета: {inv.Count}", $"эсептер: {inv.Count}", $"invoices: {inv.Count}", $"fatura: {inv.Count}", $"hisoblar: {inv.Count}")
                    + (unpaid > 0.004 ? Td($" (к оплате {ClientDocsUi.Money(unpaid)} сом)", $" (төлөнө турган {ClientDocsUi.Money(unpaid)} сом)", $" (to be paid {ClientDocsUi.Money(unpaid)} som)",
                        $" (ödenecek {ClientDocsUi.Money(unpaid)} som)", $" (to'lanadigan {ClientDocsUi.Money(unpaid)} so'm)") : ""),
                Td($"накладные: {w}", $"накладнойлор: {w}", $"waybills: {w}", $"irsaliye: {w}", $"yuk xatlari: {w}"),
            };
            if (overdue > 0)
                parts.Add(Td($"просрочено: {overdue}", $"мөөнөтү өткөн: {overdue}", $"overdue: {overdue}", $"gecikmiş: {overdue}", $"muddati o'tgan: {overdue}"));
            CardDocsDetail.Text = string.Join(" · ", parts);
        }

        CardDocsButtons.Children.Clear();
        var all = UiKit.Ghost(this, Td("Все документы", "Бардык документтер", "All documents", "Tüm belgeler", "Barcha hujjatlar"));
        Avalonia.Automation.AutomationProperties.SetName(all, Td("Все документы клиента", "Кардардын бардык документтери", "All client documents", "Müşterinin tüm belgeleri", "Mijozning barcha hujjatlari"));
        all.Height = 38;
        all.Padding = new Avalonia.Thickness(14, 0);
        all.Focusable = true;
        all.Margin = new Avalonia.Thickness(0, 0, 8, 6);
        all.IsVisible = docs.Count > 0;
        var client = c;
        all.Click += (_, _) =>
        {
            _docsFilterBuyer = BuyerOf(client);
            SelectedClient = null;
            ShowDocsTab(true, client.Id, client.FullName);
        };
        CardDocsButtons.Children.Add(all);
        foreach (var kind in new[] { ClientDocKind.Quote, ClientDocKind.Invoice, ClientDocKind.Waybill })
        {
            var b = UiKit.Ghost(this, ClientDocsUi.AddText(kind));
            b.Height = 38;
            b.Padding = new Avalonia.Thickness(14, 0);
            b.Focusable = true;
            b.Margin = new Avalonia.Thickness(0, 0, 8, 6);
            Avalonia.Automation.AutomationProperties.SetName(b, ClientDocsUi.KindLong(kind) + " — " + client.FullName);
            var k = kind;
            b.Click += (_, _) => ClientDocsUi.OpenNew(this, k, BuyerOf(client));
            CardDocsButtons.Children.Add(b);
        }
    }
}
