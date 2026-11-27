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
        info.Bind(TextBlock.ForegroundProperty, info.GetResourceObservable("BrushText"));
        var hint = new TextBlock
        {
            Text = T("Вы вошли в другую компанию, и данные прежней будут стёрты с этого компьютера. Отправить их с этого входа нельзя — они ушли бы не в ту компанию. Выберите, что сделать:",
                "Сиз башка компанияга кирдиңиз, мурункусунун маалыматтары бул компьютерден өчүрүлөт. Бул кирүү менен жөнөтүүгө болбойт — алар туура эмес компанияга кетет. Тандаңыз:",
                "You have signed in to a different company, and the data of the previous one will be erased from this computer. It cannot be sent from this sign-in — it would go to the wrong company. Choose what to do:",
                "Başka bir şirkete giriş yaptınız; önceki şirketin verileri bu bilgisayardan silinecek. Bu girişten gönderilemez — yanlış şirkete gider. Ne yapacağınızı seçin:",
                "Siz boshqa kompaniyaga kirdingiz, avvalgisining ma'lumotlari bu kompyuterdan o'chiriladi. Bu kirish orqali yuborib bo'lmaydi — noto'g'ri kompaniyaga ketadi. Tanlang:"),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 14, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));

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
        warn.Bind(TextBlock.ForegroundProperty, warn.GetResourceObservable("BrushDanger"));
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
        // 2026-11-05 (Л4-05): оплата с неизвестным исходом, очередь бонусов и данные Telegram тоже не стираем молча.
        if (s.UnknownCheckouts > 0)
            parts.Add(T($"оплата с неизвестным исходом (возможно, продажа проведена): {s.UnknownCheckouts}", $"натыйжасы белгисиз төлөм (сатуу өтүп кетиши мүмкүн): {s.UnknownCheckouts}", $"payment with unknown outcome (the sale may have gone through): {s.UnknownCheckouts}",
                $"sonucu belirsiz ödeme (satış geçmiş olabilir): {s.UnknownCheckouts}", $"natijasi noma'lum to'lov (sotuv o'tgan bo'lishi mumkin): {s.UnknownCheckouts}"));
        if (s.BonusQueue > 0)
            parts.Add(T($"бонусов в очереди: {s.BonusQueue}", $"кезектеги бонустар: {s.BonusQueue}", $"bonuses in the queue: {s.BonusQueue}", $"kuyruktaki bonuslar: {s.BonusQueue}", $"navbatdagi bonuslar: {s.BonusQueue}"));
        if (s.TelegramItems > 0)
            parts.Add(T($"обращений и клиентов Telegram: {s.TelegramItems}", $"Telegram кайрылуулары жана кардарлары: {s.TelegramItems}", $"Telegram inquiries and customers: {s.TelegramItems}", $"Telegram talepleri ve müşterileri: {s.TelegramItems}", $"Telegram murojaatlari va mijozlari: {s.TelegramItems}"));
        if (s.PendingProducts > 0) // 2026-11-06: новые товары, заведённые без интернета
            parts.Add(T($"новых товаров, не созданных на сервере: {s.PendingProducts}", $"серверде түзүлө элек жаңы товарлар: {s.PendingProducts}", $"new products not created on the server: {s.PendingProducts}",
                $"sunucuda oluşturulmamış yeni ürünler: {s.PendingProducts}", $"serverda yaratilmagan yangi mahsulotlar: {s.PendingProducts}"));
        return T("Не отправлено на сервер: ","Сервердик жөнөтүлө элек: ", "Not sent to the server: ", "Sunucuya gönderilmedi: ", "Serverga yuborilmagan: ") + string.Join("; ", parts) + ".";
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

        // 2026-11-06: В3 Л4-25 — проверено: в потоке окон (CheckAccess) окно показывается напрямую, без ожидания; ожидание GetResult() ниже выполняется ТОЛЬКО в фоновом потоке
        // (смена компании пришла из сети, а AccountDataIsolation.SwitchTo — синхронный метод и обязан дождаться ответа пользователя), окно кассы оно не замораживает.
        var decision = Dispatcher.UIThread.CheckAccess()
            ? Show()
            : Dispatcher.UIThread.InvokeAsync(Show).GetAwaiter().GetResult();

        if (decision == UnsentDecision.Cancel)
            AbortSignIn();
        return decision;
    }

    /// <summary>2026-11-05 (Л4-01): вопрос при смене компании, когда неотправленного нет. «Нет» — выход из только что выполненного входа.</summary>
    public static bool ConfirmCompanyChange(string previousKey, string newKey)
    {
        bool Show() => PosConfirmDialog.Show(ActiveWindow(),
            T("Вход в другую компанию", "Башка компанияга кирүү", "Signing in to a different company", "Başka bir şirkete giriş", "Boshqa kompaniyaga kirish"),
            T($"Вы входите в другую компанию ({newKey}). Данные прежней ({previousKey}) на этом компьютере будут убраны: копия хранится 14 дней, потом удалится. Продолжить?",
                $"Сиз башка компанияга кирип жатасыз ({newKey}). Мурункусунун ({previousKey}) маалыматтары бул компьютерден алынат: көчүрмөсү 14 күн сакталып, андан кийин өчөт. Улантасызбы?",
                $"You are signing in to a different company ({newKey}). The data of the previous one ({previousKey}) will be removed from this computer: a copy is kept for 14 days, then deleted. Continue?",
                $"Başka bir şirkete ({newKey}) giriş yapıyorsunuz. Önceki şirketin ({previousKey}) verileri bu bilgisayardan kaldırılacak: kopya 14 gün saklanır, sonra silinir. Devam edilsin mi?",
                $"Siz boshqa kompaniyaga ({newKey}) kiryapsiz. Avvalgisining ({previousKey}) ma'lumotlari bu kompyuterdan olib tashlanadi: nusxasi 14 kun saqlanadi, keyin o'chadi. Davom etasizmi?"),
            accent: PosConfirmAccent.Danger);

        var ok = Dispatcher.UIThread.CheckAccess()
            ? Show()
            : Dispatcher.UIThread.InvokeAsync(Show).GetAwaiter().GetResult();
        if (!ok)
            AbortSignIn();
        return ok;
    }

    /// <summary>2026-11-05 (Л4-04): данные прежней компании убрать не удалось (файл занят) — всё возвращено на место, вход прерываем.
    /// 2026-11-05 (ревью волн 0–3, п. 7): сообщение показываем ДО закрытия программы и ждём, пока его закроют, — раньше окно появлялось и сразу закрывалось вместе с кассой.
    /// 2026-11-27 (жалоба клиента: «…(не удалось убрать: data (Access to the path 'C:\Users\admin\AppData\Roaming\NurMarketKassa\data' is denied.))…» и тупик
    /// «войдите снова»): технические подробности (пути, текст ОС) — только в журнал; пользователю — кто держит данные и одна главная кнопка «Повторить»
    /// (держит программа владельца — «Закрыть программу владельца и повторить», касса закроет её штатно). Возвращает true = повторить; «Отмена» — выход из входа.
    /// Окно синхронное, как «Вход в другую компанию» (PosConfirmDialog.Show, свой цикл окна) — ответ нужен до продолжения смены.</summary>
    public static bool ShowSwitchFailed(AccountSwitchFailure failure)
    {
        var title = T("Не удалось сменить компанию", "Компанияны алмаштыруу мүмкүн болгон жок", "Could not switch company", "Şirket değiştirilemedi", "Kompaniyani almashtirib bo'lmadi");
        if (failure.CanRetry)
        {
            var message = SwitchBlockedMessage(failure);
            var confirm = failure.HeldByOwnerProgram
                ? T("Закрыть программу владельца и повторить", "Ээси программасын жабып, кайталоо", "Close the owner program and retry", "Sahip programını kapat ve tekrar dene", "Egasi dasturini yopib, qayta urinish")
                : T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
            var cancel = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
            bool Ask() => PosConfirmDialog.Show(ActiveWindow(), title, message, confirm, cancel);
            bool retry;
            try
            {
                retry = Dispatcher.UIThread.CheckAccess() ? Ask() : Dispatcher.UIThread.InvokeAsync(Ask).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Смена аккаунта: вопрос «Повторить» не показан ({ex.Message}).", "WARNING");
                retry = false;
            }
            if (!retry)
                AbortSignIn();
            return retry;
        }

        async Task ShowAndWaitAsync() => await PosAlertDialog.ShowAsync(ActiveWindow(), title,
            T("Данные прежней компании не удалось убрать. Ничего не потеряно: касса осталась в прежней компании. Перезапустите кассу и войдите снова. Подробности — в журнале кассы.",
                "Мурунку компаниянын маалыматтарын алып салуу мүмкүн болгон жок. Эч нерсе жоголгон жок: касса мурунку компанияда калды. Кассаны кайра иштетип, кайра кириңиз. Чоо-жайы — кассанын журналында.",
                "The previous company's data could not be removed. Nothing was lost: the till stayed in the previous company. Restart the till and sign in again. Details are in the till log.",
                "Önceki şirketin verileri kaldırılamadı. Hiçbir şey kaybolmadı: kasa önceki şirkette kaldı. Kasayı yeniden başlatıp tekrar girin. Ayrıntılar kasa günlüğünde.",
                "Avvalgi kompaniya ma'lumotlarini olib tashlab bo'lmadi. Hech narsa yo'qolmadi: kassa avvalgi kompaniyada qoldi. Kassani qayta ishga tushirib, qayta kiring. Tafsilotlar — kassa jurnalida."),
            PosAlertKind.Warning).ConfigureAwait(true);

        if (Dispatcher.UIThread.CheckAccess())
        {
            // Мы в потоке окон: ждать здесь нельзя (повисло бы окно) — выходим из входа, когда сообщение закроют.
            Dispatcher.UIThread.Post(async () =>
            {
                try { await ShowAndWaitAsync().ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Смена аккаунта: сообщение о сбое не показано ({ex.Message}).", "WARNING"); }
                AbortSignIn();
            });
            return false;
        }
        try { Dispatcher.UIThread.InvokeAsync(ShowAndWaitAsync).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Смена аккаунта: сообщение о сбое не показано ({ex.Message}).", "WARNING"); }
        AbortSignIn();
        return false;
    }

    /// <summary>2026-11-27: текст окна «Не удалось сменить компанию» без путей и сообщений ОС: кто держит данные и что сделать.</summary>
    public static string SwitchBlockedMessage(AccountSwitchFailure failure)
    {
        string who;
        if (failure.HeldByOwnerProgram)
            who = T("Данные прежней компании сейчас открыты в программе владельца NurCRM на этом компьютере.",
                "Мурунку компаниянын маалыматтары азыр бул компьютердеги NurCRM ээси программасында ачык.",
                "The previous company's data is open in the NurCRM owner program on this computer.",
                "Önceki şirketin verileri şu anda bu bilgisayardaki NurCRM sahip programında açık.",
                "Avvalgi kompaniya ma'lumotlari hozir shu kompyuterdagi NurCRM egasi dasturida ochiq.");
        else if (failure.HeldBySelfOnly)
            who = T("Касса ещё дописывает данные прежней компании.",
                "Касса мурунку компаниянын маалыматтарын али жазып жатат.",
                "The till is still finishing writing the previous company's data.",
                "Kasa önceki şirketin verilerini yazmayı henüz bitirmedi.",
                "Kassa avvalgi kompaniya ma'lumotlarini hali yozib bo'lmadi.");
        else if (failure.OtherProgramNames.Count > 0)
        {
            var names = string.Join(", ", failure.OtherProgramNames.Take(3));
            who = T($"Данные прежней компании сейчас заняты другой программой: {names}. Обычно это антивирус или открытая папка.",
                $"Мурунку компаниянын маалыматтарын азыр башка программа кармап турат: {names}. Көбүнчө бул антивирус же ачык папка.",
                $"The previous company's data is in use by another program: {names}. Usually this is an antivirus or an open folder.",
                $"Önceki şirketin verileri şu anda başka bir program tarafından kullanılıyor: {names}. Genellikle bu bir antivirüs veya açık bir klasördür.",
                $"Avvalgi kompaniya ma'lumotlarini hozir boshqa dastur band qilgan: {names}. Odatda bu antivirus yoki ochiq papka.");
        }
        else
            who = T("Данные прежней компании сейчас заняты другой программой (обычно антивирус или программа владельца NurCRM).",
                "Мурунку компаниянын маалыматтарын азыр башка программа кармап турат (көбүнчө антивирус же NurCRM ээси программасы).",
                "The previous company's data is in use by another program (usually an antivirus or the NurCRM owner program).",
                "Önceki şirketin verileri şu anda başka bir program tarafından kullanılıyor (genellikle antivirüs veya NurCRM sahip programı).",
                "Avvalgi kompaniya ma'lumotlarini hozir boshqa dastur band qilgan (odatda antivirus yoki NurCRM egasi dasturi).");

        var what = failure.HeldByOwnerProgram
            ? T("Нажмите «Закрыть программу владельца и повторить» — касса закроет её и сменит компанию. Потом программу владельца можно открыть снова.",
                "«Ээси программасын жабып, кайталоо» баскычын басыңыз — касса аны жаап, компанияны алмаштырат. Кийин ээси программасын кайра ачсаңыз болот.",
                "Press «Close the owner program and retry» — the till will close it and switch the company. You can open the owner program again afterwards.",
                "«Sahip programını kapat ve tekrar dene» düğmesine basın — kasa onu kapatıp şirketi değiştirecek. Sahip programını sonra yeniden açabilirsiniz.",
                "«Egasi dasturini yopib, qayta urinish» tugmasini bosing — kassa uni yopib, kompaniyani almashtiradi. Keyin egasi dasturini qayta ochish mumkin.")
            : T("Закройте программу владельца NurCRM, если она открыта, подождите несколько секунд и нажмите «Повторить». Если не помогает — перезапустите компьютер и войдите снова.",
                "NurCRM ээси программасы ачык болсо, жабыңыз, бир нече секунд күтүп, «Кайталоо» баскычын басыңыз. Жардам бербесе — компьютерди өчүрүп-күйгүзүп, кайра кириңиз.",
                "Close the NurCRM owner program if it is open, wait a few seconds and press «Retry». If that does not help, restart the computer and sign in again.",
                "NurCRM sahip programı açıksa kapatın, birkaç saniye bekleyin ve «Tekrar dene» düğmesine basın. İşe yaramazsa bilgisayarı yeniden başlatıp tekrar girin.",
                "NurCRM egasi dasturi ochiq bo'lsa, yoping, bir necha soniya kuting va «Qayta urinish» tugmasini bosing. Yordam bermasa — kompyuterni qayta ishga tushirib, qayta kiring.");

        var safe = T("Ничего не потеряно: пока касса работает с прежней компанией. Подробности — в журнале кассы.",
            "Эч нерсе жоголгон жок: азырынча касса мурунку компания менен иштейт. Чоо-жайы — кассанын журналында.",
            "Nothing was lost: for now the till keeps working with the previous company. Details are in the till log.",
            "Hiçbir şey kaybolmadı: kasa şimdilik önceki şirketle çalışıyor. Ayrıntılar kasa günlüğünde.",
            "Hech narsa yo'qolmadi: hozircha kassa avvalgi kompaniya bilan ishlaydi. Tafsilotlar — kassa jurnalida.");
        return who + "\n\n" + what + "\n\n" + safe;
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
        // 2026-11-06 (аудит №5, Л4-25): в потоке окон ждать стирание сессии (GetResult) нельзя — окно «замирало» на время обращения к хранилищу. Здесь синхронная часть стирается сразу,
        // а асинхронная идёт в фоне, и программа закрывается ПОСЛЕ неё. В не-UI потоке (смена компании идёт из сетевого потока) прежнее ожидание остаётся.
        if (Dispatcher.UIThread.CheckAccess())
        {
            try
            {
                App.AuthApi.ClearSession();
                OfflineAuthSessionStore.Clear();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена аккаунта: вход не стёрт при отказе ({ex.Message}).", "ERROR");
            }
            _ = Task.Run(async () =>
            {
                try { await App.GetRequiredService<NurMarketKassa.Core.Contracts.IAuthSessionManager>().ClearSessionAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Смена аккаунта: сохранённая сессия не стёрта при отказе ({ex.Message}).", "ERROR"); }
                Dispatcher.UIThread.Post(ShutdownApp);
            });
            return;
        }
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
        Dispatcher.UIThread.Post(ShutdownApp);
    }

    private static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
