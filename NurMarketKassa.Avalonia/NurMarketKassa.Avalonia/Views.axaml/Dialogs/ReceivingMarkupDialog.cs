using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-28, просьба владельца «автоматическая наценка в ручном режиме приёмки»: окно правил автонаценки (Склад → Приёмка и
/// Закупки → Приём товара). Включить/выключить автоматический расчёт цены продажи по закупке, «Наценка по умолчанию, %», наценка по
/// категориям («Категория = %» по строке), округление цены продажи. Хранится в receiving-markup.json (ReceivingMarkupSettingsStore).
/// Менять могут владелец и администратор; остальные видят значения, но не меняют.</summary>
public sealed class ReceivingMarkupDialog : Window
{
    private readonly CheckBox _auto;
    private readonly TextBox _default;
    private readonly ComboBox _rounding;
    private readonly TextBox _categories;
    private readonly TextBlock _error = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly bool _canEdit;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Владелец или администратор (программа владельца и автономный режим — владелец) — как доступ к ключам ИИ в настройках.</summary>
    internal static bool CanEditSettings()
    {
        if (AppMode.IsOwner)
            return true;
        if (NurMarketKassa.AvaloniaHost.App.AppHost?.Services is not { } sp)
            return false;
        if (sp.GetService<IAutonomousAuthService>() is { IsCurrentSessionAutonomous: true })
            return true;
        return sp.GetService<IAuthApiService>() is { } auth && PermissionService.IsOwnerOrAdmin(auth.UserPayload);
    }

    public static string RoundingTitle(SaleRounding r) => r switch
    {
        SaleRounding.Half => T("до 0,5 сома", "0,5 сомго чейин", "to 0.5 som", "0,5 som'a", "0,5 so'mgacha"),
        SaleRounding.Tenth => T("до 0,1 сома", "0,1 сомго чейин", "to 0.1 som", "0,1 som'a", "0,1 so'mgacha"),
        SaleRounding.None => T("до 0,01 (без округления)", "0,01 чейин (тегеректөөсүз)", "to 0.01 (no rounding)", "0,01'e (yuvarlamasız)", "0,01 gacha (yaxlitlashsiz)"),
        _ => T("до целого сома вверх (как раньше)", "бүтүн сомго чейин жогору (мурдагыдай)", "up to a whole som (as before)", "tam som'a yukarı (eskisi gibi)", "butun so'mgacha yuqoriga (avvalgidek)"),
    };

    public ReceivingMarkupDialog()
    {
        _canEdit = CanEditSettings();
        var s = ReceivingMarkupSettingsStore.Current;
        Title = T("Автоматическая наценка", "Автоматтык кошумча", "Automatic markup", "Otomatik kâr oranı", "Avtomatik ustama");
        SectionCards.AsModal(this, 640);

        _auto = new CheckBox
        {
            IsChecked = s.AutoEnabled, IsEnabled = _canEdit,
            Content = T("Считать цену продажи сама, когда я ввожу закупку вручную", "Сатып алуу баасын кол менен киргизгенде сатуу баасы өзү эсептелсин",
                "Calculate the sale price automatically when I enter the purchase price by hand", "Alış fiyatını elle girdiğimde satış fiyatı otomatik hesaplansın", "Xarid narxini qo'lda kiritganimda sotuv narxi o'zi hisoblansin"),
        };
        _default = new TextBox { Text = s.DefaultPercent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), Width = 120, Height = 40, IsEnabled = _canEdit, HorizontalAlignment = HorizontalAlignment.Left };
        _rounding = new ComboBox { Width = 330, MinHeight = 40, IsEnabled = _canEdit, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var r in new[] { SaleRounding.WholeUp, SaleRounding.Half, SaleRounding.Tenth, SaleRounding.None })
            _rounding.Items.Add(new ComboBoxItem { Content = RoundingTitle(r), Tag = r });
        _rounding.SelectedIndex = Math.Max(0, (int)s.Rounding);
        _categories = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 120, IsEnabled = _canEdit,
            Text = string.Join("\n", s.CategoryPercents.OrderBy(k => k.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(k => k.Key + " = " + k.Value.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")))),
            Watermark = T("Напитки = 25\nХлеб = 15", "Суусундуктар = 25\nНан = 15", "Drinks = 25\nBread = 15", "İçecekler = 25\nEkmek = 15", "Ichimliklar = 25\nNon = 15"),
        };

        Control Label(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushText"));
            return tb;
        }
        Control Hint(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
            return tb;
        }

        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));
        var root = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _auto,
                Hint(T("Общая наценка («Наценка для всех, %» над списком приёмки) действует на все строки и на каждый следующий скан. Правите цену или наценку у одной строки — она помечается «вручную» и общей наценке не следует (кнопка «Как у всех» возвращает). Если у категории задана своя наценка ниже — она важнее общей.",
                    "Жалпы кошумча (кабыл алуу тизмесинин үстүндөгү «Баарына кошумча, %») бардык саптарга жана ар бир кийинки сканга колдонулат. Бир саптын баасын же кошумчасын өзгөртсөңүз — ал «колдо» деп белгиленет жана жалпы кошумчага баш ийбейт («Баарындай» баскычы кайтарат). Төмөндө категорияга өзүнчө кошумча коюлса — ал жалпыдан маанилүү.",
                    "The common markup (“Markup for all, %” above the receiving list) applies to every line and to each next scan. Change the price or markup of one line — it is marked “manual” and stops following the common markup (the “Like all” button brings it back). If a category below has its own markup, it wins over the common one.",
                    "Ortak kâr oranı (mal kabul listesinin üstündeki «Tümü için kâr, %») tüm satırlara ve her yeni okutmaya uygulanır. Bir satırın fiyatını veya kârını değiştirirseniz «elle» işaretlenir ve ortak orana uymaz («Hepsi gibi» düğmesi geri getirir). Aşağıda kategorinin kendi kârı varsa ortaktan önceliklidir.",
                    "Umumiy ustama (qabul ro'yxati ustidagi «Hammasi uchun ustama, %») barcha qatorlarga va har bir keyingi skanerga qo'llanadi. Bitta qatorning narxi yoki ustamasini o'zgartirsangiz — u «qo'lda» deb belgilanadi va umumiy ustamaga bo'ysunmaydi («Hammasi kabi» tugmasi qaytaradi). Quyida kategoriyaning o'z ustamasi bo'lsa — u umumiydan ustun.")),
                Label(T("Наценка для всех, %", "Баарына кошумча, %", "Markup for all, %", "Tümü için kâr, %", "Hammasi uchun ustama, %")),
                _default,
                Label(T("Наценка по категориям (по строке: «Категория = %»)", "Категория боюнча кошумча (ар бир сапта: «Категория = %»)", "Markup by category (one per line: “Category = %”)",
                    "Kategoriye göre kâr (her satıra: «Kategori = %»)", "Kategoriya bo'yicha ustama (har qatorda: «Kategoriya = %»)")),
                _categories,
                Label(T("Округление цены продажи", "Сатуу баасын тегеректөө", "Sale price rounding", "Satış fiyatı yuvarlama", "Sotuv narxini yaxlitlash")),
                _rounding,
                _error,
            },
        };
        if (!_canEdit)
            root.Children.Insert(0, Hint(T("Менять эти правила могут владелец и администратор. Вы можете только посмотреть.", "Бул эрежелерди ээси жана администратор гана өзгөртө алат. Сиз карап гана көрө аласыз.",
                "Only the owner and the administrator can change these rules. You can only view them.", "Bu kuralları yalnızca sahip ve yönetici değiştirebilir. Yalnızca görüntüleyebilirsiniz.", "Bu qoidalarni faqat egasi va administrator o'zgartira oladi. Siz faqat ko'rishingiz mumkin.")));

        var cancel = SectionCards.Cancel(_canEdit ? T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish") : T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        cancel.Click += (_, _) => Close();
        var footerItems = new List<Control> { cancel };
        if (_canEdit)
        {
            var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
            save.Click += (_, _) => Save();
            footerItems.Add(save);
        }
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var c in footerItems)
            footer.Children.Add(c);
        Content = SectionCards.Shell(Title, T("Цена продажи в приёмке считается по закупке и наценке; на сервер уходит при «Оприходовать».", "Кабыл алуудагы сатуу баасы сатып алуу баасы жана кошумча боюнча эсептелет; серверге «Кирешеге алуу» басылганда кетет.",
            "The sale price in receiving is calculated from the purchase price and markup; it reaches the server on “Receive”.", "Mal kabulündeki satış fiyatı alış fiyatı ve kâr oranından hesaplanır; sunucuya «Stoğa al» ile gider.", "Qabuldagi sotuv narxi xarid narxi va ustamadan hisoblanadi; serverga «Kirim qilish»da ketadi."), root, footer);
    }

    private void Fail(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void Save()
    {
        if (CheckoutValidation.TryParseAmount(_default.Text, out var def) != CheckoutValidation.AmountParse.Ok || def > 1000)
        {
            Fail(T("«Наценка по умолчанию» — число от 0 до 1000, например 30 или 12,5.", "«Демейки кошумча» — 0дөн 1000ге чейинки сан, мисалы 30 же 12,5.",
                "“Default markup” is a number from 0 to 1000, for example 30 or 12.5.", "«Varsayılan kâr» 0 ile 1000 arasında bir sayıdır, örneğin 30 veya 12,5.", "«Standart ustama» — 0 dan 1000 gacha son, masalan 30 yoki 12,5."));
            return;
        }
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (_categories.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var cut = line.LastIndexOfAny(new[] { '=', ':', ';' });
            if (cut <= 0 || CheckoutValidation.TryParseAmount(line[(cut + 1)..], out var percent) != CheckoutValidation.AmountParse.Ok || percent > 1000)
            {
                Fail(T($"Строка «{line}»: нужно «Категория = %», например «Напитки = 25».", $"«{line}» сабы: «Категория = %» болушу керек, мисалы «Суусундуктар = 25».",
                    $"Line “{line}”: expected “Category = %”, for example “Drinks = 25”.", $"«{line}» satırı: «Kategori = %» olmalı, örneğin «İçecekler = 25».", $"«{line}» qatori: «Kategoriya = %» bo'lishi kerak, masalan «Ichimliklar = 25»."));
                return;
            }
            map[line[..cut].Trim()] = percent;
        }
        var rounding = (_rounding.SelectedItem as ComboBoxItem)?.Tag is SaleRounding r ? r : SaleRounding.WholeUp;
        var settings = new ReceivingMarkupSettings { AutoEnabled = _auto.IsChecked == true, DefaultPercent = def, CategoryPercents = map, Rounding = rounding };
        if (!ReceivingMarkupSettingsStore.Save(settings))
        {
            Fail(T("Не удалось сохранить настройки на этом компьютере.", "Жөндөөлөр бул компьютерде сакталган жок.", "The settings could not be saved on this computer.", "Ayarlar bu bilgisayarda kaydedilemedi.", "Sozlamalar bu kompyuterda saqlanmadi."));
            return;
        }
        PosLogger.Log($"Наценка приёмки: авто {(settings.AutoEnabled ? "вкл" : "выкл")}, по умолчанию {def:0.##} %, категорий {map.Count}, округление {rounding}.", "STOCK");
        Close();
    }
}
