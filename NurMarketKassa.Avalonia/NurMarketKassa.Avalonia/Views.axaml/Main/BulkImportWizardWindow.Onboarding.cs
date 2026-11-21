using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-13 (Л-10, «Новичок: перенос данных»): часть мастера загрузки товаров — перенос из чужих программ (выбор и автоопределение источника по готовому
// профилю столбцов), «Посмотреть на примере» (демо-магазин, только в памяти), «Клиенты и долги» и подсказки «что дальше» после загрузки.
public partial class BulkImportWizardWindow
{
    private Button _demoBtn = null!, _clientsBtn = null!;
    private readonly ComboBox _sourceCombo = new() { MinHeight = 44, Width = 250 };
    private readonly TextBlock _sourceNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private StackPanel _sourceRow = null!;
    private readonly Border _nextPanel = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _nextText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private bool _sourceBusy;
    private bool _anyLoaded;
    private bool _demoRequested;

    /// <summary>Что вернул мастер, открытый из «Первого запуска» (Н-1): загружено ли что-то и хочет ли кассир попробовать демо-магазин.</summary>
    public readonly record struct OnboardingResult(bool Loaded, bool DemoRequested);

    /// <summary>2026-11-13 (Л-10): точка входа для мастера первого запуска (Н-1). Открывает «Массовую загрузку товаров» модально поверх <paramref name="owner"/>;
    /// вернётся, когда окно закроют. Loaded — что-то записано (приход/карточки пошли на сервер обычным путём); DemoRequested — нажато «Посмотреть на примере →
    /// Попробовать продажу на примере» (набор уже включён в <see cref="DemoStoreCatalog"/>; учебный режим включает вызывающий).</summary>
    public static async Task<OnboardingResult> ShowForOnboardingAsync(Window owner)
    {
        if (!ImportAccess.Allowed) // 2026-11-21: в кассе мастер загрузки не открывается ни по какой ссылке
            return new OnboardingResult(false, false);
        BulkImportWizardWindow window;
        try
        {
            if (_openWizard is { IsVisible: true } already)
            {
                already.Activate();
                return new OnboardingResult(false, false);
            }
            window = new BulkImportWizardWindow();
            _openWizard = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_openWizard, window)) _openWizard = null; };
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: окно для мастера первого запуска не открылось: {DescribeException(ex)}", "ERROR");
            return new OnboardingResult(false, false);
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
        return new OnboardingResult(window._anyLoaded, window._demoRequested);
    }

    /// <summary>2026-11-20: только для тестов — подмена права (кассир без «Склад: редактирование»); в программе всегда пусто.</summary>
    public static Func<bool>? CanOpenTransferOverride { get; set; }

    /// <summary>2026-11-20 (владелец: «где импорт из других программ?»): общее право на заметные входы «Перенос данных» (Настройки → Операции, меню ☰ кассы,
    /// программа владельца, обучение) — то же, что у кнопки «Массовая загрузка» в складе: «Склад: редактирование» (владелец и администратор его имеют).</summary>
    public static bool CanOpenTransfer()
    {
        if (!ImportAccess.Allowed) // 2026-11-21: импорт — только в программе владельца, в кассе входов нет (ImportAccess)
            return false;
        if (CanOpenTransferOverride is { } probe)
            return probe();
        var permissions = App.AppHost?.Services is { } sp
            ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<NurMarketKassa.Core.Contracts.IPermissionService>(sp)
            : null;
        return permissions is null || permissions.HasPermission(NurMarketKassa.Core.Contracts.PosPermissions.EditProducts);
    }

    /// <summary>2026-11-20: все новые входы открывают это же окно мастера: он один на программу (<see cref="_openWizard"/>), повторный вход лишь выводит его вперёд.</summary>
    public static void OpenTransfer(Window? owner)
    {
        if (!CanOpenTransfer())
            return;
        Open(owner);
    }

    private void InitOnboarding()
    {
        _demoBtn = SectionCards.Secondary(T("Посмотреть на примере", "Мисалда көрүү", "See an example", "Örnekte görün", "Misolda ko'rish"));
        _clientsBtn = SectionCards.Secondary(T("Клиенты и долги", "Кардарлар жана карыздар", "Customers and debts", "Müşteriler ve borçlar", "Mijozlar va qarzlar"));
        foreach (var b in new[] { _demoBtn, _clientsBtn })
            b.MinHeight = 44;
        ToolTip.SetTip(_demoBtn, T("30 учебных товаров только для пробы: на сервер и в вашу базу не попадают.", "Сынап көрүү үчүн 30 окуу товары: серверге жана базаңызга түшпөйт.",
            "30 training products just for trying: they do not go to the server or your database.", "Yalnızca denemek için 30 eğitim ürünü: sunucuya ve veritabanınıza girmez.", "Faqat sinash uchun 30 ta o'quv mahsuloti: serverga va bazangizga tushmaydi."));
        ToolTip.SetTip(_clientsBtn, T("Перенос списка клиентов из Excel/CSV. Долги не записываются — только показываются.", "Кардарлар тизмесин Excel/CSV'ден которуу. Карыздар жазылбайт — көрсөтүлөт гана.",
            "Move a customer list from Excel/CSV. Debts are not written — only shown.", "Müşteri listesini Excel/CSV'den aktarın. Borçlar yazılmaz — yalnızca gösterilir.", "Mijozlar ro'yxatini Excel/CSV dan ko'chirish. Qarzlar yozilmaydi — faqat ko'rsatiladi."));
        _demoBtn.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (await DemoStoreWindow.ShowAsync(this).ConfigureAwait(true))
            {
                _demoRequested = true;
                Close(); // набор включён — к кассе (вызывающий включает учебный режим)
            }
        }, "демо-магазин").ConfigureAwait(true);
        _clientsBtn.Click += async (_, _) => await GuardAsync(async () => await ClientsImportWindow.ShowAsync(this).ConfigureAwait(true), "клиенты").ConfigureAwait(true);

        // «Из какой программы файл?»
        _sourceCombo.Items.Add(T("Определить по файлу", "Файлдан аныктоо", "Detect from the file", "Dosyadan algıla", "Fayldan aniqlash"));
        foreach (var p in ImportSourceProfiles.All)
            _sourceCombo.Items.Add(p.Name);
        if (AutoPartsProfile.IsActive)
        {
            _sourceCombo.Items.Add(AutoPartsPriceImport.ProfileName); // 2026-11-20: прайс поставщика (автозапчасти) — последний пункт
            _sourceCombo.Width = double.NaN; // длинное название пункта не должно обрезаться (ширина по содержимому, не меньше 250)
            _sourceCombo.MinWidth = 250;
        }
        _sourceCombo.SelectedIndex = 0;
        _sourceCombo.SelectionChanged += (_, _) =>
        {
            if (_sourceBusy || _table is null || _started)
                return;
            ApplySelectedSource();
        };
        _sourceNote.Bind(TextBlock.ForegroundProperty, _sourceNote.GetResourceObservable("BrushTextSoft"));
        _sourceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4),
            Children = { Caption(T("Из какой программы файл?", "Файл кайсы программадан?", "Which program is the file from?", "Dosya hangi programdan?", "Fayl qaysi dasturdan?")), _sourceCombo },
        };

        _nextPanel.Bind(Border.BackgroundProperty, _nextPanel.GetResourceObservable("BrushPanelSoft"));
        _nextPanel.Bind(Border.BorderBrushProperty, _nextPanel.GetResourceObservable("BrushBorder"));
        _nextText.Bind(TextBlock.ForegroundProperty, _nextText.GetResourceObservable("BrushText"));
        _nextPanel.Child = _nextText;
    }

    /// <summary>Выбор программы вручную: столбцы ставятся по её профилю (0 — обратно к автоопределению).</summary>
    private void ApplySelectedSource()
    {
        if (_table is null)
            return;
        var idx = _sourceCombo.SelectedIndex;
        if (AutoPartsProfile.IsActive && idx == ImportSourceProfiles.All.Count + 1)
        {
            EnableAutoParts(); // 2026-11-20: прайс поставщика (автозапчасти)
            _mapping = ProductImportMapping.Resolve(_table.Headers, _table);
            UpdateSourceNote();
            ScheduleRecheck();
            return;
        }
        DisableAutoParts();
        if (idx <= 0)
            _mapping = ProductImportMapping.Resolve(_table.Headers, _table);
        else
        {
            _mapping = ProductImportMapping.AutoDetect(_table.Headers, _table);
            ImportSourceProfiles.Apply(ImportSourceProfiles.All[idx - 1], _mapping, _table.Headers, _table);
        }
        _loadingFile = true;
        SetCardsMode(!_mapping.HasQuantity);
        _loadingFile = false;
        BuildMappingChoices();
        UpdateSourceNote();
        ScheduleRecheck();
    }

    /// <summary>После чтения файла: показать, какая программа узнана (или что файл обычный), и выставить выпадающий список.</summary>
    private void UpdateSourceUi()
    {
        _sourceBusy = true;
        try
        {
            var src = _mapping.Source;
            _sourceCombo.SelectedIndex = _apOn ? ImportSourceProfiles.All.Count + 1
                : src is null ? 0 : ImportSourceProfiles.All.ToList().FindIndex(p => p.Id == src.Id) + 1;
        }
        finally
        {
            _sourceBusy = false;
        }
        UpdateSourceNote();
    }

    private void UpdateSourceNote()
    {
        if (_table is null)
        {
            _sourceNote.IsVisible = false;
            return;
        }
        var src = _mapping.Source;
        if (_apOn)
        {
            _sourceNote.Text = AutoPartsPriceImport.HowTo;
        }
        else if (src is not null)
        {
            _sourceNote.Text = T($"Файл из «{src.Name}»: столбцы подобраны по готовому шаблону — проверьте и нажмите «Проверить». ",
                $"Файл «{src.Name}» программасынан: мамычалар даяр үлгү боюнча тандалды — текшерип, «Текшерүү» басыңыз. ",
                $"The file is from “{src.Name}”: columns were matched with a ready template — check them and press “Check”. ",
                $"Dosya «{src.Name}» programından: sütunlar hazır şablona göre eşlendi — kontrol edip «Kontrol et»e basın. ",
                $"Fayl «{src.Name}» dasturidan: ustunlar tayyor shablon bo'yicha tanlandi — tekshirib, «Tekshirish»ni bosing. ") + src.HowToExport;
        }
        else if (ClientsImportPlan.LooksLikeClients(_table.Headers))
        {
            _sourceNote.Text = T("Похоже, это список КЛИЕНТОВ, а не товаров. Нажмите «Клиенты и долги» вверху — там перенос клиентов.",
                "Бул товарлар эмес, КАРДАРЛАР тизмеси окшойт. Жогоруда «Кардарлар жана карыздар» басыңыз — кардарларды которуу ошол жерде.",
                "This looks like a list of CUSTOMERS, not products. Press “Customers and debts” at the top — customers are moved there.",
                "Bu ürün değil MÜŞTERİ listesi gibi görünüyor. Üstteki «Müşteriler ve borçlar»a basın — müşteri aktarımı orada.",
                "Bu mahsulotlar emas, MIJOZLAR ro'yxatiga o'xshaydi. Yuqoridagi «Mijozlar va qarzlar»ni bosing — mijozlarni ko'chirish o'sha yerda.");
        }
        else
        {
            _sourceNote.Text = T("Программа файла не узнана — столбцы подобраны по названиям. Если файл из 1С, МойСклад, Poster, Loyverse, Paloma365 или Umag — выберите её выше.",
                "Файлдын программасы аныкталган жок — мамычалар аталышы боюнча тандалды. Файл 1С, МойСклад, Poster, Loyverse, Paloma365 же Umag'дан болсо — жогоруда тандаңыз.",
                "The file's program was not recognised — columns were matched by name. If the file is from 1C, MoySklad, Poster, Loyverse, Paloma365 or Umag — choose it above.",
                "Dosyanın programı tanınmadı — sütunlar adlarına göre eşlendi. Dosya 1C, MoySklad, Poster, Loyverse, Paloma365 veya Umag'dansa — yukarıdan seçin.",
                "Fayl dasturi aniqlanmadi — ustunlar nomi bo'yicha tanlandi. Fayl 1C, MoySklad, Poster, Loyverse, Paloma365 yoki Umag dan bo'lsa — yuqoridan tanlang.");
        }
        _sourceNote.IsVisible = true;
    }

    /// <summary>Строка «Как выгрузить» по каждой программе — в панель «Какой файл подходит?».</summary>
    private static Control BuildSourcesHint()
    {
        var text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        text.Text = T("Из другой программы: ", "Башка программадан: ", "From another program: ", "Başka bir programdan: ", "Boshqa dasturdan: ")
            + string.Join("  ·  ", ImportSourceProfiles.All.Select(p => p.Name + " — " + p.HowToExport));
        return text;
    }

    /// <summary>Короткие подсказки «что дальше» после загрузки (показываются под статусом).</summary>
    internal static string NextStepsText(bool cardsOnly) => cardsOnly
        ? T("Что дальше: 1) Проверьте цены нескольких товаров на складе. 2) Остатки не загружались — впишите их через «Приёмку» или «Ревизию». "
            + "3) Распечатайте ценники и этикетки. 4) Вернитесь на кассу и отсканируйте любой товар — первая продажа.",
            "Андан ары: 1) Кампадагы бир нече товардын баасын текшериңиз. 2) Калдыктар жүктөлгөн жок — «Кабыл алуу» же «Ревизия» аркылуу киргизиңиз. "
            + "3) Баа жана энбелгилерди басып чыгарыңыз. 4) Кассага кайтып, каалаган товарды сканерлеңиз — биринчи сатуу.",
            "What next: 1) Check the prices of a few products in the warehouse. 2) Stock was not loaded — enter it through “Receiving” or “Stocktake”. "
            + "3) Print price tags and labels. 4) Go back to the till and scan any product — your first sale.",
            "Sonra: 1) Depodaki birkaç ürünün fiyatını kontrol edin. 2) Stok yüklenmedi — «Mal kabul» veya «Sayım» ile girin. "
            + "3) Fiyat etiketlerini ve etiketleri yazdırın. 4) Kasaya dönüp herhangi bir ürünü okutun — ilk satışınız.",
            "Keyin: 1) Ombordagi bir nechta mahsulot narxini tekshiring. 2) Qoldiq yuklanmadi — «Qabul» yoki «Taftish» orqali kiriting. "
            + "3) Narx yorliqlari va etiketkalarni chop eting. 4) Kassaga qaytib, istalgan mahsulotni skanerlang — birinchi sotuv.")
        : T("Что дальше: 1) Откройте склад и проверьте цены и остатки нескольких товаров. 2) Если цены не было в файле — поправьте её в карточке товара на складе. "
            + "3) Распечатайте ценники и этикетки. 4) Вернитесь на кассу и отсканируйте любой товар — первая продажа.",
            "Андан ары: 1) Кампаны ачып, бир нече товардын баасын жана калдыгын текшериңиз. 2) Файлда баа жок болсо — кампадагы товар карточкасында оңдоңуз. "
            + "3) Баа жана энбелгилерди басып чыгарыңыз. 4) Кассага кайтып, каалаган товарды сканерлеңиз — биринчи сатуу.",
            "What next: 1) Open the warehouse and check prices and stock of a few products. 2) If the file had no prices — fix them in the product card in the warehouse. "
            + "3) Print price tags and labels. 4) Go back to the till and scan any product — your first sale.",
            "Sonra: 1) Depoyu açıp birkaç ürünün fiyat ve stokunu kontrol edin. 2) Dosyada fiyat yoksa — depodaki ürün kartında düzeltin. "
            + "3) Fiyat etiketlerini ve etiketleri yazdırın. 4) Kasaya dönüp herhangi bir ürünü okutun — ilk satışınız.",
            "Keyin: 1) Omborni ochib, bir nechta mahsulot narxi va qoldig'ini tekshiring. 2) Faylda narx bo'lmasa — ombordagi mahsulot kartochkasida tuzating. "
            + "3) Narx yorliqlari va etiketkalarni chop eting. 4) Kassaga qaytib, istalgan mahsulotni skanerlang — birinchi sotuv.");

    /// <summary>Загрузка закончилась успешно — показать «что дальше».</summary>
    private void ShowNextSteps(bool cardsOnly)
    {
        _anyLoaded = true;
        _nextText.Text = NextStepsText(cardsOnly);
        _nextPanel.IsVisible = true;
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
    }
}
