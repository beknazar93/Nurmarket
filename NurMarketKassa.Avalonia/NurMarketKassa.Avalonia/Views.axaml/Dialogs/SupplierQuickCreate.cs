using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-24, ТЗ «Склад» (пакет В): «＋ Создать поставщика» прямо в Склад → Приёмка. Мини-диалог — NewClientDialog в режиме
/// поставщика (название и телефон); создание — тем же PurchaseReceivingService.CreateSupplierAsync, что и «+ Новый поставщик» в карточке
/// товара (поставщик = клиент type=suppliers). Дубль по названию (PurchaseReceivingService.SupplierNameKey) — второй не создаётся, выбирается
/// существующий; дубль телефона — предупреждение в окне и «Выбрать существующего». ProductEditDialog не затрагивается.</summary>
public static class SupplierQuickCreate
{
    /// <param name="Supplier">Созданный или выбранный существующий поставщик.</param>
    /// <param name="Existed">true — такой поставщик уже был (не создавался).</param>
    public sealed record Result(PurchaseReceivingService.Supplier Supplier, bool Existed);

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <param name="suppliers">Уже известные поставщики (без «Без поставщика») — для проверки дубля.</param>
    /// <param name="create">Создание на сервере; null — настоящий PurchaseReceivingService (стенд подставляет свой).</param>
    /// <returns>null — окно закрыли, ничего не выбрано.</returns>
    public static async Task<Result?> ShowAsync(Window owner, IReadOnlyList<PurchaseReceivingService.Supplier> suppliers,
        Func<string, string, Task<PurchaseReceivingService.Supplier?>>? create = null)
    {
        Result? result = null;
        var rows = suppliers
            .Select(s => new ClientRow { Id = s.Id, FullName = s.Name, Phone = s.Phone ?? "" })
            .ToList();
        create ??= (name, phone) => PurchaseReceivingService.Instance.CreateSupplierAsync(name, phone);

        async Task<string?> SaveAsync(string name, string phone, string email, string address)
        {
            var key = PurchaseReceivingService.SupplierNameKey(name);
            if (key.Length > 0 && suppliers.FirstOrDefault(s => PurchaseReceivingService.SupplierNameKey(s.Name) == key) is { } same)
            {
                result = new Result(same, true);
                return null;
            }
            try
            {
                var created = await create(name, phone).ConfigureAwait(true);
                if (created is null)
                    return T("Не удалось добавить поставщика.", "Жеткирүүчүнү кошуу мүмкүн болгон жок.", "Could not add the supplier.", "Tedarikçi eklenemedi.",
                        "Yetkazib beruvchini qo'shib bo'lmadi.");
                result = new Result(created, false);
                PosLogger.Log($"Приёмка: добавлен поставщик {created.Id}.", "WAREHOUSE");
                return null;
            }
            catch (ApiException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приёмка: поставщик не создан: {ex}", "WARNING");
                return T("Не удалось добавить поставщика: ", "Жеткирүүчүнү кошуу мүмкүн болгон жок: ", "Could not add the supplier: ", "Tedarikçi eklenemedi: ",
                    "Yetkazib beruvchini qo'shib bo'lmadi: ") + ex.Message;
            }
        }

        var dialog = new NewClientDialog(SaveAsync, rows, supplier: true,
            pickExisting: row =>
            {
                if (suppliers.FirstOrDefault(s => s.Id == row.Id) is { } picked)
                    result = new Result(picked, true);
            },
            subtitle: T("Он сразу будет выбран поставщиком этой приёмки.", "Ал дароо ушул кабыл алуунун жеткирүүчүсү болуп тандалат.",
                "It will be selected as the supplier of this receiving right away.", "Hemen bu mal kabulünün tedarikçisi olarak seçilecek.",
                "U darhol shu qabulning yetkazib beruvchisi sifatida tanlanadi."));
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }
}
