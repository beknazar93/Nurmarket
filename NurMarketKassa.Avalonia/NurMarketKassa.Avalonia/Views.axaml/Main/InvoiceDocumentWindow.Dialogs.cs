using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Итог окна «Проверьте правку»: причина, чем компенсировать возврат, и «в очередь» (нет связи — пароль не проверить, правка ставится в очередь).</summary>
internal sealed record PreviewResult(string Reason, string Compensation, bool Queue);

/// <summary>Строка таблицы «Проверьте правку»: в накладной сейчас → станет, изменение суммы строки (с учётом новой цены закупки), остаток склада (null — не читали).</summary>
internal sealed record PreviewRow(string Name, double Now, double Delta, double New, double? Stock, double Sum);

/// <summary>2026-10-30 (ТЗ 2.4): «Проверьте правку» — до → после: сумма прихода, деньги/долг, остатки по товарам, чем компенсируется возврат, причина (обязательна) и пароль владельца.
/// «Сохранить правку» доступна только с причиной (не короче 5 знаков) и введённым паролем; пароль сверяется с NurCRM, текущий вход не меняется.</summary>
internal sealed class InvoiceEditPreviewDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public PreviewResult? Result { get; private set; }

    private readonly ComboBox _reasonKind = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _reasonText = new() { MinHeight = 44, MaxLength = 500, AcceptsReturn = false };
    private readonly TextBox _password = new() { MinHeight = 44, PasswordChar = '•', MaxLength = 128 };
    private readonly ComboBox _comp = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _save;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, Task<bool?>> _verify;
    private bool _offlineMode;
    private readonly ReceiptEditPlan _plan;
    private readonly string[] _compKeys = { "debt_offset", "cash", "none" };
    private readonly bool _patch; // 2026-11-01: правка серверным PATCH — возмещение и деньги считает сервер

    private static string[] ReasonKinds() => new[]
    {
        T("Опечатка в количестве", "Саны жаңылыш жазылган", "Typo in the quantity", "Miktarda yazım hatası", "Miqdorda xato"),
        T("Не тот товар", "Башка товар", "Wrong product", "Yanlış ürün", "Boshqa mahsulot"),
        T("Поставщик прислал исправленную накладную", "Жеткирүүчү оңдолгон накладной жөнөттү", "The supplier sent a corrected invoice", "Tedarikçi düzeltilmiş fatura gönderdi", "Yetkazuvchi tuzatilgan yuk xati yubordi"),
        T("Недовоз", "Жеткирилген жок", "Short delivery", "Eksik teslimat", "Yetkazilmagan"),
        T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    public InvoiceEditPreviewDialog(ReceiptEditPlan plan, ReceiptPayKind pay, string docNumber, SupplierReceipt r, string supplier, int version, bool revert, Func<string, Task<bool?>> verify, IReadOnlyList<string>? priceLines = null, IReadOnlyList<AddedItem>? added = null, PatchPlan? patch = null, IReadOnlyList<PreviewRow>? rows = null)
    {
        _patch = patch is not null;
        _plan = plan;
        _verify = verify;
        Title = T("Проверьте правку", "Оңдоону текшериңиз", "Review changes", "Değişiklikleri gözden geçirin", "Tuzatishni tekshiring");
        SectionCards.AsModal(this, 760);
        var panel = new StackPanel { Spacing = 8 };
        TextBlock Line(string text, string brush = "BrushText", double size = 13.5, FontWeight? w = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = w ?? FontWeight.Normal };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
            return t;
        }
        var addedSum = added?.Sum(a => ReceivingPricing.LineTotal(a.Qty, a.Price)) ?? 0; // 2026-11-01: новые товары (дополнительный приход) входят в итог
        var diff = patch is not null ? Math.Round(patch.TotalAfter - patch.TotalBefore, 2) : Math.Round(plan.Diff + addedSum, 2);
        var sign = diff > 0 ? "+" : "";
        panel.Children.Add(Line(T("Сумма прихода: ", "Кирүүнүн суммасы: ", "Receipt total: ", "Giriş tutarı: ", "Kirim summasi: ") + $"{MoneyText.Som(patch?.TotalBefore ?? plan.TotalBefore)} → {MoneyText.Som(patch?.TotalAfter ?? plan.TotalAfter + addedSum)}  ({sign}{MoneyText.Som(diff)})", w: FontWeight.Bold, size: 15));
        panel.Children.Add(Line(patch is not null
            ? T("Остатки, закупку в карточке товара, долг поставщику и деньги пересчитает сервер.", "Калдыктарды, товардын карточкасындагы сатып алуу баасын, жеткирүүчүгө карызды жана акчаны сервер кайра эсептейт.",
                "The server recalculates stock, the purchase price in the product card, the supplier debt and the money.", "Stok, ürün kartındaki alış fiyatı, tedarikçi borcu ve parayı sunucu yeniden hesaplar.", "Ombor qoldig'i, mahsulot kartasidagi xarid narxi, yetkazib beruvchi qarzi va pulni server qayta hisoblaydi.")
            : MoneyEffect(pay, diff), "BrushTextSoft", 13));
        // 2026-11-01 (защита): сумма накладной меняется — это затрагивает расчёты с поставщиком (оплачено / долг) и себестоимость уже проданных партий. Причина правки и так обязательна.
        if (Math.Abs(diff) > 0.005)
            panel.Children.Add(Line(T("Внимание: меняется сумма накладной. Если поставщику уже заплачено или товар из этой партии продан, расчёты с поставщиком и прибыль за прошлые дни не пересчитываются сами — укажите причину и сверьте оплату.",
                "Көңүл буруңуз: накладнойдун суммасы өзгөрөт. Жеткирүүчүгө төлөнгөн же ушул партиядагы товар сатылган болсо, жеткирүүчү менен эсептешүү жана өткөн күндөрдүн пайдасы өзү кайра эсептелбейт — себебин жазып, төлөмдү салыштырыңыз.",
                "Attention: the invoice total changes. If the supplier is already paid or goods from this batch are sold, the settlement with the supplier and past profit are not recalculated automatically — enter the reason and check the payment.",
                "Dikkat: fatura tutarı değişiyor. Tedarikçiye ödeme yapıldıysa veya bu partideki ürün satıldıysa, tedarikçi hesabı ve geçmiş kâr kendiliğinden yeniden hesaplanmaz — nedeni yazın ve ödemeyi kontrol edin.",
                "Diqqat: yuk xati summasi o'zgaradi. Yetkazib beruvchiga to'langan yoki shu partiyadagi mahsulot sotilgan bo'lsa, hisob-kitob va o'tgan foyda o'zi qayta hisoblanmaydi — sababni yozing va to'lovni solishtiring."), "BrushWarning", 12.5, FontWeight.SemiBold));
        if (added is { Count: > 0 } && patch is null)
            panel.Children.Add(Line(T($"Будет создан дополнительный приход к накладной {docNumber}: {added.Count} нов. товар(ов), сумма {MoneyText.Som(addedSum)}. В накладной они появятся строками «из доп. прихода», отметка «кто и когда добавил» — рядом с товаром.",
                $"{docNumber} накладнойуна кошумча кирүү түзүлөт: {added.Count} жаңы товар, суммасы {MoneyText.Som(addedSum)}. Накладнойдо алар «кошумча кирүүдөн» сап болуп чыгат, «ким жана качан кошту» белгиси — товардын жанында.",
                $"An additional receipt will be created for invoice {docNumber}: {added.Count} new product(s), total {MoneyText.Som(addedSum)}. They appear in the invoice as “from an additional receipt” lines, with a “who added it and when” mark next to the product.",
                $"{docNumber} faturası için ek giriş oluşturulur: {added.Count} yeni ürün, tutar {MoneyText.Som(addedSum)}. Faturada «ek girişten» satırları olarak görünür, «kim ne zaman ekledi» işareti ürünün yanındadır.",
                $"{docNumber} yuk xatiga qo'shimcha kirim yaratiladi: {added.Count} ta yangi mahsulot, summa {MoneyText.Som(addedSum)}. Yuk xatida ular «qo'shimcha kirimdan» qatorlari bo'lib chiqadi, «kim va qachon qo'shdi» belgisi mahsulot yonida."), "BrushText", 13, FontWeight.SemiBold));
        if (priceLines is { Count: > 0 })
        {
            panel.Children.Add(Line(patch is not null
                ? T("Другие изменения накладной (цены, реквизиты):", "Накладнойдун башка өзгөрүүлөрү (баалар, реквизиттер):", "Other changes to the invoice (prices, details):", "Faturadaki diğer değişiklikler (fiyatlar, bilgiler):", "Yuk xatidagi boshqa o'zgarishlar (narxlar, rekvizitlar):")
                : T("Будет изменена цена продажи товара в каталоге (в документе прихода цен продажи нет):", "Каталогдогу товардын сатуу баасы өзгөрөт (кирүү документинде сатуу баасы жок):",
                "The product's sale price in the catalog will change (the receipt document has no sale prices):", "Katalogdaki ürünün satış fiyatı değişecek (giriş belgesinde satış fiyatı yok):", "Katalogdagi mahsulotning sotuv narxi o'zgaradi (kirim hujjatida sotuv narxi yo'q):"), "BrushText", 13, FontWeight.SemiBold));
            foreach (var pl in priceLines)
                panel.Children.Add(Line(pl, "BrushTextSoft", 12.5));
        }

        // таблица товаров: сейчас → станет, остаток склада
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
        var row = 0;
        void Cell(string text, int col, bool head = false, string brush = "BrushText", bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = head ? 11.5 : 13, FontWeight = head || bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(col == 0 ? 0 : 14, 3, 0, 3), TextWrapping = col == 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            if (col > 0 && !head)
                t.Classes.Add("num");
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(head ? "BrushTextSoft" : brush));
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), 0, true);
        Cell(T("В накладной", "Накладнойдо", "In the invoice", "Faturada", "Yuk xatida"), 1, true);
        Cell(T("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish"), 2, true);
        Cell(T("Станет", "Болот", "Will be", "Olacak", "Bo'ladi"), 3, true);
        Cell(T("Остаток на складе", "Кампадагы калдык", "Stock in the warehouse", "Depodaki stok", "Ombordagi qoldiq"), 4, true);
        Cell(T("Изменение суммы", "Сумманын өзгөрүшү", "Amount change", "Tutar değişimi", "Summa o'zgarishi"), 5, true);
        // 2026-11-01 (живая проверка 1.17.72, Н-2): «В накладной» — количество в документе, не на складе; остаток склада — только там, где количество меняется и остаток известен;
        // сумма строки считается по НОВОЙ цене закупки (раньше брала старую и расходилась с итогом).
        var tableRows = rows ?? plan.Lines.Where(l => l.Action != EditAction.None)
            .Select(l => new PreviewRow(l.Input.Name, l.Effective, l.Delta, l.NewQty, l.Input.Stock, Math.Sign(l.Delta) * ReceivingPricing.LineTotal(Math.Abs(l.Delta), l.Input.Purchase))).ToList();
        foreach (var l in tableRows)
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var same = Math.Abs(l.Delta) < 0.0005;
            Cell(l.Name, 0, bold: true);
            Cell(ReceiptEditPlanner.Fmt(l.Now), 1);
            Cell(same ? "—" : (l.Delta > 0 ? "+" : "") + ReceiptEditPlanner.Fmt(l.Delta), 2, brush: l.Delta < 0 ? "BrushDanger" : "BrushSuccess", bold: !same);
            Cell(ReceiptEditPlanner.Fmt(l.New), 3);
            Cell(same ? T("не меняется", "өзгөрбөйт", "unchanged", "değişmez", "o'zgarmaydi") : l.Stock is { } sb ? $"{ReceiptEditPlanner.Fmt(sb)} → {ReceiptEditPlanner.Fmt(sb + l.Delta)}" : "—", 4);
            Cell(Math.Abs(l.Sum) < 0.005 ? "—" : (l.Sum > 0 ? "+" : "−") + MoneyText.Som(Math.Abs(l.Sum)), 5);
        }
        // 2026-11-01: товары, которых нет в накладной, — строки дополнительного прихода (было 0)
        foreach (var a in added ?? Array.Empty<AddedItem>())
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(a.Name + " ＋", 0, bold: true);
            Cell("0", 1);
            Cell("+" + ReceiptEditPlanner.Fmt(a.Qty), 2, brush: "BrushSuccess", bold: true);
            Cell(ReceiptEditPlanner.Fmt(a.Qty), 3);
            Cell("—", 4);
            Cell("+" + MoneyText.Som(ReceivingPricing.LineTotal(a.Qty, a.Price)), 5);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = grid };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("BrushPanelSoft"));
        panel.Children.Add(box);
        if (patch is null)
            panel.Children.Add(Line(T("Закупка в карточке товара не меняется (цена продажи — только если указана выше). Количество уменьшается возвратом поставщику по этому приходу, увеличивается дополнительным приходом «дополнение к ", "Товардын карточкасындагы сатып алуу баасы өзгөрбөйт (сатуу баасы — жогоруда көрсөтүлсө гана). Саны азайса — ушул кирүү боюнча жеткирүүчүгө кайтаруу, көбөйсө — кошумча кирүү «",
            "The purchase price in the product card does not change (the sale price only if shown above). A smaller quantity is a return to the supplier against this receipt, a larger one is an additional receipt “addition to ", "Ürün kartındaki alış fiyatı değişmez (satış fiyatı yalnızca yukarıda gösterilmişse). Azalan miktar bu girişe karşı tedarikçiye iade, artan miktar «", "Mahsulot kartasidagi xarid narxi o'zgarmaydi (sotuv narxi — faqat yuqorida ko'rsatilgan bo'lsa). Kamaytirish — shu kirim bo'yicha yetkazib beruvchiga qaytarish, ko'paytirish — «")
            + docNumber + T("». Продажи и прибыль в отчётах не пересчитываются.", "». Сатуулар жана отчёттордогу пайда кайра эсептелбейт.", "”. Sales and profit in reports are not recalculated.", "» ek girişi. Raporlardaki satış ve kâr yeniden hesaplanmaz.", "» qo'shimcha kirimi. Hisobotlardagi sotuv va foyda qayta hisoblanmaydi."), "BrushTextSoft", 12.5));

        if (plan.Returns.Count > 0 && patch is null)
        {
            panel.Children.Add(Line(T("Чем компенсируется возврат поставщику", "Жеткирүүчүгө кайтарууну эмне менен толуктоо", "How the return to the supplier is compensated", "Tedarikçiye iade nasıl telafi edilir", "Yetkazib beruvchiga qaytarish nima bilan qoplanadi"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
            _comp.ItemsSource = new[]
            {
                T("Зачесть в долг поставщику", "Жеткирүүчүгө карызга эсептөө", "Offset against the supplier debt", "Tedarikçi borcundan düş", "Yetkazib beruvchi qarziga hisoblash"),
                T("Деньги вернули в кассу", "Акча кассага кайтарылды", "Money returned to the till", "Para kasaya iade edildi", "Pul kassaga qaytarildi"),
                T("Без возмещения (только со склада)", "Толуктоосуз (кампадан гана)", "No compensation (stock only)", "Telafi yok (yalnızca stoktan)", "Qoplamasiz (faqat ombordan)"),
            };
            _comp.SelectedIndex = Array.IndexOf(_compKeys, ReceiptEditPlanner.DefaultCompensation(pay));
            panel.Children.Add(_comp);
            if (pay == ReceiptPayKind.Unknown)
                panel.Children.Add(Line(T("Способ оплаты прихода неизвестен (сервер его не хранит) — выберите возмещение сами.", "Кирүүнүн төлөм ыкмасы белгисиз (сервер сактабайт) — толуктоону өзүңүз тандаңыз.", "The payment type is unknown (the server does not store it) — choose the compensation yourself.", "Girişin ödeme türü bilinmiyor (sunucu saklamaz) — telafiyi kendiniz seçin.", "Kirim to'lov turi noma'lum (server saqlamaydi) — qoplashni o'zingiz tanlang."), "BrushWarning", 12.5));
        }

        panel.Children.Add(Line(T("Причина правки (обязательно)", "Оңдоонун себеби (милдеттүү)", "Reason for the edit (required)", "Düzenleme nedeni (zorunlu)", "Tahrir sababi (majburiy)"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
        _reasonKind.ItemsSource = ReasonKinds();
        _reasonKind.SelectedIndex = 0;
        panel.Children.Add(_reasonKind);
        _reasonText.Watermark = T("Подробнее (не короче 5 знаков)", "Кеңири (5 белгиден кем эмес)", "Details (at least 5 characters)", "Ayrıntı (en az 5 karakter)", "Batafsil (kamida 5 belgi)");
        panel.Children.Add(_reasonText);
        panel.Children.Add(Line(T("Подтвердите паролем своей учётной записи NurCRM (под которой вы вошли)", "Кирген NurCRM эсебиңиздин сырсөзү менен ырастаңыз", "Confirm with the password of the NurCRM account you are signed in with", "Giriş yaptığınız NurCRM hesabının parolasıyla onaylayın", "Kirgan NurCRM hisobingiz paroli bilan tasdiqlang"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
        _password.Watermark = T("Пароль NurCRM", "NurCRM сырсөзү", "NurCRM password", "NurCRM parolası", "NurCRM paroli");
        panel.Children.Add(_password);
        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(_note);
        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var back = SectionCards.Cancel(T("Назад к правке", "Оңдоого кайтуу", "Back to editing", "Düzenlemeye dön", "Tahrirga qaytish"));
        back.Click += (_, _) => Close();
        _save = SectionCards.Primary(T("Сохранить правку", "Оңдоону сактоо", "Save the edit", "Düzenlemeyi kaydet", "Tahrirni saqlash"));
        _save.IsEnabled = false;
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        void Check() => _save.IsEnabled = ReasonOk() && (_password.Text ?? "").Length > 0 || (_offlineMode && ReasonOk());
        _reasonText.TextChanged += (_, _) => Check();
        _password.TextChanged += (_, _) =>
        {
            _offlineMode = false;
            Check();
        };
        _note.Text = revert
            ? T($"Откат создаёт новую версию {version + 1}; историю правок не стираем.", $"Кайтаруу жаңы {version + 1}-версияны түзөт; оңдоолордун тарыхы өчүрүлбөйт.", $"A rollback creates a new version {version + 1}; the edit history is kept.", $"Geri alma yeni sürüm {version + 1} oluşturur; geçmiş silinmez.", $"Qaytarish yangi {version + 1}-versiyani yaratadi; tarix o'chirilmaydi.")
            : T($"Будет создана версия {version + 1}; ключи повтора записаны на диск — после обрыва связи правка не задвоится.", $"{version + 1}-версия түзүлөт; кайталоо ачкычтары дискке жазылган — байланыш үзүлсө да оңдоо эки эсе болбойт.",
                $"Version {version + 1} will be created; retry keys are saved to disk — the edit won't be doubled after a connection loss.", $"Sürüm {version + 1} oluşturulacak; tekrar anahtarları diske yazıldı — bağlantı kesilirse düzenleme ikilenmez.", $"{version + 1}-versiya yaratiladi; takror kalitlari diskka yozilgan — aloqa uzilsa ham tahrir ikkilanmaydi.");

        var subtitle = T($"{docNumber} от {r.CreatedAt:dd.MM.yyyy HH:mm} · «{supplier}» · версия {version} → {version + 1}", $"{docNumber}, {r.CreatedAt:dd.MM.yyyy HH:mm} · «{supplier}» · версия {version} → {version + 1}",
            $"{docNumber} of {r.CreatedAt:dd.MM.yyyy HH:mm} · “{supplier}” · version {version} → {version + 1}", $"{docNumber}, {r.CreatedAt:dd.MM.yyyy HH:mm} · «{supplier}» · sürüm {version} → {version + 1}", $"{docNumber}, {r.CreatedAt:dd.MM.yyyy HH:mm} · «{supplier}» · versiya {version} → {version + 1}");
        Content = SectionCards.Shell(Title, subtitle, panel, SectionCards.Buttons(back, _save));
    }

    private static string MoneyEffect(ReceiptPayKind pay, double diff)
    {
        var abs = MoneyText.Som(Math.Abs(diff));
        if (Math.Abs(diff) < 0.005)
            return T("Деньги и долг не меняются.", "Акча жана карыз өзгөрбөйт.", "Money and debt do not change.", "Para ve borç değişmez.", "Pul va qarz o'zgarmaydi.");
        return pay switch
        {
            ReceiptPayKind.Debt or ReceiptPayKind.Partial => diff < 0
                ? T($"Долг поставщику уменьшится на {abs}.", $"Жеткирүүчүгө карыз {abs} азаят.", $"The debt to the supplier decreases by {abs}.", $"Tedarikçi borcu {abs} azalır.", $"Yetkazib beruvchiga qarz {abs} kamayadi.")
                : T($"Долг поставщику вырастет на {abs} (новая сделка-долг по доприходу).", $"Жеткирүүчүгө карыз {abs} өсөт (кошумча кирүү боюнча жаңы карыз).", $"The debt to the supplier grows by {abs} (a new debt deal for the addition).", $"Tedarikçi borcu {abs} artar (ek giriş için yeni borç).", $"Yetkazib beruvchiga qarz {abs} ga oshadi (qo'shimcha kirim bo'yicha yangi qarz)."),
            ReceiptPayKind.Cash or ReceiptPayKind.Drawer => diff < 0
                ? T($"В кассу вернётся {abs} (если выбрано «Деньги вернули в кассу»).", $"Кассага {abs} кайтат («Акча кассага кайтарылды» тандалса).", $"{abs} returns to the till (if “Money returned to the till” is chosen).", $"Kasaya {abs} döner («Para kasaya iade edildi» seçilirse).", $"Kassaga {abs} qaytadi («Pul kassaga qaytarildi» tanlansa).")
                : T($"Из основной кассы спишется {abs} (расход по доприходу).", $"Негизги кассадан {abs} чыгат (кошумча кирүү чыгымы).", $"{abs} is taken from the main till (expense for the addition).", $"Ana kasadan {abs} düşülür (ek giriş gideri).", $"Asosiy kassadan {abs} chiqadi (qo'shimcha kirim xarajati)."),
            _ => T($"Разница по сумме: {abs}. Способ оплаты неизвестен — деньги и долг определяет выбранное возмещение.", $"Суммадагы айырма: {abs}. Төлөм ыкмасы белгисиз — акчаны жана карызды тандалган толуктоо аныктайт.", $"Difference: {abs}. The payment type is unknown — the chosen compensation decides money and debt.", $"Tutar farkı: {abs}. Ödeme türü bilinmiyor — para ve borcu seçilen telafi belirler.", $"Summadagi farq: {abs}. To'lov turi noma'lum — pul va qarzni tanlangan qoplash belgilaydi."),
        };
    }

    private string ReasonText
    {
        get
        {
            var kind = _reasonKind.SelectedItem as string ?? "";
            var text = (_reasonText.Text ?? "").Trim();
            return text.Length == 0 ? kind : kind + ": " + text;
        }
    }

    private bool ReasonOk() => (_reasonText.Text ?? "").Trim().Length >= 5;

    private async Task SaveAsync()
    {
        _error.IsVisible = false;
        var comp = !_patch && _plan.Returns.Count > 0 && _comp.SelectedIndex >= 0 ? _compKeys[_comp.SelectedIndex] : "none";
        if (_offlineMode)
        {
            Result = new PreviewResult(ReasonText, comp, true);
            Close();
            return;
        }
        _save.IsEnabled = false;
        _password.IsEnabled = false;
        var noteBefore = _note.Text;
        _note.Text = T("Проверяю пароль…", "Сырсөз текшерилүүдө…", "Checking the password…", "Parola kontrol ediliyor…", "Parol tekshirilmoqda…"); // 2026-10-30: индикатор (проверка идёт не дольше 8 с)
        bool? ok;
        try
        {
            ok = await _verify(_password.Text ?? "").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Правка накладной: пароль не проверен ({ex.Message}).", "WARNING");
            ok = null;
        }
        _note.Text = noteBefore;
        _password.IsEnabled = true;
        if (ok == true)
        {
            Result = new PreviewResult(ReasonText, comp, false);
            Close();
            return;
        }
        _save.IsEnabled = true;
        if (ok == false)
        {
            _error.Text = T("Пароль неверный. Нужен пароль той учётной записи NurCRM, под которой вы вошли (владелец или администратор). Причина и остальные поля сохранены — введите пароль ещё раз.",
                "Сырсөз туура эмес. Сиз кирген NurCRM эсебинин сырсөзү керек (ээси же администратор). Себеби жана калган талаалар сакталды — сырсөздү дагы бир жолу киргизиңиз.",
                "Wrong password. Enter the password of the NurCRM account you are signed in with (owner or administrator). The reason and the other fields are kept — enter the password again.",
                "Parola yanlış. Giriş yaptığınız NurCRM hesabının parolası gerekir (sahip veya yönetici). Neden ve diğer alanlar korundu — parolayı tekrar girin.",
                "Parol noto'g'ri. Kirgan NurCRM hisobingiz paroli kerak (egasi yoki administrator). Sabab va boshqa maydonlar saqlangan — parolni qayta kiriting.");
            _error.IsVisible = true;
            _password.Focus();
            _password.SelectAll();
            return;
        }
        // Связи нет: пароль проверить нельзя — правка ставится в очередь, ничего не отправляется; подтвердить паролем придётся при открытии с связью.
        _offlineMode = true;
        _save.Content = T("Поставить в очередь", "Кезекке коюу", "Queue the edit", "Kuyruğa al", "Navbatga qo'yish");
        _error.Text = NoConnectionText + " " + T("Правку можно поставить в очередь: на сервер она не уйдёт, пока вы не подтвердите её паролем при появлении связи.",
            "Оңдоону кезекке коюуга болот: байланыш пайда болгондо сырсөз менен ырастамайынча серверге кетпейт.",
            "You can queue the edit: it won't go to the server until you confirm it with your password once the connection is back.",
            "Düzenlemeyi kuyruğa alabilirsiniz: bağlantı gelince parolayla onaylamadan sunucuya gitmez.", "Tahrirni navbatga qo'yish mumkin: aloqa tiklanganda parol bilan tasdiqlamaguncha serverga ketmaydi.");
        _error.IsVisible = true;
        _save.IsEnabled = ReasonOk();
    }

    /// <summary>2026-10-30: пароль не проверился за 8 с или связи нет — правка не теряется.</summary>
    internal static string NoConnectionText => T("Нет связи — пароль проверится позже.", "Байланыш жок — сырсөз кийинчерээк текшерилет.", "No connection — the password will be checked later.", "Bağlantı yok — parola daha sonra kontrol edilecek.", "Aloqa yo'q — parol keyinroq tekshiriladi.");
}

/// <summary>2026-10-30 (приёмка 1.17.66, 3ж): пароль владельца для «Довести правку» — проверка асинхронная (таймаут 8 с задаёт вызывающий), с индикатором и без блокировки
/// окна. Нет связи — понятное сообщение, окно закрывается без подтверждения (недоведённая правка остаётся, довести можно позже).</summary>
internal sealed class OwnerPasswordDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public bool Confirmed { get; private set; }

    private readonly TextBox _password = new() { MinHeight = 44, PasswordChar = '•', MaxLength = 128 };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _progressText = new() { FontSize = 12.5, IsVisible = false };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private readonly Button _ok;
    private readonly Func<string, Task<bool?>> _verify;
    private bool _checking;

    public OwnerPasswordDialog(string title, string message, Func<string, Task<bool?>> verify)
    {
        _verify = verify;
        Title = title;
        SectionCards.AsModal(this, 520);
        var panel = new StackPanel { Spacing = 8 };
        var msg = new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        msg.Bind(TextBlock.ForegroundProperty, msg.GetResourceObservable("BrushText"));
        panel.Children.Add(msg);
        _password.Watermark = T("Пароль NurCRM", "NurCRM сырсөзү", "NurCRM password", "NurCRM parolası", "NurCRM paroli");
        panel.Children.Add(_password);
        _progressText.Text = T("Проверяю пароль…", "Сырсөз текшерилүүдө…", "Checking the password…", "Parola kontrol ediliyor…", "Parol tekshirilmoqda…");
        _progressText.Bind(TextBlock.ForegroundProperty, _progressText.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(_progressText);
        panel.Children.Add(_progress);
        panel.Children.Add(SectionCards.ErrorBox(this, _error));
        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _ok = SectionCards.Primary(T("Подтвердить", "Ырастоо", "Confirm", "Onayla", "Tasdiqlash"));
        _ok.IsEnabled = false;
        _ok.Click += async (_, _) => await ConfirmAsync().ConfigureAwait(true);
        _password.TextChanged += (_, _) => _ok.IsEnabled = !_checking && (_password.Text ?? "").Length > 0;
        _password.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && _ok.IsEnabled)
            {
                e.Handled = true;
                await ConfirmAsync().ConfigureAwait(true);
            }
        };
        Opened += (_, _) => _password.Focus();
        Content = SectionCards.Shell(title, null, panel, SectionCards.Buttons(cancel, _ok));
    }

    private async Task ConfirmAsync()
    {
        if (_checking)
            return;
        _checking = true;
        _ok.IsEnabled = false;
        _password.IsEnabled = false;
        _error.IsVisible = false;
        _progressText.IsVisible = _progress.IsVisible = true;
        bool? ok;
        try
        {
            ok = await _verify(_password.Text ?? "").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Правка накладной: пароль не проверен ({ex.Message}).", "WARNING");
            ok = null;
        }
        _progressText.IsVisible = _progress.IsVisible = false;
        _checking = false;
        _password.IsEnabled = true;
        if (ok == true)
        {
            Confirmed = true;
            Close();
            return;
        }
        _error.Text = ok == false
            ? T("Пароль неверный.", "Сырсөз туура эмес.", "Wrong password.", "Parola yanlış.", "Parol noto'g'ri.")
            : InvoiceEditPreviewDialog.NoConnectionText;
        _error.IsVisible = true;
        _password.Focus();
        _password.SelectAll();
        _ok.IsEnabled = (_password.Text ?? "").Length > 0;
    }
}

/// <summary>2026-10-30 (ТЗ 2.5): «История версий» — исходный документ (v1) и каждая правка: кто, когда, с какого ПК, причина, сумма до → после и разница по строкам.
/// «Вернуть эту версию» — только у последней правки (откат = новая правка).</summary>
internal sealed class InvoiceHistoryDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Версия, которую просят откатить (последняя правка).</summary>
    public int? RevertVersion { get; private set; }

    public InvoiceHistoryDialog(SupplierReceipt r, string docNumber, ReceiptHistory history, bool canRevert, bool showMoney = true, IReadOnlyList<ServerEditVersion>? server = null)
    {
        Title = T("История версий", "Версиялар тарыхы", "Version history", "Sürüm geçmişi", "Versiyalar tarixi");
        SectionCards.AsModal(this, 760);
        var panel = new StackPanel { Spacing = 6 };
        TextBlock Line(string text, string brush = "BrushText", double size = 13, FontWeight? w = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = w ?? FontWeight.Normal };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
            return t;
        }
        var edits = history.Edits.Where(e => e.Status is "done" or "partial" or "abandoned").OrderByDescending(e => e.Version).ToList();
        if (edits.Count == 0)
            panel.Children.Add(SectionCards.Empty(this, T("Накладная не правилась: есть только исходная версия.", "Накладной оңдолгон эмес: баштапкы версия гана бар.", "The invoice was never edited: only the original version exists.", "Fatura hiç düzenlenmedi: yalnızca orijinal sürüm var.", "Yuk xati tuzatilmagan: faqat asl versiya bor.")));
        foreach (var e in edits)
        {
            var card = new StackPanel { Spacing = 2 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(Line($"v{e.Version}", size: 15, w: FontWeight.Bold));
            head.Children.Add(Line(e.Kind == "revert" ? T($"откат версии {e.RevertedVersion}", $"{e.RevertedVersion}-версияны кайтаруу", $"rollback of v{e.RevertedVersion}", $"v{e.RevertedVersion} geri alma", $"v{e.RevertedVersion} qaytarish") : e.Lines.Count == 0 && e.Diffs.Count > 0 ? T("правка накладной", "накладнойду оңдоо", "invoice edit", "fatura düzenleme", "yuk xatini tahrirlash") : T("правка количества", "санды оңдоо", "quantity edit", "miktar düzenleme", "miqdor tahriri"), "BrushTextSoft")); // 2026-11-01 (С2-24): серверная правка без строк количества — не «правка количества"
            if (e.Status == "partial")
                head.Children.Add(SectionCards.Pill(this, T("не до конца", "аягына чыккан жок", "incomplete", "tamamlanmadı", "tugallanmagan"), "BrushWarningSoft", "BrushText"));
            else if (e.Status == "abandoned") // 2026-10-30 (приёмка 1.17.67): владелец отказался от недоведённой правки
                head.Children.Add(SectionCards.Pill(this, T("прекращена", "токтотулган", "abandoned", "vazgeçildi", "to'xtatilgan"), "BrushWarningSoft", "BrushText"));
            card.Children.Add(head);
            card.Children.Add(Line($"{e.At:dd.MM.yyyy HH:mm} · {e.By} · {e.Device}", "BrushTextSoft", 12.5));
            card.Children.Add(Line(T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + e.Reason, w: FontWeight.SemiBold));
            if (showMoney) // 2026-10-30 (приёмка 1.17.66, 3е): суммы закупки видит только роль с правом на закупочные цены (RevisionShowPurchase), кассир — нет
                card.Children.Add(Line($"{MoneyText.Som(e.TotalBefore)} → {MoneyText.Som(e.TotalAfter)}"
                    + (e.PlannedTotalAfter is { } planned && Math.Abs(planned - e.TotalAfter) > 0.004 ? T($"  (по плану было {MoneyText.Som(planned)} — не проведено)", $"  (планда {MoneyText.Som(planned)} болчу — өткөрүлгөн жок)", $"  (planned {MoneyText.Som(planned)} — not posted)", $"  (planlanan {MoneyText.Som(planned)} — işlenmedi)", $"  (rejada {MoneyText.Som(planned)} edi — o'tkazilmadi)") : ""), "BrushText", 13));
            // 2026-10-31 (приёмка 1.17.68, 4б): после отказа от частично проведённой правки строки показывают проведённое, плановое — пометкой
            card.Children.Add(Line(string.Join("; ", e.Lines.Take(8).Select(l => $"{l.Name}: {ReceiptEditPlanner.Fmt(l.QtyBefore)} → {ReceiptEditPlanner.Fmt(l.QtyAfter)}"
                + (l.NotPosted ? (Math.Abs(l.QtyAfter - l.QtyBefore) < 0.0005 && l.PlannedQtyAfter is not null
                    ? T($" (не проведено, было в плане {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter.Value)})", $" (өткөрүлгөн жок, планда {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter.Value)})", $" (not posted, planned {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter.Value)})", $" (işlenmedi, planlanan {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter.Value)})", $" (o'tkazilmadi, rejada {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter.Value)})")
                    : T($" (проведено не полностью, по плану {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter ?? l.QtyAfter)})", $" (толук өткөрүлгөн жок, планда {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter ?? l.QtyAfter)})", $" (not fully posted, planned {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter ?? l.QtyAfter)})", $" (tam işlenmedi, planlanan {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter ?? l.QtyAfter)})", $" (to'liq o'tkazilmadi, rejada {ReceiptEditPlanner.Fmt(l.PlannedQtyAfter ?? l.QtyAfter)})")) : "")))
                + (e.Lines.Count > 8 ? "…" : ""), "BrushTextSoft", 12.5));
            // 2026-11-01: цена продажи / наценка товара, изменённые из накладной, — «было → стало» по каждому полю
            if (ReceiptEditAuditLog.FieldChanges(e).Where(f => f.Field is not ("quantity" or "removed" or "added_item") && (showMoney || f.Field is not ("purchase_price" or "discount" or "payment"))).ToList() is { Count: > 0 } priceFields) // 2026-11-01 (С2-24): все поля серверной правки; закупка, скидка и оплата — только с правом на деньги
                card.Children.Add(Line(string.Join("; ", priceFields.Select(ReceiptEditAuditLog.FieldText)), "BrushTextSoft", 12.5));
            if (canRevert && e == edits[0] && e.Status == "done" && e.Lines.Count > 0) // 2026-11-01 (С2-24): у серверной правки (PATCH) строк нет — откат из них собрать нельзя
            {
                var revert = SectionCards.Secondary(T("Вернуть эту версию", "Бул версияны кайтаруу", "Roll back this version", "Bu sürümü geri al", "Bu versiyani qaytarish"));
                revert.HorizontalAlignment = HorizontalAlignment.Left;
                revert.Click += (_, _) =>
                {
                    RevertVersion = e.Version;
                    Close();
                };
                ToolTip.SetTip(revert, T("Откат — новая правка с составом до этой версии: окно времени, предпросмотр, причина и пароль.", "Кайтаруу — ушул версиядан мурунку курамы менен жаңы оңдоо: убакыт терезеси, алдын ала көрүү, себеп жана сырсөз.",
                    "A rollback is a new edit with the pre-version state: time window, preview, reason and password.", "Geri alma, bu sürümden önceki durumla yeni bir düzenlemedir: süre penceresi, önizleme, neden ve parola.", "Qaytarish — shu versiyadan oldingi tarkib bilan yangi tahrir: vaqt oynasi, ko'rib chiqish, sabab va parol."));
                card.Children.Add(revert);
            }
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = card };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable(e == edits[0] ? "BrushAccentSoft" : "BrushPanelSoft"));
            panel.Children.Add(box);
        }
        // 2026-11-01: история с сервера (то, что правили на сайте, с телефона и с других касс) — рядом с локальным журналом этой кассы
        if (server is { Count: > 0 })
        {
            panel.Children.Add(Line(T("История на сервере (все кассы, сайт, телефон)", "Серверде тарых (бардык кассалар, сайт, телефон)", "Server history (all tills, site, phone)", "Sunucudaki geçmiş (tüm kasalar, site, telefon)", "Serverdagi tarix (barcha kassalar, sayt, telefon)"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
            foreach (var sv in server.Take(30))
            {
                var sp = new StackPanel { Spacing = 2 };
                sp.Children.Add(Line($"v{sv.Version} · {sv.At:dd.MM.yyyy HH:mm} · {sv.User}" + (sv.Device.Length > 0 ? " · " + sv.Device : ""), size: 13, w: FontWeight.SemiBold));
                if (sv.Reason.Length > 0)
                    sp.Children.Add(Line(T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + sv.Reason, "BrushTextSoft", 12.5));
                if (showMoney && sv.Total > 0)
                    sp.Children.Add(Line(MoneyText.Som(sv.Total), "BrushTextSoft", 12.5));
                if (sv.Summary.Length > 0 && showMoney) // 2026-11-01 (С2-24): в сводке сервера бывают закупочные цены — только с правом на деньги
                    sp.Children.Add(Line(sv.Summary.Length > 400 ? sv.Summary[..400] + "…" : sv.Summary, "BrushTextSoft", 12));
                var sbox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = sp };
                sbox.Bind(Border.BackgroundProperty, sbox.GetResourceObservable("BrushPanelSoft"));
                panel.Children.Add(sbox);
            }
        }
        // исходный документ — v1
        var first = new StackPanel { Spacing = 2 };
        first.Children.Add(Line("v1 · " + T("исходный документ", "баштапкы документ", "original document", "orijinal belge", "asl hujjat"), w: FontWeight.Bold, size: 14));
        first.Children.Add(Line($"{r.CreatedAt:dd.MM.yyyy HH:mm} · {r.CreatedBy}", "BrushTextSoft", 12.5));
        var origin = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = first };
        origin.Bind(Border.BackgroundProperty, origin.GetResourceObservable("BrushPanelSoft"));
        panel.Children.Add(origin);
        panel.Children.Add(Line(T("Историю не стираем: откат — это новая версия.", "Тарых өчүрүлбөйт: кайтаруу — жаңы версия.", "The history is never erased: a rollback is a new version.", "Geçmiş silinmez: geri alma yeni bir sürümdür.", "Tarix o'chirilmaydi: qaytarish — yangi versiya."), "BrushTextSoft", 12));
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title, $"{docNumber} · {r.SupplierName} · " + T($"сейчас версия {history.Version}", $"азыр {history.Version}-версия", $"now version {history.Version}", $"şimdi sürüm {history.Version}", $"hozir {history.Version}-versiya"), panel, SectionCards.Buttons(close));
    }
}

/// <summary>2026-11-01 (просьба владельца «рядом с товаром пишется, кто и когда редактировал»): вся история правок ОДНОЙ позиции накладной — время, кто (роль), было → стало, причина.</summary>
internal sealed class LineHistoryDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public LineHistoryDialog(string product, string docNumber, IReadOnlyList<LineEdit> edits)
    {
        Title = T("История позиции", "Позиция тарыхы", "Line history", "Satır geçmişi", "Pozitsiya tarixi");
        SectionCards.AsModal(this, 680);
        var panel = new StackPanel { Spacing = 6 };
        foreach (var e in edits)
        {
            var card = new StackPanel { Spacing = 2 };
            var head = new TextBlock { Text = ReceiptEditLineHistory.Mark(e), FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
            var why = new TextBlock { Text = T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + e.Reason + $"  ·  v{e.Version}", FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            why.Bind(TextBlock.ForegroundProperty, why.GetResourceObservable("BrushTextSoft"));
            card.Children.Add(head);
            card.Children.Add(why);
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = card };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("BrushPanelSoft"));
            panel.Children.Add(box);
        }
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title, $"{docNumber} · {product}", panel, SectionCards.Buttons(close));
    }
}
