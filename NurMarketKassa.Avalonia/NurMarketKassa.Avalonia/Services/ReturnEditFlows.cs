using System.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Возвратный документ, над которым действуют кнопки правки (общий для возврата поставщику и от клиента).</summary>
public sealed record ReturnDocRef(ReturnDocKind Kind, string Id, string Number, double Total, ReturnEditState State);

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): общие кнопки и шаги для ОБОИХ возвратных документов.
/// <list type="bullet">
/// <item><see cref="Bar"/> — «Редактировать» / «Удалить» / «История» + подсказка «можно изменить ещё N ч» (кнопки только при признаке сервера и can_edit/can_void документа);</item>
/// <item><see cref="VoidAsync"/> — подтверждение → причина → свежая проверка документа на сервере → PIN руководителя → POST …/void/ (ключ повтора);</item>
/// <item><see cref="AskPinAsync"/> — окно PIN руководителя (одноразовый токен на одно действие), <see cref="ShowHistoryAsync"/> — журнал версий;</item>
/// <item><see cref="VoidedPill"/> — плашка «АННУЛИРОВАН» для списков.</item>
/// </list>
/// 2026-11-11 (сервер Умара): «Возврат от клиента» — окно правки <see cref="EditCustomerReturnAsync"/> (CustomerReturnEditDialog), список возвратов чека с кнопками
/// <see cref="ShowCustomerReturnsAsync"/> (окно возврата, история чеков, финансы, покупки клиента), кнопка-вход <see cref="CustomerReturnsEntry"/>;
/// у кнопок, которых нет по решению сервера (can_edit/can_void = false), — пояснение по edit_block_reason (<see cref="ReturnEditGate.BlockHint"/>).
/// Решений по часам устройства нет: всё решает сервер, срок проверяется ещё раз перед PIN и при сохранении.</summary>
public static class ReturnEditFlows
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly Dictionary<string, PendingIdempotencyKey> VoidKeys = new();

    /// <summary>Подмена запросов в тестах окон; null — настоящий клиент приложения.</summary>
    internal static Func<ReturnEditApi>? ApiFactory { get; set; }

    internal static ReturnEditApi Api() => ApiFactory?.Invoke() ?? new ReturnEditApi(App.GetRequiredService<NurMarketApiClient>());

    /// <summary>Владелец/администратор (журнал аудита — только им; сервер всё равно проверит).</summary>
    public static bool IsManager => App.AuthApi is { } auth && PermissionService.IsOwnerOrAdmin(auth.UserPayload);

    private static string KindName(ReturnDocKind kind) => kind == ReturnDocKind.Supplier
        ? T("возврат поставщику", "жеткирүүчүгө кайтаруу", "return to supplier", "tedarikçiye iade", "yetkazib beruvchiga qaytarish")
        : T("возврат от клиента", "кардардан кайтаруу", "customer return", "müşteri iadesi", "mijozdan qaytarish");

    /// <summary>Подсказка «Можно изменить ещё N ч» — только по времени сервера из ответа (editable_until − server_now); null — сервер не сказал.
    /// 2026-11-12 (аудит Н-2): слова по разрешениям документа — «Изменить или удалить» только если можно и то и другое, иначе «Изменить» либо «Удалить».</summary>
    public static string? TimeLeftHint(ReturnEditState s)
    {
        if (s.TimeLeft() is not { } left || !(s.CanEdit || s.CanVoid))
            return null;
        var both = s.CanEdit && s.CanVoid;
        if (left.TotalMinutes < 60)
        {
            var m = Math.Max(1, (int)left.TotalMinutes);
            return both
                ? T($"Изменить или удалить можно ещё {m} мин", $"Дагы {m} мүн. өзгөртүүгө же өчүрүүгө болот", $"Can be changed or deleted for {m} more min",
                    $"{m} dk daha değiştirilebilir veya silinebilir", $"Yana {m} daqiqa o'zgartirish yoki o'chirish mumkin")
                : s.CanEdit
                    ? T($"Изменить можно ещё {m} мин", $"Дагы {m} мүн. өзгөртүүгө болот", $"Can be changed for {m} more min", $"{m} dk daha değiştirilebilir", $"Yana {m} daqiqa o'zgartirish mumkin")
                    : T($"Удалить можно ещё {m} мин", $"Дагы {m} мүн. өчүрүүгө болот", $"Can be deleted for {m} more min", $"{m} dk daha silinebilir", $"Yana {m} daqiqa o'chirish mumkin");
        }
        var h = (int)left.TotalHours;
        return both
            ? T($"Изменить или удалить можно ещё {h} ч", $"Дагы {h} саат өзгөртүүгө же өчүрүүгө болот", $"Can be changed or deleted for {h} more h",
                $"{h} sa daha değiştirilebilir veya silinebilir", $"Yana {h} soat o'zgartirish yoki o'chirish mumkin")
            : s.CanEdit
                ? T($"Изменить можно ещё {h} ч", $"Дагы {h} саат өзгөртүүгө болот", $"Can be changed for {h} more h", $"{h} sa daha değiştirilebilir", $"Yana {h} soat o'zgartirish mumkin")
                : T($"Удалить можно ещё {h} ч", $"Дагы {h} саат өчүрүүгө болот", $"Can be deleted for {h} more h", $"{h} sa daha silinebilir", $"Yana {h} soat o'chirish mumkin");
    }

    /// <summary>2026-11-12 (аудит Н-2): подсказка «можно ещё N мин» в окне правки раз в минуту пересчитывается (раньше считалась один раз при открытии и «замирала»); срок вышел —
    /// текст об этом (решает всё равно сервер). Таймер останавливается при закрытии окна. Нет времени сервера в ответе — подсказки нет.</summary>
    public static void AttachTimeLeft(Panel panel, Window window, ReturnEditState state)
    {
        if (TimeLeftHint(state) is not { } first)
            return;
        var text = new TextBlock { Text = first, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(text);
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) =>
        {
            text.Text = state.TimeLeft() is { } left && left <= TimeSpan.Zero
                ? T("Срок изменения по времени сервера истёк — при сохранении сервер откажет.", "Сервердин убактысы боюнча өзгөртүү мөөнөтү бүттү — сактоодо сервер четке кагат.",
                    "The change period (server time) has expired — the server will refuse on save.", "Sunucu saatine göre değişiklik süresi doldu — kaydederken sunucu reddedecek.", "Server vaqti bo'yicha o'zgartirish muddati tugadi — saqlashda server rad etadi.")
                : TimeLeftHint(state) ?? text.Text;
        };
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Плашка «АННУЛИРОВАН» (null — документ действующий).</summary>
    public static Control? VoidedPill(Control host, ReturnEditState s) =>
        s.IsVoided ? SectionCards.Pill(host, ReturnEditErrors.VoidedMark(), "BrushDangerSoft", "BrushDangerOnSoft") : null;

    /// <summary>Строка кнопок правки документа. Пустая и скрытая, если сервер ничего не разрешил (или нет признака). <paramref name="edit"/> — открыть окно правки
    /// (null — у типа его пока нет, кнопки «Редактировать» не будет); <paramref name="reload"/> — перечитать список после изменения.</summary>
    /// 2026-11-12 (владелец: «редактирование накладной возврата поставщику»): <paramref name="editText"/> — своя подпись кнопки правки («Править накладную»);
    /// <paramref name="editWithoutFlags"/> — признак сервера есть, а строка списка не принесла can_edit/version: кнопка всё равно видна, решение — по свежей карточке
    /// при нажатии (раньше кнопки молча не было); <paramref name="whyNot"/> — пояснение, почему правки нет (нет признака сервера, нет связи, прошло 24 ч без кода причины).
    public static Control Bar(Control host, ReturnDocRef doc, Func<Window, Task<bool>>? edit, Func<Task> reload,
        string? editText = null, bool editWithoutFlags = false, Func<ReturnEditState, string?>? whyNot = null)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var withoutFlags = editWithoutFlags && ReturnEditGate.FeatureOn(doc.Kind) && !doc.State.IsVoided && (!doc.State.HasServerFlags || doc.State.Version <= 0);
        var showEdit = edit is not null && (ReturnEditGate.ShowEdit(doc.Kind, doc.State) || withoutFlags);
        // 2026-11-12 (аудит Н-4): строка списка без признаков сервера — рядом с «Править накладную» видна и «Удалить»; решение (можно ли, срок, версия) — по свежей карточке в VoidAsync
        var showVoid = ReturnEditGate.ShowVoid(doc.Kind, doc.State) || withoutFlags;
        var showHistory = ReturnEditGate.ShowHistory(doc.Kind, doc.State, IsManager);
        Window? Owner() => TopLevel.GetTopLevel(host) as Window;
        if (showEdit)
            row.Children.Add(Small(editText ?? T("Редактировать", "Оңдоо", "Edit", "Düzenle", "Tahrirlash"), async () =>
            {
                if (Owner() is { } w && await edit!(w).ConfigureAwait(true))
                    await reload().ConfigureAwait(true);
            }));
        if (showVoid)
            row.Children.Add(Small(T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), async () =>
            {
                if (Owner() is { } w && await VoidAsync(w, doc).ConfigureAwait(true))
                    await reload().ConfigureAwait(true);
            }, danger: true));
        if (showHistory)
            row.Children.Add(Small(T("История", "Тарых", "History", "Geçmiş", "Tarix"), async () =>
            {
                if (Owner() is { } w)
                    await ShowHistoryAsync(w, doc).ConfigureAwait(true);
            }));
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(row);
        row.IsVisible = row.Children.Count > 0;
        if ((showEdit || showVoid) && TimeLeftHint(doc.State) is { } hint)
        {
            var t = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(t);
        }
        // 2026-11-11 (реальный контракт): кнопок нет по решению сервера — объясняем почему (чек в долг, прошло 24 ч, смена закрыта…)
        // 2026-11-12: и когда кода причины нет (нет признака сервера, нет связи, can_edit:false без причины) — пояснение от вызывающего (whyNot)
        if ((ReturnEditGate.BlockHint(doc.Kind, doc.State) ?? (showEdit ? null : whyNot?.Invoke(doc.State))) is { } blocked)
        {
            var t = new TextBlock { Text = "ⓘ " + blocked, FontSize = 12, TextWrapping = TextWrapping.Wrap, Name = "ReturnEditBlockHint" };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(t);
        }
        panel.IsVisible = panel.Children.Count > 1 || row.IsVisible;
        return panel;
    }

    private static Button Small(string text, Func<Task> click, bool danger = false)
    {
        var b = new Button
        {
            Content = text, Height = 34, MinHeight = 34, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6),
        };
        b.Classes.Add("sec-outline");
        if (danger)
            b.Bind(Button.ForegroundProperty, b.GetResourceObservable("BrushDangerOnSoft"));
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        var busy = false;
        b.Click += async (_, _) =>
        {
            if (busy)
                return;
            busy = true;
            b.IsEnabled = false;
            try
            {
                await click().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Возвраты (правка 24 ч): действие «{text}» не выполнено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                // 2026-11-12 (аудит С-4): раньше ошибка только писалась в журнал — кассир нажал, и ничего не произошло; теперь сообщение
                try
                {
                    if (TopLevel.GetTopLevel(b) is Window w)
                        await Say(w, T($"Действие «{text}» не выполнено: {ex.Message}", $"«{text}» аракети аткарылган жок: {ex.Message}", $"The action “{text}” was not completed: {ex.Message}",
                            $"«{text}» işlemi tamamlanamadı: {ex.Message}", $"«{text}» amali bajarilmadi: {ex.Message}")).ConfigureAwait(true);
                }
                catch (Exception sayEx) when (sayEx is not OutOfMemoryException)
                {
                    PosLogger.Log($"Возвраты (правка 24 ч): сообщение об ошибке не показано ({sayEx.GetType().Name}).", "WARNING");
                }
            }
            finally
            {
                busy = false;
                b.IsEnabled = true;
            }
        };
        return b;
    }

    /// <summary>PIN руководителя для одного действия над документом; токен или null (отмена/отказ). Отказ «бессмысленно повторять» — <paramref name="final"/>.</summary>
    public static async Task<(string? Token, ReturnEditException? Final)> AskPinAsync(Window owner, ReturnDocRef doc, ReturnEditAction action, int version, ReturnEditApi? api = null)
    {
        api ??= Api();
        var subtitle = (action == ReturnEditAction.Edit
            ? T("Изменение: ", "Өзгөртүү: ", "Change: ", "Değişiklik: ", "O'zgartirish: ")
            : T("Удаление: ", "Өчүрүү: ", "Deletion: ", "Silme: ", "O'chirish: ")) + KindName(doc.Kind) + " " + doc.Number;
        var dialog = new ManagerPinDialog(subtitle, (pin, ct) => api.ConfirmAsync(doc.Kind, doc.Id, action, version, pin, ct));
        await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        return (dialog.ConfirmToken, dialog.FinalError);
    }

    private static Task Say(Window owner, string text, bool warning = true) =>
        PosMessageBox.ShowModalAsync(owner, text, T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    /// <summary>Удалить (аннулировать) документ. true — список надо перечитать (документ аннулирован, или выяснилось, что он изменён/уже аннулирован).</summary>
    public static async Task<bool> VoidAsync(Window owner, ReturnDocRef doc, ReturnEditApi? api = null)
    {
        api ??= Api();
        using var syncHold = doc.Kind == ReturnDocKind.Customer ? ReturnJournalSync.Hold() : null; // 2026-11-13: пока идёт аннулирование, фоновая сверка журнала молчит — журнал пишем здесь
        var stockText = doc.Kind == ReturnDocKind.Supplier
            ? T("Списанный товар вернётся на склад, деньги/долг поставщику пересчитаются.", "Чыгарылган товар кампага кайтат, акча/жеткирүүчүгө карыз кайра эсептелет.",
                "The written-off goods return to stock, the money/supplier debt is recalculated.", "Düşülen ürün stoka geri döner, para/tedarikçi borcu yeniden hesaplanır.",
                "Chiqarilgan tovar omborga qaytadi, pul/yetkazib beruvchiga qarz qayta hisoblanadi.")
            : T("Поступление товара на склад отменится, выплата клиенту/долг клиента пересчитаются.", "Товардын кампага кириши жокко чыгат, кардарга төлөм/кардардын карызы кайра эсептелет.",
                "The stock receipt is cancelled, the customer refund/debt is recalculated.", "Stok girişi iptal edilir, müşteri iadesi/borcu yeniden hesaplanır.",
                "Omborga kirim bekor qilinadi, mijozga to'lov/mijoz qarzi qayta hisoblanadi.");
        var ask = T($"Удалить {KindName(doc.Kind)} {doc.Number} на {MoneyText.Som(doc.Total)}?\n\n{stockText} Документ останется в истории с отметкой «АННУЛИРОВАН». Нужен PIN руководителя.",
            $"{doc.Number} ({KindName(doc.Kind)}, {MoneyText.Som(doc.Total)}) өчүрөсүзбү?\n\n{stockText} Документ тарыхта «ЖОККО ЧЫГАРЫЛДЫ» белгиси менен калат. Жетекчинин PIN коду керек.",
            $"Delete {KindName(doc.Kind)} {doc.Number} for {MoneyText.Som(doc.Total)}?\n\n{stockText} The document stays in the history marked “VOIDED”. A manager PIN is required.",
            $"{doc.Number} ({KindName(doc.Kind)}, {MoneyText.Som(doc.Total)}) silinsin mi?\n\n{stockText} Belge geçmişte «İPTAL EDİLDİ» işaretiyle kalır. Yönetici PIN'i gerekir.",
            $"{doc.Number} ({KindName(doc.Kind)}, {MoneyText.Som(doc.Total)}) o'chirilsinmi?\n\n{stockText} Hujjat tarixda «BEKOR QILINDI» belgisi bilan qoladi. Rahbar PIN kodi kerak.");
        var caption = T("Удаление возврата", "Кайтарууну өчүрүү", "Delete return", "İadeyi sil", "Qaytarishni o'chirish");
        if (await PosMessageBox.ShowModalAsync(owner, ask, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                confirmText: T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), cancelText: T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), danger: true).ConfigureAwait(true) != MessageBoxResult.Yes)
            return false;

        // 2026-11-12 (аудит С-5): сначала свежая карточка (срок и версия по времени сервера), и только потом причина — раньше причину спрашивали до проверки срока, и при «24 часа прошли»
        // кассир зря писал её; устаревший список не даёт удалить документ, который уже изменили.
        ReturnEditState fresh;
        System.Text.Json.JsonElement card;
        try
        {
            card = await api.GetDocumentAsync(doc.Kind, doc.Id).ConfigureAwait(true);
            fresh = ReturnEditState.Parse(card, DateTime.UtcNow);
        }
        catch (ReturnEditException e)
        {
            await Say(owner, ReturnEditErrors.Text(e)).ConfigureAwait(true);
            return false;
        }
        if (fresh.IsVoided)
        {
            // 2026-11-12 (аудит В-2, К-1): прошлое аннулирование осталось без ответа (обрыв), а документ уже аннулирован — это наша же попытка дошла; журналы кассы обновляем по ней
            if (VoidKeys.TryGetValue(doc.Id, out var pending) && pending.HasUnansweredAttempt)
            {
                VoidKeys.Remove(doc.Id);
                var lateNote = ApplyVoidedJournals(doc, card);
                RefreshStock(card);
                await Say(owner, T("Связь оборвалась после отправки, но возврат уже аннулирован на сервере — прошлая попытка дошла. Второй раз ничего не отправлено.",
                    "Жөнөтүлгөндөн кийин байланыш үзүлдү, бирок кайтаруу серверде мурунтан эле жокко чыгарылган — мурунку аракет жетти. Экинчи жолу эч нерсе жөнөтүлгөн жок.",
                    "The connection dropped after sending, but the return is already voided on the server — the previous attempt arrived. Nothing was sent a second time.",
                    "Gönderimden sonra bağlantı koptu ancak iade sunucuda zaten iptal edilmiş — önceki deneme ulaştı. İkinci kez bir şey gönderilmedi.",
                    "Yuborilgandan keyin aloqa uzildi, lekin qaytarish serverda allaqachon bekor qilingan — oldingi urinish yetib borgan. Ikkinchi marta hech narsa yuborilmadi.")
                    + (lateNote.Length > 0 ? "\n\n" + lateNote : ""), warning: false).ConfigureAwait(true);
                return true;
            }
            // 2026-11-13 (проверка денег, п. 3): аннулировали в другом месте (программа владельца, сайт, другая касса) — журнал и смена этой кассы поправляются по данным сервера
            var elsewhereNote = doc.Kind == ReturnDocKind.Customer ? ReconcileOnOpen(doc, card, fresh) : "";
            await Say(owner, ReturnEditErrors.Text(new ReturnEditException(ReturnEditFailure.AlreadyVoided, "already_voided", "")) + (elsewhereNote.Length > 0 ? "\n\n" + elsewhereNote : "")).ConfigureAwait(true);
            return true;
        }
        if (doc.State.Version > 0 && fresh.Version != doc.State.Version)
        {
            await Say(owner, ReturnEditErrors.VersionConflictText()).ConfigureAwait(true);
            return true;
        }
        if (!ReturnEditGate.ShowVoid(doc.Kind, fresh))
        {
            await Say(owner, ReturnEditErrors.NotEditableText(fresh)).ConfigureAwait(true); // 2026-11-11: причина сервера (edit_block_reason)
            return true;
        }

        // 2026-11-12 (аудит С-5): длина причины проверяется в самом окне ввода (ошибка под полем), а не сообщением после всего пути
        var input = new SimpleInputDialog(caption, T("Причина удаления (от 5 знаков):", "Өчүрүү себеби (5 белгиден):", "Reason for deletion (at least 5 characters):", "Silme nedeni (en az 5 karakter):", "O'chirish sababi (kamida 5 belgi):"),
            confirmText: T("Далее", "Андан ары", "Next", "İleri", "Keyingi"), minLength: SupplierReturnEdit.ReasonMin, maxLength: SupplierReturnEdit.ReasonMax);
        if (!await input.ShowDialog<bool>(owner).ConfigureAwait(true))
            return false;
        var reason = input.Result.Trim();
        if (reason.Length is < SupplierReturnEdit.ReasonMin or > SupplierReturnEdit.ReasonMax)
        {
            await Say(owner, T("Причина должна быть от 5 до 500 знаков. Ничего не удалено.", "Себеп 5тен 500 белгиге чейин болушу керек. Эч нерсе өчүрүлгөн жок.",
                "The reason must be 5 to 500 characters. Nothing was deleted.", "Neden 5 ile 500 karakter arasında olmalı. Hiçbir şey silinmedi.", "Sabab 5 dan 500 belgigacha bo'lishi kerak. Hech narsa o'chirilmadi.")).ConfigureAwait(true);
            return false;
        }

        var (token, final) = await AskPinAsync(owner, doc, ReturnEditAction.Void, fresh.Version, api).ConfigureAwait(true);
        if (token is null)
            return final is not null;

        if (!VoidKeys.TryGetValue(doc.Id, out var key))
            VoidKeys[doc.Id] = key = new PendingIdempotencyKey();
        try
        {
            await api.VoidAsync(doc.Kind, doc.Id, fresh.Version, reason, token, key.Get(doc.Id + "|v" + fresh.Version + "|" + reason)).ConfigureAwait(true);
            key.OnSuccess();
            VoidKeys.Remove(doc.Id);
            RefreshStock(card); // 2026-11-11: остатки товаров документа — перечитать с сервера (каталог, склад)
            // 2026-11-12 (аудит К-1, В-1): журнал возвратов кассы и смена — по успешному аннулированию; кассиру — что делать с деньгами
            var moneyNote = ApplyVoidedJournals(doc, card);
            await Say(owner, T($"Возврат {doc.Number} аннулирован. Склад и расчёты пересчитаны сервером.", $"{doc.Number} кайтаруусу жокко чыгарылды. Кампа жана эсептешүүлөр сервер тарабынан кайра эсептелди.",
                $"Return {doc.Number} was voided. Stock and settlements were recalculated by the server.", $"{doc.Number} iadesi iptal edildi. Stok ve hesaplar sunucu tarafından yeniden hesaplandı.",
                $"{doc.Number} qaytarish bekor qilindi. Ombor va hisob-kitoblar server tomonidan qayta hisoblandi.") + (moneyNote.Length > 0 ? "\n\n" + moneyNote : ""), warning: false).ConfigureAwait(true);
            return true;
        }
        catch (ReturnEditException e)
        {
            // 2026-11-12 (аудит В-2): обрыв/5xx — исход неизвестен: ключ остаётся для повтора, текст честный; отказ по существу — ключ забыт
            var unknown = ReturnEditErrors.IsOutcomeUnknown(e);
            if (unknown)
                key.OnError(e.InnerException ?? e);
            else
                VoidKeys.Remove(doc.Id);
            await Say(owner, unknown
                ? T("Нет связи с сервером — неизвестно, дошло ли аннулирование. Нажмите «Удалить» ещё раз, когда связь появится: повтор не аннулирует возврат второй раз.",
                    "Сервер менен байланыш жок — жокко чыгаруу жеткени белгисиз. Байланыш пайда болгондо «Өчүрүү» дагы басыңыз: кайталоо кайтарууну экинчи жолу жокко чыгарбайт.",
                    "No connection to the server — it is unknown whether the voiding arrived. Press “Delete” again when the connection is back: a retry will not void the return twice.",
                    "Sunucuyla bağlantı yok — iptalin ulaşıp ulaşmadığı bilinmiyor. Bağlantı gelince yine «Sil»e basın: tekrar, iadeyi ikinci kez iptal etmez.",
                    "Server bilan aloqa yo'q — bekor qilish yetib borgani noma'lum. Aloqa paydo bo'lganda yana «O'chirish»ni bosing: takror qaytarishni ikkinchi marta bekor qilmaydi.")
                : T("Возврат не удалён: ", "Кайтаруу өчүрүлгөн жок: ", "The return was not deleted: ", "İade silinmedi: ", "Qaytarish o'chirilmadi: ") + ReturnEditErrors.Text(e)).ConfigureAwait(true);
            return e.IsFinal;
        }
    }

    /// <summary>2026-11-12 (аудит К-1, В-1): возврат аннулирован на сервере — у возврата от клиента запись убирается из журнала возвратов кассы (потолок нового возврата, пометка «возвращён»,
    /// «Возвращено всего»), в смене пишется сторно; возвращает текст «заберите у клиента N» (пусто — у возврата поставщику денег клиента нет).</summary>
    internal static string ApplyVoidedJournals(ReturnDocRef doc, System.Text.Json.JsonElement card)
    {
        if (doc.Kind != ReturnDocKind.Customer)
            return "";
        try
        {
            var saleId = SaleIdOf(card);
            var amount = CustomerReturnEdit.DocAmount(card) ?? (decimal)doc.Total;
            var change = CustomerReturnJournals.Void(saleId, doc.Id, amount, PosApp.ActiveShiftId);
            if (change.SaleId.Length > 0)
                SaleDetailCache.Forget(change.SaleId);
            return CustomerReturnJournals.MoneyNote(change, AppMode.IsOwner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возврат от клиента {doc.Id}: журналы кассы после аннулирования не обновлены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return "";
        }
    }

    /// <summary>2026-11-13 (проверка денег, п. 2/п. 3): сверить сумму документа возврата с записью журнала кассы при открытии окна правки/аннулирования. Была разница (правка дошла без ответа, правили в другом
    /// месте) — журнал пересчитан, в текущую смену записана поправка; возвращает текст «заберите/выдайте N» (пусто — разницы нет, возврат не этой кассы, смены нет, программа владельца).</summary>
    internal static string ReconcileOnOpen(ReturnDocRef doc, System.Text.Json.JsonElement card, ReturnEditState state)
    {
        if (doc.Kind != ReturnDocKind.Customer || AppMode.IsOwner || ReturnJournalSync.IsHeld)
            return "";
        try
        {
            var amount = CustomerReturnEdit.DocAmount(card) ?? (decimal)doc.Total;
            var change = CustomerReturnReconciler.ApplyDoc(doc.Id, SaleIdOf(card), amount, state.IsVoided, PosApp.ActiveShiftId);
            if (change is null)
                return "";
            if (change.SaleId.Length > 0)
                SaleDetailCache.Forget(change.SaleId);
            return CustomerReturnJournals.MoneyNote(change, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возврат от клиента {doc.Id}: сверка журнала при открытии не выполнена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return "";
        }
    }

    // ───────────── возврат поставщику: окно правки ─────────────

    /// <summary>Номер возврата поставщику для подписей: номер сервера («ВП-…»), иначе номер кассы.</summary>
    public static string SupplierNumber(SupplierReturnInfo r)
    {
        if (r.NumberDisplay.Length > 0)
            return r.NumberDisplay;
        try
        {
            return DocumentNumbering.Text(DocSeries.Return, r.Id, DocumentNumbering.ShortCode(r.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DocumentNumbering.ShortCode(r.Id);
        }
    }

    /// <summary>2026-11-12: подпись кнопки правки возврата поставщику — «Править накладную» (владелец искал правку накладной и не находил «Редактировать»).</summary>
    public static string SupplierEditButtonText() => T("Править накладную", "Накладнойду оңдоо", "Edit waybill", "İrsaliyeyi düzenle", "Yuk xatini tahrirlash");

    /// <summary>Кнопки правки у возврата поставщику (окно «Возвраты поставщикам», «Закупки → Возвраты», карточка поставщика, программа владельца).
    /// 2026-11-12: «Править накладную» видна и тогда, когда строка списка не принесла can_edit/version (решает свежая карточка); если правки нет — пояснение почему.</summary>
    public static Control SupplierBar(Control host, SupplierReturnInfo r, Func<Task> reload, ReturnEditApi? api = null)
    {
        var number = SupplierNumber(r);
        return Bar(host, new ReturnDocRef(ReturnDocKind.Supplier, r.Id, number, r.Total, r.EditState), w => EditSupplierReturnAsync(w, r, number, api), reload,
            SupplierEditButtonText(), editWithoutFlags: r.Id.Length > 0, whyNot: SupplierEditUnavailableText);
    }

    /// <summary>2026-11-12: почему у возврата поставщику нет кнопки «Править накладную» (5 языков); null — пояснять нечего (кнопка есть или документ аннулирован — у него плашка).
    /// Нет признака supplier_return_edit_24h: сервер ответил без него — «не включено», не ответил — «нет связи, обновите».</summary>
    public static string? SupplierEditUnavailableText(ReturnEditState s)
    {
        if (!ReturnEditGate.FeatureOn(ReturnDocKind.Supplier))
            return ServerCapabilities.Cached(ServerCapabilities.SupplierReturnEdit24h) == false
                ? T("Править накладную возврата нельзя: сервер NurCRM не включил эту возможность для вашей компании. Обратитесь в техподдержку NurCRM.",
                    "Кайтаруу накладнойун оңдоого болбойт: NurCRM сервери бул мүмкүнчүлүктү компанияңыз үчүн күйгүзгөн жок. NurCRM техникалык колдоосуна кайрылыңыз.",
                    "The return waybill cannot be edited: the NurCRM server has not enabled this for your company. Contact NurCRM support.",
                    "İade irsaliyesi düzenlenemez: NurCRM sunucusu bu özelliği şirketiniz için açmadı. NurCRM desteğine başvurun.",
                    "Qaytarish yuk xatini tahrirlab bo'lmaydi: NurCRM serveri bu imkoniyatni kompaniyangiz uchun yoqmagan. NurCRM texnik yordamiga murojaat qiling.")
                : T("Не удалось узнать у сервера, можно ли править накладную возврата (нет связи). Нажмите «Обновить», когда связь появится.",
                    "Кайтаруу накладнойун оңдоого болобу — серверден билүү мүмкүн болгон жок (байланыш жок). Байланыш пайда болгондо «Жаңыртуу» басыңыз.",
                    "Could not check with the server whether the return waybill can be edited (no connection). Press “Refresh” when the connection is back.",
                    "İade irsaliyesinin düzenlenip düzenlenemeyeceği sunucudan öğrenilemedi (bağlantı yok). Bağlantı gelince «Yenile»ye basın.",
                    "Qaytarish yuk xatini tahrirlash mumkinligini serverdan bilib bo'lmadi (aloqa yo'q). Aloqa paydo bo'lganda «Yangilash»ni bosing.");
        if (s.IsVoided || !s.HasServerFlags || s.CanEdit)
            return null;
        return s.CanVoid
            ? T("Изменить состав накладной сервер уже не даёт — возврат можно только удалить.", "Сервер накладнойдун курамын өзгөртүүгө мындан ары жол бербейт — кайтарууну өчүрүүгө гана болот.",
                "The server no longer allows changing the waybill contents — the return can only be deleted.", "Sunucu artık irsaliye içeriğinin değiştirilmesine izin vermiyor — iade yalnızca silinebilir.",
                "Server endi yuk xati tarkibini o'zgartirishga ruxsat bermaydi — qaytarishni faqat o'chirish mumkin.")
            : ReturnEditErrors.NotEditableText(s);
    }

    /// <summary>2026-11-12: итог подготовки правки возврата поставщику: <see cref="Card"/> — свежая карточка (открывать окно правки); иначе <see cref="Message"/> — что сказать;
    /// <see cref="Reload"/> — список устарел (перечитать после закрытия).</summary>
    public sealed record SupplierEditPrep(SupplierReturnInfo? Card, string? Message, bool Reload);

    /// <summary>2026-11-12: проверки перед окном правки без окон и сообщений (для всех точек входа и для тестов): признак сервера, id, свежая карточка
    /// (строки с id, версия, can_edit по времени сервера), причина отказа. Версия в списке устарела — НЕ отказ: окно откроется по свежей карточке (перечитано),
    /// список перечитается после закрытия.</summary>
    public static async Task<SupplierEditPrep> PrepareSupplierEditAsync(SupplierReturnInfo listed, ReturnEditApi? api = null, CancellationToken ct = default)
    {
        if (!ReturnEditGate.FeatureOn(ReturnDocKind.Supplier))
            return new SupplierEditPrep(null, SupplierEditUnavailableText(listed.EditState), false);
        if (listed.Id.Length == 0)
            return new SupplierEditPrep(null, T("Возврат ещё не найден на сервере — закройте окно, обновите список возвратов и нажмите «Править накладную» там.",
                "Кайтаруу серверде азырынча табылган жок — терезени жаап, кайтаруулар тизмесин жаңыртып, ошол жерден «Накладнойду оңдоо» басыңыз.",
                "The return was not found on the server yet — close the window, refresh the returns list and press “Edit waybill” there.",
                "İade henüz sunucuda bulunamadı — pencereyi kapatın, iade listesini yenileyin ve oradan «İrsaliyeyi düzenle»ye basın.",
                "Qaytarish hali serverda topilmadi — oynani yoping, qaytarishlar ro'yxatini yangilang va u yerda «Yuk xatini tahrirlash»ni bosing."), false);
        api ??= Api();
        SupplierReturnInfo card;
        try
        {
            card = SupplierApiService.ParseReturn(await api.GetDocumentAsync(ReturnDocKind.Supplier, listed.Id, ct).ConfigureAwait(true));
        }
        catch (ReturnEditException e)
        {
            return new SupplierEditPrep(null, ReturnEditErrors.Text(e), false);
        }
        var stale = listed.EditState.Version > 0 && card.EditState.Version != listed.EditState.Version;
        if (card.EditState.IsVoided || (card.EditState.HasServerFlags && !ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, card.EditState)))
            return new SupplierEditPrep(null, ReturnEditErrors.NotEditableText(card.EditState), true); // 2026-11-11: причина сервера
        if (!ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, card.EditState))
            return new SupplierEditPrep(null, T("Сервер не прислал разрешение на правку этого возврата (can_edit, version) — возможно, возврат создан до включения правки. Обратитесь в техподдержку NurCRM.",
                "Сервер бул кайтарууну оңдоого уруксат жөнөткөн жок (can_edit, version) — балким, кайтаруу оңдоо киргизилгенге чейин түзүлгөн. NurCRM техникалык колдоосуна кайрылыңыз.",
                "The server did not send permission to edit this return (can_edit, version) — the return may have been created before editing was enabled. Contact NurCRM support.",
                "Sunucu bu iadeyi düzenleme iznini göndermedi (can_edit, version) — iade, düzenleme açılmadan önce oluşturulmuş olabilir. NurCRM desteğine başvurun.",
                "Server bu qaytarishni tahrirlash ruxsatini yubormadi (can_edit, version) — qaytarish tahrirlash yoqilishidan oldin yaratilgan bo'lishi mumkin. NurCRM texnik yordamiga murojaat qiling."), stale);
        if (card.Lines.Count == 0)
            return new SupplierEditPrep(null, T("Сервер не прислал строки возврата — изменить состав нельзя. Обновите список.", "Сервер кайтаруунун саптарын жөнөткөн жок — курамын өзгөртүүгө болбойт. Тизмени жаңыртыңыз.",
                "The server did not send the return lines — the contents cannot be changed. Refresh the list.", "Sunucu iade satırlarını göndermedi — içerik değiştirilemez. Listeyi yenileyin.",
                "Server qaytarish qatorlarini yubormadi — tarkibni o'zgartirib bo'lmaydi. Ro'yxatni yangilang."), stale);
        if (stale)
            PosLogger.Log($"Возврат поставщику {listed.Id}: в списке v{listed.EditState.Version}, на сервере v{card.EditState.Version} — окно правки открыто по свежей карточке.", "INFO");
        return new SupplierEditPrep(card, null, stale);
    }

    /// <summary>Открыть окно правки: сначала свежая карточка с сервера (строки с id, версия, can_edit по времени сервера). true — список перечитать.
    /// 2026-11-12: общая точка входа «Править накладную» для всех мест (списки, карточка поставщика, окно возврата после оформления, программа владельца).</summary>
    public static async Task<bool> EditSupplierReturnAsync(Window owner, SupplierReturnInfo listed, string number, ReturnEditApi? api = null)
    {
        SupplierEditPrep prep;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            prep = await PrepareSupplierEditAsync(listed, api, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            prep = new SupplierEditPrep(null, ReturnEditErrors.Text(ReturnEditErrors.Classify(ex)), false);
        }
        if (prep.Card is not { } card)
        {
            if (prep.Message is { Length: > 0 } message)
                await Say(owner, message).ConfigureAwait(true);
            return prep.Reload;
        }
        var saved = await new SupplierReturnEditDialog(card, number, api).ShowDialog<bool>(owner).ConfigureAwait(true);
        if (saved) // 2026-11-11: остатки товаров возврата пересчитал сервер — перечитать
            foreach (var pid in card.Lines.Select(l => l.ProductId).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                StockSyncService.ScheduleStockReread(pid, TimeSpan.FromSeconds(2));
        return saved || prep.Reload;
    }

    // ───────────── 2026-11-11: возврат от клиента (сервер Умара, pos_return_edit_24h) ─────────────

    private static string Str(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number ? v.ToString() : "";

    /// <summary>id товаров строк документа (product_id / product — строка или объект с id).</summary>
    internal static List<string> ProductIdsOf(System.Text.Json.JsonElement card)
    {
        var ids = new List<string>();
        if (card.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ids;
        foreach (var arrName in new[] { "items", "lines" })
        {
            if (!card.TryGetProperty(arrName, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var it in arr.EnumerateArray())
            {
                var id = Str(it, "product_id");
                if (id.Length == 0 && it.ValueKind == System.Text.Json.JsonValueKind.Object && it.TryGetProperty("product", out var p))
                    id = p.ValueKind == System.Text.Json.JsonValueKind.Object ? Str(p, "id") : Str(it, "product");
                if (id.Length > 0 && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                    ids.Add(id);
            }
            break;
        }
        return ids;
    }

    /// <summary>После правки/аннулирования остаток товаров документа поменял сервер — плитки каталога и склад перечитывают его (очередь перечитывания остатка).</summary>
    internal static void RefreshStock(System.Text.Json.JsonElement card)
    {
        try
        {
            foreach (var id in ProductIdsOf(card))
                StockSyncService.ScheduleStockReread(id, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возвраты: перечитывание остатков после изменения не поставлено ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>Подпись возврата от клиента: свой номер документа сервера, иначе «№ чека · дата время», иначе «от дд.мм чч:мм».
    /// 2026-11-11 (живой показ 1.17.85, Д-7): без номера чека показывался кусок технического id («№7328e9cf») — теперь его не показываем.</summary>
    public static string CustomerNumber(NurCrmReportsApi.PosReturn r)
    {
        var when = r.CreatedAt is { } at ? at.LocalDateTime.ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null;
        if (!string.IsNullOrWhiteSpace(r.Number))
            return "№" + r.Number!.Trim().TrimStart('№', '#') + (when is null ? "" : " · " + when);
        if (r.SaleNumber is { } n)
            return "№" + n.ToString("000000", System.Globalization.CultureInfo.InvariantCulture) + (when is null ? "" : " · " + when);
        return when is null ? "" : T($"от {when}", $"{when} күнкү", $"of {when}", $"{when} tarihli", $"{when} dagi");
    }

    public static ReturnDocRef CustomerDoc(NurCrmReportsApi.PosReturn r) =>
        new(ReturnDocKind.Customer, r.Id, CustomerNumber(r), (double)r.Amount, r.Edit ?? ReturnEditState.None);

    /// <summary>Кнопки «Редактировать / Удалить / История» у возврата от клиента (+ пояснение, если сервер не разрешает).</summary>
    public static Control CustomerBar(Control host, ReturnDocRef doc, Func<Task> reload, ReturnEditApi? api = null) =>
        Bar(host, doc, w => EditCustomerReturnAsync(w, doc, api), reload);

    /// <summary>id чека у карточки возврата: sale (строка или объект), sale_id.</summary>
    internal static string SaleIdOf(System.Text.Json.JsonElement card)
    {
        if (Str(card, "sale_id") is { Length: > 0 } a)
            return a;
        if (card.ValueKind == System.Text.Json.JsonValueKind.Object && card.TryGetProperty("sale", out var s))
        {
            if (s.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
                return s.ToString();
            if (s.ValueKind == System.Text.Json.JsonValueKind.Object)
                return Str(s, "id");
        }
        return "";
    }

    /// <summary>Открыть окно правки возврата от клиента: свежая карточка возврата (версия, can_edit по времени сервера) и чека (максимумы). true — список перечитать.</summary>
    public static async Task<bool> EditCustomerReturnAsync(Window owner, ReturnDocRef doc, ReturnEditApi? api = null)
    {
        api ??= Api();
        System.Text.Json.JsonElement card;
        try
        {
            card = await api.GetDocumentAsync(ReturnDocKind.Customer, doc.Id).ConfigureAwait(true);
        }
        catch (ReturnEditException e)
        {
            await Say(owner, ReturnEditErrors.Text(e)).ConfigureAwait(true);
            return false;
        }
        var state = ReturnEditState.Parse(card, DateTime.UtcNow);
        // 2026-11-13 (проверка денег, п. 2): прошлая правка/аннулирование дошли до сервера, а ответ потерялся (или возврат изменили в другом месте) — раньше при повторном открытии окно
        // показывало уже новые количества, «Изменений нет», и журнал с сменой так и оставались со старой суммой. Теперь при открытии сумма документа сверяется с журналом кассы, разница — в смену.
        var lateNote = ReconcileOnOpen(doc, card, state);
        if (lateNote.Length > 0)
            await Say(owner, T("Этот возврат уже изменён на сервере (прошлая отправка дошла или его меняли в другом месте). Журнал кассы и смена поправлены по данным сервера.",
                "Бул кайтаруу серверде мурунтан эле өзгөртүлгөн (мурунку жөнөтүү жеткен же аны башка жерде өзгөрткөн). Кассанын журналы жана смена сервердин маалыматы боюнча оңдолду.",
                "This return has already been changed on the server (the previous send arrived or it was changed elsewhere). The till journal and shift were corrected from the server data.",
                "Bu iade sunucuda zaten değiştirilmiş (önceki gönderim ulaştı veya başka yerde değiştirildi). Kasa günlüğü ve vardiya sunucu verisine göre düzeltildi.",
                "Bu qaytarish serverda allaqachon o'zgartirilgan (oldingi yuborish yetib borgan yoki boshqa joyda o'zgartirilgan). Kassa jurnali va smena server ma'lumoti bo'yicha tuzatildi.")
                + "\n\n" + lateNote, warning: false).ConfigureAwait(true);
        // 2026-11-12 (аудит С-2): версия в списке устарела — НЕ отказ (как у возврата поставщику): окно откроется по свежей карточке, список перечитается после закрытия
        var stale = doc.State.Version > 0 && state.Version != doc.State.Version;
        if (stale)
            PosLogger.Log($"Возврат от клиента {doc.Id}: в списке v{doc.State.Version}, на сервере v{state.Version} — окно правки открыто по свежей карточке.", "INFO");
        if (!ReturnEditGate.ShowEdit(ReturnDocKind.Customer, state))
        {
            await Say(owner, ReturnEditErrors.NotEditableText(state)).ConfigureAwait(true);
            return true;
        }
        var sale = default(System.Text.Json.JsonElement);
        if (SaleIdOf(card) is { Length: > 0 } saleId)
        {
            try
            {
                sale = await api.GetSaleAsync(saleId).ConfigureAwait(true);
            }
            catch (ReturnEditException e)
            {
                // без чека окно всё равно откроется: количество можно только уменьшать (максимум = текущее)
                PosLogger.Log($"Возврат от клиента {doc.Id}: чек {saleId} для максимумов не получен ({e.Failure}) — увеличивать количество нельзя.", "WARNING");
            }
        }
        var lines = CustomerReturnEdit.FromCards(card, sale);
        if (lines.Count == 0)
        {
            await Say(owner, T("Сервер не прислал строки возврата — изменить состав нельзя. Обновите список.", "Сервер кайтаруунун саптарын жөнөткөн жок — курамын өзгөртүүгө болбойт. Тизмени жаңыртыңыз.",
                "The server did not send the return lines — the contents cannot be changed. Refresh the list.", "Sunucu iade satırlarını göndermedi — içerik değiştirilemez. Listeyi yenileyin.",
                "Server qaytarish qatorlarini yubormadi — tarkibni o'zgartirib bo'lmaydi. Ro'yxatni yangilang.")).ConfigureAwait(true);
            return false;
        }
        // 2026-11-12 (аудит К-1, В-1): сумма документа и номер чека — из свежей карточки; после правки журналы кассы обновлены окном, кассиру — что делать с деньгами
        var dialog = new CustomerReturnEditDialog(doc with { State = state, Total = (double)(CustomerReturnEdit.DocAmount(card) ?? (decimal)doc.Total) }, lines, api, SaleIdOf(card));
        var saved = await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        if (saved)
        {
            RefreshStock(card);
            if (dialog.JournalChange is { SaleId.Length: > 0 } journal)
                SaleDetailCache.Forget(journal.SaleId);
            if (dialog.MoneyNote.Length > 0)
                await Say(owner, T("Возврат изменён. ", "Кайтаруу өзгөртүлдү. ", "The return was changed. ", "İade değiştirildi. ", "Qaytarish o'zgartirildi. ") + dialog.MoneyNote, warning: false).ConfigureAwait(true);
        }
        return saved || stale;
    }

    /// <summary>Возвраты одного чека (с аннулированными, ?status=all). null — сервер недоступен.</summary>
    public static async Task<List<NurCrmReportsApi.PosReturn>?> LoadSaleReturnsAsync(string saleId, CancellationToken ct = default)
    {
        var list = await NurCrmReportsApi.ListReturnsAsync(null, null, saleId: saleId, ct: ct, includeVoided: ReturnEditGate.FeatureOn(ReturnDocKind.Customer)).ConfigureAwait(true);
        // 2026-11-13 (проверка денег, п. 3): список возвратов чека пришёл — журнал и смена кассы сверяются с ним (возврат правили/аннулировали в программе владельца, на сайте, другой кассой)
        if (list is not null)
            ReturnJournalSync.ReconcileDocs(list.Where(r => r.SaleId is null || string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase)).Select(CustomerReturnReconciler.ToDoc).ToList());
        return list?.Where(r => r.SaleId is null || string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.CreatedAt).ToList();
    }

    /// <summary>Окно «Возвраты по чеку»: каждый возврат (когда, что, сумма, «АННУЛИРОВАН») с кнопками «Редактировать / Удалить / История» и пояснением.
    /// true — что-то изменили (вызывающий перечитывает чек, список, долг и остатки).</summary>
    public static async Task<bool> ShowCustomerReturnsAsync(Window owner, string saleId, string receiptNumber, ReturnEditApi? api = null)
    {
        var changed = false;
        var list = new StackPanel { Spacing = 10 };
        var w = new Window { Title = T("Возвраты по чеку", "Чек боюнча кайтаруулар", "Returns on the receipt", "Fişteki iadeler", "Chek bo'yicha qaytarishlar") };
        SectionCards.AsModal(w, 620);
        async Task RenderAsync()
        {
            list.Children.Clear();
            // 2026-11-12 (аудит С-13): до 15 с окно было пустым — теперь «Загрузка…», а при сбое — кнопка «Повторить»
            list.Children.Add(SectionCards.Empty(w, T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            List<NurCrmReportsApi.PosReturn>? returns;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                returns = await LoadSaleReturnsAsync(saleId, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                returns = null;
            }
            list.Children.Clear();
            if (returns is null || returns.Count == 0)
            {
                list.Children.Add(SectionCards.Empty(w, returns is null
                    ? T("Возвраты не загрузились — нет связи с сервером.", "Кайтаруулар жүктөлгөн жок — сервер менен байланыш жок.",
                        "Returns did not load — no connection to the server.", "İadeler yüklenmedi — sunucuyla bağlantı yok.",
                        "Qaytarishlar yuklanmadi — server bilan aloqa yo'q.")
                    : T("По этому чеку возвратов нет.", "Бул чек боюнча кайтаруу жок.", "There are no returns on this receipt.", "Bu fişte iade yok.", "Bu chek bo'yicha qaytarishlar yo'q.")));
                if (returns is null)
                {
                    var retry = SectionCards.Secondary(T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
                    retry.Name = "CustomerReturnsRetryButton";
                    retry.HorizontalAlignment = HorizontalAlignment.Center;
                    retry.Click += async (_, _) => await RenderAsync().ConfigureAwait(true);
                    list.Children.Add(retry);
                }
                return;
            }
            foreach (var r in returns)
            {
                var doc = CustomerDoc(r);
                var box = new StackPanel { Spacing = 4 };
                var head = new WrapPanel { Orientation = Orientation.Horizontal };
                // 2026-11-12 (аудит 12.11, Н-7): два возврата одного чека выглядели одинаково — впереди номер документа сервера, без него — «Возврат 1 из 2»
                var ordinal = returns.Count > 1 || !string.IsNullOrWhiteSpace(r.Number)
                    ? (!string.IsNullOrWhiteSpace(r.Number)
                        ? T("Возврат ", "Кайтаруу ", "Return ", "İade ", "Qaytarish ") + "№" + r.Number!.Trim().TrimStart('№', '#')
                        : T($"Возврат {returns.IndexOf(r) + 1} из {returns.Count}", $"Кайтаруу {returns.IndexOf(r) + 1}/{returns.Count}", $"Return {returns.IndexOf(r) + 1} of {returns.Count}",
                            $"İade {returns.IndexOf(r) + 1}/{returns.Count}", $"Qaytarish {returns.IndexOf(r) + 1}/{returns.Count}")) + " · "
                    : "";
                var caption = new TextBlock
                {
                    Text = ordinal + (r.CreatedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "—") + " · " + MoneyText.Som(r.Amount),
                    FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap,
                };
                caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushText"));
                head.Children.Add(caption);
                if (VoidedPill(w, doc.State) is { } pill)
                    head.Children.Add(pill);
                box.Children.Add(head);
                var what = string.IsNullOrWhiteSpace(r.ItemsText)
                    ? (r.IsFull ? T("весь чек", "бүт чек", "whole receipt", "tüm fiş", "butun chek") : "")
                    : r.ItemsText!;
                if (!string.IsNullOrWhiteSpace(r.Reason))
                    what += (what.Length > 0 ? " · " : "") + r.Reason;
                if (doc.State.IsVoided && doc.State.VoidReason.Length > 0)
                    what += (what.Length > 0 ? " · " : "") + ReturnEditErrors.VoidedMark() + ": " + doc.State.VoidReason;
                if (doc.State.EditCount > 0) // 2026-11-11 (сводка Умара): edit_count из списка — после правки видно, что возврат меняли
                    what += (what.Length > 0 ? " · " : "") + EditedTimesText(doc.State.EditCount);
                if (what.Length > 0)
                {
                    var t = new TextBlock { Text = what, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                    t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
                    box.Children.Add(t);
                }
                box.Children.Add(CustomerBar(w, doc, async () =>
                {
                    changed = true;
                    await RenderAsync().ConfigureAwait(true);
                }, api));
                list.Children.Add(SectionCards.Group(w, box));
            }
        }
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => w.Close();
        w.Content = SectionCards.Shell(w.Title, T("Чек ", "Чек ", "Receipt ", "Fiş ", "Chek ") + receiptNumber, list, SectionCards.Buttons(close));
        w.Opened += async (_, _) => await RenderAsync().ConfigureAwait(true);
        await w.ShowDialog(owner).ConfigureAwait(true);
        return changed;
    }

    /// <summary>2026-11-11: «Изменён N раз» (edit_count сервера) — 5 языков.</summary>
    public static string EditedTimesText(int count) => T($"изменён {count} раз(а)", $"{count} жолу өзгөртүлгөн", $"edited {count} time(s)", $"{count} kez düzenlendi", $"{count} marta o'zgartirilgan");

    /// <summary>Журнал версий (только руководителю; 403 сервера покажется текстом).</summary>
    public static async Task ShowHistoryAsync(Window owner, ReturnDocRef doc, ReturnEditApi? api = null)
    {
        api ??= Api();
        var dialog = new ReturnHistoryDialog(KindName(doc.Kind) + " " + doc.Number, ct => api.GetHistoryAsync(doc.Kind, doc.Id, ct));
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}

/// <summary>2026-11-11: кнопка-вход «Правка возвратов» под чеком (история чеков, покупки клиента) + пояснение, если сервер возвраты этого чека менять не даёт
/// (например, чек в долг — кнопок нет, только причина). Видна только при признаке pos_return_edit_24h и если у возвратов чека есть что делать
/// (can_edit / can_void, история руководителю). <see cref="Changed"/> — после правки/аннулирования (перечитать чек, долг, остатки).</summary>
public sealed class CustomerReturnsEntry
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string _saleId = "";
    private string _receipt = "";
    private int _gen;

    public StackPanel View { get; } = new() { Spacing = 4, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    public Button Button { get; }
    public Func<Task>? Changed { get; set; }

    /// <summary>2026-11-12 (программа владельца: карточка чека — всплывающее окно Popup, у него нет Window): окно-владелец для «Возвратов по чеку»;
    /// null — окно кнопки, как раньше.</summary>
    public Func<Window?>? OwnerWindow { get; set; }

    /// <summary>2026-11-12: перед открытием «Возвратов по чеку» (закрыть карточку чека — всплывающее окно лежало бы поверх).</summary>
    public Action? BeforeOpen { get; set; }

    public CustomerReturnsEntry()
    {
        Button = new Button
        {
            Content = T("Правка возвратов", "Кайтарууларды оңдоо", "Edit returns", "İadeleri düzenle", "Qaytarishlarni tahrirlash"),
            Name = "CustomerReturnsEditButton", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false,
        };
        Button.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(Button, (string)Button.Content);
        Button.Click += async (_, _) =>
        {
            if (_saleId.Length == 0 || (OwnerWindow?.Invoke() ?? TopLevel.GetTopLevel(Button) as Window) is not { } owner)
                return;
            Button.IsEnabled = false;
            try
            {
                BeforeOpen?.Invoke();
                if (await ReturnEditFlows.ShowCustomerReturnsAsync(owner, _saleId, _receipt).ConfigureAwait(true) && Changed is { } changed)
                    await changed().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Правка возвратов по чеку {_saleId}: окно не открылось ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
            finally
            {
                Button.IsEnabled = true;
            }
        };
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("BrushTextSoft"));
        View.Children.Add(Button);
        View.Children.Add(_hint);
    }

    public void Hide()
    {
        _gen++;
        _saleId = "";
        View.IsVisible = Button.IsVisible = _hint.IsVisible = false;
    }

    /// <summary>Показать по уже загруженному списку возвратов чека.</summary>
    public void ShowFor(string saleId, string receipt, IReadOnlyList<NurCrmReportsApi.PosReturn> returns)
    {
        _gen++;
        _saleId = saleId;
        _receipt = receipt;
        var on = ReturnEditGate.FeatureOn(ReturnDocKind.Customer) && saleId.Length > 0 && !saleId.StartsWith("local", StringComparison.Ordinal);
        var states = on ? returns.Select(r => r.Edit ?? ReturnEditState.None).ToList() : new List<ReturnEditState>();
        var manager = ReturnEditFlows.IsManager;
        var any = states.Any(s => ReturnEditGate.ShowEdit(ReturnDocKind.Customer, s) || ReturnEditGate.ShowVoid(ReturnDocKind.Customer, s)
                                  || ReturnEditGate.ShowHistory(ReturnDocKind.Customer, s, manager));
        var hint = states.Select(s => ReturnEditGate.BlockHint(ReturnDocKind.Customer, s)).FirstOrDefault(h => h is not null);
        Button.IsVisible = any;
        _hint.Text = hint is null ? "" : "ⓘ " + T("Возврат по этому чеку: ", "Бул чек боюнча кайтаруу: ", "Return on this receipt: ", "Bu fişteki iade: ", "Bu chek bo'yicha qaytarish: ") + hint;
        // 2026-11-12 (аудит С-12): кнопка «Правка возвратов» есть (какой-то возврат править можно) — подсказка одного заблокированного возврата сбивала: «24 часа прошли — нельзя» рядом с кнопкой.
        // Тогда говорим о части возвратов, а причина — у каждого возврата в окне.
        if (any && hint is not null)
        {
            var blocked = states.Count(s => ReturnEditGate.BlockHint(ReturnDocKind.Customer, s) is not null);
            _hint.Text = "ⓘ " + T($"У {blocked} из {states.Count} возвратов по этому чеку изменить нельзя — причина у каждого возврата в окне «Правка возвратов».",
                $"Бул чек боюнча {states.Count} кайтаруунун {blocked} өзгөртүүгө болбойт — себеби «Кайтарууларды оңдоо» терезесинде ар бир кайтарууда.",
                $"{blocked} of {states.Count} returns on this receipt cannot be changed — the reason is shown for each return in the “Edit returns” window.",
                $"Bu fişteki {states.Count} iadenin {blocked} tanesi değiştirilemez — neden «İadeleri düzenle» penceresinde her iadede gösterilir.",
                $"Bu chek bo'yicha {states.Count} ta qaytarishdan {blocked} tasini o'zgartirib bo'lmaydi — sababi «Qaytarishlarni tahrirlash» oynasida har bir qaytarishda ko'rsatiladi.");
        }
        _hint.IsVisible = hint is not null;
        View.IsVisible = Button.IsVisible || _hint.IsVisible;
    }

    /// <summary>2026-11-12 (аудит С-14): список возвратов чека не загрузился (таймаут 8 с, нет связи) — раньше кнопок правки просто не было, без объяснения; теперь строка-пояснение.</summary>
    private void ShowLoadFailed()
    {
        _hint.Text = "ⓘ " + T("Не удалось проверить возвраты этого чека (нет связи с сервером) — кнопки правки скрыты. Обновите карточку чека, когда связь появится.",
            "Бул чектин кайтарууларын текшерүү мүмкүн болгон жок (сервер менен байланыш жок) — оңдоо баскычтары жашырылды. Байланыш пайда болгондо чектин карточкасын жаңыртыңыз.",
            "Could not check the returns of this receipt (no connection to the server) — the edit buttons are hidden. Refresh the receipt card when the connection is back.",
            "Bu fişin iadeleri kontrol edilemedi (sunucuyla bağlantı yok) — düzenleme düğmeleri gizlendi. Bağlantı gelince fiş kartını yenileyin.",
            "Bu chekning qaytarishlarini tekshirib bo'lmadi (server bilan aloqa yo'q) — tahrirlash tugmalari yashirildi. Aloqa paydo bo'lganda chek kartasini yangilang.");
        _hint.IsVisible = true;
        Button.IsVisible = false;
        View.IsVisible = true;
    }

    /// <summary>Загрузить возвраты чека (с аннулированными) и показать кнопку; без признака сервера — сразу скрыто, без запроса.</summary>
    public async Task LoadForAsync(string saleId, string receipt)
    {
        Hide();
        if (!ReturnEditGate.FeatureOn(ReturnDocKind.Customer) || string.IsNullOrWhiteSpace(saleId) || saleId.StartsWith("local", StringComparison.Ordinal))
            return;
        var gen = _gen;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var list = await ReturnEditFlows.LoadSaleReturnsAsync(saleId, cts.Token).ConfigureAwait(true);
            if (gen != _gen)
                return;
            if (list is null)
            {
                ShowLoadFailed();
                return;
            }
            ShowFor(saleId, receipt, list);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Правка возвратов: возвраты чека {saleId} не загружены ({ex.GetType().Name}).", "WARNING");
            if (gen == _gen)
                ShowLoadFailed();
        }
    }
}
