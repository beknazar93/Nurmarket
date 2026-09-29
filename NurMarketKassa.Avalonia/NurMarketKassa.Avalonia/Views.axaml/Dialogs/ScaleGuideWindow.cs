using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.Services;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-30, просьба владельца: «в каждой модели весов добавь кнопку „Инструкция подключения“ и
/// подробный план подключения с гифками». Пошаговый план для марки (Штрих-ПРИНТ, Rongta, TM-30F, AI)
/// с анимациями Assets/kb/guide-*.gif (на языке программы: guide-net.ky.gif …; рисуются схематично
/// скриптом scale_guide_gifs.py — без снимков чужих данных). GIF проигрывается кадрами, как в базе знаний.
/// Шаги — только то, что проверено или прямо описано в руководствах и окнах кассы.
/// </summary>
public sealed class ScaleGuideWindow : Window
{
    private const string AssetRoot = "avares://NurMarketKassa.Avalonia/Assets/kb/";

    private sealed record Step(string Title, string Text, string? Image = null);

    public ScaleGuideWindow(string brand)
    {
        brand = NormalizeBrand(brand);
        Title = L("Инструкция подключения — ", "Туташтыруу нускамасы — ", "Connection guide — ", "Bağlantı kılavuzu — ", "Ulanish yo‘riqnomasi — ") + LabelBrandTitle(brand);
        Width = 980;
        Height = 820;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Opened += (_, _) => this.FitToKassaScreen();

        var list = new StackPanel { Spacing = 14, Margin = new Thickness(24, 20, 24, 20) };
        list.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush(this, "BrushText", Brushes.Black) });
        var number = 1;
        foreach (var step in StepsFor(brand))
            list.Children.Add(StepCard(number++, step));

        var close = new Button
        {
            Content = L("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
            Classes = { "btn-primary" },
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        close.Click += (_, _) => Close();
        list.Children.Add(close);
        Content = new ScrollViewer { Content = list };
        Background = ThemeBrush(this, "BrushBackground", Brushes.WhiteSmoke);
    }

    private Control StepCard(int number, Step step)
    {
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = ThemeBrush(this, "BrushAccent", Brushes.Gold),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(), FontWeight = FontWeight.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black },
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = step.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush(this, "BrushText", Brushes.Black) });
        body.Children.Add(new TextBlock { Text = step.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Foreground = ThemeBrush(this, "BrushText", Brushes.Black) });
        if (step.Image is not null && Picture(step.Image) is { } picture)
            body.Children.Add(picture);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*") };
        grid.Children.Add(badge);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return new Border
        {
            Background = ThemeBrush(this, "BrushSurface", Brushes.White),
            BorderBrush = ThemeBrush(this, "BrushBorder", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            Child = grid,
        };
    }

    /// <summary>GIF на языке программы (guide-net.ky.gif), иначе русский; кадры сменяются таймером.</summary>
    private static Control? Picture(string file)
    {
        var language = L("ru", "ky", "en", "tr", "uz");
        var localized = language == "ru" ? file : System.IO.Path.GetFileNameWithoutExtension(file) + "." + language + System.IO.Path.GetExtension(file);
        try
        {
            var name = AssetLoader.Exists(new Uri(AssetRoot + localized)) ? localized : file;
            using var stream = AssetLoader.Open(new Uri(AssetRoot + name));
            var frames = KnowledgeBaseWindow.ReadGifFrames(stream);
            if (frames.Count == 0)
                return null;
            var image = new Image { Source = frames[0].Frame, Stretch = Stretch.Uniform, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
            if (frames.Count > 1)
            {
                var index = 0;
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = frames[0].Delay };
                timer.Tick += (_, _) =>
                {
                    index = (index + 1) % frames.Count;
                    image.Source = frames[index].Frame;
                    timer.Interval = frames[index].Delay;
                };
                image.AttachedToVisualTree += (_, _) => timer.Start();
                image.DetachedFromVisualTree += (_, _) => timer.Stop();
            }
            return new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Left, Child = image };
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Инструкция весов: нет картинки {file}: {ex.Message}", "WARNING");
            return null;
        }
    }

    // ================================================================== шаги по маркам

    private static Step Network() => new(
        L("Весы и компьютер кассы — в одной сети", "Тараза менен кассанын компьютери — бир тармакта", "The scale and the till computer are on the same network", "Tartı ve kasa bilgisayarı aynı ağda", "Tarozi va kassa kompyuteri bitta tarmoqda"),
        L("Подключите весы кабелем к тому же роутеру, что и компьютер кассы, или к той же сети Wi-Fi. Первые три числа IP-адреса весов и компьютера должны совпадать: компьютер 192.168.1.198 — весы 192.168.1.87. Если у весов 192.168.0.x, а у компьютера 192.168.1.x, связи не будет — поменяйте адрес на весах (следующий шаг). Адрес компьютера видно в «Найти весы в сети…».",
          "Таразаны кассанын компьютери турган роутерге кабель менен же ошол эле Wi-Fi тармагына туташтырыңыз. Таразанын жана компьютердин IP-дарегинин алгачкы үч саны бирдей болушу керек: компьютер 192.168.1.198 — тараза 192.168.1.87. Таразада 192.168.0.x, компьютерде 192.168.1.x болсо, байланыш болбойт — таразадагы даректи өзгөртүңүз (кийинки кадам). Компьютердин дареги «Тармактан тараза табуу…» терезесинде көрүнөт.",
          "Connect the scale by cable to the same router as the till computer, or to the same Wi-Fi. The first three numbers of the scale and computer IP must match: computer 192.168.1.198 — scale 192.168.1.87. If the scale has 192.168.0.x and the computer 192.168.1.x there is no link — change the address on the scale (next step). The computer address is shown in “Find scales on the network…”.",
          "Tartıyı kasa bilgisayarıyla aynı modeme kabloyla ya da aynı Wi-Fi ağına bağlayın. Tartının ve bilgisayarın IP adresinin ilk üç sayısı aynı olmalı: bilgisayar 192.168.1.198 — tartı 192.168.1.87. Tartıda 192.168.0.x, bilgisayarda 192.168.1.x ise bağlantı olmaz — tartıdaki adresi değiştirin (sonraki adım). Bilgisayarın adresi «Ağda tartı bul…» penceresinde görünür.",
          "Tarozini kassa kompyuteri ulangan routerga kabel bilan yoki o‘sha Wi-Fi tarmog‘iga ulang. Tarozi va kompyuter IP manzilining dastlabki uch soni bir xil bo‘lishi kerak: kompyuter 192.168.1.198 — tarozi 192.168.1.87. Tarozida 192.168.0.x, kompyuterda 192.168.1.x bo‘lsa, aloqa bo‘lmaydi — tarozidagi manzilni o‘zgartiring (keyingi qadam). Kompyuter manzili «Tarmoqda tarozi topish…» oynasida ko‘rinadi."),
        "guide-net.gif");

    private static Step Label(string brandText) => new(
        L("Проверьте этикетку сканером кассы", "Этикетканы кассанын сканери менен текшериңиз", "Check a label with the till scanner", "Etiketi kasa tarayıcısıyla kontrol edin", "Yorliqni kassa skaneri bilan tekshiring"),
        L($"Вызовите товар на весах, положите груз, напечатайте этикетку и отсканируйте её в кассе. Штрих-код: префикс 20–29, код товара (колонка «Код в ШК»), вес или сумма. Касса должна найти этот же товар и сумму как на этикетке. Не та сумма — Настройки → Весы → «Настроить по этикетке». {brandText}",
          $"Таразада товарды чакырып, жүк коюп, этикетканы басып чыгарыңыз жана кассада сканерлеңиз. Штрих-код: 20–29 префикси, товардын коду («ШКдагы код» тилкеси), салмак же сумма. Касса ушул эле товарды жана этикеткадагыдай сумманы табышы керек. Сумма туура эмес болсо — Жөндөөлөр → Таразалар → «Этикетка боюнча жөндөө». {brandText}",
          $"Call the product on the scale, put a load, print a label and scan it at the till. Barcode: prefix 20–29, item code (the “Barcode code” column), weight or amount. The till must find the same product and the amount printed on the label. Wrong amount — Settings → Scales → “Set up from a label”. {brandText}",
          $"Tartıda ürünü çağırın, yük koyun, etiketi yazdırıp kasada okutun. Barkod: 20–29 öneki, ürün kodu («Barkod kodu» sütunu), ağırlık veya tutar. Kasa aynı ürünü ve etiketteki tutarı bulmalı. Tutar yanlışsa — Ayarlar → Tartılar → «Etiketten ayarla». {brandText}",
          $"Tarozida tovarni chaqiring, yuk qo‘ying, yorliqni chop eting va kassada skanerlang. Shtrix-kod: 20–29 prefiksi, tovar kodi («ShKdagi kod» ustuni), vazn yoki summa. Kassa o‘sha tovarni va yorliqdagi summani topishi kerak. Summa noto‘g‘ri bo‘lsa — Sozlamalar → Tarozilar → «Yorliq bo‘yicha sozlash». {brandText}"),
        "guide-label.gif");

    private static Step SendGoods(string keysText) => new(
        L("Отправьте товары на весы", "Товарларды таразага жөнөтүңүз", "Send the goods to the scale", "Ürünleri tartıya gönderin", "Tovarlarni taroziga yuboring"),
        L($"«Отправить товары →» — окно «Весы». Там только весовые товары с сайта; поиск и фильтр «Показать» — сверху. Отметьте товары и нажмите «Отправить на весы». PLU берётся с сайта; у товара без PLU касса назначит его на сайте сама. PLU можно поменять прямо в колонке — изменится и на сайте. {keysText} «Лист кнопок» — печать на A4 или Word: какая кнопка какой товар вызывает.",
          $"«Товарларды жөнөтүү →» — «Таразалар» терезеси. Анда сайттагы салмактуу товарлар гана; издөө жана «Көрсөтүү» чыпкасы — жогоруда. Товарларды белгилеп «Таразага жөнөтүү» басыңыз. PLU сайттан алынат; PLU'су жок товарга касса аны сайтта өзү берет. PLU'ну тилкеде эле өзгөртсө болот — сайтта да өзгөрөт. {keysText} «Баскычтар барагы» — A4 же Word: кайсы баскыч кайсы товарды чакырат.",
          $"“Send goods →” opens the “Scales” window. It lists only weighed goods from the website; search and the “Show” filter are at the top. Tick the goods and press “Send to scale”. The PLU comes from the website; a product without one gets it assigned on the website by the till. You can change the PLU right in the column — it changes on the website too. {keysText} “Key sheet” — A4 or Word: which key calls which product.",
          $"«Ürünleri gönder →» — «Tartı» penceresi. Orada yalnızca sitedeki tartılı ürünler var; arama ve «Göster» filtresi üstte. Ürünleri işaretleyip «Tartıya gönder»e basın. PLU siteden alınır; PLU'su olmayan ürüne kasa onu sitede kendisi verir. PLU'yu sütunda değiştirebilirsiniz — sitede de değişir. {keysText} «Tuş listesi» — A4 veya Word: hangi tuş hangi ürünü çağırır.",
          $"«Tovarlarni yuborish →» — «Tarozi» oynasi. Unda faqat saytdagi vaznli tovarlar; qidiruv va «Ko‘rsatish» filtri — yuqorida. Tovarlarni belgilang va «Taroziga yuborish»ni bosing. PLU saytdan olinadi; PLU'si yo‘q tovarga kassa uni saytda o‘zi beradi. PLU'ni ustunda o‘zgartirish mumkin — saytda ham o‘zgaradi. {keysText} «Tugmalar varag‘i» — A4 yoki Word: qaysi tugma qaysi tovarni chaqiradi."),
        "guide-kassa-send.gif");

    private static IEnumerable<Step> StepsFor(string brand)
    {
        switch (brand)
        {
            case BrandRongta:
                yield return Network();
                yield return new Step(
                    L("Задайте IP-адрес на весах Rongta", "Rongta таразасында IP-даректи коюңуз", "Set the IP address on the Rongta scale", "Rongta tartıda IP adresini ayarlayın", "Rongta tarozisida IP manzilni belgilang"),
                    L("Держите [SET] 3 секунды — откроются системные настройки. Кнопками выберите «@ IP address», введите адрес из сети кассы (например 192.168.1.87) и нажмите PRN/ENTER. Если спросят маску — 255.255.255.0, шлюз — адрес роутера. Выключите и включите весы. Заводской адрес Rongta — 192.168.1.87.",
                      "[SET] баскычын 3 секунд басып туруңуз — системалык жөндөөлөр ачылат. «@ IP address» тандап, кассанын тармагындагы даректи киргизиңиз (мисалы 192.168.1.87) жана PRN/ENTER басыңыз. Маска сураса — 255.255.255.0, шлюз — роутердин дареги. Таразаны өчүрүп күйгүзүңүз. Rongta'нын заводдук дареги — 192.168.1.87.",
                      "Hold [SET] for 3 seconds — system settings open. Choose “@ IP address”, enter an address from the till's network (e.g. 192.168.1.87) and press PRN/ENTER. If asked: mask 255.255.255.0, gateway — the router address. Switch the scale off and on. The Rongta factory address is 192.168.1.87.",
                      "[SET] tuşunu 3 saniye basılı tutun — sistem ayarları açılır. «@ IP address» seçin, kasanın ağından bir adres girin (ör. 192.168.1.87) ve PRN/ENTER'e basın. Sorarsa maske 255.255.255.0, ağ geçidi modemin adresi. Tartıyı kapatıp açın. Rongta fabrika adresi 192.168.1.87'dir.",
                      "[SET] ni 3 soniya bosib turing — tizim sozlamalari ochiladi. «@ IP address» ni tanlang, kassa tarmog‘idagi manzilni kiriting (masalan 192.168.1.87) va PRN/ENTER bosing. So‘rasa: niqob 255.255.255.0, shlyuz — router manzili. Tarozini o‘chirib yoqing. Rongta zavod manzili — 192.168.1.87."),
                    "guide-rongta-ip.gif");
                yield return new Step(
                    L("По Wi-Fi — если нет кабеля", "Wi-Fi аркылуу — кабель жок болсо", "Over Wi-Fi — if there is no cable", "Wi-Fi ile — kablo yoksa", "Wi-Fi orqali — kabel bo‘lmasa"),
                    L("В тех же настройках — «Set WiFi» («Настройка WIFI»): имя сети (SSID) — та же сеть, что у компьютера кассы, пароль Wi-Fi, режим WPA2. Сохраните и перезапустите весы. Весы по Wi-Fi иногда «засыпают» — касса повторяет подключение 3 раза.",
                      "Ошол эле жөндөөлөрдө — «Set WiFi» («Настройка WIFI»): тармактын аты (SSID) — кассанын компьютериндей тармак, Wi-Fi сырсөзү, WPA2 режими. Сактап, таразаны өчүрүп күйгүзүңүз. Wi-Fi'даги тараза кээде «уктайт» — касса туташууну 3 жолу кайталайт.",
                      "In the same settings — “Set WiFi”: network name (SSID) — the same network as the till computer, the Wi-Fi password, WPA2 mode. Save and restart the scale. A Wi-Fi scale sometimes “sleeps” — the till retries the connection 3 times.",
                      "Aynı ayarlarda — «Set WiFi»: ağ adı (SSID) — kasa bilgisayarıyla aynı ağ, Wi-Fi şifresi, WPA2 modu. Kaydedip tartıyı yeniden başlatın. Wi-Fi'daki tartı bazen «uyur» — kasa bağlanmayı 3 kez dener.",
                      "O‘sha sozlamalarda — «Set WiFi»: tarmoq nomi (SSID) — kassa kompyuteridagi tarmoq, Wi-Fi paroli, WPA2 rejimi. Saqlang va tarozini qayta yoqing. Wi-Fi'dagi tarozi ba’zan «uxlaydi» — kassa ulanishni 3 marta takrorlaydi."),
                    "guide-rongta-wifi.gif");
                yield return new Step(
                    L("Добавьте весы в кассе", "Таразаны кассага кошуңуз", "Add the scale in the till", "Tartıyı kasaya ekleyin", "Tarozini kassaga qo‘shing"),
                    L("Настройки → Весы → «+ Добавить весы» → Rongta. Впишите IP весов или нажмите «Найти весы в сети…». Способ отправки — «Напрямую по сети (без RLS1000)»: касса сама пишет товары в весы по порту 4001, программа RLS1000 не нужна. Нажмите «Проверить связь» — должно быть «весы на связи».",
                      "Жөндөөлөр → Таразалар → «+ Тараза кошуу» → Rongta. Таразанын IP'син жазыңыз же «Тармактан тараза табуу…» басыңыз. Жөнөтүү жолу — «Тармак аркылуу түз (RLS1000'сиз)»: касса товарларды таразага 4001 порт аркылуу өзү жазат, RLS1000 программасы керек эмес. «Байланышты текшерүү» басыңыз — «тараза байланышта» чыгышы керек.",
                      "Settings → Scales → “+ Add scale” → Rongta. Enter the scale IP or press “Find scales on the network…”. Upload method — “Directly over the network (no RLS1000)”: the till writes goods to the scale itself over port 4001, RLS1000 is not needed. Press “Check connection” — it must say “scale connected”.",
                      "Ayarlar → Tartılar → «+ Tartı ekle» → Rongta. Tartı IP'sini yazın veya «Ağda tartı bul…»a basın. Gönderim yolu — «Doğrudan ağ üzerinden (RLS1000'siz)»: kasa ürünleri tartıya 4001 portundan kendisi yazar, RLS1000 gerekmez. «Bağlantıyı kontrol et»e basın — «tartı bağlı» görünmeli.",
                      "Sozlamalar → Tarozilar → «+ Tarozi qo‘shish» → Rongta. Tarozi IP'sini yozing yoki «Tarmoqda tarozi topish…»ni bosing. Yuborish usuli — «To‘g‘ridan-to‘g‘ri tarmoq orqali (RLS1000'siz)»: kassa tovarlarni taroziga 4001 port orqali o‘zi yozadi, RLS1000 kerak emas. «Aloqani tekshirish»ni bosing — «tarozi aloqada» chiqishi kerak."),
                    "guide-kassa-setup-rongta.gif");
                yield return SendGoods(L("Кнопки: «Кнопки весов…» — сетка кнопок; нажмите кнопку и выберите товар (или впишите номер в колонку «Клавиша»). На весах Rongta кнопка вызывает ячейку PLU с тем же номером, поэтому касса ставит товару PLU = номер кнопки (и на сайте), а товар, который был на этой кнопке, переносит на свободный номер. Затем — «Отправить на весы».",
                    "Баскычтар: «Тараза баскычтары…» — баскычтардын торчосу; баскычты басып товарды тандаңыз (же «Баскыч» тилкесине номерди жазыңыз). Rongta таразасында баскыч ошол эле номердеги PLU уячасын чакырат, ошондуктан касса товарга PLU = баскычтын номери коёт (сайтта да), ал баскычтагы товарды бош номерге көчүрөт. Андан кийин — «Таразага жөнөтүү».",
                    "Keys: “Scale keys…” — a grid of keys; press a key and choose a product (or type the number in the “Key” column). On a Rongta scale a key calls the PLU slot with the same number, so the till gives the product PLU = key number (on the website too) and moves the product that was on that key to a free number. Then — “Send to scale”.",
                    "Tuşlar: «Tartı tuşları…» — tuş ızgarası; bir tuşa basıp ürün seçin (veya numarayı «Tuş» sütununa yazın). Rongta tartıda tuş aynı numaralı PLU hücresini çağırır; kasa ürüne PLU = tuş numarası verir (sitede de), o tuştaki ürünü boş bir numaraya taşır. Sonra — «Tartıya gönder».",
                    "Tugmalar: «Tarozi tugmalari…» — tugmalar to‘ri; tugmani bosing va tovarni tanlang (yoki raqamni «Tugma» ustuniga yozing). Rongta tarozisida tugma shu raqamli PLU katagini chaqiradi, shuning uchun kassa tovarga PLU = tugma raqami beradi (saytda ham), o‘sha tugmadagi tovarni bo‘sh raqamga ko‘chiradi. So‘ng — «Taroziga yuborish»."));
                yield return Label(L("У Rongta тип штрих-кода 02 (отдел + код + сумма), отдел = префикс из «Настройки весов Rongta» → «Штрих-код» (обычно 20). Буквы, которых весы не печатают, касса заменяет: «я» в конце — «Я».",
                    "Rongta'да штрих-коддун түрү 02 (бөлүм + код + сумма), бөлүм = «Rongta таразасынын жөндөөлөрү» → «Штрих-код» ичиндеги префикс (адатта 20). Тараза баса албаган тамгаларды касса алмаштырат: аягындагы «я» — «Я».",
                    "Rongta uses barcode type 02 (department + code + amount); department = prefix from “Rongta scale settings” → “Barcode” (usually 20). Letters the scale cannot print are replaced: a final «я» becomes «Я».",
                    "Rongta'da barkod türü 02 (reyon + kod + tutar), reyon = «Rongta tartı ayarları» → «Barkod» içindeki önek (genellikle 20). Tartının basamadığı harfler değiştirilir: sondaki «я» → «Я».",
                    "Rongta'da shtrix-kod turi 02 (bo‘lim + kod + summa), bo‘lim = «Rongta tarozi sozlamalari» → «Shtrix-kod» dagi prefiks (odatda 20). Tarozi chop eta olmaydigan harflar almashtiriladi: oxiridagi «я» — «Я»."));
                yield return new Step(
                    L("Если не работает", "Иштебесе", "If it does not work", "Çalışmazsa", "Ishlamasa"),
                    L("• «ping: нет ответа» — весы выключены, «уснули» по Wi-Fi, стоят в меню настроек или в другой сети (шаг 1).\n• «Не отправлено — код в ШК … откроет …» — у товара код, который уже есть у другого товара: оставьте поле пустым, касса подберёт свободный код.\n• На кнопке старый товар — у нужного товара другой PLU: поставьте PLU = номер кнопки и отправьте снова.",
                      "• «ping: жооп жок» — тараза өчүк, Wi-Fi'да «уктап» калган, жөндөө менюсунда же башка тармакта (1-кадам).\n• «Жөнөтүлгөн жок — ШКдагы код … ачат» — товардын коду башка товарда бар: талааны бош калтырыңыз, касса бош код тандайт.\n• Баскычта эски товар — керектүү товардын PLU'су башка: PLU = баскычтын номери коюп, кайра жөнөтүңүз.",
                      "• “ping: no reply” — the scale is off, “asleep” on Wi-Fi, in its settings menu or on another network (step 1).\n• “Not sent — barcode code … opens …” — another product already has this code: leave the field empty, the till picks a free code.\n• An old product on a key — the right product has another PLU: set PLU = key number and send again.",
                      "• «ping: yanıt yok» — tartı kapalı, Wi-Fi'da «uykuda», ayar menüsünde ya da başka ağda (1. adım).\n• «Gönderilmedi — barkod kodu … açar» — bu kod başka üründe var: alanı boş bırakın, kasa boş kod seçer.\n• Tuşta eski ürün — doğru ürünün PLU'su farklı: PLU = tuş numarası yapıp tekrar gönderin.",
                      "• «ping: javob yo‘q» — tarozi o‘chiq, Wi-Fi'da «uxlagan», sozlamalar menyusida yoki boshqa tarmoqda (1-qadam).\n• «Yuborilmadi — ShKdagi kod … ochadi» — bu kod boshqa tovarda bor: maydonni bo‘sh qoldiring, kassa bo‘sh kod tanlaydi.\n• Tugmada eski tovar — kerakli tovarning PLU'si boshqa: PLU = tugma raqami qo‘ying va qayta yuboring."));
                break;

            case BrandTm:
                yield return Network();
                yield return new Step(
                    L("IP-адрес весов TM-30F", "TM-30F таразасынын IP-дареги", "TM-30F scale IP address", "TM-30F tartı IP adresi", "TM-30F tarozi IP manzili"),
                    L("Адрес весов задаётся их программой «Русский масштаб» (или утилитой сетевого модуля весов); порт — 4001. Адрес должен быть из сети кассы (шаг 1). Перед отправкой из кассы закройте «Русский масштаб»: он занимает весы, и касса не подключится.",
                      "Таразанын дареги алардын «Русский масштаб» программасы (же сетевой модулдун утилитасы) менен коюлат; порт — 4001. Дарек кассанын тармагынан болушу керек (1-кадам). Кассадан жөнөтүүдөн мурун «Русский масштаб»ты жабыңыз: ал таразаны ээлеп турат, касса туташпайт.",
                      "The scale address is set with their software “Russian Scale” (or the network module utility); the port is 4001. The address must be in the till's network (step 1). Close “Russian Scale” before sending from the till: it occupies the scale and the till cannot connect.",
                      "Tartı adresi kendi programları «Русский масштаб» ile (veya ağ modülü aracıyla) ayarlanır; port 4001. Adres kasanın ağından olmalı (1. adım). Kasadan göndermeden önce «Русский масштаб»ı kapatın: tartıyı meşgul eder ve kasa bağlanamaz.",
                      "Tarozi manzili ularning «Русский масштаб» dasturi (yoki tarmoq moduli utilitasi) bilan belgilanadi; port — 4001. Manzil kassa tarmog‘idan bo‘lishi kerak (1-qadam). Kassadan yuborishdan oldin «Русский масштаб»ni yoping: u tarozini band qiladi va kassa ulana olmaydi."));
                yield return new Step(
                    L("Добавьте весы в кассе", "Таразаны кассага кошуңуз", "Add the scale in the till", "Tartıyı kasaya ekleyin", "Tarozini kassaga qo‘shing"),
                    L("Настройки → Весы → «+ Добавить весы» → TM-30F. IP весов, порт 4001 → «Проверить связь»: касса читает ячейку PLU №1, весы должны ответить. Цена уходит в тыйынах; если на весах формат штрих-кода с суммой, касса сама читает этикетки этого префикса как сумму.",
                      "Жөндөөлөр → Таразалар → «+ Тараза кошуу» → TM-30F. Таразанын IP'си, 4001 порт → «Байланышты текшерүү»: касса PLU №1 уячасын окуйт, тараза жооп бериши керек. Баа тыйын менен кетет; таразада суммалуу штрих-код форматы болсо, касса бул префикстин этикеткаларын сумма катары өзү окуйт.",
                      "Settings → Scales → “+ Add scale” → TM-30F. Scale IP, port 4001 → “Check connection”: the till reads PLU slot No. 1, the scale must answer. Prices go in tiyin; if the scale barcode format carries the amount, the till reads that prefix as amount itself.",
                      "Ayarlar → Tartılar → «+ Tartı ekle» → TM-30F. Tartı IP'si, port 4001 → «Bağlantıyı kontrol et»: kasa PLU No. 1 hücresini okur, tartı yanıt vermeli. Fiyat tiyin olarak gider; tartıda tutarlı barkod biçimi varsa kasa bu öneki tutar olarak kendisi okur.",
                      "Sozlamalar → Tarozilar → «+ Tarozi qo‘shish» → TM-30F. Tarozi IP'si, 4001 port → «Aloqani tekshirish»: kassa PLU №1 katagini o‘qiydi, tarozi javob berishi kerak. Narx tiyinda ketadi; tarozida summali shtrix-kod formati bo‘lsa, kassa bu prefiksni summa sifatida o‘zi o‘qiydi."),
                    "guide-kassa-setup-tm.gif");
                yield return SendGoods(L("Кнопка на весах = PLU; «Кнопки весов…» или колонка «Клавиша» — назначить товару другую кнопку; касса запишет раскладку кнопок после товаров.",
                    "Таразадагы баскыч = PLU; «Баскыч» тилкесинде товарга башка баскыч берсе болот — касса баскычтардын жайгашуусун товарлардан кийин жазат.",
                    "Scale key = PLU; in the “Key” column you can give a product another key — the till writes the key layout after the goods.",
                    "Tartı tuşu = PLU; «Tuş» sütununda ürüne başka tuş verebilirsiniz — kasa tuş düzenini ürünlerden sonra yazar.",
                    "Tarozi tugmasi = PLU; «Tugma» ustunida tovarga boshqa tugma berish mumkin — kassa tugmalar tartibini tovarlardan keyin yozadi."));
                yield return Label("");
                break;

            case BrandAi:
                yield return new Step(
                    L("Файл для программы весов", "Тараза программасы үчүн файл", "A file for the scale's software", "Tartı programı için dosya", "Tarozi dasturi uchun fayl"),
                    L("У AI-весов нет общего сетевого протокола — касса готовит файл. Настройки → Весы → AI весы → «Подготовить файл →»: CSV (PLU; название; единица; цена). Загрузите его программой ваших весов по её инструкции.",
                      "AI таразада жалпы тармак протоколу жок — касса файл даярдайт. Жөндөөлөр → Таразалар → AI тараза → «Файл даярдоо →»: CSV (PLU; аталышы; бирдиги; баасы). Аны таразаңыздын программасы менен анын нускамасы боюнча жүктөңүз.",
                      "AI scales have no common network protocol — the till prepares a file. Settings → Scales → AI scales → “Prepare the file →”: CSV (PLU; name; unit; price). Load it with your scale's software following its manual.",
                      "AI tartıların ortak ağ protokolü yok — kasa bir dosya hazırlar. Ayarlar → Tartılar → AI tartı → «Dosyayı hazırla →»: CSV (PLU; ad; birim; fiyat). Onu tartınızın programıyla, kılavuzuna göre yükleyin.",
                      "AI tarozilarda umumiy tarmoq protokoli yo‘q — kassa fayl tayyorlaydi. Sozlamalar → Tarozilar → AI tarozi → «Faylni tayyorlash →»: CSV (PLU; nomi; birligi; narxi). Uni tarozingiz dasturi bilan uning yo‘riqnomasi bo‘yicha yuklang."));
                yield return Label(L("Настройте на весах штрих-код «префикс 20–29 + код 5 цифр + вес или сумма 5 цифр».",
                    "Таразада штрих-кодду «20–29 префикс + 5 сандуу код + 5 сандуу салмак же сумма» деп жөндөңүз.",
                    "Set the scale barcode to “prefix 20–29 + 5-digit code + 5-digit weight or amount”.",
                    "Tartıda barkodu «20–29 önek + 5 haneli kod + 5 haneli ağırlık veya tutar» olarak ayarlayın.",
                    "Tarozida shtrix-kodni «20–29 prefiks + 5 xonali kod + 5 xonali vazn yoki summa» qilib sozlang."));
                break;

            default: // Штрих-ПРИНТ
                yield return Network();
                yield return new Step(
                    L("Сетевые настройки весов Штрих-ПРИНТ", "Штрих-ПРИНТ таразасынын тармак жөндөөлөрү", "Shtrih-PRINT network settings", "Shtrih-PRINT ağ ayarları", "Shtrix-PRINT tarmoq sozlamalari"),
                    L("IP-адрес, порт обмена (обычно 1111) и пароль задаются в системном режиме весов (параметры сети) — по руководству вашей модели. Адрес — из сети кассы (шаг 1). Эти три значения понадобятся в кассе.",
                      "IP-дарек, алмашуу порту (адатта 1111) жана сырсөз таразанын системалык режиминде (тармак параметрлери) коюлат — моделиңиздин нускамасы боюнча. Дарек — кассанын тармагынан (1-кадам). Бул үч маани кассада керек болот.",
                      "The IP address, exchange port (usually 1111) and password are set in the scale's system mode (network parameters) — see your model's manual. The address must be in the till's network (step 1). You will need these three values in the till.",
                      "IP adresi, iletişim portu (genellikle 1111) ve şifre tartının sistem modunda (ağ parametreleri) ayarlanır — modelinizin kılavuzuna bakın. Adres kasanın ağından olmalı (1. adım). Bu üç değer kasada gerekecek.",
                      "IP manzil, almashuv porti (odatda 1111) va parol tarozining tizim rejimida (tarmoq parametrlari) belgilanadi — modelingiz qo‘llanmasiga qarang. Manzil kassa tarmog‘idan bo‘lishi kerak (1-qadam). Bu uch qiymat kassada kerak bo‘ladi."));
                yield return new Step(
                    L("Добавьте весы в кассе", "Таразаны кассага кошуңуз", "Add the scale in the till", "Tartıyı kasaya ekleyin", "Tarozini kassaga qo‘shing"),
                    L("Настройки → Весы → «+ Добавить весы» → Штрих-ПРИНТ. Способ: «Напрямую по сети» (касса пишет сама, UDP) или «Через сервер NurCRM». Впишите IP, порт и пароль → «Проверить связь»: весы пискнут и покажут модель.",
                      "Жөндөөлөр → Таразалар → «+ Тараза кошуу» → Штрих-ПРИНТ. Жол: «Тармак аркылуу түз» (касса өзү жазат, UDP) же «NurCRM сервери аркылуу». IP, порт жана сырсөздү жазыңыз → «Байланышты текшерүү»: тараза чыйылдап, моделин көрсөтөт.",
                      "Settings → Scales → “+ Add scale” → Shtrih-PRINT. Method: “Directly over the network” (the till writes itself, UDP) or “Via the NurCRM server”. Enter IP, port and password → “Check connection”: the scale beeps and shows its model.",
                      "Ayarlar → Tartılar → «+ Tartı ekle» → Shtrih-PRINT. Yöntem: «Doğrudan ağ üzerinden» (kasa kendisi yazar, UDP) veya «NurCRM sunucusu üzerinden». IP, port ve şifreyi girin → «Bağlantıyı kontrol et»: tartı bip sesi verir ve modelini gösterir.",
                      "Sozlamalar → Tarozilar → «+ Tarozi qo‘shish» → Shtrix-PRINT. Usul: «To‘g‘ridan-to‘g‘ri tarmoq orqali» (kassa o‘zi yozadi, UDP) yoki «NurCRM serveri orqali». IP, port va parolni kiriting → «Aloqani tekshirish»: tarozi signal beradi va modelini ko‘rsatadi."),
                    "guide-kassa-setup-shtrikh.gif");
                yield return SendGoods(L("Кнопки Штрих-ПРИНТ задаются в колонке «Клавиша» (1–90); если товар сменил PLU, касса переводит его кнопки на новый номер.",
                    "Штрих-ПРИНТ баскычтары «Баскыч» тилкесинде берилет (1–90); товардын PLU'су өзгөрсө, касса анын баскычтарын жаңы номерге которот.",
                    "Shtrih-PRINT keys are set in the “Key” column (1–90); if a product's PLU changes, the till moves its keys to the new number.",
                    "Shtrih-PRINT tuşları «Tuş» sütununda verilir (1–90); ürünün PLU'su değişirse kasa tuşlarını yeni numaraya taşır.",
                    "Shtrix-PRINT tugmalari «Tugma» ustunida beriladi (1–90); tovar PLU'si o‘zgarsa, kassa uning tugmalarini yangi raqamga o‘tkazadi."));
                yield return Label(L("Структуру штрих-кода на весах смотрите в «Настройки весов Штрих-ПРИНТ» → «Штрих-код».",
                    "Таразадагы штрих-коддун түзүлүшүн «Штрих-ПРИНТ таразасынын жөндөөлөрү» → «Штрих-код» ичинен караңыз.",
                    "See the scale barcode structure in “Shtrih-PRINT scale settings” → “Barcode”.",
                    "Tartıdaki barkod yapısını «Shtrih-PRINT tartı ayarları» → «Barkod» bölümünde görün.",
                    "Tarozidagi shtrix-kod tuzilmasini «Shtrix-PRINT tarozi sozlamalari» → «Shtrix-kod» bo‘limida ko‘ring."));
                break;
        }
    }
}
