using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21, просьба владельца «Кнопка «Очистить неотправленные чеки»»: окно подтверждения очистки. Показывает «N чеков на сумму X сом, самый старый — дата», явное предупреждение,
/// что чеки НЕ попадут в отчёты и учёт, отдельное предупреждение про фискально пробитые чеки и про чеки, отправка которых уже начиналась, и путь будущей резервной копии.
/// Выбор: «выбранные» (если в окне «Неотправленное» отмечены чеки) или «все»; по умолчанию ничего не очищается, пока не поставлена галочка «Я понимаю…». PIN спрашивается следом (UnsentClearFlow).
/// Само окно ничего не удаляет — только возвращает решение.</summary>
public sealed class UnsentClearDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly UnsentClearPlan _all;
    private readonly UnsentClearPlan _selected;
    private readonly RadioButton _scopeSelected;
    private readonly RadioButton _scopeAll;
    private readonly TextBlock _summary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _warnings = new() { Spacing = 8 };
    private readonly CheckBox _understand;
    private readonly Button _ok;

    /// <summary>true — очищать выбранные, false — все.</summary>
    public bool UseSelected => _scopeSelected.IsChecked == true;

    public UnsentClearPlan ChosenPlan => UseSelected ? _selected : _all;

    public UnsentClearDialog(UnsentClearPlan all, UnsentClearPlan selected, string backupRoot)
    {
        _all = all;
        _selected = selected;
        Title = T("Очистить неотправленные чеки", "Жөнөтүлбөгөн чектерди тазалоо", "Clear unsent receipts", "Gönderilmemiş fişleri temizle", "Yuborilmagan cheklarni tozalash");
        SectionCards.AsModal(this, 540);

        var panel = new StackPanel { Spacing = 10 };

        _scopeSelected = new RadioButton
        {
            GroupName = "UnsentClearScope", MinHeight = 44, IsEnabled = selected.Count > 0, IsChecked = selected.Count > 0,
            Content = Wrap(T($"Только выбранные: {selected.Count}", $"Тандалгандар гана: {selected.Count}", $"Selected only: {selected.Count}", $"Yalnızca seçilenler: {selected.Count}", $"Faqat tanlanganlar: {selected.Count}")),
        };
        _scopeAll = new RadioButton
        {
            GroupName = "UnsentClearScope", MinHeight = 44, IsChecked = selected.Count == 0,
            Content = Wrap(T($"Все неотправленные чеки: {all.Count}", $"Бардык жөнөтүлбөгөн чектер: {all.Count}", $"All unsent receipts: {all.Count}", $"Tüm gönderilmemiş fişler: {all.Count}", $"Barcha yuborilmagan cheklar: {all.Count}")),
        };
        _scopeSelected.IsCheckedChanged += (_, _) => Rebuild();
        _scopeAll.IsCheckedChanged += (_, _) => Rebuild();
        panel.Children.Add(_scopeSelected);
        panel.Children.Add(_scopeAll);

        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        panel.Children.Add(_summary);
        panel.Children.Add(_warnings);

        var backup = new TextBlock
        {
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Text = T("Перед очисткой касса сохранит копию этих чеков (JSON и CSV) в папку: ", "Тазалоодон мурун касса бул чектердин көчүрмөсүн (JSON жана CSV) папкага сактайт: ",
                "Before clearing, the till saves a copy of these receipts (JSON and CSV) to the folder: ", "Temizlemeden önce kasa bu fişlerin bir kopyasını (JSON ve CSV) şu klasöre kaydeder: ",
                "Tozalashdan oldin kassa bu cheklarning nusxasini (JSON va CSV) papkaga saqlaydi: ") + System.IO.Path.Combine(backupRoot, "<" + T("дата-время", "күн-убакыт", "date-time", "tarih-saat", "sana-vaqt") + ">"),
        };
        backup.Bind(TextBlock.ForegroundProperty, backup.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(backup);

        _understand = new CheckBox
        {
            MinHeight = 44,
            Content = Wrap(T("Я понимаю: эти чеки не попадут в отчёты и учёт", "Түшүндүм: бул чектер отчётторго жана эсепке кирбейт", "I understand: these receipts will not reach the reports and accounting",
                "Anladım: bu fişler raporlara ve muhasebeye girmeyecek", "Tushundim: bu cheklar hisobotlar va hisobga kirmaydi")),
        };
        _understand.IsCheckedChanged += (_, _) => _ok!.IsEnabled = _understand.IsChecked == true && ChosenPlan.Count > 0;

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _ok = SectionCards.Danger(T("Очистить…", "Тазалоо…", "Clear…", "Temizle…", "Tozalash…"));
        _ok.IsEnabled = false;
        _ok.Click += (_, _) => { if (ChosenPlan.Count > 0 && _understand.IsChecked == true) Close(true); };

        // Высота: на 1024×600 всё не помещается без прокрутки — тело в ScrollViewer, кнопки подвала всегда видны.
        var scroll = new ScrollViewer
        {
            Content = panel, MaxHeight = 250, // 2026-11-21: тело листается, галочка и кнопки подвала всегда видны (окно ≤ 600 px по высоте)
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var shell = SectionCards.Shell(Title,
            T("Чеки пропадут из очереди «Неотправленное». Деньги по ним приняты в кассе, но на сервере этих продаж не будет.",
                "Чектер «Жөнөтүлбөгөн» кезегинен жок болот. Алар боюнча акча кассада алынган, бирок серверде бул сатуулар болбойт.",
                "The receipts leave the “Unsent” queue. The money was taken at the till, but these sales will not exist on the server.",
                "Fişler «Gönderilmeyenler» kuyruğundan çıkar. Para kasada alındı ama bu satışlar sunucuda olmayacak.",
                "Cheklar «Yuborilmaganlar» navbatidan chiqadi. Pul kassada olingan, lekin bu sotuvlar serverda bo'lmaydi."),
            scroll, new StackPanel { Spacing = 6, Children = { _understand, SectionCards.Buttons(cancel, _ok) } });
        shell.InitialFocus = cancel;
        Content = shell;
        Rebuild();
    }

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };

    private void Rebuild()
    {
        var plan = ChosenPlan;
        var oldest = plan.Oldest is { } o ? o.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
        var total = MoneyText.Som(plan.Total);
        _summary.Text = T($"{plan.Count} чеков на сумму {total}, самый старый — {oldest}", $"{plan.Count} чек, жалпы {total}, эң эскиси — {oldest}",
            $"{plan.Count} receipts worth {total}, the oldest is from {oldest}", $"{plan.Count} fiş, toplam {total}, en eskisi {oldest} tarihli", $"{plan.Count} ta chek, jami {total}, eng eskisi — {oldest}");

        _warnings.Children.Clear();
        _warnings.Children.Add(Warn(T("Эти чеки НЕ попадут в отчёты и учёт: выручка, остатки и долги на сервере по ним не изменятся. Вернуть их в очередь будет нельзя — только по резервной копии.",
            "Бул чектер отчётторго жана эсепке КИРБЕЙТ: алар боюнча түшүм, калдык жана карыздар серверде өзгөрбөйт. Аларды кезекке кайтаруу мүмкүн эмес — көчүрмө боюнча гана.",
            "These receipts will NOT reach the reports and accounting: revenue, stock and debts on the server will not change for them. They cannot be put back in the queue — only restored from the backup.",
            "Bu fişler raporlara ve muhasebeye GİRMEYECEK: sunucuda ciro, stok ve borçlar bunlar için değişmeyecek. Kuyruğa geri alınamaz — yalnızca yedekten.",
            "Bu cheklar hisobotlar va hisobga KIRMAYDI: ular bo'yicha serverda tushum, qoldiq va qarzlar o'zgarmaydi. Ularni navbatga qaytarib bo'lmaydi — faqat nusxadan."), danger: true));
        if (plan.FiscalCount > 0)
            _warnings.Children.Add(Warn(T($"Чек уже пробит фискально: {plan.FiscalCount} шт. Данные ФД/ФМ уже в налоговой, а продажи в учёте не будет — это расхождение придётся исправлять вручную.",
                $"Чек фискалдык бектилген: {plan.FiscalCount} даана. ФД/ФМ маалыматтары салык органында, ал эми эсепте сатуу болбойт — бул айырманы кол менен оңдоого туура келет.",
                $"The receipt is already fiscalized: {plan.FiscalCount}. The FD/FM data is already with the tax office, but the sale will not be in the accounting — this mismatch must be fixed by hand.",
                $"Fiş zaten fiskal olarak basıldı: {plan.FiscalCount} adet. FD/FM verileri vergi dairesinde, ama satış muhasebede olmayacak — bu fark elle düzeltilmeli.",
                $"Chek allaqachon fiskal urilgan: {plan.FiscalCount} ta. FD/FM ma'lumotlari soliq organida, lekin sotuv hisobda bo'lmaydi — bu farqni qo'lda tuzatish kerak."), danger: true));
        if (plan.MaybeOnServerCount > 0)
            _warnings.Children.Add(Warn(T($"У {plan.MaybeOnServerCount} чек. отправка уже начиналась — продажа могла дойти до сервера. Сверьте с «Чеками» на сервере до очистки.",
                $"{plan.MaybeOnServerCount} чектин жөнөтүлүшү башталган — сатуу серверге жетип калышы мүмкүн. Тазалоодон мурун серверде «Чектер» менен салыштырыңыз.",
                $"Sending already started for {plan.MaybeOnServerCount} receipt(s) — the sale may have reached the server. Compare with the server's “Receipts” before clearing.",
                $"{plan.MaybeOnServerCount} fişte gönderim başlamıştı — satış sunucuya ulaşmış olabilir. Temizlemeden önce sunucudaki «Fişler» ile karşılaştırın.",
                $"{plan.MaybeOnServerCount} ta chekda yuborish boshlangan — sotuv serverga yetib borgan bo'lishi mumkin. Tozalashdan oldin serverdagi «Cheklar» bilan solishtiring."), danger: false));
        if (plan.DebtCount > 0)
            _warnings.Children.Add(Warn(T($"Из них в долг: {plan.DebtCount}. Долг клиента на сервере по ним не появится.", $"Алардын ичинде карызга: {plan.DebtCount}. Кардардын карызы серверде көрүнбөйт.",
                $"Of them on credit: {plan.DebtCount}. The client's debt will not appear on the server.", $"Bunlardan veresiye: {plan.DebtCount}. Müşteri borcu sunucuda görünmeyecek.", $"Ulardan qarzga: {plan.DebtCount}. Mijoz qarzi serverda ko'rinmaydi."), danger: false));
        _ok.IsEnabled = _understand.IsChecked == true && plan.Count > 0;
    }

    private Border Warn(string text, bool danger)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(danger ? "BrushDangerOnSoft" : "BrushText"));
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = t };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable(danger ? "BrushDangerSoft" : "BrushSurfaceSubtle"));
        return box;
    }

    /// <summary>Весь путь «Очистить неотправленные чеки»: права → окно подтверждения → PIN владельца/администратора (проверяет сервер) → очистка с резервной копией.
    /// null — владелец отказался (отмена на любом шаге); иначе итог со сообщением для окна.</summary>
    public static async Task<UnsentClearResult?> RunAsync(Window owner, OutboxAccess access, IReadOnlyCollection<string> selectedSaleIds)
    {
        if (!access.IsAdmin)
            return new UnsentClearResult(UnsentClearOutcome.NotAllowed, 0, 0, null, UnsentReceiptsCleaner.Texts.NotAllowed);

        var allIds = OutboxService.Default.Snapshot(access).Where(i => i.Kind == OutboxKind.Sale).Select(i => i.Id).ToList();
        var all = UnsentReceiptsCleaner.Plan(allIds);
        if (all.Count == 0)
            return new UnsentClearResult(UnsentClearOutcome.NothingToClear, 0, all.SkippedIds.Count, null, UnsentReceiptsCleaner.Texts.Nothing);
        var selected = UnsentReceiptsCleaner.Plan(selectedSaleIds.Where(id => allIds.Contains(id, StringComparer.OrdinalIgnoreCase)));

        var dialog = new UnsentClearDialog(all, selected, UnsentReceiptsCleaner.BackupRoot);
        if (!await dialog.ShowDialog<bool>(owner).ConfigureAwait(true))
            return null;
        var ids = dialog.ChosenPlan.Ids;

        var sync = SyncService.Current;
        if (sync is null)
            return new UnsentClearResult(UnsentClearOutcome.PinRequired, 0, 0, null, UnsentReceiptsCleaner.Texts.PinRequired);
        string? refusal = null;
        var pin = await LateAcceptPrompt.AskConfirmTokenAsync(sync, m => refusal = m,
            T("Введите PIN владельца или администратора (не пароль от входа), чтобы очистить неотправленные чеки. Копия чеков будет сохранена.",
                "Жөнөтүлбөгөн чектерди тазалоо үчүн ээсинин же администратордун PIN кодун киргизиңиз (кирүү сырсөзү эмес). Чектердин көчүрмөсү сакталат.",
                "Enter the owner's or administrator's PIN (not the login password) to clear the unsent receipts. A copy of the receipts will be saved.",
                "Gönderilmemiş fişleri temizlemek için sahibin veya yöneticinin PIN'ini girin (giriş şifresi değil). Fişlerin kopyası kaydedilir.",
                "Yuborilmagan cheklarni tozalash uchun egasi yoki administrator PIN kodini kiriting (kirish paroli emas). Cheklar nusxasi saqlanadi.")).ConfigureAwait(true);
        if (pin is null)
            return refusal is null ? null : new UnsentClearResult(UnsentClearOutcome.PinRequired, 0, 0, null, refusal + "\n" + UnsentReceiptsCleaner.Texts.PinRequired);

        // Сервер подтвердил PIN (токен не нужен и нигде не хранится). Очистка — только теперь.
        return await Task.Run(() => UnsentReceiptsCleaner.Clear(ids, access, pinVerified: true, approvedBy: NurMarketKassa.PosApp.CurrentUserDisplayName)).ConfigureAwait(true);
    }
}
