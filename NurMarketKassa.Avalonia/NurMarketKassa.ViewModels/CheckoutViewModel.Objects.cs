using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NurMarketKassa.ViewModels
{
    /// <summary>2026-11-14 (ответ Умара 13.11 вечер, строймаркет): выбор «объекта» клиента-прораба (стройка, адрес) в окне оплаты. Объекты — GET clients/&lt;id&gt;/objects/ (загружаются при выборе клиента),
    /// выбор необязательный; id уходит в pos/checkout/ полем <c>object</c> (<see cref="ClientObjectIdForApi"/>). Блок виден только в магазине «Хозтовары», когда у клиента есть объекты.</summary>
    public partial class CheckoutViewModel
    {
        private CancellationTokenSource? _objectsCts;
        private ClientObjectInfo? _selectedClientObject;
        private static ClientObjectInfo NoObject => new("", Tr.T("Без объекта", "Объектисиз", "No site", "Şantiyesiz", "Obyektsiz"), null);

        /// <summary>Объекты выбранного клиента; первая строка — «Без объекта» (пустой id).</summary>
        public ObservableCollection<ClientObjectInfo> ClientObjects { get; } = new();

        public ClientObjectInfo? SelectedClientObject
        {
            get => _selectedClientObject;
            set
            {
                _selectedClientObject = value;
                OnPropertyChanged();
            }
        }

        public bool ShowClientObjects => HasSelectedClient && ClientObjects.Count > 1;

        public string ClientObjectLabel => Tr.T("Объект клиента (стройка)", "Кардардын объектиси (курулуш)", "Client site (building project)", "Müşteri şantiyesi", "Mijoz obyekti (qurilish)");

        /// <summary>Id выбранного объекта для pos/checkout/ (поле <c>object</c>); null — клиент без объектов или выбрано «Без объекта».</summary>
        public string? ClientObjectIdForApi => ShowClientObjects && SelectedClientObject is { Id: { Length: > 0 } id } ? id : null;

        /// <summary>Подмена для проверок: загрузка объектов клиента; по умолчанию — <see cref="PriceLevelPricing.LoadObjectsAsync"/>.</summary>
        public static Func<string, CancellationToken, Task<IReadOnlyList<ClientObjectInfo>>>? ClientObjectsLoader { get; set; }

        /// <summary>Выбран другой клиент (или клиент убран): список объектов сбрасывается и, для хозмага, загружается заново.</summary>
        private void RefreshClientObjects(ClientOption? client)
        {
            _objectsCts?.Cancel();
            _objectsCts = null;
            ClientObjects.Clear();
            SelectedClientObject = null;
            OnPropertyChanged(nameof(ShowClientObjects));
            OnPropertyChanged(nameof(ClientObjectLabel));
            if (client is null || string.IsNullOrWhiteSpace(client.Id) || !MarketSpheres.IsHardware)
                return;
            var cts = _objectsCts = new CancellationTokenSource();
            _ = LoadClientObjectsAsync(client.Id, cts.Token);
        }

        private async Task LoadClientObjectsAsync(string clientId, CancellationToken ct)
        {
            try
            {
                var loader = ClientObjectsLoader ?? ((id, token) => PriceLevelPricing.LoadObjectsAsync(id, token));
                var list = await loader(clientId, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || _selectedClient?.Id != clientId || list.Count == 0)
                    return;
                var none = NoObject;
                ClientObjects.Add(none);
                foreach (var o in list)
                    ClientObjects.Add(o);
                SelectedClientObject = none;
                OnPropertyChanged(nameof(ShowClientObjects));
            }
            catch (OperationCanceledException)
            {
                // выбран другой клиент — ответ уже не нужен
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Объекты клиента {clientId}: не загружены ({ex.Message}) — продажа без объекта.", "WARNING");
            }
        }
    }
}
