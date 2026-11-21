using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-21 («Базар»): функции базара в «Настройки → Операции → Сфера»: переключатели модулей (плитки категорий, цена по количеству, торг, валюты, накладная и «карго»),
// «Курсы валют…» и пороги торга (PIN выше N %, предел выше M %). Строится кодом один раз внутри BazaarPanel; видно только в сфере «Базар» (RefreshBazaarUi прячет панель целиком).
public partial class OperationsSettingsView
{
    private bool _bazaarExtrasBuilt;
    private readonly Dictionary<string, CheckBox> _bazaarModuleChecks = new();
    private Button? _bazaarRatesButton;
    private TextBox? _bazaarPinPercent, _bazaarHardPercent;
    private TextBlock? _bazaarHaggleMessage;

    private static string ModuleTitle(string id) => id switch
    {
        BazaarModules.CategoryTiles => Tr.T("Плитки категорий на главном экране", "Башкы экранда категория плиткалары", "Category tiles on the main screen", "Ana ekranda kategori kutuları", "Asosiy ekranda kategoriya plitkalari"),
        BazaarModules.VolumePrice => Tr.T("Цена по количеству («от N штук — дешевле»)", "Санына жараша баа («N даанадан — арзан»)", "Price by quantity (“from N pieces — cheaper”)", "Miktara göre fiyat («N adetten — daha ucuz»)", "Miqdorga qarab narx («N dan — arzonroq»)"),
        BazaarModules.Haggle => Tr.T("Торг: «До 10 / 50 / 100» и «Договорились за …»", "Соодалашуу: «10 / 50 / 100 чейин» жана «Макулдашылган сумма …»", "Haggling: “To 10 / 50 / 100” and “Agreed for …”", "Pazarlık: «10 / 50 / 100'e kadar» ve «Anlaşılan tutar …»", "Savdolashuv: «10 / 50 / 100 gacha» va «Kelishilgan summa …»"),
        BazaarModules.Currencies => Tr.T("Валюты: оплата в USD / RUB / CNY", "Валюталар: USD / RUB / CNY менен төлөм", "Currencies: payment in USD / RUB / CNY", "Dövizler: USD / RUB / CNY ile ödeme", "Valyutalar: USD / RUB / CNY da to'lov"),
        BazaarModules.InvoiceCargo => Tr.T("Накладная A4 и «карго» (опт)", "A4 накладная жана «карго» (дүң)", "A4 invoice and “cargo” (wholesale)", "A4 irsaliye ve «kargo» (toptan)", "A4 hisob-faktura va «kargo» (ulgurji)"),
        _ => id,
    };

    private void BuildBazaarExtras()
    {
        if (!_bazaarExtrasBuilt)
        {
            _bazaarExtrasBuilt = true;
            var title = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), Text = Tr.T("Функции «Базара»", "«Базар» функциялары", "Bazaar features", "Pazar işlevleri", "«Bozor» funksiyalari") };
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
            BazaarPanel.Children.Add(title);
            foreach (var id in BazaarModules.All)
            {
                var moduleId = id;
                var check = new CheckBox { Classes = { "ToggleSwitch" }, MinHeight = 44 };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (_bazaarUiBusy)
                        return;
                    if (!BazaarModules.SetOwnerChoice(moduleId, check.IsChecked == true))
                        PosLogger.Log("Модуль «Базара» не записан: настройки сфер не помещаются в лимит.", "WARNING");
                    RefreshBazaarExtras();
                };
                _bazaarModuleChecks[id] = check;
                BazaarPanel.Children.Add(check);
            }

            _bazaarRatesButton = new Button { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 0) };
            _bazaarRatesButton.Classes.Add("modal-cancel");
            _bazaarRatesButton.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                    await PosDialogHost.ShowModalAsync(new BazaarRatesDialog(), owner).ConfigureAwait(true);
            };
            BazaarPanel.Children.Add(_bazaarRatesButton);

            // Пороги торга: подпись отдельной строкой над полем, два поля одной высоты в ячейках сетки.
            var haggleTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            haggleTitle.Text = Tr.T("Предел скидки торга", "Соодалашуу арзандатуусунун чеги", "Haggling discount limit", "Pazarlık indirimi sınırı", "Savdolashuv chegirmasi chegarasi");
            haggleTitle.Bind(TextBlock.ForegroundProperty, haggleTitle.GetResourceObservable("BrushText"));
            BazaarPanel.Children.Add(haggleTitle);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), RowDefinitions = new RowDefinitions("Auto,4,Auto"), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
            var pinLabel = SmallLabel(Tr.T("PIN руководителя выше, %", "Жетекчинин PIN коду жогору, %", "Manager PIN above, %", "Yönetici PIN'i üzerinde, %", "Rahbar PIN kodi yuqori, %"));
            var hardLabel = SmallLabel(Tr.T("Только владелец выше, %", "Ээси гана жогору, %", "Owner only above, %", "Yalnızca sahip üzerinde, %", "Faqat egasi yuqori, %"));
            _bazaarPinPercent = new TextBox { Height = 44, MaxLength = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
            _bazaarHardPercent = new TextBox { Height = 44, MaxLength = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetColumn(hardLabel, 2);
            Grid.SetRow(_bazaarPinPercent, 2);
            Grid.SetRow(_bazaarHardPercent, 2);
            Grid.SetColumn(_bazaarHardPercent, 2);
            grid.Children.Add(pinLabel);
            grid.Children.Add(hardLabel);
            grid.Children.Add(_bazaarPinPercent);
            grid.Children.Add(_bazaarHardPercent);
            _bazaarPinPercent.LostFocus += (_, _) => SaveBazaarHaggleLimits();
            _bazaarHardPercent.LostFocus += (_, _) => SaveBazaarHaggleLimits();
            BazaarPanel.Children.Add(grid);
            _bazaarHaggleMessage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            _bazaarHaggleMessage.Bind(TextBlock.ForegroundProperty, _bazaarHaggleMessage.GetResourceObservable("BrushTextSoft"));
            BazaarPanel.Children.Add(_bazaarHaggleMessage);
        }
        RefreshBazaarExtras();
    }

    private static TextBlock SmallLabel(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushTextSoft"));
        return l;
    }

    private void RefreshBazaarExtras()
    {
        var wasBusy = _bazaarUiBusy;
        _bazaarUiBusy = true;
        try
        {
            foreach (var (id, check) in _bazaarModuleChecks)
            {
                check.Content = ModuleTitle(id);
                check.IsChecked = BazaarModules.IsOn(id);
            }
            if (_bazaarRatesButton is not null)
            {
                _bazaarRatesButton.Content = Tr.T("Курсы валют…", "Валюта курстары…", "Exchange rates…", "Döviz kurları…", "Valyuta kurslari…");
                _bazaarRatesButton.IsVisible = BazaarModules.CurrenciesOn;
            }
            if (_bazaarPinPercent is not null && _bazaarHardPercent is not null)
            {
                _bazaarPinPercent.Text = BazaarHaggle.PinPercent.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
                _bazaarHardPercent.Text = BazaarHaggle.HardPercent.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
            }
        }
        finally
        {
            _bazaarUiBusy = wasBusy;
        }
    }

    private void SaveBazaarHaggleLimits()
    {
        if (_bazaarUiBusy || _bazaarPinPercent is null || _bazaarHardPercent is null || _bazaarHaggleMessage is null)
            return;
        var pin = BazaarHaggle.ParseAmount(_bazaarPinPercent.Text);
        var hard = BazaarHaggle.ParseAmount(_bazaarHardPercent.Text);
        if (pin is null || hard is null || !BazaarHaggle.SetPercents((double)pin.Value, (double)hard.Value))
        {
            _bazaarHaggleMessage.Text = Tr.T("Проценты: числа от 0 до 100, «только владелец» не меньше «PIN».", "Пайыз: 0дөн 100гө чейин сандар, «ээси гана» «PIN»ден аз болбосун.",
                "Percentages: numbers from 0 to 100, “owner only” not below “PIN”.", "Yüzdeler: 0 ile 100 arası sayılar, «yalnızca sahip» «PIN»'den az olmasın.", "Foizlar: 0 dan 100 gacha sonlar, «faqat egasi» «PIN» dan kam bo'lmasin.");
            RefreshBazaarExtras();
            return;
        }
        _bazaarHaggleMessage.Text = "";
    }
}
