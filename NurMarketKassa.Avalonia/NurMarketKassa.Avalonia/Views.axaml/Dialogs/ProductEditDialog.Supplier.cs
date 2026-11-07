using NurMarketKassa.Core.Application;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvKey = Avalonia.Input.Key; // 2026-10-24: System.Windows.Input.Key тоже виден
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-06: В15г — часть класса ProductEditDialog (Supplier): перенесено из ProductEditDialog.axaml.cs без изменений логики.
public partial class ProductEditDialog
{
    // ---------- Поставщик товара (2026-10-20, владелец: «в описание добавить поставщика — выбор и добавление нового») ----------
    // На сервере NurCRM поставщик товара — поле «client» товара (id из clients/?type=suppliers); в карточке GET products/{id}/
    // рядом «client_name». Форма товара сайта шлёт его тем же create-manual/PATCH — касса тоже (ProductEditRequest.SupplierId).
    // Список поставщиков — тот же, что у приёмки склада (PurchaseReceivingService.LoadSuppliersAsync), грузится при первом
    // раскрытии списка. У существующего товара поставщик читается из карточки при открытии окна (LoadMinimumQuantityAsync).
    private List<PurchaseReceivingService.Supplier> _suppliers = new();
    private bool _suppliersLoaded;
    private bool _suppliersLoading;
    private string? _suppliersError;
    private string _supplierId = "";
    private string _supplierName = "";
    private string _supplierPhone = "";
    private bool _supplierChanged;
    private const int SupplierOptionsLimit = 100;

    private void SetupSupplier()
    {
        SupplierLabel.Text = Tr.T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi");
        SupplierSearchBox.Watermark = Tr.T("Найти по названию или телефону", "Аты же телефону боюнча табуу", "Find by name or phone",
            "Ada veya telefona göre bul", "Nomi yoki telefoni bo'yicha topish");
        NewSupplierButton.Content = Tr.T("+ Новый поставщик", "+ Жаңы жеткирүүчү", "+ New supplier", "+ Yeni tedarikçi", "+ Yangi yetkazib beruvchi");
        Avalonia.Automation.AutomationProperties.SetName(SupplierToggleButton, SupplierLabel.Text);
        Avalonia.Automation.AutomationProperties.SetName(NewSupplierButton, (string)NewSupplierButton.Content);
        Avalonia.Automation.AutomationProperties.SetName(SupplierSearchBox, SupplierSearchBox.Watermark);
        SupplierSearchBox.TextChanged += (_, _) => BuildSupplierOptions();
        // Enter в поиске: найден ровно один — выбираем его.
        SupplierSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter)
                return;
            e.Handled = true;
            var matches = FilterSuppliers(SupplierSearchBox.Text).Take(2).ToList();
            if (matches.Count == 1)
                SelectSupplier(matches[0].Id, matches[0].Name, matches[0].Phone);
        };
        // Поставщики — справочник сервера: офлайн выбрать нельзя (как клиентов), подсказка вместо списка.
        if (OfflineModeHelper.UseLocalOperations)
        {
            SupplierToggleButton.IsEnabled = false;
            SupplierHint.Text = Tr.T("Без связи с сервером поставщика не выбрать.", "Сервер менен байланыш жок — жеткирүүчүнү тандоого болбойт.",
                "No connection to the server — a supplier can't be chosen.", "Sunucu bağlantısı yok — tedarikçi seçilemez.",
                "Server bilan aloqa yo'q — yetkazib beruvchini tanlab bo'lmaydi.");
            SupplierHint.IsVisible = true;
        }
        BuildSupplierCurrent();
    }

    private void BuildSupplierCurrent()
    {
        SupplierCurrentText.Text = _supplierName.Length > 0
            ? _supplierName
            : Tr.T("Выберите поставщика", "Жеткирүүчүнү тандаңыз", "Select a supplier", "Tedarikçi seçin", "Yetkazib beruvchini tanlang");
        SupplierCurrentPhone.Text = _supplierPhone;
        SupplierCurrentPhone.IsVisible = _supplierPhone.Length > 0;
    }

    /// <summary>Поставщик существующего товара из карточки сервера: «client» — id (строка, число или объект с id),
    /// название — «client_name», иначе «suppliers»[0].name, иначе из загруженного списка. Выбранного в окне не перетирает.</summary>
    private void ApplySupplierFromDetail(System.Text.Json.JsonElement el)
    {
        if (_supplierChanged || !el.TryGetProperty("client", out var client))
            return;
        var id = client.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => client.GetString(),
            System.Text.Json.JsonValueKind.Number => client.GetRawText(),
            System.Text.Json.JsonValueKind.Object when client.TryGetProperty("id", out var cid) => cid.ToString(),
            _ => null,
        };
        id = id?.Trim() ?? "";
        if (id.Length == 0)
            return;
        string? name = el.TryGetProperty("client_name", out var cn) && cn.ValueKind == System.Text.Json.JsonValueKind.String ? cn.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) && el.TryGetProperty("suppliers", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                if (s.ValueKind == System.Text.Json.JsonValueKind.Object && s.TryGetProperty("id", out var sid) && sid.ToString() == id
                    && s.TryGetProperty("name", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String)
                    name = sn.GetString();
        var known = _suppliers.FirstOrDefault(s => s.Id == id);
        _supplierId = id;
        _supplierName = (string.IsNullOrWhiteSpace(name) ? known?.Name : name)?.Trim() ?? "";
        _supplierPhone = known?.Phone ?? "";
        if (_supplierName.Length == 0)
            _supplierName = Tr.T("Поставщик выбран на сайте", "Жеткирүүчү сайтта тандалган", "Supplier set on the website", "Tedarikçi sitede seçildi",
                "Yetkazib beruvchi saytda tanlangan");
        BuildSupplierCurrent();
        if (SupplierOptionsPanel.IsVisible)
            BuildSupplierOptions();
    }

    private async void SupplierToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        SupplierOptionsPanel.IsVisible = !SupplierOptionsPanel.IsVisible;
        if (!SupplierOptionsPanel.IsVisible)
            return;
        BuildSupplierOptions();
        SupplierSearchBox.Focus();
        await EnsureSuppliersLoadedAsync().ConfigureAwait(true);
    }

    private async Task EnsureSuppliersLoadedAsync()
    {
        if (_suppliersLoaded || _suppliersLoading || OfflineModeHelper.UseLocalOperations)
            return;
        _suppliersLoading = true;
        _suppliersError = null;
        BuildSupplierOptions();
        try
        {
            _suppliers = await PurchaseReceivingService.Instance.LoadSuppliersAsync().ConfigureAwait(true);
            _suppliersLoaded = true;
            // Телефон выбранного (у карточки товара его нет) — из списка.
            if (_supplierId.Length > 0 && _suppliers.FirstOrDefault(s => s.Id == _supplierId) is { } current)
            {
                _supplierPhone = current.Phone ?? "";
                BuildSupplierCurrent();
            }
        }
        catch (Exception ex)
        {
            _suppliersError = ex.Message;
            PosLogger.Log($"Карточка товара: список поставщиков не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            _suppliersLoading = false;
            BuildSupplierOptions();
        }
    }

    /// <summary>Поиск: по части названия (без регистра) или по цифрам телефона (от 3 цифр: «555 12» найдёт +996 555 12…).</summary>
    private IEnumerable<PurchaseReceivingService.Supplier> FilterSuppliers(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return _suppliers;
        var digits = new string(q.Where(char.IsDigit).ToArray());
        return _suppliers.Where(s =>
            SearchMatch.Matches(q, s.Name) // 2026-10-29: единый поиск
            || (digits.Length >= 3 && new string((s.Phone ?? "").Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
    }

    private void BuildSupplierOptions()
    {
        SupplierOptionsList.Children.Clear();
        if (_suppliersLoading && _suppliers.Count == 0)
        {
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            return;
        }
        if (_suppliersError != null)
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T("Список поставщиков не загрузился: ", "Жеткирүүчүлөрдүн тизмеси жүктөлгөн жок: ",
                "The supplier list didn't load: ", "Tedarikçi listesi yüklenmedi: ", "Yetkazib beruvchilar ro'yxati yuklanmadi: ") + _suppliersError));

        var query = SupplierSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
            SupplierOptionsList.Children.Add(MakeSupplierOption("", Tr.T("— без поставщика —", "— жеткирүүчүсүз —", "— no supplier —", "— tedarikçi yok —",
                "— yetkazib beruvchisiz —"), null, _supplierId.Length == 0));
        // Выбранный, которого нет в списке (список ещё не загружен, поставщика удалили) — показываем, чтобы было видно, что выбрано.
        if (query.Length == 0 && _supplierId.Length > 0 && _suppliers.All(s => s.Id != _supplierId))
            SupplierOptionsList.Children.Add(MakeSupplierOption(_supplierId, _supplierName, _supplierPhone, true));

        var matches = FilterSuppliers(query).ToList();
        foreach (var s in matches.Take(SupplierOptionsLimit))
            SupplierOptionsList.Children.Add(MakeSupplierOption(s.Id, s.Name, s.Phone, s.Id == _supplierId));
        if (matches.Count > SupplierOptionsLimit)
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T($"Показаны первые {SupplierOptionsLimit} из {matches.Count} — уточните поиск.",
                $"{matches.Count} ичинен алгачкы {SupplierOptionsLimit} көрсөтүлдү — издөөнү тактаңыз.",
                $"Showing the first {SupplierOptionsLimit} of {matches.Count} — refine the search.",
                $"{matches.Count} kayıttan ilk {SupplierOptionsLimit} gösteriliyor — aramayı daraltın.",
                $"{matches.Count} tadan birinchi {SupplierOptionsLimit} ko'rsatildi — qidiruvni aniqlang.")));
        if (!_suppliersLoading && _suppliersError == null && matches.Count == 0)
            SupplierOptionsList.Children.Add(MakeOptionHint(query.Length > 0
                ? Tr.T("Не найдено — добавьте нового поставщика.", "Табылган жок — жаңы жеткирүүчү кошуңуз.", "Not found — add a new supplier.",
                    "Bulunamadı — yeni tedarikçi ekleyin.", "Topilmadi — yangi yetkazib beruvchi qo'shing.")
                : Tr.T("Поставщиков пока нет — добавьте первого.", "Азырынча жеткирүүчүлөр жок — биринчисин кошуңуз.", "No suppliers yet — add the first one.",
                    "Henüz tedarikçi yok — ilkini ekleyin.", "Hozircha yetkazib beruvchilar yo'q — birinchisini qo'shing.")));
    }

    /// <summary>Строка списка: название слева, телефон справа; выбранная — тёмно-синяя (SecondaryButton + PrimaryButton).</summary>
    private Button MakeSupplierOption(string id, string name, string? phone, bool selected)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var phoneText = new TextBlock { Text = phone, Opacity = 0.75, FontWeight = Avalonia.Media.FontWeight.Normal, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(phoneText, 1);
            grid.Children.Add(phoneText);
        }
        var button = new Button
        {
            Classes = { "SecondaryButton" },
            Content = grid,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 7),
            MinHeight = 44, // 2026-10-22 (модалки, пакет 3): строка поставщика 44 px (было 38) — касание пальцем
        };
        if (selected)
            button.Classes.Add("PrimaryButton");
        Avalonia.Automation.AutomationProperties.SetName(button, string.IsNullOrWhiteSpace(phone) ? name : $"{name}, {phone}");
        button.Click += (_, _) => SelectSupplier(id, id.Length == 0 ? "" : name, phone);
        return button;
    }

    private void SelectSupplier(string id, string name, string? phone)
    {
        _supplierId = id;
        _supplierName = name;
        _supplierPhone = phone ?? "";
        _supplierChanged = true;
        SupplierOptionsPanel.IsVisible = false;
        SupplierSearchBox.Text = "";
        BuildSupplierCurrent();
    }

    private async void NewSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (OfflineModeHelper.UseLocalOperations)
            return;
        try
        {
            // Список нужен для подсказки «такой номер уже есть»; не загрузился — добавить всё равно можно.
            await EnsureSuppliersLoadedAsync().ConfigureAwait(true);
            var existing = _suppliers.Select(s => new ClientRow { Id = s.Id, FullName = s.Name, Phone = s.Phone ?? "" }).ToList();
            await new NewClientDialog(AddSupplierAsync, existing, supplier: true).ShowDialog(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Новый поставщик» не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>Сохранение окна «Новый поставщик»: null — добавлен и выбран (окно закроется), иначе текст ошибки.
    /// Поставщик с тем же названием уже есть — выбираем его, второй такой же не создаём.</summary>
    private async Task<string?> AddSupplierAsync(string name, string phone, string email, string address)
    {
        var key = PurchaseReceivingService.SupplierNameKey(name);
        if (key.Length > 0 && _suppliers.FirstOrDefault(s => PurchaseReceivingService.SupplierNameKey(s.Name) == key) is { } same)
        {
            SelectSupplier(same.Id, same.Name, same.Phone);
            return null;
        }
        try
        {
            var created = await PurchaseReceivingService.Instance.CreateSupplierAsync(name, phone).ConfigureAwait(true);
            if (created is null)
                return Tr.T("Не удалось добавить поставщика.", "Жеткирүүчүнү кошуу мүмкүн болгон жок.", "Could not add the supplier.", "Tedarikçi eklenemedi.",
                    "Yetkazib beruvchini qo'shib bo'lmadi.");
            _suppliers = _suppliers.Append(created).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SelectSupplier(created.Id, created.Name, created.Phone);
            PosLogger.Log($"Карточка товара: добавлен поставщик {created.Id}.", "WAREHOUSE");
            return null;
        }
        catch (ApiException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Supplier create failed: {ex}", "WARNING");
            return Tr.T("Не удалось добавить поставщика: ", "Жеткирүүчүнү кошуу мүмкүн болгон жок: ", "Could not add the supplier: ", "Tedarikçi eklenemedi: ",
                "Yetkazib beruvchini qo'shib bo'lmadi: ") + ex.Message;
        }
    }
}
