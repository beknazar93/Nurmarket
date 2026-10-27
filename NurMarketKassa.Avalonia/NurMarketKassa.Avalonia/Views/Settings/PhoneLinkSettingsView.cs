using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-27, пакет К-П3 (урезанный, ТЗ «Мобилка и касса»): карточка «Телефоны» в настройках печати — тумблер
/// «Принимать сканы с телефонов» (по умолчанию выключен; порт открывается только при включении), кнопка «Подключить телефон»
/// (окно с QR), состояние и список подключённых. Включать и сопрягать могут владелец и администратор; кассир видит состояние.
/// Тумблер срабатывает сразу (кнопка «Сохранить» страницы не нужна). Вставляется в PrintSettingsView через PhoneLinkHost.</summary>
public sealed class PhoneLinkSettingsView : Border
{
    private readonly CheckBox _toggle = new() { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _phones = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    // 2026-10-27, ревью Ф-6: «Отключить» у каждого телефона прямо в настройках (раньше — только в окне сопряжения, которое при открытии
    // само создавало новый ключ: украденный телефон приходилось отключать через выдачу нового кода).
    private readonly StackPanel _phoneRows = new() { Spacing = 6 };
    private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect;
    private bool _suppress;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Стиль «Border.SettingsCard» (общие стили настроек) — как у обычной карточки: селектор типа точный.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public PhoneLinkSettingsView()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = T("Телефон как сканер", "Телефон сканер катары", "Phone as a scanner", "Telefon tarayıcı olarak", "Telefon skaner sifatida"),
            Classes = { "SettingsCardTitle" }, FontSize = 14,
        });
        stack.Children.Add(Hint(T("Приложение NurMarket на телефоне сканирует штрихкод камерой, и товар сразу попадает в чек этой кассы. Телефон должен быть в той же Wi-Fi сети, что и касса. Включают и подключают телефоны владелец и администратор.",
            "Телефондогу NurMarket тиркемеси штрихкодду камера менен сканерлейт, товар түз эле ушул кассанын чегине түшөт. Телефон кассадагы Wi-Fi тармагында болушу керек. Телефондорду ээси жана администратор күйгүзөт жана туташтырат.",
            "The NurMarket app on the phone scans a barcode with the camera and the goods go straight into this till's receipt. The phone must be on the same Wi-Fi network as the till. The owner and administrators turn it on and connect phones.",
            "Telefondaki NurMarket uygulaması barkodu kamerayla tarar ve ürün doğrudan bu kasanın fişine düşer. Telefon kasayla aynı Wi-Fi ağında olmalı. Telefonları sahip ve yönetici açar ve bağlar.",
            "Telefondagi NurMarket ilovasi shtrix-kodni kamera bilan skanerlaydi va tovar bevosita shu kassa chekiga tushadi. Telefon kassa bilan bir xil Wi-Fi tarmog'ida bo'lishi kerak. Telefonlarni egasi va administrator yoqadi va ulaydi.")));

        var label = T("Принимать сканы с телефонов", "Телефондордон сканерлөөнү кабыл алуу", "Accept scans from phones", "Telefonlardan taramaları kabul et", "Telefonlardan skanerlashni qabul qilish");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        Avalonia.Automation.AutomationProperties.SetName(_toggle, label);
        Grid.SetColumn(_toggle, 1);
        row.Children.Add(text);
        row.Children.Add(_toggle);
        stack.Children.Add(row);

        _state.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _phones.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(_state);
        stack.Children.Add(_phones);
        stack.Children.Add(_phoneRows);

        var connectText = T("Подключить телефон", "Телефонду туташтыруу", "Connect a phone", "Telefonu bağla", "Telefonni ulash");
        _connect = new Button { Content = connectText, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 0) };
        _connect.Classes.Add("PrintGoldButton"); // стиль объявлен в PrintSettingsView — карточка живёт внутри неё
        Avalonia.Automation.AutomationProperties.SetName(_connect, connectText);
        _connect.Click += async (_, _) => await OpenPairWindowAsync().ConfigureAwait(true);
        stack.Children.Add(_connect);

        _note.Text = OperatingSystem.IsWindows()
            ? T("При первом включении Windows может спросить про брандмауэр («Разрешить доступ») — нажмите «Разрешить» для частной сети. Касса сама правил брандмауэра не создаёт.",
                "Биринчи күйгүзгөндө Windows брандмауэр жөнүндө сурашы мүмкүн («Мүмкүнчүлүк берүү») — жеке тармак үчүн «Уруксат берүү» баскычын басыңыз. Касса брандмауэр эрежелерин өзү түзбөйт.",
                "On the first start Windows may ask about the firewall (“Allow access”) — press “Allow” for the private network. The till never creates firewall rules itself.",
                "İlk açılışta Windows güvenlik duvarını sorabilir («Erişime izin ver») — özel ağ için «İzin ver» düğmesine basın. Kasa güvenlik duvarı kuralı oluşturmaz.",
                "Birinchi yoqishda Windows brandmauer haqida so'rashi mumkin («Ruxsat berish») — xususiy tarmoq uchun «Ruxsat berish» tugmasini bosing. Kassa brandmauer qoidalarini o'zi yaratmaydi.")
            : "";
        _note.IsVisible = _note.Text.Length > 0;
        stack.Children.Add(_note);
        Child = stack;

        _toggle.IsCheckedChanged += (_, _) => OnToggle();

        void OnState() => Dispatcher.UIThread.Post(Refresh);
        AttachedToVisualTree += (_, _) =>
        {
            PhoneLinkHost.StateChanged -= OnState;
            PhoneLinkHost.StateChanged += OnState;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => PhoneLinkHost.StateChanged -= OnState;
        Refresh();
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private void OnToggle()
    {
        if (_suppress)
            return;
        var on = _toggle.IsChecked == true;
        var done = PhoneLinkHost.SetEnabled(on, out var error);
        Refresh();
        if (!done)
        {
            _state.Text = error ?? "";
            Tint(_state, "BrushDanger");
        }
    }

    private async System.Threading.Tasks.Task OpenPairWindowAsync()
    {
        if (!PhoneLinkHost.IsListening || !PhoneLinkHost.CanManage())
            return;
        var window = new PhoneLinkPairWindow();
        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner).ConfigureAwait(true);
        else
            window.Show();
        Refresh();
    }

    private void Tint(TextBlock block, string brush) =>
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));

    /// <summary>Привести вид к текущему состоянию кассы (включено ли, открыт ли порт, сколько телефонов, есть ли право).</summary>
    private void Refresh()
    {
        var canManage = PhoneLinkHost.CanManage();
        var enabled = PhoneLinkHost.Pairing.Enabled;
        _suppress = true;
        try
        {
            _toggle.IsChecked = enabled;
        }
        finally
        {
            _suppress = false;
        }
        _toggle.IsEnabled = canManage;
        _connect.IsEnabled = canManage && PhoneLinkHost.IsListening;

        if (!enabled)
        {
            _state.Text = canManage
                ? T("Выключено: порт закрыт, телефоны не могут передавать товары.", "Өчүк: порт жабык, телефондор товар жөнөтө алышпайт.",
                    "Off: the port is closed, phones cannot send goods.", "Kapalı: bağlantı noktası kapalı, telefonlar ürün gönderemez.", "O'chiq: port yopiq, telefonlar tovar yubora olmaydi.")
                : T("Выключено. Включить может владелец или администратор.", "Өчүк. Ээси же администратор күйгүзө алат.",
                    "Off. The owner or an administrator can turn it on.", "Kapalı. Sahip veya yönetici açabilir.", "O'chiq. Egasi yoki administrator yoqa oladi.");
            Tint(_state, "BrushTextSoft");
        }
        else if (PhoneLinkHost.IsListening)
        {
            _state.Text = T($"Включено: касса принимает телефоны (порт {PhoneLinkHost.Port}). Принято сканов с запуска: {PhoneLinkHost.ScansAccepted}.",
                $"Күйгүзүлгөн: касса телефондорду кабыл алат (порт {PhoneLinkHost.Port}). Иштетүүдөн бери кабыл алынган сканерлөө: {PhoneLinkHost.ScansAccepted}.",
                $"On: the till accepts phones (port {PhoneLinkHost.Port}). Scans accepted since start: {PhoneLinkHost.ScansAccepted}.",
                $"Açık: kasa telefonları kabul ediyor (port {PhoneLinkHost.Port}). Başlangıçtan beri kabul edilen tarama: {PhoneLinkHost.ScansAccepted}.",
                $"Yoqilgan: kassa telefonlarni qabul qiladi (port {PhoneLinkHost.Port}). Ishga tushgandan beri qabul qilingan skanerlash: {PhoneLinkHost.ScansAccepted}.");
            Tint(_state, "BrushSuccess");
        }
        else
        {
            _state.Text = PhoneLinkHost.LastError
                          ?? T("Включено, но порт ещё не открыт (откройте главное окно кассы).", "Күйгүзүлгөн, бирок порт азыр ачыла элек (кассанын башкы терезесин ачыңыз).",
                              "On, but the port is not open yet (open the till main window).", "Açık, ancak bağlantı noktası henüz açılmadı (kasa ana penceresini açın).",
                              "Yoqilgan, lekin port hali ochilmagan (kassaning asosiy oynasini oching).");
            Tint(_state, "BrushDanger");
        }

        var phones = PhoneLinkHost.Pairing.Phones();
        _phones.Text = phones.Count == 0
            ? T("Подключённых телефонов нет.", "Туташтырылган телефон жок.", "No phones connected.", "Bağlı telefon yok.", "Ulangan telefon yo'q.")
            : T("Подключены: ", "Туташтырылган: ", "Connected: ", "Bağlı: ", "Ulangan: ") + string.Join(", ", phones.Select(p => p.Name));

        _phoneRows.Children.Clear();
        if (!canManage)
            return;
        foreach (var phone in phones)
        {
            var label = new TextBlock { Text = phone.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var disconnect = new Button
            {
                Content = T("Отключить", "Ажыратуу", "Disconnect", "Bağlantıyı kes", "Uzish"),
                MinHeight = 44, Padding = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Avalonia.Automation.AutomationProperties.SetName(disconnect,
                T("Отключить телефон ", "Телефонду ажыратуу ", "Disconnect phone ", "Telefonun bağlantısını kes ", "Telefonni uzish ") + phone.Name);
            var id = phone.Id;
            disconnect.Click += (_, _) =>
            {
                PhoneLinkHost.Revoke(id);
                Refresh();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(disconnect, 1);
            row.Children.Add(label);
            row.Children.Add(disconnect);
            _phoneRows.Children.Add(row);
        }
    }
}
