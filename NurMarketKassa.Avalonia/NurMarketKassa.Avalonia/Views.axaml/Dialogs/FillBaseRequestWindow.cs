using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 («создай то, чего у нас нет»: услуга «Заполним вашу базу»): «Загрузим вашу базу товаров под ключ». Окно объясняет, что делаем (из Excel, из фото накладных,
/// из 1С), собирает название магазина, телефон, что есть и ссылку на файлы и по кнопке открывает WhatsApp поддержки с готовым сообщением (<see cref="FillBaseRequest"/>, wa.me).
/// Автоотправки нет — «Отправить» нажимает владелец. Цену называет поддержка: в программе её нет. Вход: «Перенос данных» (мастер импорта, Настройки → Операции).</summary>
public sealed class FillBaseRequestWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static FillBaseRequestWindow? _open;

    private readonly TextBox _shop = SectionCards.Field(new TextBox { MinHeight = 44, MaxLength = 120 });
    private readonly TextBox _phone = SectionCards.Field(new TextBox { MinHeight = 44, MaxLength = 30 });
    private readonly CheckBox _excel = new() { MinHeight = 40, IsChecked = true };
    private readonly CheckBox _photos = new() { MinHeight = 40 };
    private readonly CheckBox _oneC = new() { MinHeight = 40 };
    private readonly CheckBox _other = new() { MinHeight = 40 };
    private readonly TextBox _link = SectionCards.Field(new TextBox { MinHeight = 44, MaxLength = 400 });
    private readonly TextBox _comment = SectionCards.Field(new TextBox { MinHeight = 64, MaxHeight = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 400 });
    private readonly TextBlock _error;
    private readonly Button _send;

    /// <summary>Только для тестов: сюда уходит готовая ссылка вместо запуска WhatsApp; вернуть false — «не открылось».</summary>
    public static Func<string, bool>? OpenUrlOverride { get; set; }

    public static void Open(Window? owner)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new FillBaseRequestWindow();
        _open = window;
        window.Closed += (_, _) => _open = null;
        if (owner is null)
            window.Show();
        else
            _ = window.ShowDialog(owner);
    }

    public FillBaseRequestWindow()
    {
        Title = T("Загрузим вашу базу товаров под ключ", "Товарлар базаңызды ачкычка даяр кылып жүктөйбүз", "We will load your product database for you",
            "Ürün veritabanınızı sizin için yükleyelim", "Mahsulotlar bazangizni kalitga tayyor qilib yuklaymiz");
        SectionCards.AsModal(this, 620);
        MinWidth = 480;
        Avalonia.Automation.AutomationProperties.SetAutomationId(this, "FillBaseRequestWindow");

        var what = Soft(T("Не хотите вводить товары сами? Наши специалисты перенесут вашу базу в NurCRM: из файла Excel или CSV, из фотографий накладных или из программы 1С. Вы проверяете результат в кассе — и работаете.",
            "Товарларды өзүңүз киргизгиңиз келбейби? Адистерибиз базаңызды NurCRM'ге которот: Excel же CSV файлынан, накладныйлардын сүрөттөрүнөн же 1С программасынан. Натыйжаны кассадан текшерип — иштейсиз.",
            "Do not want to enter the products yourself? Our specialists will move your database into NurCRM: from an Excel or CSV file, from photos of invoices or from 1C. You check the result in the till and start working.",
            "Ürünleri kendiniz girmek istemiyor musunuz? Uzmanlarımız veritabanınızı NurCRM'e taşır: Excel veya CSV dosyasından, fatura fotoğraflarından ya da 1C'den. Sonucu kasada kontrol edip çalışmaya başlarsınız.",
            "Mahsulotlarni o'zingiz kiritishni xohlamaysizmi? Mutaxassislarimiz bazangizni NurCRM ga ko'chiradi: Excel yoki CSV fayldan, hujjat suratlaridan yoki 1C dasturidan. Natijani kassada tekshirib — ishlaysiz."));
        var price = Soft(T("Стоимость назовёт поддержка в WhatsApp после того, как увидит объём. Сообщение подготовлено — вы сами нажимаете «Отправить».",
            "Баасын колдоо кызматы көлөмдү көргөндөн кийин WhatsApp'та айтат. Билдирүү даяр — «Жөнөтүү» баскычын өзүңүз басасыз.",
            "Support will name the price in WhatsApp once they see the volume. The message is ready — you press Send yourself.",
            "Fiyatı destek, hacmi gördükten sonra WhatsApp'ta söyler. Mesaj hazır — Gönder'e siz basarsınız.",
            "Narxni yordam hajmni ko'rgach WhatsApp'da aytadi. Xabar tayyor — «Yuborish»ni o'zingiz bosasiz."));

        _shop.Text = ReportShopName.Current;
        Avalonia.Automation.AutomationProperties.SetAutomationId(_shop, "FillBaseShop");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_phone, "FillBasePhone");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_link, "FillBaseLink");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_comment, "FillBaseComment");
        _phone.Watermark = "+996 700 123 456";
        _link.Watermark = "https://drive.google.com/…";

        Tick(_excel, "FillBaseExcel", FillBaseRequest.Sources.Excel);
        Tick(_photos, "FillBasePhotos", FillBaseRequest.Sources.InvoicePhotos);
        Tick(_oneC, "FillBaseOneC", FillBaseRequest.Sources.OneC);
        Tick(_other, "FillBaseOther", FillBaseRequest.Sources.Other);
        var sources = new UniformGrid { Columns = 2, Children = { _excel, _photos, _oneC, _other } };

        _error = SectionCards.ErrorText(this);
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                what, price,
                Caption(T("Название магазина", "Дүкөндүн аталышы", "Store name", "Mağaza adı", "Do'kon nomi")), _shop,
                Caption(T("Телефон для связи", "Байланыш телефону", "Contact phone", "İletişim telefonu", "Aloqa telefoni")), _phone,
                Caption(T("Что у вас есть", "Эмнеңиз бар", "What you have", "Elinizde ne var", "Sizda nima bor")), sources,
                Caption(T("Ссылка на файлы (необязательно)", "Файлдарга шилтеме (милдеттүү эмес)", "Link to the files (optional)", "Dosya bağlantısı (isteğe bağlı)", "Fayllarga havola (majburiy emas)")), _link,
                Caption(T("Комментарий (необязательно)", "Комментарий (милдеттүү эмес)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (majburiy emas)")), _comment,
                _error,
            },
        };
        var scroll = new ScrollViewer { Content = body, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        _send = SectionCards.Primary(T("Открыть WhatsApp с сообщением", "Билдирүү менен WhatsApp ачуу", "Open WhatsApp with the message", "Mesajla WhatsApp'ı aç", "Xabar bilan WhatsApp'ni ochish"));
        _send.MinHeight = 44;
        Avalonia.Automation.AutomationProperties.SetAutomationId(_send, "FillBaseSendButton");
        _send.Click += (_, _) => Send();

        Content = SectionCards.Shell(Title ?? "", MarketSphereLock.SupportPhone, scroll, SectionCards.Buttons(close, _send));
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, -4), TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static void Tick(CheckBox box, string id, FillBaseRequest.Sources source)
    {
        box.Content = new TextBlock { Text = FillBaseRequest.SourceTitle(source), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetAutomationId(box, id);
    }

    public FillBaseRequest.Form CurrentForm()
    {
        var sources = FillBaseRequest.Sources.None;
        if (_excel.IsChecked == true) sources |= FillBaseRequest.Sources.Excel;
        if (_photos.IsChecked == true) sources |= FillBaseRequest.Sources.InvoicePhotos;
        if (_oneC.IsChecked == true) sources |= FillBaseRequest.Sources.OneC;
        if (_other.IsChecked == true) sources |= FillBaseRequest.Sources.Other;
        return new FillBaseRequest.Form(_shop.Text ?? "", _phone.Text ?? "", sources, _link.Text ?? "", _comment.Text ?? "");
    }

    private void Send()
    {
        var form = CurrentForm();
        var problem = FillBaseRequest.Validate(form);
        SectionCards.ShowError(_error, problem);
        if (problem is not null)
            return;
        var url = FillBaseRequest.BuildUrl(form);
        var opened = OpenUrlOverride is { } probe ? probe(url) : PlatformLauncher.OpenUrl(url);
        if (!opened)
        {
            PosLogger.Log("Заполним вашу базу: WhatsApp поддержки не открылся.", "WARNING");
            SectionCards.ShowError(_error, T("Не удалось открыть WhatsApp. Напишите в поддержку по номеру " + MarketSphereLock.SupportPhone + ".",
                "WhatsApp'ты ачуу мүмкүн болгон жок. Колдоого " + MarketSphereLock.SupportPhone + " номери боюнча жазыңыз.",
                "Could not open WhatsApp. Message support at " + MarketSphereLock.SupportPhone + ".", "WhatsApp açılamadı. Desteğe " + MarketSphereLock.SupportPhone + " numarasından yazın.",
                "WhatsApp'ni ochib bo'lmadi. Yordamga " + MarketSphereLock.SupportPhone + " raqami orqali yozing."));
            return;
        }
        PosLogger.Log("Заполним вашу базу: открыт чат WhatsApp поддержки с готовым сообщением (отправляет владелец).", "INFO");
        Close();
    }
}
