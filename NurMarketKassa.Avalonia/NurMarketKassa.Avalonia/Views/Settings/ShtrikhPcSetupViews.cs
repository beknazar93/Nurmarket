using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-06: поля подключения весов «Штрих РС-200С2» в Настройки → Весы (LabelScaleSetupPanel). Обмен — файлами через общую папку весов
/// (руководство администратора, п. 7.4): папка, файл импорта, флаг-файл, файл экспорта, число знаков цены, «Очистить базу». Всё хранится в
/// <see cref="ShtrikhPcSettings"/> (UserPreferences.ShtrikhPcConfig) и сохраняется при каждой правке. Разметка — кодом, чтобы не трогать XAML панели.</summary>
internal sealed class ShtrikhPcFieldsView : StackPanel
{
    /// <summary>Поля поменялись и сохранены (родитель обновляет строку состояния).</summary>
    public event Action? Changed;

    private readonly TextBox _folder = new() { Classes = { "ModernTextBox" }, Height = 36, Watermark = "\\\\192.168.0.10\\Shared" };
    private readonly TextBox _import = new() { Classes = { "ModernTextBox" }, Height = 36, Watermark = ShtrikhPcSettings.DefaultImportFile };
    private readonly TextBox _flag = new() { Classes = { "ModernTextBox" }, Height = 36, Watermark = ShtrikhPcSettings.DefaultFlagFile };
    private readonly TextBox _export = new() { Classes = { "ModernTextBox" }, Height = 36, Watermark = ShtrikhPcSettings.DefaultExportFile };
    private readonly TextBox _login = new() { Classes = { "ModernTextBox" }, Height = 36, Watermark = "web-scale1\\kassa" };
    private readonly TextBox _password = new() { Classes = { "ModernTextBox" }, Height = 36, PasswordChar = '●' };
    private readonly TextBox _number = new() { Classes = { "ModernTextBox" }, Height = 36, Width = 100, Watermark = "1" };
    private readonly ComboBox _decimals = new() { Height = 36, Width = 100, ItemsSource = new[] { "0", "1", "2", "3" } };
    private readonly ComboBox _lineStyle = new() { Height = 36, MinWidth = 220 };
    private readonly ComboBox _separator = new() { Height = 36, MinWidth = 220 };
    private readonly CheckBox _sendPictures = new();
    private readonly TextBox _pictureOffset = new() { Classes = { "ModernTextBox" }, Height = 36, Width = 140, Watermark = "100000" };
    private bool _loading;

    /// <summary>2026-11-06 (живой прогон 3, P3-63): «Очистить базу» и «Файлы обмена и дополнительно» — отдельный блок. Родитель ставит его ПОСЛЕ
    /// кнопки «Проверить связь» и её результата: раньше раскрытый список файлов уносил кнопку и результат вниз, под подвал окна (за прокрутку).</summary>
    public StackPanel AdvancedPart { get; } = new() { Spacing = 10, IsVisible = false };

    public ShtrikhPcFieldsView()
    {
        Spacing = 10;
        static TextBlock Label(string text) => new() { Text = text, Classes = { "FieldLabel" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        static TextBlock Hint(string text) => new() { Text = text, Classes = { "SettingsCardBody" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        Children.Add(Label(Tr.T("Папка обмена весов (общая папка Shared)", "Таразанын алмашуу папкасы (Shared жалпы папкасы)", "Scale exchange folder (the shared Shared folder)", "Tartı alışveriş klasörü (paylaşımlı Shared klasörü)", "Tarozi almashuv papkasi (umumiy Shared papkasi)")));
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        folderRow.Children.Add(_folder);
        var browse = new Button { Content = Tr.T("Выбрать…", "Тандоо…", "Browse…", "Seç…", "Tanlash…"), Classes = { "ScaleSecondary" }, Margin = new Thickness(0), MinHeight = 36 };
        browse.Click += async (_, _) => await BrowseAsync().ConfigureAwait(true);
        Grid.SetColumn(browse, 2);
        folderRow.Children.Add(browse);
        Children.Add(folderRow);
        Children.Add(Hint(Tr.T("Сетевая папка весов \\\\IP-весов\\Shared (IP — командой ipconfig на весах) либо локальная папка/флешка. Весы забирают файл сами через «Обновление» минут или по кнопке «Импорт данных» на весах.",
            "Таразанын тармак папкасы \\\\таразанын-IP\\Shared (IP — таразада ipconfig буйругу менен) же жергиликтүү папка/флешка. Тараза файлды «Обновление» мүнөттөн кийин өзү алат же таразадагы «Импорт данных» баскычы менен.",
            "The scale's network folder \\\\scale-IP\\Shared (IP — the ipconfig command on the scale) or a local folder/USB stick. The scale picks the file up itself after “Update” minutes or with the “Import data” button on the scale.",
            "Tartının ağ klasörü \\\\tartı-IP\\Shared (IP — tartıda ipconfig komutu) veya yerel klasör/USB bellek. Tartı dosyayı «Обновление» dakika sonra kendisi alır ya da tartıdaki «Импорт данных» düğmesiyle.",
            "Tarozining tarmoq papkasi \\\\tarozi-IP\\Shared (IP — tarozida ipconfig buyrug‘i) yoki mahalliy papka/fleshka. Tarozi faylni «Обновление» daqiqadan keyin o‘zi oladi yoki tarozidagi «Импорт данных» tugmasi bilan.")));

        var credsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var loginBox = new StackPanel();
        loginBox.Children.Add(Label(Tr.T("Логин Windows весов", "Таразанын Windows логини", "Scale Windows login", "Tartı Windows kullanıcı adı", "Tarozi Windows logini")));
        loginBox.Children.Add(_login);
        var passwordBox = new StackPanel();
        passwordBox.Children.Add(Label(Tr.T("Пароль", "Сырсөз", "Password", "Şifre", "Parol")));
        passwordBox.Children.Add(_password);
        Grid.SetColumn(passwordBox, 2);
        credsRow.Children.Add(loginBox);
        credsRow.Children.Add(passwordBox);
        Children.Add(credsRow);

        Children.Add(Hint(Tr.T("Если папка Shared на весах закрыта паролем — впишите логин и пароль пользователя Windows весов (в виде ИМЯ_КОМПЬЮТЕРА\\пользователь, например web-scale1\\kassa — без имени компьютера Windows ищет пользователя на кассе и пишет «неверный пароль»). Пароль хранится зашифрованно и в журнал не пишется. Если на весах открыть папке «Все» с правом записи — логин и пароль не нужны. Сетевая папка работает только в Windows.",
            "Таразадагы Shared папкасы сырсөз менен жабык болсо — таразадагы Windows колдонуучусунун логинин жана сырсөзүн жазыңыз (КОМПЬЮТЕР_АТЫ\\колдонуучу түрүндө, мисалы web-scale1\\kassa — компьютердин атысыз Windows колдонуучуну кассадан издеп «сырсөз туура эмес» деп жазат). Сырсөз шифрленип сакталат жана журналга жазылбайт. Таразада папкага «Все» үчүн жазуу укугун ачсаңыз — логин жана сырсөз керек эмес. Тармак папкасы Windows'то гана иштейт.",
            "If the Shared folder on the scale is password-protected, enter the login and password of the Windows user on the scale (as COMPUTER_NAME\\user, e.g. web-scale1\\kassa — without the computer name Windows looks for the user on the till and says “wrong password”). The password is stored encrypted and never written to the log. If you open the folder to “Everyone” with write access on the scale, no login or password is needed. A network folder works on Windows only.",
            "Tartıdaki Shared klasörü şifreliyse tartıdaki Windows kullanıcısının adını ve şifresini girin (BİLGİSAYAR_ADI\\kullanıcı biçiminde, örn. web-scale1\\kassa — bilgisayar adı olmadan Windows kullanıcıyı kasada arar ve «şifre yanlış» der). Şifre şifreli saklanır ve günlüğe yazılmaz. Tartıda klasörü «Herkes» için yazma izniyle açarsanız ad ve şifre gerekmez. Ağ klasörü yalnızca Windows'ta çalışır.",
            "Tarozidagi Shared papkasi parol bilan yopiq bo‘lsa — tarozidagi Windows foydalanuvchisining login va parolini yozing (KOMPYUTER_NOMI\\foydalanuvchi ko‘rinishida, masalan web-scale1\\kassa — kompyuter nomisiz Windows foydalanuvchini kassada qidiradi va «parol noto‘g‘ri» deydi). Parol shifrlangan holda saqlanadi va jurnalga yozilmaydi. Tarozida papkani «Все» uchun yozish huquqi bilan ochsangiz — login va parol kerak emas. Tarmoq papkasi faqat Windows'da ishlaydi.")));

        // Пример заполнения по весам магазина владельца (Умар, 06.11.2026) — подсказка, а не навязанные значения.
        var example = new Button { Content = Tr.T("Заполнить примером: весы магазина (IP 192.168.0.200)", "Мисал менен толтуруу: дүкөндүн таразасы (IP 192.168.0.200)", "Fill in the example: the shop's scale (IP 192.168.0.200)", "Örnekle doldur: mağaza tartısı (IP 192.168.0.200)", "Namuna bilan to‘ldirish: do‘kon tarozisi (IP 192.168.0.200)"), Classes = { "ScaleSecondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        example.Click += (_, _) =>
        {
            var s = ShtrikhPcSettings.Current;
            s.Folder = ShtrikhPcSettings.ExampleFolder;
            s.Login = ShtrikhPcSettings.ExampleLogin;
            s.PriceDecimals = 2;
            s.DecimalSeparator = ".";
            s.BarcodePrefix = 27;
            s.CodeLength = 5;
            s.BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight;
            s.CodeChecksum = false;
            ScaleBarcodeRules.ApplyShtrikhPc(s);
            UserPreferences.Instance.SaveToDisk();
            Load();
            Changed?.Invoke();
        };
        Children.Add(example);
        Children.Add(Hint(Tr.T("Пример: папка \\\\192.168.0.200\\Shared, логин web-scale1\\kassa (пароль впишите сами), префикс 27, «Код-Вес», 5 знаков кода, цена с точкой и 2 знаками — так настроены весы владельца. Пароль подставляется только вами.",
            "Мисал: \\\\192.168.0.200\\Shared папкасы, web-scale1\\kassa логини (сырсөздү өзүңүз жазыңыз), 27 префикси, «Код-Салмак», коддун 5 белгиси, баа чекит жана 2 белги менен — ээсинин таразалары ушундай жөндөлгөн. Сырсөздү өзүңүз жазасыз.",
            "Example: folder \\\\192.168.0.200\\Shared, login web-scale1\\kassa (enter the password yourself), prefix 27, “Code-Weight”, 5 code digits, price with a dot and 2 decimals — this is how the owner's scale is set up. The password is entered only by you.",
            "Örnek: klasör \\\\192.168.0.200\\Shared, kullanıcı web-scale1\\kassa (şifreyi kendiniz girin), önek 27, «Kod-Ağırlık», 5 kod hanesi, fiyat noktalı ve 2 ondalıklı — sahibinin tartısı böyle ayarlı. Şifreyi yalnızca siz girersiniz.",
            "Namuna: \\\\192.168.0.200\\Shared papkasi, login web-scale1\\kassa (parolni o‘zingiz yozing), prefiks 27, «Kod-Vazn», kodning 5 raqami, narx nuqta va 2 xona bilan — egasining tarozisi shunday sozlangan. Parolni faqat siz kiritasiz.")));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,Auto") };
        var decimalsBox = new StackPanel();
        decimalsBox.Children.Add(Label(Tr.T("Знаков в цене", "Баадагы белгилер", "Price decimals", "Fiyat ondalığı", "Narxdagi xonalar")));
        decimalsBox.Children.Add(_decimals);
        var numberBox = new StackPanel();
        numberBox.Children.Add(Label(Tr.T("Номер весов", "Таразанын номери", "Scale number", "Tartı numarası", "Tarozi raqami")));
        numberBox.Children.Add(_number);
        Grid.SetColumn(numberBox, 2);
        row.Children.Add(decimalsBox);
        row.Children.Add(numberBox);
        Children.Add(row);
        Children.Add(Hint(Tr.T("«Число десятичных знаков цены и стоимости» с вкладки «Общие параметры» весов (по умолчанию 2) — цена в файле пишется с таким числом знаков.",
            "Таразанын «Общие параметры» өтмөгүндөгү «Число десятичных знаков цены и стоимости» (демейки 2) — файлда баа ушунча белги менен жазылат.",
            "“Number of decimals of price and cost” from the scale's “General parameters” tab (default 2) — the price in the file is written with that many digits.",
            "Tartının «Общие параметры» sekmesindeki «Число десятичных знаков цены и стоимости» (varsayılan 2) — dosyada fiyat bu kadar hane ile yazılır.",
            "Tarozining «Общие параметры» varag‘idagi «Число десятичных знаков цены и стоимости» (standart 2) — faylda narx shuncha xona bilan yoziladi.")));

        _sendPictures.Content = Tr.T("Отправлять картинки товаров на весы", "Товарлардын сүрөттөрүн таразага жөнөтүү", "Send product pictures to the scale", "Ürün resimlerini tartıya gönder", "Tovar rasmlarini taroziga yuborish");
        Children.Add(_sendPictures);
        Children.Add(Hint(Tr.T("На главном экране весов видны только товары с картинкой; без картинки товар находится лишь поиском. Касса переведёт фото товара в BMP 288×288 и положит в папку Pictures на весах (уже записанные, неизменившиеся не перекачиваются). Очистка базы весов — отдельная кнопка «Полная выгрузка (с очисткой)» в окне «Весы».",
            "Таразанын башкы экранында сүрөтү бар товарлар гана көрүнөт; сүрөтсүз товар издөө аркылуу гана табылат. Касса товардын сүрөтүн BMP 288×288 кылып, таразадагы Pictures папкасына салат (жазылып калган, өзгөрбөгөндөрү кайра жүктөлбөйт). Таразанын базасын тазалоо — «Весы» терезесиндеги өзүнчө «Толук жүктөө (тазалоо менен)» баскычы.",
            "On the scale's main screen only products with a picture are visible; a product without one can only be found by search. The till converts the product photo to BMP 288×288 and puts it into the Pictures folder on the scale (already written, unchanged pictures are not re-sent). Clearing the scale database is a separate “Full upload (with clearing)” button in the “Scales” window.",
            "Tartının ana ekranında yalnızca resmi olan ürünler görünür; resimsiz ürün sadece aramayla bulunur. Kasa ürün fotoğrafını BMP 288×288'e çevirip tartıdaki Pictures klasörüne koyar (yazılmış, değişmemiş resimler yeniden gönderilmez). Tartı veritabanını temizleme — «Tartı» penceresinde ayrı «Tam yükleme (temizlemeyle)» düğmesi.",
            "Tarozining bosh ekranida faqat rasmli tovarlar ko‘rinadi; rasmsiz tovar faqat qidiruv orqali topiladi. Kassa tovar fotosini BMP 288×288 ga o‘tkazib, tarozidagi Pictures papkasiga qo‘yadi (yozilgan, o‘zgarmagan rasmlar qayta yuborilmaydi). Tarozi bazasini tozalash — «Tarozi» oynasida alohida «To‘liq yuklash (tozalash bilan)» tugmasi.")));

        // Файлы обмена и «Дополнительно» — под выключателем: по умолчанию значения из руководства, менять нужно редко.
        var advanced = new Expander
        {
            Header = Tr.T("Файлы обмена и дополнительно", "Алмашуу файлдары жана кошумча", "Exchange files and advanced", "Alışveriş dosyaları ve gelişmiş", "Almashuv fayllari va qo‘shimcha"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var inner = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        inner.Children.Add(Label(Tr.T("Файл импорта (относительно папки)", "Импорт файлы (папкага карата)", "Import file (relative to the folder)", "İçe aktarma dosyası (klasöre göre)", "Import fayli (papkaga nisbatan)")));
        inner.Children.Add(_import);
        inner.Children.Add(Label(Tr.T("Флаг-файл импорта", "Импорттун флаг-файлы", "Import flag file", "İçe aktarma bayrak dosyası", "Import bayroq-fayli")));
        inner.Children.Add(_flag);
        inner.Children.Add(Label(Tr.T("Файл экспорта", "Экспорт файлы", "Export file", "Dışa aktarma dosyası", "Eksport fayli")));
        inner.Children.Add(_export);
        inner.Children.Add(Hint(Tr.T("Должны совпадать с вкладкой «База данных» на весах (по умолчанию Import\\PCScale.txt, Import\\PCScale.flz, Export\\Export.txt).",
            "Таразадагы «База данных» өтмөгү менен дал келиши керек (демейки Import\\PCScale.txt, Import\\PCScale.flz, Export\\Export.txt).",
            "Must match the “Database” tab on the scale (default Import\\PCScale.txt, Import\\PCScale.flz, Export\\Export.txt).",
            "Tartıdaki «База данных» sekmesiyle aynı olmalı (varsayılan Import\\PCScale.txt, Import\\PCScale.flz, Export\\Export.txt).",
            "Tarozidagi «База данных» varag‘i bilan bir xil bo‘lishi kerak (standart Import\\PCScale.txt, Import\\PCScale.flz, Export\\Export.txt).")));
        inner.Children.Add(Label(Tr.T("Начало строки товара в файле", "Файлдагы товар сабынын башталышы", "Start of a product line in the file", "Dosyada ürün satırının başlangıcı", "Fayldagi tovar qatorining boshlanishi")));
        _lineStyle.ItemsSource = new[]
        {
            Tr.T("«<D 1;…» — как в примере руководства", "«<D 1;…» — нускамадагы мисалдай", "“<D 1;…” — as in the manual's example", "«<D 1;…» — kılavuzdaki örnekteki gibi", "«<D 1;…» — qo‘llanmadagi namunadagidek"),
            Tr.T("«<D1;…» — слитно", "«<D1;…» — бириктирилген", "“<D1;…” — joined", "«<D1;…» — bitişik", "«<D1;…» — qo‘shib"),
            Tr.T("«<D;1;…» — через «;»", "«<D;1;…» — «;» аркылуу", "“<D;1;…” — with “;”", "«<D;1;…» — «;» ile", "«<D;1;…» — «;» bilan"),
        };
        inner.Children.Add(_lineStyle);
        inner.Children.Add(Label(Tr.T("Десятичный разделитель цены в файле", "Файлдагы баанын ондук бөлгүчү", "Price decimal separator in the file", "Dosyada fiyat ondalık ayıracı", "Fayldagi narxning o‘nlik ajratgichi")));
        _separator.ItemsSource = new[]
        {
            Tr.T("Запятая (12,50)", "Үтүр (12,50)", "Comma (12,50)", "Virgül (12,50)", "Vergul (12,50)"),
            Tr.T("Точка (12.50)", "Чекит (12.50)", "Dot (12.50)", "Nokta (12.50)", "Nuqta (12.50)"),
        };
        inner.Children.Add(_separator);
        inner.Children.Add(Hint(Tr.T("Точный вид строки «<D…» и разделитель цены в руководстве описаны не до конца. Если весы приняли файл, но товаров нет или цены неверные — переключите эти два пункта и отправьте снова.",
            "«<D…» сабынын так түрү жана баанын бөлгүчү нускамада толук сүрөттөлгөн эмес. Тараза файлды кабыл алса да товар жок же баа туура эмес болсо — ушул эки пунктту алмаштырып, кайра жөнөтүңүз.",
            "The exact shape of the “<D…” line and the price separator are not fully described in the manual. If the scale accepted the file but there are no goods or the prices are wrong — switch these two items and send again.",
            "«<D…» satırının tam biçimi ve fiyat ayıracı kılavuzda tam anlatılmamış. Tartı dosyayı kabul etti ama ürün yok ya da fiyatlar yanlışsa — bu iki öğeyi değiştirip tekrar gönderin.",
            "«<D…» qatorining aniq shakli va narx ajratgichi qo‘llanmada to‘liq tavsiflanmagan. Tarozi faylni qabul qilsa-yu, tovar yo‘q yoki narxlar noto‘g‘ri bo‘lsa — shu ikki bandni almashtirib, qayta yuboring.")));
        inner.Children.Add(Label(Tr.T("Код ресурса картинки = число + код товара", "Сүрөт ресурсунун коду = сан + товар коду", "Picture resource code = number + item code", "Resim kaynak kodu = sayı + ürün kodu", "Rasm resursi kodi = son + tovar kodi")));
        inner.Children.Add(_pictureOffset);
        inner.Children.Add(Hint(Tr.T("По умолчанию 100000: картинка товара с кодом 123 получает код ресурса 100123. Число не должно пересекаться с кодами ресурсов, уже лежащими на весах (у прежней системы — код + 6000).",
            "Демейки 100000: 123 коддуу товардун сүрөтү 100123 ресурс кодун алат. Сан таразада турган ресурс коддору менен дал келбеши керек (мурунку системада — код + 6000).",
            "Default 100000: the picture of the product with code 123 gets resource code 100123. The number must not overlap with resource codes already on the scale (the previous system used code + 6000).",
            "Varsayılan 100000: 123 kodlu ürünün resmi 100123 kaynak kodunu alır. Sayı tartıda zaten bulunan kaynak kodlarıyla çakışmamalı (önceki sistem kod + 6000 kullanıyordu).",
            "Standart 100000: 123 kodli tovar rasmi 100123 resurs kodini oladi. Son tarozida mavjud resurs kodlari bilan to‘qnashmasligi kerak (oldingi tizimda — kod + 6000).")));
        var reset = new Button { Content = Tr.T("Вернуть значения из руководства", "Нускамадагы маанилерди кайтаруу", "Restore the manual's defaults", "Kılavuzdaki değerleri geri yükle", "Qo‘llanmadagi qiymatlarni qaytarish"), Classes = { "ScaleSecondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            var s = ShtrikhPcSettings.Current;
            s.ImportFile = ShtrikhPcSettings.DefaultImportFile;
            s.FlagFile = ShtrikhPcSettings.DefaultFlagFile;
            s.ExportFile = ShtrikhPcSettings.DefaultExportFile;
            s.LineStyle = ShtrikhPcSettings.LineSpace;
            s.DecimalSeparator = "."; // как в настоящем файле весов (650.00)
            s.PictureOffset = 100_000;
            Store(s);
            Load();
        };
        inner.Children.Add(reset);
        advanced.Content = inner;
        AdvancedPart.Children.Add(advanced);

        foreach (var box in new[] { _folder, _login, _password, _import, _flag, _export, _number, _pictureOffset })
            box.LostFocus += (_, _) => Save();
        _decimals.SelectionChanged += (_, _) => Save();
        _lineStyle.SelectionChanged += (_, _) => Save();
        _separator.SelectionChanged += (_, _) => Save();
        _sendPictures.IsCheckedChanged += (_, _) => Save();
    }

    /// <summary>Заполняет поля из настроек. Ничего не сохраняет.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var s = ShtrikhPcSettings.Current;
            _folder.Text = s.Folder;
            _login.Text = s.Login;
            _password.Text = s.Password;
            _import.Text = s.ImportFile;
            _flag.Text = s.FlagFile;
            _export.Text = s.ExportFile;
            _number.Text = s.ScaleNumber.ToString(CultureInfo.InvariantCulture);
            _decimals.SelectedIndex = s.PriceDecimals;
            _sendPictures.IsChecked = s.SendPictures;
            _pictureOffset.Text = s.PictureOffset.ToString(CultureInfo.InvariantCulture);
            _lineStyle.SelectedIndex = s.LineStyle switch { ShtrikhPcSettings.LineGlued => 1, ShtrikhPcSettings.LineSemicolon => 2, _ => 0 };
            _separator.SelectedIndex = s.DecimalSeparator == "." ? 1 : 0;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Записывает поля в настройки (вызывается и родителем перед кнопками).</summary>
    public void Save()
    {
        if (_loading)
            return;
        var s = ShtrikhPcSettings.Current;
        s.Folder = (_folder.Text ?? "").Trim();
        s.Login = (_login.Text ?? "").Trim();
        s.Password = _password.Text ?? "";
        s.ImportFile = (_import.Text ?? "").Trim();
        s.FlagFile = (_flag.Text ?? "").Trim();
        s.ExportFile = (_export.Text ?? "").Trim();
        s.ScaleNumber = int.TryParse((_number.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        s.PriceDecimals = Math.Clamp(_decimals.SelectedIndex, 0, 3);
        s.SendPictures = _sendPictures.IsChecked == true;
        s.PictureOffset = int.TryParse((_pictureOffset.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off) ? off : 100_000;
        s.LineStyle = _lineStyle.SelectedIndex switch { 1 => ShtrikhPcSettings.LineGlued, 2 => ShtrikhPcSettings.LineSemicolon, _ => ShtrikhPcSettings.LineSpace };
        s.DecimalSeparator = _separator.SelectedIndex == 1 ? "." : ",";
        Store(s);
    }

    private void Store(ShtrikhPcSettings s)
    {
        ShtrikhPcSettings.Save(s);
        UserPreferences.Instance.SaveToDisk();
        // Выбранные весы (профиль) помнят папку — у каждых весов она своя.
        if (string.Equals(UserPreferences.Instance.ScaleBrand, ScaleUi.BrandShtrikhPc, StringComparison.Ordinal))
            LabelScaleStore.CaptureActive();
        Changed?.Invoke();
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } provider)
            return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Tr.T("Папка обмена весов", "Таразанын алмашуу папкасы", "Scale exchange folder", "Tartı alışveriş klasörü", "Tarozi almashuv papkasi"),
            AllowMultiple = false,
        }).ConfigureAwait(true);
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
            return;
        _folder.Text = path;
        Save();
    }
}

/// <summary>2026-11-06: пресет раскладки штрихкода «Штрих РС-200» (руководство, п. 7.6 «Конфигурация. Штрих-код»): префикс, число знаков кода (1…6), формат
/// «Код-Вес» / «Код-Стоимость» / «Вес-Код» / «Стоимость-Код», «КС кода товара». Те же значения должны стоять на весах. Пример показывает этикетку для кода товара
/// и вес 0,144 кг и как её прочтёт касса. Стоит в Настройки → Весы → «Штрих-код этикеток» и открывается из окна «Весы».</summary>
internal sealed class ShtrikhPcBarcodeCard : Border
{
    /// <summary>Раскладка поменялась и сохранена (родитель перерисовывает правила и примеры).</summary>
    public event Action? Changed;

    private readonly CheckBox _enabled = new();
    private readonly ComboBox _prefix = new() { Height = 36, Width = 90 };
    private readonly ComboBox _length = new() { Height = 36, Width = 90, ItemsSource = new[] { "1", "2", "3", "4", "5", "6" } };
    private readonly ComboBox _format = new() { Height = 36, MinWidth = 200 };
    private readonly CheckBox _checksum = new();
    private readonly TextBlock _example = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _warn = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12.5, FontWeight = Avalonia.Media.FontWeight.SemiBold };
    private bool _loading;

    public ShtrikhPcBarcodeCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = Tr.T("Весы «Штрих РС-200»: раскладка этикетки", "«Штрих РС-200» таразасы: этикетка жайгашуусу", "Shtrih RS-200 scale: label layout", "Shtrih RS-200 tartı: etiket düzeni", "Shtrix RS-200 tarozisi: yorliq tartibi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
        });
        _enabled.Content = Tr.T("Читать этикетки по раскладке РС-200", "Этикеткаларды РС-200 жайгашуусу боюнча окуу", "Read labels by the RS-200 layout", "Etiketleri RS-200 düzenine göre oku", "Yorliqlarni RS-200 tartibi bo‘yicha o‘qish");
        root.Children.Add(_enabled);
        root.Children.Add(new TextBlock
        {
            Text = Tr.T("Как на весах: вкладка «Штрих-код» (Система и печать → Конфигурация). Раскладка действует на весь префикс на этой кассе — этикетки других весов с тем же префиксом читаться по-старому не будут.",
                "Таразадагыдай: «Штрих-код» өтмөгү (Система и печать → Конфигурация). Жайгашуу бул кассада бүт префикске таасир этет — ошол эле префикстеги башка таразалардын этикеткалары мурункудай окулбайт.",
                "As on the scale: the “Barcode” tab (System and printing → Configuration). The layout applies to the whole prefix on this till — labels of other scales with the same prefix will not be read the old way.",
                "Tartıdaki gibi: «Штрих-код» sekmesi (Система и печать → Конфигурация). Düzen bu kasada tüm öneki etkiler — aynı önekli diğer tartıların etiketleri eski biçimde okunmaz.",
                "Tarozidagidek: «Штрих-код» varag‘i (Система и печать → Конфигурация). Tartib bu kassada butun prefiksga ta’sir qiladi — shu prefiksdagi boshqa tarozilarning yorliqlari eskicha o‘qilmaydi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
        });

        for (var p = 20; p <= 29; p++)
            _prefix.Items.Add(p.ToString(CultureInfo.InvariantCulture));
        _format.ItemsSource = ShtrikhPcSettings.Formats.Select(ShtrikhPcSettings.FormatTitle).ToArray();

        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        StackPanel Cell(string label, Control editor)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
            cell.Children.Add(new TextBlock { Text = label, Classes = { "FieldLabel" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            cell.Children.Add(editor);
            return cell;
        }
        grid.Children.Add(Cell(Tr.T("Префикс весового товара", "Салмактуу товар префикси", "Weighed-item prefix", "Tartılı ürün öneki", "Vaznli tovar prefiksi"), _prefix));
        grid.Children.Add(Cell(Tr.T("Число знаков кода товара", "Товар коду белгилеринин саны", "Number of code digits", "Ürün kodu hane sayısı", "Tovar kodi raqamlari soni"), _length));
        grid.Children.Add(Cell(Tr.T("Формат весового товара", "Салмактуу товар форматы", "Weighed-item format", "Tartılı ürün biçimi", "Vaznli tovar formati"), _format));
        root.Children.Add(grid);
        _checksum.Content = Tr.T("КС кода товара (контрольная цифра кода)", "Товар коду КС (коддун текшерүү цифрасы)", "Item code check digit (КС)", "Ürün kodu KS (kod kontrol hanesi)", "Tovar kodi KS (kodning nazorat raqami)");
        root.Children.Add(_checksum);
        root.Children.Add(new TextBlock
        {
            Text = Tr.T("Числа на этикетке: 10 знаков на код и значение. Для веса до 99,999 кг нужно 5 знаков значения, то есть код — не длиннее 5 знаков. С КС последняя цифра кода — младший разряд суммы остальных (код 5 знаков с КС = 4 цифры кода + КС).",
                "Этикетка саны: код менен мааниге 10 белги. 99,999 кг чейинки салмак үчүн маанинин 5 белгиси керек, башкача айтканда код 5 белгиден узун эмес. КС менен коддун акыркы цифрасы — калгандарынын суммасынын кичине разряды (КС менен 5 белгилүү код = 4 цифра код + КС).",
                "Label numbers: 10 digits for code and value. A weight up to 99.999 kg needs 5 value digits, so the code is at most 5 digits. With КС the last code digit is the low digit of the sum of the others (a 5-digit field with КС = 4 code digits + КС).",
                "Etiket sayıları: kod ve değer için 10 hane. 99,999 kg'a kadar ağırlık 5 değer hanesi ister, yani kod en çok 5 hane. KS ile kodun son hanesi diğerlerinin toplamının son basamağıdır (KS'li 5 haneli alan = 4 kod hanesi + KS).",
                "Yorliq raqamlari: kod va qiymatga 10 raqam. 99,999 kg gacha vazn uchun qiymatning 5 raqami kerak, ya’ni kod 5 raqamdan uzun emas. KS bilan kodning oxirgi raqami — qolganlari yig‘indisining kichik razryadi (KS bilan 5 xonali maydon = 4 kod raqami + KS)."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
        });
        root.Children.Add(_example);
        root.Children.Add(_warn);
        root.Children.Add(new TextBlock
        {
            Text = Tr.T("Сверка с весами: на весах (командная строка) выполните  findstr \"303 304 305 306 307 308 817\" C:\\PC-200\\Config.txt  — 303 префикс весового товара (у владельца 27), 305 формат весового (0 = Код-Вес, 1 = Код-Стоимость), 307 число цифр кода (5), 308 КС кода (0 = выкл), 304 и 306 — то же для штучного, 817 — импорт товаров (1 = включён). Те же значения должны стоять здесь.",
                "Таразалар менен салыштыруу: таразада (буйрук сабы) аткарыңыз  findstr \"303 304 305 306 307 308 817\" C:\\PC-200\\Config.txt  — 303 салмактуу товар префикси (ээсинде 27), 305 салмактуунун форматы (0 = Код-Салмак, 1 = Код-Баа), 307 коддун цифраларынын саны (5), 308 коддун КС (0 = өчүк), 304 жана 306 — штучныйга ошол эле, 817 — товарларды импорттоо (1 = күйүк). Ушул эле маанилер бул жерде да турушу керек.",
                "Check against the scale: on the scale (command line) run  findstr \"303 304 305 306 307 308 817\" C:\\PC-200\\Config.txt  — 303 weighed-item prefix (the owner has 27), 305 weighed format (0 = Code-Weight, 1 = Code-Cost), 307 number of code digits (5), 308 code check digit (0 = off), 304 and 306 — the same for piece goods, 817 — goods import (1 = on). The same values must be set here.",
                "Tartıyla karşılaştırma: tartıda (komut satırı)  findstr \"303 304 305 306 307 308 817\" C:\\PC-200\\Config.txt  çalıştırın — 303 tartılı ürün öneki (sahibinde 27), 305 tartılı biçim (0 = Kod-Ağırlık, 1 = Kod-Tutar), 307 kod hane sayısı (5), 308 kod KS (0 = kapalı), 304 ve 306 — adetli için aynısı, 817 — ürün içe aktarma (1 = açık). Aynı değerler burada da olmalı.",
                "Tarozi bilan solishtirish: tarozida (buyruq qatori)  findstr \"303 304 305 306 307 308 817\" C:\\PC-200\\Config.txt  ni bajaring — 303 vaznli tovar prefiksi (egasida 27), 305 vaznli format (0 = Kod-Vazn, 1 = Kod-Narx), 307 kod raqamlari soni (5), 308 kod KS (0 = o‘chiq), 304 va 306 — donali uchun xuddi shu, 817 — tovarlarni import qilish (1 = yoqilgan). Xuddi shu qiymatlar bu yerda ham bo‘lishi kerak."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        Child = root;

        _enabled.IsCheckedChanged += (_, _) => Save();
        _prefix.SelectionChanged += (_, _) => Save();
        _length.SelectionChanged += (_, _) => Save();
        _format.SelectionChanged += (_, _) => Save();
        _checksum.IsCheckedChanged += (_, _) => Save();
        AttachedToVisualTree += (_, _) => Load();
    }

    /// <summary>Карточка нужна, когда выбраны весы РС-200 или раскладка уже включена.</summary>
    public void Reload() => Load();

    private void Load()
    {
        _loading = true;
        try
        {
            var s = ShtrikhPcSettings.Current;
            _enabled.IsChecked = s.BarcodeEnabled;
            _prefix.SelectedIndex = s.BarcodePrefix - 20;
            _length.SelectedIndex = s.CodeLength - 1;
            _format.SelectedIndex = Math.Max(0, Array.IndexOf(ShtrikhPcSettings.Formats, s.BarcodeFormat));
            _checksum.IsChecked = s.CodeChecksum;
            _checksum.IsEnabled = s.CodeLength >= 2;
            IsVisible = s.BarcodeEnabled || string.Equals(UserPreferences.Instance.ScaleBrand, ScaleUi.BrandShtrikhPc, StringComparison.Ordinal);
            ShowExample(s);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (_loading)
            return;
        var s = ShtrikhPcSettings.Current;
        s.BarcodeEnabled = _enabled.IsChecked == true;
        s.BarcodePrefix = 20 + Math.Max(0, _prefix.SelectedIndex);
        s.CodeLength = 1 + Math.Max(0, _length.SelectedIndex);
        s.BarcodeFormat = ShtrikhPcSettings.Formats[Math.Clamp(_format.SelectedIndex, 0, ShtrikhPcSettings.Formats.Length - 1)];
        s.CodeChecksum = _checksum.IsChecked == true && s.CodeLength >= 2;
        ScaleBarcodeRules.ApplyShtrikhPc(s);
        _checksum.IsEnabled = s.CodeLength >= 2;
        ShowExample(s);
        Changed?.Invoke();
    }

    private void ShowExample(ShtrikhPcSettings s)
    {
        s.Normalize();
        // Пример по первому весовому товару каталога с кодом, помещающимся в раскладку; иначе условный код 12.
        var tile = CatalogCacheService.Products.FirstOrDefault(p => p.IsWeighted && p.Plu is > 0 && p.Plu <= s.MaxProductCode);
        var code = tile?.Plu ?? 12;
        var price = tile is null ? 100m : (decimal)LocalCartService.ParsePrice(tile.PriceLine);
        var example = ScaleBarcodeRules.ShtrikhPcExample(s, code, price <= 0 ? 100m : price);
        _example.Text = example is null
            ? Tr.T($"Код {code} не помещается в {s.CodeLength} зн. — увеличьте число знаков кода.", $"{code} коду {s.CodeLength} белгиге батпайт — белгилердин санын көбөйтүңүз.", $"Code {code} does not fit {s.CodeLength} digits — raise the number of code digits.", $"{code} kodu {s.CodeLength} haneye sığmıyor — hane sayısını artırın.", $"{code} kodi {s.CodeLength} raqamga sig‘maydi — raqamlar sonini oshiring.")
            : Tr.T($"Пример (вес 0,144 кг): {example.Value.Barcode} → касса: {example.Value.Reading}", $"Мисал (салмагы 0,144 кг): {example.Value.Barcode} → касса: {example.Value.Reading}", $"Example (weight 0.144 kg): {example.Value.Barcode} → till: {example.Value.Reading}", $"Örnek (ağırlık 0,144 kg): {example.Value.Barcode} → kasa: {example.Value.Reading}", $"Namuna (vazn 0,144 kg): {example.Value.Barcode} → kassa: {example.Value.Reading}");
        var amountFormat = s.ByAmount;
        _warn.IsVisible = amountFormat;
        _warn.Text = amountFormat
            ? Tr.T("Формат со стоимостью работает, только если цены загружены в весы. Если на весах цена 0,00, в этикетке нет суммы: касса предупредит «загрузите цены в весы или выберите Код-Вес». «Код-Вес» работает и без цен на весах.",
                "Баасы бар формат баалар таразага жүктөлгөндө гана иштейт. Таразада баа 0,00 болсо, этикеткада сумма жок: касса «баалар таразага жүктөңүз же Код-Салмак тандаңыз» деп эскертет. «Код-Салмак» таразада баа жок болсо да иштейт.",
                "The cost format works only if prices are loaded into the scale. If the price on the scale is 0.00, the label has no amount: the till warns “load the prices into the scale or choose Code-Weight”. “Code-Weight” works without prices on the scale too.",
                "Tutarlı biçim yalnızca fiyatlar tartıya yüklüyse çalışır. Tartıda fiyat 0,00 ise etikette tutar yoktur: kasa «fiyatları tartıya yükleyin veya Kod-Ağırlık seçin» uyarısı verir. «Kod-Ağırlık» tartıda fiyat olmadan da çalışır.",
                "Narxli format faqat narxlar taroziga yuklangan bo‘lsagina ishlaydi. Tarozida narx 0,00 bo‘lsa, yorliqda summa yo‘q: kassa «narxlarni taroziga yuklang yoki Kod-Vazn ni tanlang» deb ogohlantiradi. «Kod-Vazn» tarozida narx bo‘lmasa ham ishlaydi.")
            : "";
    }
}
