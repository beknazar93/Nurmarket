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

// 2026-11-06: В15г — часть класса ProductEditDialog (Save): перенесено из ProductEditDialog.axaml.cs без изменений логики.
public partial class ProductEditDialog
{
    /// <summary>2026-09-17: не своя экранная клавиатура — пользователь уже один раз (2026-09-15,
    /// см. MainWindow.ToggleKeyboard) явно попросил вернуть системную клавиатуру Windows вместо
    /// самодельной, та же логика применяется и здесь.</summary>
    private void ToggleKeyboard_Click(object? sender, RoutedEventArgs e) =>
        App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();

    public string ErrorMessage { get => _errorMessage; private set { _errorMessage = value; OnPropertyChanged(); } }
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    public bool CanSaveNow => !_isSaving && !string.IsNullOrWhiteSpace(_name);

    private static string ExtractPrice(string priceLine)
    {
        var digits = new string((priceLine ?? "").TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray());
        // 2026-10-20, аудит программы владельца: «Цена продажи 85.00» — с точкой, а везде запятая. Показываем с запятой;
        // ParseNumber принимает и запятую, и точку — сохранение не меняется.
        return digits.Replace('.', ',');
    }

    private void GenerateBarcode()
    {
        // 2026-10-26 (требование владельца): общий генератор BarcodeGenerator — EAN-13, первая цифра 1–9 (не 0)
        // и не 2 (префиксы 20–29 касса читает как весовые), криптослучайные цифры, без повтора с кодами каталога.
        // Раньше были случайные 12 цифр без проверки уникальности и без защиты от весовых префиксов.
        Barcode = BarcodeGenerator.GenerateForCatalog();
    }

    /// <summary>2026-11-05 (С2-34): текст ошибки, если новый/изменённый штрихкод уже занят другим товаром каталога или повторён в карточке; null — всё в порядке.</summary>
    private string? FindBarcodeConflict()
    {
        string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();
        var oldCodes = new HashSet<string>();
        if (_existing is not null)
        {
            oldCodes.Add(Norm(_existing.Barcode));
            foreach (var v in _existing.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>())
                oldCodes.Add(Norm(v.Barcode));
        }

        var mine = new List<(string Code, string Raw)> { (Norm(_barcode), _barcode.Trim()) };
        mine.AddRange(_variants.Select(v => (Norm(v.Barcode), (v.Barcode ?? "").Trim())));
        mine = mine.Where(m => m.Code.Length > 0).ToList();

        var dup = mine.GroupBy(m => m.Code).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            var code = dup.First().Raw;
            return Tr.T($"Штрихкод {code} повторяется в этой карточке (основной и дополнительные не должны совпадать).",
                $"{code} штрихкоду бул карточкада кайталанды (негизги жана кошумча штрихкоддор дал келбеши керек).",
                $"Barcode {code} is repeated in this card (the main and extra barcodes must differ).",
                $"{code} barkodu bu kartta tekrarlanıyor (ana ve ek barkodlar farklı olmalı).",
                $"{code} shtrix-kodi bu kartada takrorlandi (asosiy va qo'shimcha shtrix-kodlar har xil bo'lishi kerak).");
        }

        var fresh = mine.Where(m => !oldCodes.Contains(m.Code)).ToList();
        if (fresh.Count == 0)
            return null;
        List<CatalogProductTileVm> snapshot;
        try { snapshot = CatalogCacheService.Products.ToList(); }
        catch (InvalidOperationException) { return null; } // каталог обновлялся в этот момент — серверная проверка при сохранении остаётся
        foreach (var (code, raw) in fresh)
        {
            var owner = snapshot.FirstOrDefault(p =>
                !string.Equals(p.Id, _existing?.Id, StringComparison.OrdinalIgnoreCase)
                && (Norm(p.Barcode) == code || (p.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>()).Any(a => Norm(a.Barcode) == code)));
            if (owner is not null)
                return Tr.T($"Штрихкод {raw} уже есть у товара «{owner.Title}». Один штрихкод не может быть у двух товаров.",
                    $"{raw} штрихкоду «{owner.Title}» товарында мурунтан бар. Бир штрихкод эки товарда болушу мүмкүн эмес.",
                    $"Barcode {raw} already belongs to “{owner.Title}”. One barcode cannot be on two products.",
                    $"{raw} barkodu zaten «{owner.Title}» ürününde var. Bir barkod iki üründe olamaz.",
                    $"{raw} shtrix-kodi «{owner.Title}» mahsulotida allaqachon bor. Bitta shtrix-kod ikki mahsulotda bo'lishi mumkin emas.");
        }
        return null;
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            ErrorMessage = Tr.T("Введите наименование товара.", "Товардын аталышын киргизиңиз.", "Enter the product name.", "Ürün adını girin.", "Mahsulot nomini kiriting.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_barcode))
        {
            ErrorMessage = Tr.T(
                "Введите штрихкод (или нажмите «Сгенерировать»).",
                "Штрихкодду киргизиңиз (же «Түзүү» баскычын басыңыз).", "Enter a barcode (or click “Generate”).", "Barkodu girin (veya «Oluştur»a basın).", "Shtrix-kodni kiriting (yoki «Yaratish» tugmasini bosing).");
            return;
        }

        // 2026-10-29: штрихкод нового товара (или изменённый) — только цифры, 4–14 знаков.
        var barcodeChanged = _existing is null || !string.Equals(_barcode.Trim(), _existing.Barcode ?? "", StringComparison.Ordinal);
        if (barcodeChanged && !BarcodeInput.IsValid(_barcode.Trim()))
        {
            ErrorMessage = BarcodeInput.Hint(_barcode.Trim()) is { Length: > 0 } hint ? hint : BarcodeInput.Hint("0");
            return;
        }

        // 2026-11-05 (аудит №3, С2-34): штрихкод (основной или дополнительный), который уже принадлежит ДРУГОМУ товару каталога или повторяется в этой же карточке,
        // не сохраняем: касса при сканировании нашла бы не тот товар. Проверяются только новые/изменённые коды (старые дубли не блокируют правку названия).
        if (FindBarcodeConflict() is { } conflictText)
        {
            ErrorMessage = conflictText;
            return;
        }

        // 2026-10-29, жалоба владельца «нет цены, количества и единицы»: числа проверяются единым разбором CheckoutValidation.TryParseAmount
        // (запятая и точка, пробелы разрядов; «abc», «1e3», «-5», «1.000» — ошибка с понятным текстом, а не молчаливый ноль), как в приёмке и оплате.
        // Количество — не меньше нуля; у штучной единицы («шт», «уп», «пара») — целое; у весового/«кг»/«л»/«м» — дробное (0,5; 2,25).
        // Остаток существующего товара, который не трогали, не проверяется: у товара продано «в минус» остаток бывает отрицательным, и правка названия не должна из-за него блокироваться.
        double? quantityValue = null;
        var quantityUntouched = _existing is not null && string.Equals(_quantity, _existing.Quantity.ToString("0.###", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (quantityUntouched)
            quantityValue = _existing!.Quantity;
        if ((!quantityUntouched && !CheckAmount(_quantity, ("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), 3, out quantityValue))
            || !CheckAmount(_purchasePrice, ("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), 4, out var purchaseValue, ServerPriceLimits.PurchaseTooBigText, ServerPriceLimits.PurchaseExceeds)
            || !CheckAmount(_price, ("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), 2, out var saleValue, ServerPriceLimits.SaleTooBigText, ServerPriceLimits.SaleExceeds)
            || !CheckAmount(_wholesalePrice, ("Оптовая цена", "Оптом баасы", "Wholesale price", "Toptan fiyat", "Ulgurji narx"), 2, out var wholesaleValue, ServerPriceLimits.SaleTooBigText, ServerPriceLimits.SaleExceeds)
            || !CheckAmount(_minimumQuantity, ("Минимальный остаток", "Минималдуу калдык", "Minimum stock", "Asgari stok", "Minimal qoldiq"), 3, out var minimumValue)
            || !CheckAmount(_discountPercent, ("Скидка, %", "Арзандатуу, %", "Discount, %", "İndirim, %", "Chegirma, %"), 2, out var discountValue)
            || !CheckMarkup(_markupPercent))
            return;
        if (discountValue is > 100)
        {
            ErrorMessage = Tr.T("Скидка: не больше 100 %.", "Арзандатуу: 100 %дан ашпайт.", "Discount: no more than 100%.", "İndirim: en fazla %100.", "Chegirma: 100 % dan oshmasligi kerak.");
            return;
        }
        var quantityChanged = _existing is null || Math.Abs((quantityValue ?? 0) - _existing.Quantity) > 0.0005;
        if (_kind == "product" && quantityChanged && UnitCatalog.QuantityProblem(quantityValue ?? 0, _unit, _isWeight) is { } fractionalProblem)
        {
            ErrorMessage = fractionalProblem;
            return;
        }
        // Поштучная продажа: штук — целое от 2, цена штуки больше нуля; цена штуки × штук далеко от цены упаковки — спрашиваем (как в мобилке).
        if (_enablePieceSale && _kind == "product")
        {
            var packQty = TryParseNumber(_packageQuantity) ?? 0;
            var pieceValue = TryParseNumber(_packagePiecePrice) ?? 0;
            if (packQty < 2 || Math.Abs(packQty - Math.Round(packQty)) > 1e-9)
            {
                ErrorMessage = Tr.T("Упаковка: «Штук в упаковке» — целое число от 2.", "Таңгак: «Таңгактагы штук саны» — 2ден баштап бүтүн сан.", "Pack: “Pieces in a pack” must be a whole number, 2 or more.",
                    "Paket: «Paketteki adet» 2 veya daha büyük bir tam sayı olmalı.", "Qadoq: «Qadoqdagi dona soni» 2 dan boshlab butun son bo'lishi kerak.");
                return;
            }
            if (pieceValue <= 0)
            {
                ErrorMessage = Tr.T("Упаковка: укажите «Цена за штуку» (или нажмите «Авто»).", "Таңгак: «Штуканын баасын» жазыңыз (же «Авто» басыңыз).", "Pack: enter the “Price per piece” (or press “Auto”).",
                    "Paket: «Adet fiyatı»nı girin (veya «Oto»ya basın).", "Qadoq: «Dona narxi»ni kiriting (yoki «Avto»ni bosing).");
                return;
            }
            if (HasPackWarning)
            {
                var goOn = await PosConfirmDialog.ShowModalAsync(this,
                    Tr.T("Цена штуки не сходится с упаковкой", "Штуканын баасы таңгакка дал келбейт", "Piece price does not match the pack", "Adet fiyatı paketle uyuşmuyor", "Dona narxi qadoqqa mos kelmaydi"),
                    PackWarning.TrimStart('⚠', ' ') + " " + Tr.T("Сохранить как есть?", "Ушундайча сактайбызбы?", "Save as it is?", "Olduğu gibi kaydedilsin mi?", "Shundayligicha saqlansinmi?"),
                    Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), Tr.T("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga")).ConfigureAwait(true);
                if (!goOn)
                    return;
            }
        }
        // Цена продажи не указана — спрашиваем явно (было: товар молча сохранялся с ценой 0 и пробивался на кассе бесплатно).
        var oldPrice = _existing is null ? 0 : LocalCartService.ParsePrice(_existing.PriceLine);
        if ((saleValue ?? 0) <= 0 && (_existing is null || oldPrice > 0))
        {
            var withoutPrice = await PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Цена продажи не указана", "Сатуу баасы көрсөтүлгөн эмес", "No sale price", "Satış fiyatı girilmedi", "Sotuv narxi kiritilmagan"),
                Tr.T($"Товар «{_name.Trim()}» сохранится без цены продажи — пока вы её не впишете, его нельзя продать по нормальной цене. Сохранить без цены?",
                    $"«{_name.Trim()}» товары сатуу баасыз сакталат — баасын жазмайынча кадимки баада сатууга болбойт. Баасыз сактайбызбы?",
                    $"“{_name.Trim()}” will be saved without a sale price — it cannot be sold at a normal price until you enter one. Save without a price?",
                    $"«{_name.Trim()}» satış fiyatı olmadan kaydedilecek — girene kadar normal fiyattan satılamaz. Fiyatsız kaydedilsin mi?",
                    $"«{_name.Trim()}» sotuv narxisiz saqlanadi — narx kiritilmaguncha oddiy narxda sotib bo'lmaydi. Narxsiz saqlansinmi?"),
                Tr.T("Сохранить без цены", "Баасыз сактоо", "Save without price", "Fiyatsız kaydet", "Narxsiz saqlash"),
                Tr.T("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga")).ConfigureAwait(true);
            if (!withoutPrice)
                return;
        }

        _isSaving = true;
        OnPropertyChanged(nameof(CanSaveNow));
        UpdateSaveButtonState();
        ErrorMessage = "";
        StatusMessage = Tr.T("Сохранение...", "Сакталууда...", "Saving...", "Kaydediliyor...", "Saqlanmoqda...");
        try
        {
            int? pluValue = null;
            if (_isWeight)
            {
                if (int.TryParse(_plu.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPlu))
                {
                    pluValue = parsedPlu;
                }
                else
                {
                    // Как на сайте (useAddProductBootstrap/buildProductPayload): пустой PLU у
                    // весового товара автоназначается как count существующих весовых + 1.
                    // 2026-09-09: в офлайне (обычном или автономном) считаем по локальной базе —
                    // GetWeightProductCountAsync всё равно недоступен без сервера.
                    try
                    {
                        var count = OfflineModeHelper.UseLocalOperations
                            ? LocalProductEditor.GetLocalWeightProductCount()
                            : await _catalogApi.GetWeightProductCountAsync().ConfigureAwait(true);
                        pluValue = count + 1;
                        // 2026-09-30: «число весовых + 1» бывает уже занято (товары удаляли, PLU
                        // вписывали руками) — сервер тогда отвечает 400 «Этот PLU уже используется»,
                        // и товар не сохранялся. Берём следующий номер, свободный в каталоге кассы.
                        var usedPlu = CatalogCacheService.Products.Where(p => p.Plu is > 0).Select(p => p.Plu!.Value).ToHashSet();
                        while (usedPlu.Contains(pluValue.Value))
                            pluValue++;
                        Plu = pluValue.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"PLU autogen failed, saving without PLU: {ex}", "WAREHOUSE");
                    }
                }
            }

            // 2026-11-06 (класс Б, Б-3): у существующего товара на сервер идут только изменённые кассиром поля. Карточка могла открыться из памяти кассы
            // (сервер не ответил) — «шт»/не весовой/без штрихкода из сокращённой записи затёрли бы настоящие значения.
            HashSet<string>? unchangedKeys = null;
            if (_existing is not null)
            {
                var unchangedSet = new HashSet<string>();
                unchangedKeys = unchangedSet;
                void Same(bool same, params string[] keys)
                {
                    if (same)
                        foreach (var key in keys)
                            unchangedSet.Add(key);
                }
                Same(string.Equals(_name.Trim(), _origName.Trim(), StringComparison.Ordinal), "name");
                Same(string.Equals(_code.Trim(), _origCode.Trim(), StringComparison.Ordinal), "article");
                Same(string.Equals(_barcode.Trim(), _origBarcode.Trim(), StringComparison.Ordinal), "barcode");
                Same(string.Equals(_category.Trim(), _origCategory.Trim(), StringComparison.Ordinal), "category_name");
                Same(string.Equals(_brand.Trim(), _origBrand.Trim(), StringComparison.Ordinal), "brand_name");
                Same(string.Equals(_hotkeyGroup, _origHotkey, StringComparison.Ordinal), "hotkey_group");
                Same(string.Equals(_kind, _origKind, StringComparison.Ordinal), "kind");
                // Единица, «весовой» и PLU связаны (PLU есть только у весового) — уходят вместе или не уходят вовсе.
                Same(string.Equals(_unit, _origUnit, StringComparison.Ordinal) && _isWeight == _origIsWeight
                    && string.Equals(_plu.Trim(), _origPlu.Trim(), StringComparison.Ordinal), "unit", "is_weight", "plu");
            }

            var request = new ProductEditRequest
            {
                UnchangedKeys = unchangedKeys,
                Name = _name,
                Article = _code,
                Barcode = _barcode,
                // 2026-09-21: список "доп. штрихкодов" (_variants) — единственный источник
                // alternate_barcodes при сохранении (см. doc-comment у _variants.Add в конструкторе
                // и CatalogApiService.BuildAlternateBarcodes). Раньше эти строки создавали отдельные
                // новые товары вместо записи в это же поле — теперь пишут прямо сюда, как на сайте.
                AlternateBarcodes = string.Join("\n", _variants.Select(v => v.Barcode.Trim()).Where(b => b.Length > 0)),
                KnownAlternateBarcodeVariants = _variants
                    .Select(v => new AlternateBarcodeVariant
                    {
                        Barcode = v.Barcode.Trim(),
                        Name = string.IsNullOrWhiteSpace(v.Name) ? null : v.Name.Trim(),
                        Quantity = v.Quantity,
                    })
                    .ToList(),
                CategoryName = _category,
                BrandName = _brand,
                Unit = string.IsNullOrWhiteSpace(_unit) ? "шт" : _unit,
                IsWeight = _isWeight,
                Quantity = quantityValue ?? 0,
                // 2026-11-01 (аудит 10 из 10, С-01): правка существующего товара шлёт остаток и цены, только если кассир их менял.
                // Раньше PATCH нёс значения, прочитанные при открытии, и затирал продажи с других касс и цену, изменённую на сайте.
                SendQuantity = _existing is null || quantityChanged,
                // доп. штрихкоды (с количествами/названиями, прочитанными при открытии) — только если список менялся (приёмка С-01)
                // код-ревью: сравниваем полный состав (штрихкод + название + количество), а не только коды — правка названия/количества тоже должна уйти
                SendAlternateBarcodes = _existing is null || !_variants.Select(v => $"{v.Barcode.Trim().ToLowerInvariant()}|{(v.Name ?? "").Trim()}|{Math.Round(v.Quantity, 3).ToString("0.###", CultureInfo.InvariantCulture)}")
                    .Where(s => !s.StartsWith('|')).OrderBy(s => s, StringComparer.Ordinal)
                    .SequenceEqual((_existing.AlternateBarcodeVariants ?? new()).Select(v => $"{(v.Barcode ?? "").Trim().ToLowerInvariant()}|{(v.Name ?? "").Trim()}|{Math.Round(v.Quantity, 3).ToString("0.###", CultureInfo.InvariantCulture)}")
                        .Where(s => !s.StartsWith('|')).OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal),
                SendDiscountPercent = _existing is null || !string.Equals(_discountPercent.Trim(), _origDiscount, StringComparison.Ordinal),
                SendWholesalePrice = _existing is null || !string.Equals(_wholesalePrice.Trim(), _origWholesale, StringComparison.Ordinal),
                SendDescription = _existing is null || !string.Equals(_description.Trim(), _origDescription.Trim(), StringComparison.Ordinal),
                SendCountry = _existing is null || !string.Equals(_country.Trim(), _origCountry.Trim(), StringComparison.Ordinal),
                DisablePieceSale = _existing?.PieceOption is not null && !_enablePieceSale,
                SendCharacteristics = _existing is null || !string.Equals(DimensionsSignature(), _origDimensions, StringComparison.Ordinal),
                SendPurchasePrice = _existing is null || !string.Equals(_purchasePrice.Trim(), _origPurchasePrice, StringComparison.Ordinal),
                SendMarkupPercent = _existing is null || !string.Equals(_markupPercent.Trim(), _origMarkupPercent, StringComparison.Ordinal),
                SendPrice = _existing is null || !string.Equals(_price.Trim(), _origPrice, StringComparison.Ordinal),
                PurchasePrice = purchaseValue,
                MarkupPercent = TryParseNumber(_markupPercent),
                Price = saleValue,
                Description = _description,
                HotkeyGroup = _hotkeyGroup == "Без горячей клавиши" ? null : _hotkeyGroup,
                Plu = pluValue,
                WholesalePrice = wholesaleValue, // 2026-11-01 (С2-13): значение из проверки («1 500» — 1500, а не 0)
                MinimumQuantity = _minimumQuantityKnown ? minimumValue ?? 0 : null,
                // 2026-10-20: поставщик — только если кассир его выбрал/сменил в этом окне (иначе «client» на сервере не трогаем).
                SupplierId = _supplierChanged ? _supplierId : null,
                DiscountPercent = discountValue,
                HeightCm = TryParseNumber(_heightCm),
                WidthCm = TryParseNumber(_widthCm),
                DepthCm = TryParseNumber(_depthCm),
                WeightKg = TryParseNumber(_weightKg),
                Country = _country,
                EnablePieceSale = _enablePieceSale,
                PackageQuantity = TryParseNumber(_packageQuantity),
                PackagePiecePrice = TryParseNumber(_packagePiecePrice),
                IsNew = _existing is null,
                Kind = _kind,
                BundleItems = _kind == "bundle" ? _bundleItems.ToList() : null,
            };

            if (OfflineModeHelper.UseLocalOperations)
            {
                // 2026-09-09: офлайн (обычный или автономный режим) — товар пишется прямо в
                // локальную SQLite, минуя ICatalogApiService (реальный HTTP к NurCRM, которого
                // сейчас нет). Никогда не попадёт на сервер — ожидаемо для автономного режима.
                LocalProductEditor.SaveLocally(request, _existing?.Id);
            }
            else if (_existing is null)
            {
                // 2026-09-16, по репорту пользователя ("долгое добавление и зависание") — см.
                // CreateNewProductLocalFirstAsync.
                await CreateNewProductLocalFirstAsync(request).ConfigureAwait(true);
            }
            else
            {
                // 2026-11-01 (расследование «остаток не меняется», п. 6): в журнале видно, что именно ушло на сервер.
                // 2026-11-02 (аудит №2, С3-02): остаток уходит «как введён» — а пока окно было открыто, другая касса могла продать. Перед записью читаем остаток с сервера;
                // он отличается от того, что было при открытии, — спрашиваем: применить вашу разницу к текущему остатку или оставить серверный.
                if (request.SendQuantity && _existing.Id is { Length: > 0 })
                {
                    double? serverNow = null;
                    try
                    {
                        // 2026-11-03 (С-06, аудит «зависание при медленном сервере»): сверка остатка перед записью — не дольше 8 с (раньше до 55 с); не успели — остаток НЕ шлём, остальные поля сохраняются (ветка catch; 2026-11-05, Т3-14: комментарий приведён к коду).
                        var existingId = _existing.Id;
                        if (await ServerAnswerWait.ForegroundAsync("products/detail", t => PosApp.CatalogApi.ProductsDetailAsync(existingId, t)).ConfigureAwait(true) is { ValueKind: System.Text.Json.JsonValueKind.Object } freshCard)
                            serverNow = StockSyncService.ResolveStockQuantity(freshCard, _isWeight);
                    }
                    catch (Exception readEx) when (readEx is not OperationCanceledException)
                    {
                        // 2026-11-03 (ревью С-1): сверить остаток не удалось (сервер медлит/ошибка) — остаток НЕ шлём: устаревшее абсолютное значение затёрло бы продажи других касс. Остальные поля сохраняются.
                        request.SendQuantity = false;
                        App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(
                            Tr.T("Остаток не сверен с сервером и не записан — остальные поля сохраняются. Измените остаток позже.",
                                "Калдык сервер менен салыштырылган жок жана жазылган жок — калган талаалар сакталат. Калдыкты кийинчерээк өзгөртүңүз.",
                                "Stock could not be checked against the server and was not saved — other fields are saved. Change the stock later.",
                                "Stok sunucuyla doğrulanamadı ve kaydedilmedi — diğer alanlar kaydedilir. Stoğu daha sonra değiştirin.",
                                "Qoldiq server bilan solishtirilmadi va yozilmadi — qolgan maydonlar saqlanadi. Qoldiqni keyinroq o'zgartiring."), isWarning: true);
                        PosLogger.Log($"PRODUCT save: остаток на сервере не прочитан перед записью ({readEx.Message}) — остаток не отправляем.", "WAREHOUSE");
                    }
                    if (serverNow is { } y && Math.Abs(y - _existing.Quantity) > 0.0005)
                    {
                        var typed = request.Quantity;
                        var shift = typed - _existing.Quantity;
                        var applied = Math.Round(Math.Max(0, y + shift), 3);
                        var applyDelta = await PosConfirmDialog.ShowModalAsync(this,
                            Tr.T("Остаток изменился", "Калдык өзгөрдү", "The stock has changed", "Stok değişti", "Qoldiq o'zgardi"),
                            Tr.T($"Пока карточка была открыта, остаток на сервере изменился: было {_existing.Quantity:0.###}, стало {y:0.###}. Вы ввели {typed:0.###}. Применить вашу разницу ({shift:+0.###;-0.###}) к текущему остатку — получится {applied:0.###}? Если оставить как на сервере, остаток не изменится.",
                                $"Карточка ачык турганда сервердеги калдык өзгөрдү: болчу {_existing.Quantity:0.###}, болду {y:0.###}. Сиз {typed:0.###} киргиздиңиз. Айырманы ({shift:+0.###;-0.###}) азыркы калдыкка колдонобузбу — {applied:0.###} болот? Сервердегидей калтырсаңыз, калдык өзгөрбөйт.",
                                $"While the card was open the stock on the server changed: was {_existing.Quantity:0.###}, now {y:0.###}. You entered {typed:0.###}. Apply your difference ({shift:+0.###;-0.###}) to the current stock — it will become {applied:0.###}? If you keep the server value, the stock will not change.",
                                $"Kart açıkken sunucudaki stok değişti: önceden {_existing.Quantity:0.###}, şimdi {y:0.###}. Siz {typed:0.###} girdiniz. Farkınız ({shift:+0.###;-0.###}) mevcut stoğa uygulansın mı — {applied:0.###} olacak? Sunucudaki değeri korursanız stok değişmez.",
                                $"Karta ochiq turganda serverdagi qoldiq o'zgardi: avval {_existing.Quantity:0.###}, hozir {y:0.###}. Siz {typed:0.###} kiritdingiz. Farqingiz ({shift:+0.###;-0.###}) joriy qoldiqqa qo'llansinmi — {applied:0.###} bo'ladi? Serverdagi qiymat qolsa, qoldiq o'zgarmaydi."),
                            Tr.T("Применить разницу", "Айырманы колдонуу", "Apply difference", "Farkı uygula", "Farqni qo'llash"),
                            Tr.T("Оставить как на сервере", "Сервердегидей калтыруу", "Keep server value", "Sunucudakini koru", "Serverdagicha qoldirish")).ConfigureAwait(true);
                        if (applyDelta)
                            request.Quantity = applied;
                        else
                            request.SendQuantity = false;
                        PosLogger.Log($"PRODUCT save: остаток на сервере изменился ({_existing.Quantity:0.###} → {y:0.###}), введено {typed:0.###}, выбрано: {(applyDelta ? "применить разницу → " + applied.ToString("0.###") : "оставить серверный")}.", "WAREHOUSE");
                    }
                }
                PosLogger.Log($"PRODUCT save: id={_existing.Id}, остаток отправлен: {(request.SendQuantity ? "да" : "нет")}, цены: {(request.SendPrice ? "да" : "нет")}, закупка: {(request.SendPurchasePrice ? "да" : "нет")}.", "WAREHOUSE");
                // 2026-11-03 (С-06, В-13): запись карточки — ForegroundWriteAsync: запрос НЕ отменяется. Ответа нет 8 с — окно закрывается («Сохранение продолжается»),
                // итог придёт плашкой, каталог обновится сам по окончании. Раньше «Сохранить» была серой до ~110 с.
                var updateId = _existing.Id;
                var write = await ServerAnswerWait.ForegroundWriteAsync("products/update", t => _catalogApi.UpdateProductAsync(updateId, request, t)).ConfigureAwait(true);
                SavedProductId = updateId; // 2026-11-07: склад обновит в каталоге только этот товар (без полной загрузки)
                if (write.Pending)
                {
                    var pendingName = request.Name;
                    _ = write.Completion.ContinueWith(done => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
                        if (done.IsCompletedSuccessfully)
                        {
                            prompts?.ShowToast(Tr.T($"Товар «{pendingName}» сохранён.", $"«{pendingName}» товары сакталды.", $"Product “{pendingName}” saved.", $"«{pendingName}» ürünü kaydedildi.", $"«{pendingName}» mahsuloti saqlandi."));
                            _ = CatalogCacheService.RefreshProductsAsync([updateId]); // 2026-11-07: только этот товар, не весь каталог
                        }
                        else
                        {
                            var why = done.Exception?.GetBaseException() is { } be ? ServerOutageMonitor.FriendlyError(be) : ServerOutageMonitor.FriendlyError(new OperationCanceledException()); // 2026-11-03 (ревью Н-2): отменённая задача — не пустая причина
                            prompts?.ShowToast(Tr.T($"Товар «{pendingName}» не сохранён: {why}", $"«{pendingName}» товары сакталган жок: {why}", $"Product “{pendingName}” was not saved: {why}", $"«{pendingName}» ürünü kaydedilemedi: {why}", $"«{pendingName}» mahsuloti saqlanmadi: {why}"), isWarning: true);
                        }
                    }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(NurMarketKassa.AvaloniaHost.Services.ServerWaitIndicator.WritePendingText, isWarning: true);
                    SavePending = true; // 2026-11-03 (ревью Н-2): склад не пишет «Товар сохранён» и не перечитывает каталог — это сделает плашка по итогу записи.
                }
            }

            Saved = true;
            Close(true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ServerOutageMonitor.FriendlyError(ex, showServerText: true); // 2026-11-05 (С2-38): 401/403/5xx — понятным текстом, ответ сервера 4xx — как есть
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось сохранить товар: ", "Товарды сактоо мүмкүн болгон жок: ", "Could not save the product: ", "Ürün kaydedilemedi: ", "Mahsulotni saqlab bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex); // 2026-11-05 (С2-38)
            StatusMessage = "";
            PosLogger.Log($"Product save failed: {ex}", "WAREHOUSE");
        }
        finally
        {
            _isSaving = false;
            OnPropertyChanged(nameof(CanSaveNow));
            UpdateSaveButtonState();
        }
    }

    private readonly Dictionary<string, string> _createKeys = new(); // 2026-11-01 (С-03): ключи повтора создания по содержимому карточки

    /// <summary>Мгновенное создание нового товара: короткая попытка создать на сервере (несколько
    /// секунд) — этого достаточно в норме, и товар сразу получает настоящий id без риска
    /// рассинхронизации. Если сеть реально тормозит или недоступна — товар всё равно СРАЗУ
    /// пишется в локальную базу (сканируется и продаётся немедленно), а создание на сервере
    /// продолжается в фоне (PendingProductQueue — очередь на диске, повторы до успеха, в том числе после перезапуска)
    /// и подменяет временный локальный id на настоящий, когда сервер создаст товар. Используется и для основного товара, и для каждого
    /// товара-варианта из "доп. штрихкодов".</summary>
    private async Task CreateNewProductLocalFirstAsync(ProductEditRequest request)
    {
        // 2026-11-01 (аудит 10 из 10, С-03): один ключ повтора на создание — и для быстрой попытки, и для фонового повтора.
        // Приёмка С-03: ключ держим на всё время диалога, пока ввод не изменился (повторное «Сохранить» после ошибки/таймаута — тот же ключ).
        var createSignature = string.Join("|", request.Name, request.Barcode, request.Article, request.Unit, request.Quantity, request.Price, request.PurchasePrice, request.Kind);
        if (!_createKeys.TryGetValue(createSignature, out var createKey))
            _createKeys[createSignature] = createKey = Guid.NewGuid().ToString("N");
        request.IdempotencyKey = createKey;
        // 2026-11-06: сервер уже объявлен недоступным — 4 секунды быстрой попытки не тратим, товар сразу в локальную базу и в очередь на диске.
        if (ServerOutageMonitor.IsOutage)
        {
            QueueNewProductForServer(request, maybeSent: false);
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(FastCreateTimeout);
            var created = await _catalogApi.CreateProductAsync(request, cts.Token).ConfigureAwait(true);
            var realId = ProductCatalogMapper.TryId(created) ?? Guid.NewGuid().ToString("N");
            LocalProductEditor.SaveLocally(request, realId, created); // 2026-11-06 (Б-4): код товара, фото и is_weight — из ответа сервера
            if (ProductCatalogMapper.TryId(created) is { Length: > 0 } serverId)
                SavedProductId = serverId; // 2026-11-07: склад обновит в каталоге только этот товар
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or ApiException { StatusCode: 408 or 502 or 503 or 504 or (>= 520 and <= 524) })
        {
            // Таймаут / ответ шлюза — запрос мог дойти до сервера (очередь сначала ищет товар по штрихкоду и ждёт); обрыв соединения — не дошёл.
            QueueNewProductForServer(request, maybeSent: ex is not HttpRequestException);
        }
    }

    /// <summary>2026-11-06: товар в локальную базу под временным id и задача создания на сервере — в очередь на диске (PendingProductQueue). Очередь повторяет
    /// с растущей паузой и сразу при возврате связи, переживает перезапуск; приход с временным id дождётся настоящего.</summary>
    private static void QueueNewProductForServer(ProductEditRequest request, bool maybeSent)
    {
        var tempId = LocalProductEditor.SaveLocally(request, null);
        try
        {
            PendingProductQueue.Instance.Enqueue(request, tempId, maybeSent);
        }
        catch (InvalidOperationException ex)
        {
            // Файл очереди не записался — товар остаётся только в этой кассе; кассир должен об этом знать.
            PosLogger.Log($"PRODUCT save: товар {tempId} не поставлен в очередь отправки: {ex.Message}", "WAREHOUSE");
            CatalogCacheService.RaiseToast(ex.Message, true);
        }
    }

    /// <summary>2026-10-29: разбор числа поля карточки единым CheckoutValidation.TryParseAmount. Пусто — null (поле не заполнено, это не ошибка);
    /// непонятное число — понятный текст в ErrorMessage и false. Название поля — на 5 языках.</summary>
    private bool CheckAmount(string? text, (string Ru, string Ky, string En, string Tr, string Uz) label, int maxDecimals, out double? value,
        string? tooBigText = null, Func<double, bool>? exceeds = null)
    {
        value = null;
        var parse = CheckoutValidation.TryParseAmount(text, out var parsed, maxDecimals);
        if (parse == CheckoutValidation.AmountParse.Empty)
            return true;
        if (parse == CheckoutValidation.AmountParse.Ok)
        {
            // 2026-11-11 (ответ Умара): цена выше предела сервера не сохраняется — понятный текст вместо ошибки сервера.
            if (exceeds is not null && exceeds(parsed))
            {
                ErrorMessage = tooBigText ?? "";
                return false;
            }

            value = parsed;
            return true;
        }
        var name = Tr.T(label.Ru, label.Ky, label.En, label.Tr, label.Uz);
        ErrorMessage = parse == CheckoutValidation.AmountParse.TooManyDecimals
            ? Tr.T($"{name}: не больше {maxDecimals} знаков после запятой.", $"{name}: үтүрдөн кийин {maxDecimals} цифрадан ашык болбойт.",
                $"{name}: no more than {maxDecimals} decimal places.", $"{name}: virgülden sonra en fazla {maxDecimals} basamak.", $"{name}: verguldan keyin {maxDecimals} tadan ko'p raqam bo'lmaydi.")
            : Tr.T($"{name}: введите число не меньше нуля, например 12,75.", $"{name}: нөлдөн кем эмес сан киргизиңиз, мисалы 12,75.",
                $"{name}: enter a number, zero or more, for example 12.75.", $"{name}: sıfır veya daha büyük bir sayı girin, örn. 12,75.", $"{name}: noldan kam bo'lmagan son kiriting, masalan 12,75.");
        return false;
    }

    /// <summary>Наценка может быть отрицательной (продажа ниже закупки считается автоматически и показывает красное предупреждение) —
    /// знак «−» впереди допускается, остальное разбирается как число.</summary>
    private bool CheckMarkup(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith('-') || trimmed.StartsWith('−'))
            trimmed = trimmed[1..];
        return CheckAmount(trimmed, ("Наценка", "Үстөк", "Markup", "Kâr oranı", "Ustama"), 2, out _);
    }

    private string DimensionsSignature() => string.Join("|", _heightCm.Trim(), _widthCm.Trim(), _depthCm.Trim(), _weightKg.Trim());

    private static double ParseNumber(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double? TryParseNumber(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseNumber(s);

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(Saved);

    /// <summary>2026-10-01: тексты блока «Размеры и цвета» (5 языков). Вызывается при открытии окна.</summary>
    private void ApplyVariantsTexts()
    {
        VariantsTitle.Text = Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar");
        VariantsHint.Text = _existing is null
            ? Tr.T("Сначала сохраните товар — потом добавьте размеры и цвета.", "Адегенде товарды сактаңыз — андан кийин өлчөмдөрдү жана түстөрдү кошуңуз.",
                "Save the product first, then add sizes and colors.", "Önce ürünü kaydedin, sonra beden ve renk ekleyin.", "Avval mahsulotni saqlang, so'ng o'lcham va ranglarni qo'shing.")
            : Tr.T("Для магазина одежды: свой остаток и цена у каждого размера/цвета (скидка на размер — цена ниже).",
                "Кийим дүкөнү үчүн: ар бир өлчөм/түстүн өз калдыгы жана баасы (өлчөмгө арзандатуу — баасы төмөн).",
                "For clothing stores: each size/color has its own stock and price (a lower price = a discount).",
                "Giyim mağazası için: her beden/rengin kendi stoku ve fiyatı (düşük fiyat = indirim).",
                "Kiyim do'koni uchun: har bir o'lcham/rangning o'z qoldig'i va narxi (pastroq narx = chegirma).");
        VariantsButton.Content = Tr.T("Размеры и цвета…", "Өлчөмдөр жана түстөр…", "Sizes and colors…", "Bedenler ve renkler…", "O'lchamlar va ranglar…");
        VariantsButton.IsEnabled = _existing is not null && !string.IsNullOrWhiteSpace(_existing.Id);
    }

    private async void VariantsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_existing is null || string.IsNullOrWhiteSpace(_existing.Id) || _catalogApi is null)
            return;
        var editor = new VariantEditorWindow(_catalogApi, _existing.Id, _existing.Title, LocalCartService.ParsePrice(_existing.PriceLine));
        await editor.ShowDialog(this).ConfigureAwait(true);
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        CloseDropdownPanelsIfClickedOutside(e);
        // 2026-10-18, ревью этапа 5: окно без рамки — тянем только мышью; касание пальцем по пустому месту
        // (сенсорный моноблок) должно прокручивать/выбирать вкладку, а не двигать окно.
        if (e.Pointer.Type == Avalonia.Input.PointerType.Mouse && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>2026-09-15, аудит удобства: панели Категория/Бренд/Горячая клавиша — не Popup
    /// (см. комментарий в XAML — обычный Popup у ComboBox не рендерится в окне без системного
    /// хрома), а обычные Border внутри самой формы, поэтому клик мимо них никак их не закрывал.
    /// Кассир открывал список категорий, кликал в соседнее поле продолжить заполнение — панель
    /// оставалась раскрытой и просто раздувала форму, пока по ней не кликали ещё раз явно.</summary>
    private void CloseDropdownPanelsIfClickedOutside(Avalonia.Input.PointerPressedEventArgs e)
    {
        ClosePanelIfClickedOutside(e, CategoryOptionsPanel, CategoryToggleButton);
        ClosePanelIfClickedOutside(e, BrandOptionsPanel, BrandToggleButton);
        ClosePanelIfClickedOutside(e, HotkeyOptionsPanel, HotkeyToggleButton);
    }

    private static void ClosePanelIfClickedOutside(Avalonia.Input.PointerPressedEventArgs e, Border panel, Control toggleButton)
    {
        if (!panel.IsVisible)
            return;
        if (IsPointInsideControl(e, panel) || IsPointInsideControl(e, toggleButton))
            return;
        panel.IsVisible = false;
    }

    private static bool IsPointInsideControl(Avalonia.Input.PointerPressedEventArgs e, Control control)
    {
        var p = e.GetPosition(control);
        return p.X >= 0 && p.Y >= 0 && p.X <= control.Bounds.Width && p.Y <= control.Bounds.Height;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
