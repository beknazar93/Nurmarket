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
/// Для «Возврата от клиента» точки подключения — у другого исполнителя (окна чеков/возвратов): <c>ReturnEditFlows.Bar(host, new ReturnDocRef(ReturnDocKind.Customer, …), edit: null, reload)</c>.
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

    /// <summary>Подсказка «Можно изменить ещё N ч» — только по времени сервера из ответа (editable_until − server_now); null — сервер не сказал.</summary>
    public static string? TimeLeftHint(ReturnEditState s)
    {
        if (s.TimeLeft() is not { } left || !(s.CanEdit || s.CanVoid))
            return null;
        if (left.TotalMinutes < 60)
        {
            var m = Math.Max(1, (int)left.TotalMinutes);
            return T($"Изменить или удалить можно ещё {m} мин", $"Дагы {m} мүн. өзгөртүүгө же өчүрүүгө болот", $"Can be changed or deleted for {m} more min",
                $"{m} dk daha değiştirilebilir veya silinebilir", $"Yana {m} daqiqa o'zgartirish yoki o'chirish mumkin");
        }
        var h = (int)left.TotalHours;
        return T($"Изменить или удалить можно ещё {h} ч", $"Дагы {h} саат өзгөртүүгө же өчүрүүгө болот", $"Can be changed or deleted for {h} more h",
            $"{h} sa daha değiştirilebilir veya silinebilir", $"Yana {h} soat o'zgartirish yoki o'chirish mumkin");
    }

    /// <summary>Плашка «АННУЛИРОВАН» (null — документ действующий).</summary>
    public static Control? VoidedPill(Control host, ReturnEditState s) =>
        s.IsVoided ? SectionCards.Pill(host, ReturnEditErrors.VoidedMark(), "BrushDangerSoft", "BrushDangerOnSoft") : null;

    /// <summary>Строка кнопок правки документа. Пустая и скрытая, если сервер ничего не разрешил (или нет признака). <paramref name="edit"/> — открыть окно правки
    /// (null — у типа его пока нет, кнопки «Редактировать» не будет); <paramref name="reload"/> — перечитать список после изменения.</summary>
    public static Control Bar(Control host, ReturnDocRef doc, Func<Window, Task<bool>>? edit, Func<Task> reload)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var showEdit = edit is not null && ReturnEditGate.ShowEdit(doc.Kind, doc.State);
        var showVoid = ReturnEditGate.ShowVoid(doc.Kind, doc.State);
        var showHistory = ReturnEditGate.ShowHistory(doc.Kind, doc.State, IsManager);
        Window? Owner() => TopLevel.GetTopLevel(host) as Window;
        if (showEdit)
            row.Children.Add(Small(T("Редактировать", "Оңдоо", "Edit", "Düzenle", "Tahrirlash"), async () =>
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
        var panel = new StackPanel { Spacing = 2, IsVisible = row.Children.Count > 0 };
        panel.Children.Add(row);
        if ((showEdit || showVoid) && TimeLeftHint(doc.State) is { } hint)
        {
            var t = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(t);
        }
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

        var input = new SimpleInputDialog(caption, T("Причина удаления (от 5 знаков):", "Өчүрүү себеби (5 белгиден):", "Reason for deletion (at least 5 characters):", "Silme nedeni (en az 5 karakter):", "O'chirish sababi (kamida 5 belgi):"),
            confirmText: T("Далее", "Андан ары", "Next", "İleri", "Keyingi"));
        if (!await input.ShowDialog<bool>(owner).ConfigureAwait(true))
            return false;
        var reason = input.Result.Trim();
        if (reason.Length is < SupplierReturnEdit.ReasonMin or > SupplierReturnEdit.ReasonMax)
        {
            await Say(owner, T("Причина должна быть от 5 до 500 знаков. Ничего не удалено.", "Себеп 5тен 500 белгиге чейин болушу керек. Эч нерсе өчүрүлгөн жок.",
                "The reason must be 5 to 500 characters. Nothing was deleted.", "Neden 5 ile 500 karakter arasında olmalı. Hiçbir şey silinmedi.", "Sabab 5 dan 500 belgigacha bo'lishi kerak. Hech narsa o'chirilmadi.")).ConfigureAwait(true);
            return false;
        }

        // Свежая проверка на сервере (срок по времени сервера, версия): устаревший список не даёт удалить документ, который уже изменили.
        ReturnEditState fresh;
        try
        {
            fresh = await api.GetStateAsync(doc.Kind, doc.Id).ConfigureAwait(true);
        }
        catch (ReturnEditException e)
        {
            await Say(owner, ReturnEditErrors.Text(e)).ConfigureAwait(true);
            return false;
        }
        if (fresh.IsVoided)
        {
            await Say(owner, ReturnEditErrors.Text(new ReturnEditException(ReturnEditFailure.AlreadyVoided, "already_voided", ""))).ConfigureAwait(true);
            return true;
        }
        if (doc.State.Version > 0 && fresh.Version != doc.State.Version)
        {
            await Say(owner, ReturnEditErrors.VersionConflictText()).ConfigureAwait(true);
            return true;
        }
        if (!ReturnEditGate.ShowVoid(doc.Kind, fresh))
        {
            await Say(owner, ReturnEditErrors.Text(new ReturnEditException(fresh.BlockReason == "permission_denied" ? ReturnEditFailure.PermissionDenied : ReturnEditFailure.WindowExpired, fresh.BlockReason, ""))).ConfigureAwait(true);
            return true;
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
            await Say(owner, T($"Возврат {doc.Number} аннулирован. Склад и расчёты пересчитаны сервером.", $"{doc.Number} кайтаруусу жокко чыгарылды. Кампа жана эсептешүүлөр сервер тарабынан кайра эсептелди.",
                $"Return {doc.Number} was voided. Stock and settlements were recalculated by the server.", $"{doc.Number} iadesi iptal edildi. Stok ve hesaplar sunucu tarafından yeniden hesaplandı.",
                $"{doc.Number} qaytarish bekor qilindi. Ombor va hisob-kitoblar server tomonidan qayta hisoblandi."), warning: false).ConfigureAwait(true);
            return true;
        }
        catch (ReturnEditException e)
        {
            key.OnError(e.InnerException ?? e); // исход неизвестен (обрыв, 5xx) — ключ остаётся для повтора; отказ по существу — забыт
            await Say(owner, T("Возврат не удалён: ", "Кайтаруу өчүрүлгөн жок: ", "The return was not deleted: ", "İade silinmedi: ", "Qaytarish o'chirilmadi: ") + ReturnEditErrors.Text(e)).ConfigureAwait(true);
            return e.IsFinal;
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

    /// <summary>Кнопки правки у возврата поставщику (окно «Возвраты поставщикам», «Закупки → Возвраты», карточка поставщика).</summary>
    public static Control SupplierBar(Control host, SupplierReturnInfo r, Func<Task> reload)
    {
        var number = SupplierNumber(r);
        return Bar(host, new ReturnDocRef(ReturnDocKind.Supplier, r.Id, number, r.Total, r.EditState), w => EditSupplierReturnAsync(w, r, number), reload);
    }

    /// <summary>Открыть окно правки: сначала свежая карточка с сервера (строки с id, версия, can_edit по времени сервера). true — список перечитать.</summary>
    public static async Task<bool> EditSupplierReturnAsync(Window owner, SupplierReturnInfo listed, string number)
    {
        SupplierReturnInfo card;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            card = await new SupplierApiService(App.GetRequiredService<NurMarketApiClient>()).GetReturnAsync(listed.Id, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Say(owner, ReturnEditErrors.Text(ReturnEditErrors.Classify(ex))).ConfigureAwait(true);
            return false;
        }
        if (listed.EditState.Version > 0 && card.EditState.Version != listed.EditState.Version)
        {
            await Say(owner, ReturnEditErrors.VersionConflictText()).ConfigureAwait(true);
            return true;
        }
        if (!ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, card.EditState))
        {
            await Say(owner, ReturnEditErrors.Text(new ReturnEditException(card.EditState.IsVoided ? ReturnEditFailure.AlreadyVoided : ReturnEditFailure.WindowExpired, card.EditState.BlockReason, ""))).ConfigureAwait(true);
            return true;
        }
        return await new SupplierReturnEditDialog(card, number).ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    /// <summary>Журнал версий (только руководителю; 403 сервера покажется текстом).</summary>
    public static async Task ShowHistoryAsync(Window owner, ReturnDocRef doc, ReturnEditApi? api = null)
    {
        api ??= Api();
        var dialog = new ReturnHistoryDialog(KindName(doc.Kind) + " " + doc.Number, ct => api.GetHistoryAsync(doc.Kind, doc.Id, ct));
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }
}
