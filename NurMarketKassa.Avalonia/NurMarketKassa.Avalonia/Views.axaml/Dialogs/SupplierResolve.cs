using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-30, жалоба владельца «нет создания поставщика при нажатии «Оприходовать товар» в Нур Советнике»: единое место для всех
/// окон накладной (окно закупки Нур Советника — InvoiceEditorWindow, чат Нур Советника — AdvisorSupplierPrompt, «Загрузить накладную» —
/// InvoiceImportWindow). Здесь: подбор поставщика из накладной с причиной и процентом уверенности, право создавать, защита от дубля
/// («похожий поставщик уже есть — использовать его?») и создание поставщика формой «Поставщики» (SupplierEditDialog, те же поля и проверки),
/// предзаполненной из накладной. Новых запросов нет: создание — SupplierApiService.CreateAsync (POST clients/, type=suppliers).</summary>
public static class SupplierResolve
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public enum MatchKind { Inn, Name, Similar }

    /// <param name="Percent">Уверенность подбора: ИНН — 100, точное название — 95, «похоже по названию» — 50–85.</param>
    public sealed record Match(PurchaseReceivingService.Supplier Supplier, MatchKind Kind, int Percent)
    {
        /// <summary>«по ИНН · 100 %» — причина и уверенность для подписи рядом с найденным поставщиком.</summary>
        public string Reason => Kind switch
        {
            MatchKind.Inn => T("по ИНН", "ИНН боюнча", "by tax ID", "vergi numarasına göre", "STIR bo'yicha"),
            MatchKind.Name => T("по названию", "аталышы боюнча", "by name", "ada göre", "nomi bo'yicha"),
            _ => T("название похоже", "аталышы окшош", "similar name", "ad benzer", "nomi o'xshash"),
        } + $" · {Percent} %";
    }

    /// <summary>Что известно о поставщике из накладной (всё, кроме названия, может быть пустым).</summary>
    public sealed record Prefill(string Name, string Inn, string Phone, string Address, string Bank, string Contact, string Note)
    {
        public static Prefill From(string? name, string? inn, string? phone = null, string? address = null, string? invoiceNumber = null) =>
            new((name ?? "").Trim(), Digits(inn), (phone ?? "").Trim(), (address ?? "").Trim(), "", "",
                string.IsNullOrWhiteSpace(invoiceNumber)
                    ? T("Создан из накладной", "Накладнойдон түзүлдү", "Created from an invoice", "Faturadan oluşturuldu", "Yuk xatidan yaratildi")
                    : T($"Создан из накладной № {invoiceNumber!.Trim()}", $"№ {invoiceNumber!.Trim()} накладнойдон түзүлдү", $"Created from invoice No. {invoiceNumber!.Trim()}",
                        $"{invoiceNumber!.Trim()} no'lu faturadan oluşturuldu", $"№ {invoiceNumber!.Trim()} yuk xatidan yaratildi"));

        public bool IsEmpty => Name.Length == 0 && Inn.Length == 0;
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Создавать поставщиков можно тому, кто открывает раздел «Поставщики»: право «Склад» (PosPermissions.ViewProducts) — то же, что
    /// у OwnerShellWindow для «Поставщики» и у «Загрузить накладную». Иначе — подсказка <see cref="AskOwnerText"/>.</summary>
    public static bool CanCreate()
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewProducts);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поставщик из накладной: проверка прав не удалась: {ex.Message}", "WARNING");
            return false;
        }
    }

    public static string AskOwnerText => T("Попросите владельца добавить поставщика.", "Ээсинен жеткирүүчүнү кошуусун сураныңыз.", "Ask the owner to add the supplier.",
        "İşletme sahibinden tedarikçiyi eklemesini isteyin.", "Egasidan yetkazib beruvchini qo'shishni so'rang.");

    /// <summary>Подбор поставщика из накладной среди списка: сначала ИНН, затем название (PurchaseReceivingService.MatchSupplier) + причина и процент.</summary>
    public static Match? Find(IEnumerable<PurchaseReceivingService.Supplier> suppliers, string? name, string? inn)
    {
        var found = PurchaseReceivingService.MatchSupplier(suppliers, name, inn);
        if (found is null)
            return null;
        var d = Digits(inn);
        if (d.Length >= 6 && Digits(found.Inn) == d)
            return new Match(found, MatchKind.Inn, 100);
        var a = PurchaseReceivingService.SupplierNameKey(name);
        var b = PurchaseReceivingService.SupplierNameKey(found.Name);
        if (a == b)
            return new Match(found, MatchKind.Name, 95);
        var ratio = a.Length == 0 || b.Length == 0 ? 50 : (int)Math.Round(100.0 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length));
        return new Match(found, MatchKind.Similar, Math.Clamp(ratio, 50, 85));
    }

    /// <summary>ИНН: пусто — можно; иначе только цифры, 9–14 штук (КР — 14, РФ — 10/12, KZ — 12, UZ — 9; та же граница, что у разбора накладной).
    /// Возвращает текст ошибки или null.</summary>
    public static string? InnProblem(string? inn)
    {
        var raw = (inn ?? "").Trim();
        if (raw.Length == 0)
            return null;
        if (raw.Any(c => !char.IsDigit(c)) || raw.Length is < 9 or > 14)
            return T("ИНН — только цифры, от 9 до 14 (Кыргызстан — 14, Россия — 10 или 12).", "ИНН — цифралар гана, 9дан 14кө чейин (Кыргызстан — 14, Россия — 10 же 12).",
                "Tax ID — digits only, 9 to 14 (Kyrgyzstan — 14, Russia — 10 or 12).", "Vergi no — yalnızca rakam, 9 ile 14 arası (Kırgızistan — 14, Rusya — 10 veya 12).",
                "STIR — faqat raqamlar, 9 dan 14 gacha (Qirg'iziston — 14, Rossiya — 10 yoki 12).");
        return null;
    }

    private static SupplierInfo ToInfo(PurchaseReceivingService.Supplier s) =>
        new(s.Id, s.Name, s.Phone ?? "", "", "", s.Inn ?? "", "", "", "", "", null);

    /// <summary>Создать поставщика из накладной. Сначала защита от дубля: поставщик с тем же ИНН или похожим названием уже есть — спрашиваем
    /// «использовать его?» (да — возвращается он, ничего не создаётся). Иначе открывается форма «Новый поставщик» с полями из накладной.
    /// null — отмена или ошибка (в журнале).</summary>
    public static async Task<PurchaseReceivingService.Supplier?> CreateAsync(Window owner, IReadOnlyList<PurchaseReceivingService.Supplier> known, Prefill prefill)
    {
        // 2026-10-30 (приёмка 1.17.66): защита от дубля поставщика. (1) Повторный «Создать» того же поставщика (двойной клик, второе окно, карточка
        // советника + «Загрузить накладную»), пока первое создание не закончилось, не открывает вторую форму — ждёт первую и берёт её итог.
        // (2) Перед формой список освежается с сервера и дополняется созданными в этом запуске: тот же ИНН или название уже есть — берём найденного.
        var key = CreateKey(prefill);
        TaskCompletionSource<PurchaseReceivingService.Supplier?> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PurchaseReceivingService.Supplier?>? running;
        lock (Gate)
        {
            if (!InFlight.TryGetValue(key, out running))
                InFlight[key] = mine.Task;
        }
        if (running is not null)
        {
            PosLogger.Log($"Поставщик из накладной: создание «{prefill.Name}» уже идёт — повторный запуск не нужен.", "INFO");
            return await running.ConfigureAwait(true);
        }
        PurchaseReceivingService.Supplier? result = null;
        try
        {
            result = await CreateCoreAsync(owner, known, prefill).ConfigureAwait(true);
            return result;
        }
        finally
        {
            lock (Gate)
                InFlight.Remove(key);
            mine.TrySetResult(result);
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<PurchaseReceivingService.Supplier?>> InFlight = new();
    private static readonly List<PurchaseReceivingService.Supplier> CreatedHere = new();

    private static string CreateKey(Prefill prefill) =>
        prefill.Inn.Length >= 6 ? "inn:" + prefill.Inn
        : PurchaseReceivingService.SupplierNameKey(prefill.Name) is { Length: > 0 } n ? "name:" + n : "*";

    private static async Task<PurchaseReceivingService.Supplier?> CreateCoreAsync(Window owner, IReadOnlyList<PurchaseReceivingService.Supplier> known, Prefill prefill)
    {
        if (!CanCreate())
        {
            PosDialogs.Info(owner, AskOwnerText, T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
            return null;
        }
        var list = known.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
        var seenBefore = Find(list, prefill.Name, prefill.Inn)?.Supplier.Id;
        void Merge(IEnumerable<PurchaseReceivingService.Supplier> more)
        {
            foreach (var s in more)
                if (!string.IsNullOrEmpty(s.Id) && list.All(x => x.Id != s.Id))
                    list.Add(s);
        }
        lock (Gate)
            Merge(CreatedHere.ToList());
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Merge(await PurchaseReceivingService.Instance.LoadSuppliersAsync(cts.Token).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поставщик из накладной: свежий список поставщиков не получен ({ex.Message}) — сверяю с имеющимся.", "WARNING");
        }
        // Точное совпадение (ИНН или название), которого в устаревшем списке не было, — поставщика только что создали в другом месте: берём его.
        if (Find(list, prefill.Name, prefill.Inn) is { Kind: MatchKind.Inn or MatchKind.Name } fresh && fresh.Supplier.Id != seenBefore)
        {
            PosLogger.Log($"Поставщик из накладной: «{fresh.Supplier.Name}» уже создан ({fresh.Supplier.Id}, {fresh.Kind}) — использую его, второй не создаю.", "INFO");
            return fresh.Supplier;
        }
        if (Find(list, prefill.Name, prefill.Inn) is { Kind: MatchKind.Inn } sameInn)
        {
            // 2026-10-31 (приёмка 1.17.68, С-7): ИНН совпал — это тот же поставщик; «Создать нового» не предлагаем (второй поставщик с тем же ИНН — дубль).
            PosLogger.Log($"Поставщик из накладной: ИНН совпал с существующим {sameInn.Supplier.Id} — использую его, второй не создаю.", "INFO");
            return sameInn.Supplier;
        }
        if (Find(list, prefill.Name, prefill.Inn) is { } dup)
        {
            var name = dup.Supplier.Name;
            var use = await PosDialogs.ConfirmYesNoModalAsync(owner,
                T($"Похожий поставщик уже есть: «{name}» ({dup.Reason}). Использовать его, а не создавать нового?",
                    $"Окшош жеткирүүчү бар: «{name}» ({dup.Reason}). Жаңысын түзбөй, ушуну колдоносузбу?",
                    $"A similar supplier already exists: “{name}” ({dup.Reason}). Use it instead of creating a new one?",
                    $"Benzer bir tedarikçi zaten var: «{name}» ({dup.Reason}). Yenisini oluşturmak yerine bunu kullanın mı?",
                    $"O'xshash yetkazib beruvchi allaqachon bor: «{name}» ({dup.Reason}). Yangisini yaratmasdan shuni ishlatamizmi?"),
                T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"),
                T("Использовать его", "Муну колдонуу", "Use it", "Bunu kullan", "Shuni ishlatish"),
                T("Создать нового", "Жаңысын түзүү", "Create new", "Yenisini oluştur", "Yangisini yaratish")).ConfigureAwait(true);
            if (use)
            {
                PosLogger.Log($"Поставщик из накладной: вместо создания выбран существующий {dup.Supplier.Id} ({dup.Kind}, {dup.Percent} %).", "INFO");
                return dup.Supplier;
            }
        }
        var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        var requisites = new SupplierRequisites(prefill.Name, prefill.Phone, "", "", prefill.Inn, "", prefill.Bank, "", prefill.Address);
        var dialog = new SupplierEditDialog(svc, null, list.Select(ToInfo).ToList(), requisites, prefill.Note);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Saved is not { } saved)
            return null;
        PosLogger.Log($"Поставщик из накладной: создан {saved.Id}.", "INFO");
        var createdSupplier = new PurchaseReceivingService.Supplier(saved.Id, saved.Name, saved.Inn.Length > 0 ? saved.Inn : null, saved.Phone.Length > 0 ? saved.Phone : null);
        lock (Gate)
            CreatedHere.Add(createdSupplier);
        return createdSupplier;
    }

    /// <summary>«Без поставщика» — приход не создаёт долг и расход из кассы, остаток пишется прямо в товары. Предупреждение с подтверждением.</summary>
    public static Task<bool> ConfirmNoSupplierAsync(Window owner) => PosDialogs.ConfirmYesNoModalAsync(owner,
        T("Без поставщика приход запишется только в остаток и цены товаров: долг поставщику, расход из кассы и история закупок по поставщику не создаются. Продолжить без поставщика?",
            "Жеткирүүчүсүз киреше товарлардын калдыгына жана баасына гана жазылат: жеткирүүчүгө карыз, кассадан чыгым жана жеткирүүчү боюнча сатып алуу тарыхы түзүлбөйт. Жеткирүүчүсүз улантасызбы?",
            "Without a supplier the receipt only updates product stock and prices: no supplier debt, no cash expense and no purchase history for a supplier. Continue without a supplier?",
            "Tedarikçisiz giriş yalnızca ürün stoğunu ve fiyatlarını günceller: tedarikçi borcu, kasa gideri ve tedarikçi alım geçmişi oluşmaz. Tedarikçisiz devam edilsin mi?",
            "Yetkazib beruvchisiz kirim faqat mahsulot qoldig'i va narxlarini yangilaydi: yetkazib beruvchiga qarz, kassadan xarajat va xarid tarixi yaratilmaydi. Yetkazib beruvchisiz davom etamizmi?"),
        T("Без поставщика", "Жеткирүүчүсүз", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz"),
        T("Без поставщика", "Жеткирүүчүсүз", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz"),
        T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
}
