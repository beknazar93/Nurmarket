using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-29, владелец: «ничего не должно остаться от старого аккаунта». Окно при смене компании, когда от прежней остались
/// неотправленные продажи, внесения/изъятия, приходы в фоне, отложенные чеки или оплаты долга (AccountDataIsolation.SwitchTo).
/// Три выхода: вернуться в прежний аккаунт (данные уйдут при его входе), выгрузить в файл и сменить, сменить БЕЗ отправки
/// (двойное подтверждение: ввод слова). Отправить «прямо сейчас» здесь нельзя — вход новой компании уже выполнен, и отправка ушла бы
/// от её имени. Окно разделено на два шага: выбор → (для потери) ввод слова.</summary>
public sealed class UnsentDataDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Слова подтверждения потери (подходит любое, регистр не важен).</summary>
    private static string[] ConfirmWords => ["УДАЛИТЬ", T("УДАЛИТЬ", "ӨЧҮРҮҮ", "DELETE", "SİL", "O'CHIRISH")];

    private UnsentDecision _result = UnsentDecision.Cancel;
    private readonly StackPanel _step1 = new() { Spacing = 10 };
    private readonly StackPanel _step2 = new() { Spacing = 10, IsVisible = false };
    private readonly ModalShell _shell;

    private UnsentDataDialog(UnsentSummary summary)
    {
        Title = T("Есть неотправленные данные", "Жөнөтүлбөгөн маалыматтар бар", "There is unsent data", "Gönderilmemiş veri var", "Yuborilmagan ma'lumotlar bor");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SystemDecorations = SystemDecorations.None;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Finish(UnsentDecision.Cancel); } };

        var info = new TextBlock { Text = Describe(summary), TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 14, 0) };
        info.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var hint = new TextBlock
        {
            Text = T("Вы вошли в другую компанию, и данные прежней будут стёрты с этого компьютера. Отправить их с этого входа нельзя — они ушли бы не в ту компанию. Выберите, что сделать:",
                "Сиз башка компанияга кирдиңиз, мурункусунун маалыматтары бул компьютерден өчүрүлөт. Бул кирүү менен жөнөтүүгө болбойт — алар туура эмес компанияга кетет. Тандаңыз:",
                "You have signed in to a different company, and the data of the previous one will be erased from this computer. It cannot be sent from this sign-in — it would go to the wrong company. Choose what to do:",
                "Başka bir şirkete giriş yaptınız; önceki şirketin verileri bu bilgisayardan silinecek. Bu girişten gönderilemez — yanlış şirkete gider. Ne yapacağınızı seçin:",
                "Siz boshqa kompaniyaga kirdingiz, avvalgisining ma'lumotlari bu kompyuterdan o'chiriladi. Bu kirish orqali yuborib bo'lmaydi — noto'g'ri kompaniyaga ketadi. Tanlang:"),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 14, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        var back = UiKit.Primary(this, T("Вернуться в прежний аккаунт (программа закроется)", "Мурунку аккаунтка кайтуу (программа жабылат)",
            "Go back to the previous account (the program will close)", "Önceki hesaba dön (program kapanır)", "Avvalgi akkauntga qaytish (dastur yopiladi)"), isDefault: true, isCancel: true);
        back.Click += (_, _) => Finish(UnsentDecision.Cancel);
        var export = UiKit.Ghost(this, T("Выгрузить в файл и сменить", "Файлга сактап, алмаштыруу", "Save to a file and switch", "Dosyaya kaydet ve değiştir", "Faylga saqlab, almashtirish"));
        export.Click += (_, _) => Finish(UnsentDecision.ExportAndSwitch);
        var discard = UiKit.Danger(this, T("Сменить БЕЗ отправки — всё будет ПОТЕРЯНО", "Жөнөтпөй алмаштыруу — баары ЖОГОЛОТ",
            "Switch WITHOUT sending — everything will be LOST", "Göndermeden değiştir — hepsi KAYBOLACAK", "Yubormay almashtirish — hammasi YO'QOLADI"));
        discard.Click += (_, _) => { _step1.IsVisible = false; _step2.IsVisible = true; };
        foreach (var b in new[] { back, export, discard })
            b.HorizontalAlignment = HorizontalAlignment.Stretch;

        _step1.Children.Add(info);
        _step1.Children.Add(hint);
        _step1.Children.Add(back);
        _step1.Children.Add(export);
        _step1.Children.Add(discard);

        var warn = new TextBlock
        {
            Text = T("Неотправленное будет потеряно навсегда. Если это продажи — деньги за них не попадут в учёт. Чтобы подтвердить, введите слово УДАЛИТЬ.",
                "Жөнөтүлбөгөн маалымат биротоло жоголот. Бул сатуулар болсо — алардын акчасы эсепке кирбейт. Ырастоо үчүн ӨЧҮРҮҮ сөзүн жазыңыз.",
                "Unsent data will be lost forever. If these are sales, their money will not be recorded. To confirm, type the word DELETE.",
                "Gönderilmemiş veri sonsuza dek kaybolur. Satışlarsa, paraları kayda geçmez. Onaylamak için SİL kelimesini yazın.",
                "Yuborilmagan ma'lumotlar butunlay yo'qoladi. Agar bu sotuvlar bo'lsa — ularning puli hisobga kirmaydi. Tasdiqlash uchun O'CHIRISH so'zini yozing."),
            TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 14, 0),
        };
        warn.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        var input = new TextBox { Watermark = ConfirmWords[1], FontSize = 16, Height = 46 };
        var sure = UiKit.Danger(this, T("Стереть и сменить аккаунт", "Өчүрүп, аккаунтту алмаштыруу", "Erase and switch account", "Sil ve hesabı değiştir", "O'chirib, akkauntni almashtirish"));
        sure.IsEnabled = false;
        sure.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.TextChanged += (_, _) =>
            sure.IsEnabled = ConfirmWords.Any(w => string.Equals(w, (input.Text ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase));
        sure.Click += (_, _) => Finish(UnsentDecision.DiscardAndSwitch);
        var cancel2 = UiKit.Ghost(this, T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        cancel2.HorizontalAlignment = HorizontalAlignment.Stretch;
        cancel2.Click += (_, _) => { input.Text = ""; _step2.IsVisible = false; _step1.IsVisible = true; };
        _step2.Children.Add(warn);
        _step2.Children.Add(input);
        _step2.Children.Add(sure);
        _step2.Children.Add(cancel2);

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(_step1);
        content.Children.Add(_step2);
        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = Title,
            ShowClose = false,
            Content = content,
        };
        Content = _shell;
        Opened += (_, _) => back.Focus();
    }

    private void Finish(UnsentDecision decision)
    {
        _result = decision;
        Close();
    }

    private static string Describe(UnsentSummary s)
    {
        var parts = new List<string>();
        if (s.Sales > 0)
            parts.Add(T($"продаж: {s.Sales} на {MoneyText.Som((double)s.SalesTotal)}", $"сатуулар: {s.Sales}, {MoneyText.Som((double)s.SalesTotal)}", $"sales: {s.Sales} for {MoneyText.Som((double)s.SalesTotal)}",
                $"satış: {s.Sales}, {MoneyText.Som((double)s.SalesTotal)}", $"sotuvlar: {s.Sales}, {MoneyText.Som((double)s.SalesTotal)}"));
        if (s.CashOperations > 0)
            parts.Add(T($"внесений и изъятий: {s.CashOperations}", $"киргизүү жана алуулар: {s.CashOperations}", $"cash deposits and withdrawals: {s.CashOperations}", $"para girişi ve çıkışı: {s.CashOperations}", $"kiritish va yechib olishlar: {s.CashOperations}"));
        if (s.ReceivingJobs > 0)
            parts.Add(T($"приходов в фоне: {s.ReceivingJobs}", $"фондогу кирүүлөр: {s.ReceivingJobs}", $"background receivings: {s.ReceivingJobs}", $"arka plandaki mal kabulleri: {s.ReceivingJobs}", $"fondagi kirimlar: {s.ReceivingJobs}"));
        if (s.DeferredCarts > 0)
            parts.Add(T($"отложенных чеков: {s.DeferredCarts}", $"токтотулган чектер: {s.DeferredCarts}", $"parked receipts: {s.DeferredCarts}", $"bekletilen fişler: {s.DeferredCarts}", $"kechiktirilgan cheklar: {s.DeferredCarts}"));
        if (s.DebtOperations > 0)
            parts.Add(T($"неподтверждённых операций по долгам: {s.DebtOperations}", $"тастыкталбаган карыз операциялары: {s.DebtOperations}", $"unconfirmed debt operations: {s.DebtOperations}", $"onaylanmamış borç işlemleri: {s.DebtOperations}", $"tasdiqlanmagan qarz amallari: {s.DebtOperations}"));
        return T("Не отправлено на сервер: ", "Сервердик жөнөтүлө элек: ", "Not sent to the server: ", "Sunucuya gönderilmedi: ", "Serverga yuborilmagan: ") + string.Join("; ", parts) + ".";
    }

    /// <summary>Показывает окно из любого потока (смена компании приходит из сетевого потока) и ждёт выбор. «Вернуться» дополнительно
    /// стирает вход только что вошедшего (иначе кассa продолжила бы работать на данных прежней компании под чужим входом) и закрывает программу.</summary>
    public static UnsentDecision Ask(UnsentSummary summary)
    {
        UnsentDecision Show()
        {
            var dialog = new UnsentDataDialog(summary);
            PosDialogHost.Show(dialog, ActiveWindow());
            return dialog._result;
        }

        var decision = Dispatcher.UIThread.CheckAccess()
            ? Show()
            : Dispatcher.UIThread.InvokeAsync(Show).GetAwaiter().GetResult();

        if (decision == UnsentDecision.Cancel)
            AbortSignIn();
        return decision;
    }

    /// <summary>Файл «неотправленное» записан — сказать, где он лежит (не блокирует: окно покажется после смены).</summary>
    public static void ShowExported(string path)
    {
        Dispatcher.UIThread.Post(() => _ = PosAlertDialog.ShowAsync(ActiveWindow(),
            T("Неотправленное сохранено в файл", "Жөнөтүлбөгөн маалымат файлга сакталды", "Unsent data saved to a file", "Gönderilmemiş veriler dosyaya kaydedildi", "Yuborilmagan ma'lumotlar faylga saqlandi"),
            T($"Файл: {path}\n\nИз кассы эти данные стёрты и сами не отправятся. Сохраните файл и передайте его в поддержку.",
                $"Файл: {path}\n\nКассадан бул маалыматтар өчүрүлдү жана өзү жөнөтүлбөйт. Файлды сактап, колдоого берңиз.",
                $"File: {path}\n\nThis data has been erased from the till and will not be sent automatically. Keep the file and give it to support.",
                $"Dosya: {path}\n\nBu veriler kasadan silindi ve kendiliğinden gönderilmez. Dosyayı saklayın ve desteğe verin.",
                $"Fayl: {path}\n\nBu ma'lumotlar kassadan o'chirildi va o'zi yuborilmaydi. Faylni saqlab, qo'llab-quvvatlashga bering."),
            PosAlertKind.Warning));
    }

    private static Window? ActiveWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.LastOrDefault(w => w.IsVisible);

    /// <summary>Выход из только что выполненного входа: токен и сохранённая сессия стираются, программа закрывается — при следующем
    /// запуске пользователь войдёт в прежний аккаунт и неотправленное уйдёт на сервер как обычно.</summary>
    private static void AbortSignIn()
    {
        try
        {
            App.AuthApi.ClearSession();
            Task.Run(async () =>
            {
                await App.GetRequiredService<NurMarketKassa.Core.Contracts.IAuthSessionManager>().ClearSessionAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
            OfflineAuthSessionStore.Clear();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смена аккаунта: вход не стёрт при отказе ({ex.Message}).", "ERROR");
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }
}
