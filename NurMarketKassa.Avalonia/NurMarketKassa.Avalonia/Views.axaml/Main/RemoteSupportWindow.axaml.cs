using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Тех поддержка NurCRM» (этап 4 бэклога «Доработки»). 2026-10-16, владелец: сторонняя программа удалённого
/// доступа из кассы убрана — удалённая помощь только своя, «Нур Поддержка» (RemoteSupportWindow.NurSupport.cs,
/// Services/RemoteSupport): кассир получает код и PIN, диктует их оператору по телефону/WhatsApp и сам нажимает
/// «Разрешить» или «Только просмотр». Плюс «Скопировать информацию об устройстве».
/// </summary>
public partial class RemoteSupportWindow : Window, IOwnerSection
{
    public RemoteSupportWindow()
    {
        InitializeComponent();
        // 2026-10-21: вступление своим текстом: в программе владельца — «экран этого компьютера», а не «экран кассы»; без
        // лишнего бренда и без номера (номер — в карточке «Связаться с поддержкой») (пакет 3, п. 143).
        ApplyIntroText();
        Tr.LanguageChanged += OnIntroLanguageChanged;
        Closed += (_, _) => Tr.LanguageChanged -= OnIntroLanguageChanged;
#if !NURANDROID
        // 2026-10-16: блок «Нур Поддержка» — единственный способ удалённой помощи, есть всегда (на Windows).
        BuildNurSupport();
#endif
        // 2026-10-05: на Android (2026-10-16: и на Linux) своей удалённой помощи нет — главная кнопка окна
        // «Скопировать информацию об устройстве»; вступление про «Нур Поддержку» там не к месту.
        if (!OperatingSystem.IsWindows())
        {
            IntroText.IsVisible = false;
            CopyDeviceInfoButton.Classes.Set("SecondaryActionButton", false);
            CopyDeviceInfoButton.Classes.Set("PrimaryActionButton", true);
            StatusText.Text = Tr.T(
                "Нажмите «Скопировать информацию об устройстве» и отправьте текст в поддержку NurMarket (Telegram, WhatsApp) — по нему программу подстроят под ваш аппарат. Удалённое подключение «Нур Поддержка» работает на кассе с Windows.",
                "«Түзмөк жөнүндө маалыматты көчүрүү» баскычын басып, текстти NurMarket колдоосуна жибериңиз (Telegram, WhatsApp) — ал боюнча программа аппаратыңызга ылайыкталат. «Нур Колдоо» аркылуу алыстан туташуу Windows'тогу кассада иштейт.",
                "Tap “Copy device information” and send the text to NurMarket support (Telegram, WhatsApp) — it helps adapt the app to your device. Remote connection via “Nur Support” works on Windows tills.",
                "«Cihaz bilgilerini kopyala»ya dokunun ve metni NurMarket desteğine gönderin (Telegram, WhatsApp) — program cihazınıza göre uyarlanır. «Nur Destek» ile uzaktan bağlantı Windows kasalarında çalışır.",
                "«Qurilma ma'lumotlarini nusxalash»ni bosing va matnni NurMarket qo'llab-quvvatlash xizmatiga yuboring (Telegram, WhatsApp) — dastur qurilmangizga moslashtiriladi. «Nur Yordam» orqali masofaviy ulanish Windows'dagi kassada ishlaydi.");
        }
        WrapInModalShell();
    }

    private Dialogs.ModalShell? _shell;

    /// <summary>2026-10-22 (модалки, пакет 2): в кассе окно — общая модалка (ModalShell 512, без рамки Windows и без «рамки»
    /// другого цвета вокруг содержимого): шапка с ✕, «Закрыть» (Esc) справа в подвале, «Скопировать информацию об устройстве» —
    /// слева (на Android/Linux — главная справа). У владельца окно — раздел: <see cref="AsOwnerSection"/> возвращает прежнюю
    /// раскладку.</summary>
    private void WrapInModalShell()
    {
        var primaryCopy = CopyDeviceInfoButton.Classes.Contains("PrimaryActionButton");
        SectionCards.AsModal(this, 512);
        ClearValue(HeightProperty);
        Content = null;
        ButtonsPanel.Children.Clear();
        RootGrid.Children.Remove(ButtonsPanel);
        TitlePanel.IsVisible = false;
        IntroText.Margin = new Avalonia.Thickness(0);
        RootGrid.Margin = new Avalonia.Thickness(0);
        RootGrid.Background = Avalonia.Media.Brushes.Transparent;
        CopyDeviceInfoButton.Margin = new Avalonia.Thickness(0);
        CloseButton.Margin = new Avalonia.Thickness(0);
        CopyDeviceInfoButton.Classes.Set("SecondaryActionButton", false);
        CopyDeviceInfoButton.Classes.Set("PrimaryActionButton", false);
        CopyDeviceInfoButton.Classes.Set(primaryCopy ? "modal-primary" : "modal-secondary", true);
        CloseButton.Classes.Set("SecondaryActionButton", false);
        CloseButton.Classes.Set("modal-cancel", true);
        _shell = new Dialogs.ModalShell
        {
            Margin = new Avalonia.Thickness(16),
            ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = RootGrid,
        };
        _shell.Bind(Dialogs.ModalShell.TitleProperty, this.GetResourceObservable("csh.menu.remoteSupport"));
        if (primaryCopy)
            _shell.Footer = SectionCards.Buttons(CloseButton, CopyDeviceInfoButton);
        else
        {
            _shell.Hint = CopyDeviceInfoButton;
            _shell.Footer = SectionCards.Buttons(CloseButton);
        }
        Content = _shell;
    }

    /// <summary>2026-10-22 (модалки, пакет 2): раздел владельца — без оболочки модалки, кнопки на прежнем месте.</summary>
    private void UnwrapModalShell()
    {
        if (_shell is null)
            return;
        _shell.Footer = null;
        _shell.Hint = null;
        _shell.Content = null;
        _shell = null;
        ClearValue(BackgroundProperty);
        ClearValue(TransparencyLevelHintProperty);
        CopyDeviceInfoButton.Classes.Set("modal-primary", false);
        CopyDeviceInfoButton.Classes.Set("modal-secondary", false);
        CopyDeviceInfoButton.Classes.Set(OperatingSystem.IsWindows() ? "SecondaryActionButton" : "PrimaryActionButton", true);
        CopyDeviceInfoButton.Margin = new Avalonia.Thickness(0, 0, 12, 8);
        CloseButton.Classes.Set("modal-cancel", false);
        CloseButton.Classes.Set("SecondaryActionButton", true);
        CloseButton.Margin = new Avalonia.Thickness(0, 0, 0, 8);
        if (CopyDeviceInfoButton.Parent is Panel p1)
            p1.Children.Remove(CopyDeviceInfoButton);
        if (CloseButton.Parent is Panel p2)
            p2.Children.Remove(CloseButton);
        ButtonsPanel.Children.Add(CopyDeviceInfoButton);
        ButtonsPanel.Children.Add(CloseButton);
        Grid.SetRow(ButtonsPanel, 3);
        RootGrid.Children.Add(ButtonsPanel);
        TitlePanel.IsVisible = true;
        Content = RootGrid;
    }

    private void OnIntroLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyIntroText);

    /// <summary>2026-10-21: вступление окна «Тех. поддержка» (пакет 3, п. 143).</summary>
    private void ApplyIntroText() =>
        IntroText.Text = AppMode.IsOwner
            ? Tr.T(
                "Удалённая помощь «Нур Поддержка»: оператор видит экран этого компьютера и помогает разобраться. Позвоните в поддержку или напишите в WhatsApp, нажмите «Получить код для поддержки» и продиктуйте оператору код и PIN.",
                "Алыстан жардам «Нур Колдоо»: оператор бул компьютердин экранын көрүп, түшүнүүгө жардам берет. Колдоо кызматына чалыңыз же WhatsApp'ка жазыңыз, «Колдоо үчүн код алуу» баскычын басып, операторго код менен PIN'ди айтып бериңиз.",
                "Remote help “Nur Support”: the operator sees this computer's screen and helps you sort things out. Call support or message on WhatsApp, press “Get a code for support” and read the code and PIN to the operator.",
                "Uzaktan yardım «Nur Destek»: operatör bu bilgisayarın ekranını görür ve sorunu çözmenize yardım eder. Desteği arayın veya WhatsApp'tan yazın, «Destek için kod al» düğmesine basın ve kodu ile PIN'i operatöre söyleyin.",
                "Masofaviy yordam «Nur Yordam»: operator ushbu kompyuter ekranini ko'radi va muammoni hal qilishga yordam beradi. Yordam xizmatiga qo'ng'iroq qiling yoki WhatsApp'ga yozing, «Yordam uchun kod olish» tugmasini bosing va operatorga kod hamda PIN'ni ayting.")
            : Tr.T(
                "Удалённая помощь «Нур Поддержка»: оператор видит экран кассы и помогает разобраться. Позвоните в поддержку или напишите в WhatsApp, нажмите «Получить код для поддержки» и продиктуйте оператору код и PIN.",
                "Алыстан жардам «Нур Колдоо»: оператор кассанын экранын көрүп, түшүнүүгө жардам берет. Колдоо кызматына чалыңыз же WhatsApp'ка жазыңыз, «Колдоо үчүн код алуу» баскычын басып, операторго код менен PIN'ди айтып бериңиз.",
                "Remote help “Nur Support”: the operator sees the till screen and helps you sort things out. Call support or message on WhatsApp, press “Get a code for support” and read the code and PIN to the operator.",
                "Uzaktan yardım «Nur Destek»: operatör kasa ekranını görür ve sorunu çözmenize yardım eder. Desteği arayın veya WhatsApp'tan yazın, «Destek için kod al» düğmesine basın ve kodu ile PIN'i operatöre söyleyin.",
                "Masofaviy yordam «Nur Yordam»: operator kassa ekranini ko'radi va muammoni hal qilishga yordam beradi. Yordam xizmatiga qo'ng'iroq qiling yoki WhatsApp'ga yozing, «Yordam uchun kod olish» tugmasini bosing va operatorga kod hamda PIN'ni ayting.");

    /// <summary>2026-10-05, владелец: «скопировать информацию об устройстве — в буфер обмена».</summary>
    private async void CopyDeviceInfo_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = DeviceInfoReport.Build(this);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                throw new InvalidOperationException("буфер обмена недоступен");
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            PosLogger.Log("Поддержка: информация об устройстве скопирована:\n" + text, "INFO");
            StatusText.Text = Tr.T(
                "Скопировано. Вставьте текст в чат поддержки NurMarket (долгое нажатие → «Вставить»).",
                "Көчүрүлдү. Текстти NurMarket колдоо чатына чаптаңыз (узак басуу → «Чаптоо»).",
                "Copied. Paste the text into the NurMarket support chat (long press → “Paste”).",
                "Kopyalandı. Metni NurMarket destek sohbetine yapıştırın (uzun basın → «Yapıştır»).",
                "Nusxalandi. Matnni NurMarket qo'llab-quvvatlash chatiga joylashtiring (uzoq bosing → «Joylashtirish»).");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: информацию об устройстве скопировать не удалось: {ex.Message}", "WARNING");
            StatusText.Text = Tr.T(
                $"Скопировать не удалось: {ex.Message}", $"Көчүрүү мүмкүн болгон жок: {ex.Message}",
                $"Could not copy: {ex.Message}", $"Kopyalanamadı: {ex.Message}", $"Nusxalab bo'lmadi: {ex.Message}");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): значок с названием и
    /// «Закрыть» убраны — название над разделом. Окно рассчитано на 520 px; растянутое на весь
    /// раздел, оно разнесло бы текст и кнопку по краям экрана, поэтому содержимое остаётся колонкой
    /// привычной ширины у левого края, как текст остальных разделов.</summary>
    public void AsOwnerSection()
    {
        UnwrapModalShell();
        TitlePanel.IsVisible = false;
        CloseButton.IsVisible = false;
        IntroText.Margin = new Avalonia.Thickness(0);
        // Без белой подложки: в кассе она заполняла всё маленькое окно, а колонкой посреди
        // раздела выглядела бы карточкой, прилипшей к тексту.
        RootGrid.Background = Avalonia.Media.Brushes.Transparent;
        RootGrid.Margin = OwnerSectionLayout.Margin;
        RootGrid.MaxWidth = 600;
        RootGrid.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
#if !NURANDROID
        // 2026-10-21: контакты поддержки — справа от «Нур Поддержки» (на Windows) (пакет 3, п. 143).
        PlaceNurContactsBeside();
#endif
    }
}
