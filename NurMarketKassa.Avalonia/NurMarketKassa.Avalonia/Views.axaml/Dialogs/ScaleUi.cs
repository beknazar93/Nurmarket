using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-09-28: общее для окон «Поиск весов в сети», «Настройки весов Rongta» и
/// «Настройки весов TM-30F»: строительные блоки карточек (как в ShtrikhScaleSettingsWindow),
/// сверка примера штрих-кода с кассой, запись IP в настройки нужной марки и открытие окна
/// настроек по марке.</summary>
// 2026-09-28: partial — проверка адреса и связи по марке вынесена в ScaleUi.Connection.cs
// (редизайн Настройки → Весы и окна «Весы»: одна проверка на оба места).
internal static partial class ScaleUi
{
    public const string BrandShtrikh = "shtrikh";
    public const string BrandRongta = "rongta";
    public const string BrandAi = "ai";
    public const string BrandTm = "tm";

    public static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ------------------------------------------------------------------ блоки разметки

    public static Border Card(string title, string? hint, out StackPanel body)
    {
        body = new StackPanel { Spacing = 10 };
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock { Text = title, Classes = { "cardTitle" } });
        if (!string.IsNullOrWhiteSpace(hint))
            root.Children.Add(new TextBlock { Text = hint, Classes = { "hint" } });
        root.Children.Add(body);
        return new Border { Classes = { "card" }, Child = root };
    }

    public static Button MakeButton(string text, bool primary, EventHandler<RoutedEventArgs> onClick)
    {
        var button = new Button { Content = text, Classes = { primary ? "btn-primary" : "btn-ok" } };
        button.Click += onClick;
        return button;
    }

    public static WrapPanel ButtonRow(params Control[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(b);
        }
        return row;
    }

    public static Grid Row(string label, Control editor, int labelWidth = 240)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*") };
        grid.Children.Add(new TextBlock { Text = label, Classes = { "label" }, Margin = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = editor is TextBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        grid.Children.Add(editor);
        return grid;
    }

    public static TextBlock Text(string text, string cssClass = "label") =>
        new() { Text = text, Classes = { cssClass }, TextWrapping = TextWrapping.Wrap };

    public static IBrush ThemeBrush(Control scope, string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, scope.ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    // ------------------------------------------------------------------ сверка штрих-кода с кассой

    /// <summary>Как касса сейчас разбирает весовые штрих-коды (настройка компании).</summary>
    public static string CompanyFormatText()
    {
        var isCode = string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);
        var mode = WeightBarcodeParser.Mode?.ToLowerInvariant() ?? "auto";
        var modeText = mode switch
        {
            "weight" => L("по весу", "салмак боюнча", "by weight", "ağırlığa göre", "vazn bo‘yicha"),
            "amount" => L("по сумме", "сумма боюнча", "by amount", "tutara göre", "summa bo‘yicha"),
            _ => L("авто (префикс 25 — сумма, остальные 20–29 — вес)", "авто (25 префикси — сумма, калган 20–29 — салмак)", "auto (prefix 25 = amount, other 20–29 = weight)", "otomatik (25 öneki = tutar, diğer 20–29 = ağırlık)", "avto (25 prefiksi — summa, qolgan 20–29 — vazn)"),
        };
        return L("Касса ждёт (сайт → Весы → Настройки): раскладка ", "Касса күтөт (сайт → Таразалар → Жөндөөлөр): ", "The till expects (website → Scales → Settings): layout ", "Kasa bekliyor (site → Tartılar → Ayarlar): düzen ", "Kassa kutadi (sayt → Tarozilar → Sozlamalar): tartib ")
               + (isCode
                   ? L("«по коду» — 2 цифры префикса + код 6 + значение 4 + контрольная", "«код боюнча» — 2 префикс + код 6 + маани 4 + текшерүү", "“by code” — 2-digit prefix + code 6 + value 4 + check", "“koda göre” — 2 önek + kod 6 + değer 4 + kontrol", "«kod bo‘yicha» — 2 prefiks + kod 6 + qiymat 4 + nazorat")
                   : L("«по PLU» — 2 цифры префикса + PLU 5 + значение 5 + контрольная", "«PLU боюнча» — 2 префикс + PLU 5 + маани 5 + текшерүү", "“by PLU” — 2-digit prefix + PLU 5 + value 5 + check", "“PLU’ya göre” — 2 önek + PLU 5 + değer 5 + kontrol", "«PLU bo‘yicha» — 2 prefiks + PLU 5 + qiymat 5 + nazorat"))
               + L(", режим ", ", режим ", ", mode ", ", mod ", ", rejim ") + modeText
               + L(". Префикс — 20…29.", ". Префикс — 20…29.", ". Prefix 20…29.", ". Önek 20…29.", ". Prefiks 20…29.");
    }

    /// <summary>Разбирает пример тем же WeightBarcodeParser, что и скан этикетки, и сверяет, что
    /// касса получит ИМЕННО тот код и то значение, которые весы хотели напечатать.</summary>
    public static (bool Ok, string Message) VerifyWithKassa(string? sample, long code, bool isWeight, int grams, decimal amountSom)
    {
        if (string.IsNullOrEmpty(sample) || sample.Length != 13 || !sample.All(char.IsDigit))
        {
            return (false, L("это не EAN-13 из 13 цифр — касса такой штрих-код как весовой не разбирает",
                "бул 13 сандан турган EAN-13 эмес — касса аны салмактуу катары окубайт",
                "this is not a 13-digit EAN-13 — the till does not parse it as a weight barcode",
                "bu 13 haneli EAN-13 değil — kasa bunu tartı barkodu olarak çözmez",
                "bu 13 raqamli EAN-13 emas — kassa uni vaznli shtrix-kod sifatida o‘qimaydi"));
        }
        if (sample[0] != '2')
        {
            return (false, L($"штрих-код начинается с «{sample[..2]}», а касса ждёт префикс 20–29",
                $"штрих-код «{sample[..2]}» менен башталат, касса 20–29 префиксин күтөт",
                $"the barcode starts with “{sample[..2]}”, the till expects prefix 20–29",
                $"barkod “{sample[..2]}” ile başlıyor, kasa 20–29 öneki bekler",
                $"shtrix-kod «{sample[..2]}» bilan boshlanadi, kassa 20–29 prefiksini kutadi"));
        }
        if (!WeightBarcodeParser.TryParse(sample, out var parsed))
        {
            return (false, L("касса не смогла разобрать этот штрих-код (значение 0 или ошибка контрольной цифры)",
                "касса бул штрих-кодду окуй алган жок (маани 0 же текшерүү цифрасы туура эмес)",
                "the till could not parse this barcode (zero value or bad check digit)",
                "kasa bu barkodu çözemedi (değer 0 veya kontrol hanesi hatalı)",
                "kassa bu shtrix-kodni o‘qiy olmadi (qiymat 0 yoki nazorat raqami xato)"));
        }

        var problems = new List<string>();
        var width = parsed.ProductCode.Length;
        var expectedCode = width >= 18 ? code : code % (long)Math.Pow(10, width);
        if (!long.TryParse(parsed.ProductCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gotCode) || gotCode != expectedCode)
        {
            problems.Add(L($"касса прочтёт код товара {parsed.ProductCode} вместо {code} — длина кода на весах не совпадает с раскладкой компании",
                $"касса товар кодун {code} ордуна {parsed.ProductCode} деп окуйт — таразадагы коддун узундугу компаниянын раскладкасына дал келбейт",
                $"the till reads item code {parsed.ProductCode} instead of {code} — the code length on the scale does not match the company layout",
                $"kasa ürün kodunu {code} yerine {parsed.ProductCode} okur — tartıdaki kod uzunluğu şirket düzeniyle uyuşmuyor",
                $"kassa tovar kodini {code} o‘rniga {parsed.ProductCode} deb o‘qiydi — tarozidagi kod uzunligi kompaniya tartibiga mos emas"));
        }

        var gotWeight = parsed.Kind == NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Weight;
        if (gotWeight != isWeight)
        {
            problems.Add(isWeight
                ? L("весы печатают ВЕС, а касса примет число как СУММУ (режим компании/префикс 25)",
                    "тараза САЛМАКТЫ басат, касса санды СУММА катары кабыл алат (компаниянын режими/25 префикси)",
                    "the scale prints WEIGHT but the till takes the number as AMOUNT (company mode / prefix 25)",
                    "tartı AĞIRLIK basar, kasa sayıyı TUTAR sayar (şirket modu / 25 öneki)",
                    "tarozi VAZNNI chop etadi, kassa sonni SUMMA deb qabul qiladi (kompaniya rejimi / 25 prefiks)")
                : L("весы печатают СУММУ, а касса примет число как ВЕС (режим компании/префикс не 25)",
                    "тараза СУММАНЫ басат, касса санды САЛМАК катары кабыл алат (компаниянын режими/префикс 25 эмес)",
                    "the scale prints AMOUNT but the till takes the number as WEIGHT (company mode / prefix not 25)",
                    "tartı TUTAR basar, kasa sayıyı AĞIRLIK sayar (şirket modu / önek 25 değil)",
                    "tarozi SUMMANI chop etadi, kassa sonni VAZN deb qabul qiladi (kompaniya rejimi / prefiks 25 emas)"));
        }
        else if (isWeight && Math.Abs(parsed.Value - grams / 1000.0) > 0.0005)
        {
            problems.Add(L($"касса прочтёт {parsed.Value:0.000} кг вместо {grams / 1000.0:0.000} кг — единица веса на весах другая",
                $"касса {grams / 1000.0:0.000} кг ордуна {parsed.Value:0.000} кг окуйт — таразадагы салмак бирдиги башка",
                $"the till reads {parsed.Value:0.000} kg instead of {grams / 1000.0:0.000} kg — the scale uses another weight unit",
                $"kasa {grams / 1000.0:0.000} kg yerine {parsed.Value:0.000} kg okur — tartının ağırlık birimi farklı",
                $"kassa {grams / 1000.0:0.000} kg o‘rniga {parsed.Value:0.000} kg o‘qiydi — tarozidagi vazn birligi boshqa"));
        }
        else if (!isWeight && Math.Abs(parsed.Value - (double)amountSom) > 0.005)
        {
            problems.Add(L($"касса прочтёт {parsed.Value:0.00} сом вместо {amountSom:0.00} — единица суммы (тыйын/сом) или длина поля не совпадает",
                $"касса {amountSom:0.00} ордуна {parsed.Value:0.00} сом окуйт — сумманын бирдиги (тыйын/сом) же талаанын узундугу дал келбейт",
                $"the till reads {parsed.Value:0.00} som instead of {amountSom:0.00} — amount unit (tiyin/som) or field length differs",
                $"kasa {amountSom:0.00} yerine {parsed.Value:0.00} som okur — tutar birimi (tiyin/som) veya alan uzunluğu farklı",
                $"kassa {amountSom:0.00} o‘rniga {parsed.Value:0.00} so‘m o‘qiydi — summa birligi (tiyin/so‘m) yoki maydon uzunligi mos emas"));
        }

        if (problems.Count > 0)
            return (false, string.Join("; ", problems));

        var product = LocalCartService.FindByEmbeddedCode(parsed.ProductCode);
        var what = product?.Title ?? L("товар с кодом ", "коду бар товар ", "item with code ", "kodlu ürün ", "kodli tovar ") + parsed.ProductCode.TrimStart('0');
        var value = gotWeight
            ? parsed.Value.ToString("0.000", CultureInfo.CurrentCulture) + L(" кг", " кг", " kg", " kg", " kg")
            : parsed.Value.ToString("0.00", CultureInfo.CurrentCulture) + L(" сом", " сом", " som", " som", " so‘m");
        return (true, L("Касса прочитает: ", "Касса окуйт: ", "The till reads: ", "Kasa okur: ", "Kassa o‘qiydi: ") + what + " · " + value
                      + (product is null ? L(" (товара с таким кодом в каталоге кассы нет — это только пример)", " (мындай коддуу товар кассанын каталогунда жок — бул мисал гана)", " (no item with this code in the till catalog — just an example)", " (kasa kataloğunda bu kodla ürün yok — yalnızca örnek)", " (kassa katalogida bunday kodli tovar yo‘q — faqat namuna)") : ""));
    }

    // ------------------------------------------------------------------ IP в настройки марки

    public static string BrandTitle(string brand) => brand switch
    {
        BrandRongta => "Rongta",
        BrandTm => "TM-30F (Dahua)", // 2026-09-28: это весы Dahua (TM-A/TM-F), не JHScale
        BrandAi => L("AI весы", "AI тараза", "AI scales", "AI tartı", "AI tarozi"),
        _ => L("Штрих-М", "Штрих-М", "Shtrih-M", "Shtrih-M", "Shtrix-M"),
    };

    /// <summary>Записывает найденный адрес в настройки выбранной марки и сохраняет файл.</summary>
    public static void ApplyIpToBrand(string brand, string ip)
    {
        var prefs = UserPreferences.Instance;
        switch (brand)
        {
            case BrandRongta:
                prefs.RongtaScaleIp = ip;
                break;
            case BrandTm:
                prefs.TmScaleIp = ip;
                break;
            default:
                // Штрих-М: тот же адрес, что в Настройки → Весы → «Сетевые весы» и ScaleConnectionDialog.
                prefs.ScaleNetworkIp = ip;
                break;
        }
        prefs.SaveToDisk();
    }

    /// <summary>IP весов марки из настроек. Для Rongta при пустом поле — общий ScaleNetworkIp,
    /// если марка выбрана Rongta (раньше адрес Rongta хранился там).</summary>
    public static string IpOf(string brand)
    {
        var prefs = UserPreferences.Instance;
        return brand switch
        {
            BrandRongta => !string.IsNullOrWhiteSpace(prefs.RongtaScaleIp) ? prefs.RongtaScaleIp!
                : prefs.ScaleBrand == BrandRongta ? prefs.ScaleNetworkIp ?? "" : "",
            BrandTm => prefs.TmScaleIp ?? "",
            _ => prefs.ScaleNetworkIp ?? "",
        };
    }

    // ------------------------------------------------------------------ открыть окна

    /// <summary>Окно настроек весов выбранной марки. Для AI-весов отдельного окна нет.</summary>
    public static async Task OpenBrandSettingsAsync(Window owner, string brand)
    {
        Window window = brand switch
        {
            BrandRongta => new RongtaScaleSettingsWindow(),
            BrandTm => new TmScaleSettingsWindow(),
            BrandAi => null!,
            _ => new ShtrikhScaleSettingsWindow(),
        };
        if (window is null)
        {
            await PosAlertDialog.ShowAsync(owner, BrandTitle(brand), L(
                "У AI-весов нет общего протокола: настройки делаются в их собственной программе. Касса готовит для неё файл в окне «Весы».",
                "AI таразаларда жалпы протокол жок: жөндөөлөр алардын өз программасында жасалат. Касса ал үчүн «Таразалар» терезесинде файл даярдайт.",
                "AI scales have no common protocol: they are configured in their own software. The till prepares a file for it in the “Scales” window.",
                "AI tartıların ortak protokolü yok: ayarlar kendi programlarında yapılır. Kasa onun için «Tartı» penceresinde dosya hazırlar.",
                "AI tarozilarning umumiy protokoli yo‘q: sozlamalar ularning o‘z dasturida qilinadi. Kassa u uchun «Tarozi» oynasida fayl tayyorlaydi.")).ConfigureAwait(true);
            return;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    /// <summary>Открывает «Поиск весов в сети». Возвращает true, если какой-то адрес записали.</summary>
    public static async Task<bool> OpenScanAsync(Window owner, string? preferredBrand = null)
    {
        var window = new ScaleNetworkScanWindow(preferredBrand);
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.AnyAddressApplied;
    }

    /// <summary>Открывает окно «Весы» (выгрузка товаров) с нужной маркой. Если окно настроек
    /// само открыто из «Весов» — просто переключает там марку и закрывается.</summary>
    public static void OpenScalesWindow(Window current, string brand)
    {
        var prefs = UserPreferences.Instance;
        prefs.ScaleBrand = brand;
        prefs.SaveToDisk();

        if (current.Owner is ScalesPluWindow scales)
        {
            scales.ReloadBrandFromPreferences();
            current.Close();
            return;
        }

        var owner = current.Owner as Window;
        var window = App.GetRequiredService<ScalesPluWindow>();
        current.Close();
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}
