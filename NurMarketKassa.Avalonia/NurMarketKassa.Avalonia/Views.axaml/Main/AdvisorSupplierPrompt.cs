using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, жалоба владельца «нет создания поставщика при нажатии «Оприходовать товар» в Нур Советнике»: реплика в карточке накладной
/// Нур Советника — «Поставщик «X» не найден — создать?» с кнопками «Создать» / «Выбрать другого» / «Без поставщика». Поставщик найден — «Найден: «Y» — по ИНН · 100 %»
/// и «Выбрать его». Нейросеть здесь не участвует: список поставщиков — PurchaseReceivingService.LoadSuppliersAsync, создание — форма «Поставщики»
/// (SupplierResolve.CreateAsync). Выбор переходит в окно закупки (<see cref="Attach"/>), где его всё равно можно поменять.</summary>
public sealed class AdvisorSupplierPrompt : Border
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly Window _owner;
    private readonly string? _name, _inn, _phone, _address;
    private readonly StackPanel _body = new() { Spacing = 6 };
    private List<PurchaseReceivingService.Supplier>? _list;
    private bool _loading = true, _failed, _busy, _pickingOther;
    private PurchaseReceivingService.Supplier? _picked;
    private bool _none;

    public AdvisorSupplierPrompt(Window owner, string? name, string? inn, string? phone, string? address)
    {
        _owner = owner;
        _name = name;
        _inn = inn;
        _phone = phone;
        _address = address;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 8);
        BorderThickness = new Thickness(1);
        Child = _body;
        Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        Render();
        _ = LoadAsync();
    }

    // 2026-10-30 (приёмка 1.17.66): «текущий поставщик» — одно состояние в черновике закупки (PurchaseDraft.Supplier / NoSupplier), его же читает
    // проведение. Раньше карточка копировала выбор в черновик только при ПЕРВОМ открытии окна — выбранный позже не доходил, и долг уходил прежнему.
    // Теперь карточка привязана к черновику: выбор владельца записывается в него сразу и при каждом открытии окна, а после закрытия окна карточка
    // показывает то, что стоит в черновике (поставщика могли сменить в самом окне).
    private InvoiceEditorWindow.PurchaseDraft? _draft;
    private bool _dirty;

    /// <summary>Привязать карточку к черновику закупки (вызывается при каждом открытии окна закупки): выбор владельца в карточке → черновик
    /// (окно откроется уже с этим поставщиком или с «Без поставщика»). Ничего не выбрано — черновик не трогаем.</summary>
    public void Attach(InvoiceEditorWindow.PurchaseDraft draft)
    {
        _draft = draft;
        _dirty |= _picked is not null || _none;
        Push();
        if (_list is not null)
            draft.Suppliers ??= _list;
    }

    private void Push()
    {
        if (_draft is null || _draft.Posting || !_dirty)
            return;
        _dirty = false;
        _draft.Supplier = _picked;
        _draft.NoSupplier = _picked is null && _none;
    }

    /// <summary>Окно закупки закрыто: в карточке — тот поставщик, который стоит в черновике (его могли выбрать или создать в самом окне).</summary>
    public void SyncFromDraft()
    {
        if (_draft is null)
            return;
        var supplier = _draft.Supplier;
        var none = supplier is null && _draft.NoSupplier;
        if (_picked?.Id == supplier?.Id && _none == none)
            return;
        _picked = supplier;
        _none = none;
        _dirty = false;
        Render();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _list = await PurchaseReceivingService.Instance.LoadSuppliersAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _failed = true;
            PosLogger.Log($"Нур Советник: список поставщиков для накладной не загружен ({ex.Message}).", "WARNING");
        }
        finally
        {
            _loading = false;
            Render();
        }
    }

    private TextBlock Line(string text, string brush, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private Button Btn(string text, Func<Task> click)
    {
        var b = UiKit.Ghost(this, text);
        b.Height = 40;
        b.IsEnabled = !_busy;
        b.Click += async (_, _) =>
        {
            if (_busy)
                return;
            _busy = true;
            b.IsEnabled = false; // 2026-10-30: кнопка заблокирована на время запроса (повторный клик «Создать» не нужен)
            var (before, beforeNone) = (_picked, _none);
            try
            {
                await click().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Нур Советник: действие с поставщиком накладной не выполнено: {ex.Message}", "WARNING");
            }
            finally
            {
                _busy = false;
                if (!Equals(before, _picked) || beforeNone != _none)
                {
                    _dirty = true;
                    Push(); // 2026-10-30: выбор сразу в черновик закупки, если окно уже открывали
                }
                Render();
            }
        };
        return b;
    }

    private void Render()
    {
        _body.Children.Clear();
        var shown = string.IsNullOrWhiteSpace(_name) ? (_inn ?? "?") : _name!;
        var innWord = T("ИНН", "ИНН", "TIN", "VKN", "STIR");
        if (_picked is not null || _none)
        {
            _body.Children.Add(Line(_picked is not null
                ? "✓ " + T($"Поставщик закупки: «{_picked.Name}»", $"Сатып алуунун жеткирүүчүсү: «{_picked.Name}»", $"Purchase supplier: “{_picked.Name}”", $"Alımın tedarikçisi: «{_picked.Name}»", $"Xaridning yetkazib beruvchisi: «{_picked.Name}»")
                : "⚠ " + T("Без поставщика: приход запишется только в остатки товаров.", "Жеткирүүчүсүз: киреше товарлардын калдыгына гана жазылат.", "No supplier: the receipt only goes into product stock.",
                    "Tedarikçisiz: giriş yalnızca ürün stoğuna yazılır.", "Yetkazib beruvchisiz: kirim faqat mahsulot qoldig'iga yoziladi."),
                _picked is not null ? "BrushText" : "BrushWarning"));
            _body.Children.Add(Btn(T("Изменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish"), () => { _picked = null; _none = false; return Task.CompletedTask; }));
            return;
        }
        if (_loading)
        {
            _body.Children.Add(Line(T($"Ищу поставщика «{shown}» в NurCRM…", $"«{shown}» жеткирүүчүсүн NurCRM'ден издеп жатам…", $"Looking for supplier “{shown}” in NurCRM…",
                $"«{shown}» tedarikçisi NurCRM'de aranıyor…", $"«{shown}» yetkazib beruvchisi NurCRM'dan qidirilmoqda…"), "BrushTextSoft"));
            return;
        }
        if (_failed || _list is null)
        {
            _body.Children.Add(Line(T("Список поставщиков не загрузился — выберите поставщика в окне закупки («Провести закупку…»).", "Жеткирүүчүлөрдүн тизмеси жүктөлгөн жок — жеткирүүчүнү сатып алуу терезесинен тандаңыз («Сатып алууну өткөрүү…»).",
                "The supplier list did not load — choose the supplier in the purchase window (“Post purchase…”).", "Tedarikçi listesi yüklenemedi — tedarikçiyi alım penceresinden seçin («Alımı işle…»).",
                "Yetkazib beruvchilar ro'yxati yuklanmadi — yetkazib beruvchini xarid oynasida tanlang («Xaridni o'tkazish…»)."), "BrushTextSoft"));
            return;
        }

        var match = SupplierResolve.Find(_list, _name, _inn);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (match is not null)
        {
            var found = match.Supplier;
            _body.Children.Add(Line(T($"Поставщик «{shown}» найден: «{found.Name}» — {match.Reason}.", $"«{shown}» жеткирүүчүсү табылды: «{found.Name}» — {match.Reason}.", $"Supplier “{shown}” found: “{found.Name}” — {match.Reason}.",
                $"«{shown}» tedarikçisi bulundu: «{found.Name}» — {match.Reason}.", $"«{shown}» yetkazib beruvchisi topildi: «{found.Name}» — {match.Reason}."), "BrushText", bold: true));
            buttons.Children.Add(Btn(T($"Выбрать «{found.Name}»", $"«{found.Name}» тандоо", $"Choose “{found.Name}”", $"«{found.Name}» seç", $"«{found.Name}»ni tanlash"), () => { _picked = found; return Task.CompletedTask; }));
        }
        else
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(_inn))
                details.Add($"{innWord} {_inn}");
            if (!string.IsNullOrWhiteSpace(_phone))
                details.Add(_phone!);
            if (!string.IsNullOrWhiteSpace(_address))
                details.Add(_address!);
            _body.Children.Add(Line(T($"Поставщик «{shown}» не найден — создать?", $"«{shown}» жеткирүүчүсү табылган жок — түзөбүзбү?", $"Supplier “{shown}” not found — create it?",
                $"«{shown}» tedarikçisi bulunamadı — oluşturulsun mu?", $"«{shown}» yetkazib beruvchisi topilmadi — yaratamizmi?"), "BrushText", bold: true));
            if (details.Count > 0)
                _body.Children.Add(Line(string.Join(" · ", details), "BrushTextSoft"));
            if (SupplierResolve.CanCreate())
                buttons.Children.Add(Btn(T("Создать", "Түзүү", "Create", "Oluştur", "Yaratish"), async () =>
                {
                    var created = await SupplierResolve.CreateAsync(_owner, _list, SupplierResolve.Prefill.From(_name, _inn, _phone, _address)).ConfigureAwait(true);
                    if (created is null)
                        return;
                    if (_list.All(x => x.Id != created.Id))
                        _list.Add(created);
                    _picked = created;
                }));
            else
                _body.Children.Add(Line(SupplierResolve.AskOwnerText, "BrushWarning"));
        }
        buttons.Children.Add(Btn(T("Выбрать другого", "Башкасын тандоо", "Choose another", "Başkasını seç", "Boshqasini tanlash"), () => { _pickingOther = !_pickingOther; return Task.CompletedTask; }));
        buttons.Children.Add(Btn(T("Без поставщика", "Жеткирүүчүсүз", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz"), async () =>
        {
            if (await SupplierResolve.ConfirmNoSupplierAsync(_owner).ConfigureAwait(true))
                _none = true;
        }));
        _body.Children.Add(buttons);
        if (_pickingOther)
        {
            // Выбор из списка с поиском — тот же PurchasesSupplierPicker, что в «Закупках» (без «＋»: создание — кнопкой «Создать» выше).
            var picker = new PurchasesSupplierPicker(new SupplierApiService(App.GetRequiredService<NurMarketApiClient>())) { ShowAdd = false, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
            picker.SetSuppliers(_list.Select(s => new SupplierInfo(s.Id, s.Name, s.Phone ?? "", "", "", s.Inn ?? "", "", "", "", "", null)));
            picker.Changed += chosen =>
            {
                if (chosen is null)
                    return;
                _picked = _list.FirstOrDefault(x => x.Id == chosen.Id) ?? new PurchaseReceivingService.Supplier(chosen.Id, chosen.Name, chosen.Inn.Length > 0 ? chosen.Inn : null, chosen.Phone.Length > 0 ? chosen.Phone : null);
                _pickingOther = false;
                _dirty = true;
                Push();
                Render();
            };
            _body.Children.Add(picker);
        }
    }
}
