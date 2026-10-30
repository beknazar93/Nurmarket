using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-18, владелец: общее для приёма оплаты долга в кассе (PayDebtDialog) и в программе владельца
/// (AcceptDebtPaymentWindow) — подписи способов, код сотрудника, «долг уже погашен», оповещение соседей по LAN,
/// раскладка суммы по чекам (от старых к новым, как гасит сервер). Сами запросы — ClientDebtsApiService.</summary>
internal static class DebtPaymentFlow
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    public static string MethodTitle(string method) => method == DebtPaymentSettings.MethodTransfer
        ? T("Безнал (карта, перевод)", "Накталай эмес (карта, которуу)", "Non-cash (card, transfer)", "Nakitsiz (kart, havale)", "Naqd emas (karta, o'tkazma)")
        : T("Наличные", "Накталай", "Cash", "Nakit", "Naqd");

    /// <summary>2026-10-30: подпись под полосой «Идёт оплата долга…» (владелец: «при оплате долга система зависает»). Сервер гасит
    /// взносы по очереди одним запросом (~0,1–0,15 с на взнос: 122 взноса ≈ 13 с, 113 ≈ 18 с) — прогресса по взносам кассе не видно, поэтому
    /// честно показываем секунды ожидания и что делать. Общая для кассы (PayDebtDialog) и владельца (AcceptDebtPaymentWindow).</summary>
    public static string PayingText(int seconds) => T(
        $"Идёт оплата долга… {seconds} с. Сервер гасит взносы по очереди — при большом долге это может занять до полуминуты. Окно работает, не закрывайте его и не нажимайте «Принять» ещё раз: повторного списания не будет.",
        $"Карыз төлөнүүдө… {seconds} с. Сервер төгүмдөрдү кезеги менен жабат — карыз чоң болсо жарым мүнөткө чейин созулушу мүмкүн. Терезе иштеп жатат, аны жаппаңыз жана «Кабыл алуу» баскычын кайра баспаңыз: экинчи жолу алынбайт.",
        $"Paying the debt… {seconds} s. The server clears the instalments one by one — a large debt can take up to half a minute. The window is working: don't close it and don't press “Accept” again, you won't be charged twice.",
        $"Borç ödeniyor… {seconds} sn. Sunucu taksitleri sırayla kapatır — büyük borçta yarım dakikaya kadar sürebilir. Pencere çalışıyor, kapatmayın ve «Al» düğmesine tekrar basmayın: ikinci kez tahsil edilmez.",
        $"Qarz to'lanmoqda… {seconds} s. Server badallarni navbat bilan yopadi — katta qarzda yarim daqiqagacha cho'zilishi mumkin. Oyna ishlayapti, uni yopmang va «Qabul qilish»ni qayta bosmang: ikkinchi marta yechilmaydi.");

    /// <summary>Для журнала (по-русски, как остальные строки PAYMENT).</summary>
    public static string MethodLog(string method) => method == DebtPaymentSettings.MethodTransfer ? "безнал" : "наличные";

    /// <summary>Сервер отказал: долга уже нет (400 no_debt) — оплатили в другой кассе, у владельца или на сайте.</summary>
    public static bool IsNoDebt(Exception ex) =>
        ex is ApiException { StatusCode: 400 } api
        && ((api.Payload?.GetRawText() ?? "") + " " + api.Message).Contains("no_debt", StringComparison.OrdinalIgnoreCase);

    public static string NoDebtMessage => T(
        "Долг уже погашен (возможно, оплачен в другой кассе или у владельца). Список обновлён.",
        "Карыз мурунтан эле жабылган (балким, башка кассада же ээсинде төлөнгөн). Тизме жаңыртылды.",
        "The debt is already paid off (perhaps at another till or by the owner). The list has been refreshed.",
        "Borç zaten kapanmış (belki başka bir kasada veya sahibinde ödendi). Liste yenilendi.",
        "Qarz allaqachon yopilgan (ehtimol, boshqa kassada yoki egasida to'langan). Ro'yxat yangilandi.");

    public static string MethodChangedMessage => T(
        "Способы оплаты долга изменились в настройках — проверьте способ и нажмите ещё раз.",
        "Карыз төлөө ыкмалары жөндөөлөрдө өзгөрдү — ыкманы текшерип, кайра басыңыз.",
        "The debt payment methods changed in the settings — check the method and press again.",
        "Borç ödeme yöntemleri ayarlarda değişti — yöntemi kontrol edip tekrar basın.",
        "Qarz to'lash usullari sozlamalarda o'zgardi — usulni tekshirib, qayta bosing.");

    public static string ChangedElsewhereMessage => T(
        "Долг клиента изменился (оплата в другой кассе или у владельца) — список обновлён.",
        "Кардардын карызы өзгөрдү (башка кассада же ээсинде төлөм) — тизме жаңыртылды.",
        "The client's debt changed (paid at another till or by the owner) — the list has been refreshed.",
        "Müşterinin borcu değişti (başka kasada veya sahibinde ödeme) — liste yenilendi.",
        "Mijozning qarzi o'zgardi (boshqa kassada yoki egasida to'lov) — ro'yxat yangilandi.");

    /// <summary>Код сотрудника на приём оплаты (EmployeeAccessGate.DebtPayment): включён в «Оплате долга» и вошёл не
    /// владелец/администратор — спросить, как у других действий. true — можно принимать.</summary>
    /// <remarks>2026-10-18, ревью безопасности: «закрыто при неизвестности» — если настройки «Оплата долга» для этой компании
    /// ни разу не получены (свежая установка, смена компании) и получить их сейчас не удалось, сотрудник (не владелец и не
    /// администратор) оплату не принимает: иначе «нужен код» молча считался бы выключенным. Error — текст для окна.</remarks>
    public static async Task<(bool Granted, string? Error)> ConfirmEmployeeCodeAsync()
    {
        // Настройка «нужен код» — свежая (не старше 30 с; сбой сети — последние полученные значения).
        // 2026-10-18, третий круг ревью: перед каждой оплатой GET не ждём — настройки этой компании известны (получены раньше,
        // опрос раз в 2 мин обновляет их сам) → кэш; несвежие — обновить фоном. Неизвестны — ждём принудительный GET.
        // 2026-10-18, четвёртый круг ревью — асимметрично: кэш «код нужен» — берём сразу; кэш «код НЕ нужен» и он старше
        // 2 мин — ждём свежий GET (до 8 с), не удался — код требуется (закрыто при неизвестности).
        // 2026-10-18, пятый круг ревью (п.8): владелец/администратор — сразу, без запроса настроек.
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        // Закрыто при неизвестности — нет службы прав = НЕ владелец/админ.
        var ownerOrAdmin = permissions?.HasPermission(PosPermissions.ViewSettings) ?? false;
        if (ownerOrAdmin)
            return (true, null);
        var forceCode = false;
        if (!DebtPaymentSettings.KnownForCurrentCompany)
            await DebtPaymentSettings.RefreshAsync(force: true).ConfigureAwait(true);
        else if (!DebtPaymentSettings.IsFresh)
        {
            if (DebtPaymentSettings.Current.RequireEmployeeCode)
                _ = DebtPaymentSettings.RefreshAsync();
            else if (!await DebtPaymentSettings.RefreshAsync(force: true).ConfigureAwait(true))
                forceCode = true;
        }
        if (!DebtPaymentSettings.KnownForCurrentCompany)
        {
            PosLogger.Log("Оплата долга: настройки «Оплата долга» для этой компании не получены — оплата сотрудником не принята.", "PAYMENT");
            return (false, T("Не удалось получить настройки оплаты долга — проверьте связь и повторите.",
                "Карыз төлөөнүн жөндөөлөрүн алуу мүмкүн болгон жок — байланышты текшерип, кайталаңыз.",
                "Couldn't get the debt payment settings — check the connection and try again.",
                "Borç ödemesi ayarları alınamadı — bağlantıyı kontrol edip tekrar deneyin.",
                "Qarz to'lash sozlamalarini olib bo'lmadi — aloqani tekshirib, qayta urining."));
        }
        if (!forceCode && !EmployeeAccessGate.IsActiveFor(EmployeeAccessGate.DebtPayment, permissions))
            return (true, null);
        if (forceCode && !EmployeeAccessGate.CurrentEmployeeHasCode(EmployeeAccessGate.DebtPayment))
        {
            PosLogger.Log("Оплата долга: настройки не обновились, а кода на приём оплаты у сотрудника нет — оплата не принята.", "PAYMENT");
            return (false, T("Не удалось проверить настройки оплаты долга, а вашего кода на приём оплаты нет — проверьте связь и повторите (или попросите владельца задать код).",
                "Карыз төлөөнүн жөндөөлөрүн текшерүү мүмкүн болгон жок, ал эми сиздин төлөм кабыл алуу кодуңуз жок — байланышты текшерип, кайталаңыз (же ээсинен код коюуну сураңыз).",
                "Couldn't check the debt payment settings and you have no debt-payment code — check the connection and try again (or ask the owner to set a code).",
                "Borç ödemesi ayarları kontrol edilemedi ve borç ödemesi kodunuz yok — bağlantıyı kontrol edip tekrar deneyin (veya sahibinden kod isteyin).",
                "Qarz to'lash sozlamalarini tekshirib bo'lmadi, sizda esa to'lov qabul qilish kodi yo'q — aloqani tekshirib, qayta urining (yoki egasidan kod so'rang)."));
        }
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (prompts is null)
        {
            // 2026-10-18, четвёртый круг: спросить код нечем — отказ с текстом и строкой в журнале (не молча).
            PosLogger.Log("Оплата долга: нужен код сотрудника, но окно ввода кода недоступно (нет IUserPrompts) — оплата не принята.", "PAYMENT");
            return (false, T("Не удалось открыть окно ввода кода сотрудника — оплата не принята. Перезапустите программу.",
                "Кызматкердин кодун киргизүү терезесин ачуу мүмкүн болгон жок — төлөм кабыл алынган жок. Программаны кайра иштетиңиз.",
                "Couldn't open the employee code window — the payment was not taken. Restart the program.",
                "Personel kodu penceresi açılamadı — ödeme alınmadı. Programı yeniden başlatın.",
                "Xodim kodini kiritish oynasini ochib bo'lmadi — to'lov qabul qilinmadi. Dasturni qayta ishga tushiring."));
        }
        var granted = await prompts.ConfirmWithCodeAsync(
            T("Приём оплаты долга", "Карыз төлөмүн кабыл алуу", "Accept a debt payment", "Borç ödemesi alma", "Qarz to'lovini qabul qilish"),
            T("Введите свой код на приём оплаты долга (его задаёт владелец: Настройки → Сотрудники).",
                "Карыз төлөмүн кабыл алуу кодуңузду киргизиңиз (аны ээси коёт: Жөндөөлөр → Кызматкерлер).",
                "Enter your debt-payment code (the owner sets it in Settings → Employees).",
                "Borç ödemesi alma kodunuzu girin (sahibi belirler: Ayarlar → Personel).",
                "Qarz to'lovini qabul qilish kodingizni kiriting (uni egasi o'rnatadi: Sozlamalar → Xodimlar)."),
            entered => EmployeeAccessGate.TryValidate(EmployeeAccessGate.DebtPayment, entered)).ConfigureAwait(true);
        if (!granted)
            PosLogger.Log("Оплата долга: код сотрудника не введён — оплата не принята.", "PAYMENT");
        return (granted, null);
    }

    /// <summary>Оплата прошла: соседям по LAN — «debt_paid» (их окна перечитают долг клиента), этой программе — событие
    /// изменения долга. operationKey — ключ идемпотентности оплаты (повтор не задвоит событие).</summary>
    public static void Announce(string operationKey, string clientId, string? clientName, double paid, double left, string method)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;
        LanJournal.PublishDebtPaid(operationKey, new LanDebtPaidPayload
        {
            ClientId = clientId,
            ClientName = clientName,
            Amount = paid,
            Left = left,
            Method = method,
            Source = AppMode.IsOwner ? "owner" : "kassa",
            PaidAtUtc = DateTime.UtcNow,
        });
        PosDataEvents.RaiseClientDebtChanged(clientId, operationKey);
    }

    /// <summary>2026-10-18, девятый круг (деньги): «точный остаток или отказ» — по этим чекам remaining_debt сделки не получен.</summary>
    /// <summary>2026-10-18, ревью после живой проверки (п.8): « №1, №2…» (до пяти чеков) для текстов про чеки.</summary>
    public static string Which(IEnumerable<DebtSaleRow> rows)
    {
        var list = rows.Select(r => r.Label).Where(x => x.Length > 0).Distinct().ToList();
        return list.Count == 0 ? "" : " " + string.Join(", ", list.Take(5)) + (list.Count > 5 ? "…" : "");
    }

    public static string RemainingUnknownText(IEnumerable<DebtSaleRow> rows, bool owner)
    {
        var which = Which(rows);
        return owner
            ? T($"Не удалось получить точный остаток по чеку{which} — по нему принять оплату нельзя. Нажмите «Повторить».",
                $"Чек{which} боюнча так калдыкты алуу мүмкүн болгон жок — ал боюнча төлөмдү кабыл алууга болбойт. «Кайталоо» басыңыз.",
                $"Couldn't get the exact balance of receipt{which} — a payment for it can't be taken. Press “Retry”.",
                $"Fiş{which} için kesin bakiye alınamadı — bu fiş için ödeme alınamaz. «Tekrar dene»ye basın.",
                $"Chek{which} bo'yicha aniq qoldiqni olib bo'lmadi — u bo'yicha to'lovni qabul qilib bo'lmaydi. «Qayta urinish»ni bosing.")
            : T($"Не удалось получить точный остаток по чеку{which} — оплата этого чека закрыта. Обновите: выберите клиента заново.",
                $"Чек{which} боюнча так калдыкты алуу мүмкүн болгон жок — бул чекти төлөө жабык. Жаңыртыңыз: кардарды кайра тандаңыз.",
                $"Couldn't get the exact balance of receipt{which} — paying this receipt is closed. Refresh: choose the client again.",
                $"Fiş{which} için kesin bakiye alınamadı — bu fişin ödemesi kapalı. Yenileyin: müşteriyi yeniden seçin.",
                $"Chek{which} bo'yicha aniq qoldiqni olib bo'lmadi — bu chekni to'lash yopiq. Yangilang: mijozni qayta tanlang.");
    }

    /// <summary>2026-10-18, живая проверка NBS: «одной суммой» при неуточнённых чеках. Allowed — можно; Limit — не больше суммы
    /// ТОЧНЫХ остатков; Text — пояснение (null — неуточнённых нет).</summary>
    public sealed record OneSumState(bool Allowed, double Limit, string? Text);

    /// <summary>Сервер гасит «одной суммой» от старых к новым. Неуточнённый чек НОВЕЕ всех точных — сумма до суммы точных уйдёт
    /// только в точные: можно, с ограничением. Неуточнённый старше хоть одного точного (или без даты) — сервер погасил бы
    /// сначала его: закрыто, «оплатите по чекам». Одна неуточнённая строка больше не закрывает «одной суммой» навсегда.</summary>
    public static OneSumState OneSumRule(IEnumerable<DebtSaleRow> rows, bool owner)
    {
        var all = rows.ToList();
        var known = all.Where(r => !r.RemainingUnknown && r.Amount > 0.005).ToList();
        var unknown = all.Where(r => r.RemainingUnknown).ToList();
        var limit = Math.Round(known.Sum(r => r.Amount), 2);
        if (unknown.Count == 0)
            return new OneSumState(true, limit, null);
        if (known.Count == 0)
            return new OneSumState(false, 0, RemainingUnknownText(unknown, owner));
        var which = Which(unknown);
        if (unknown.All(r => r.CreatedAt != default) && unknown.Min(r => r.CreatedAt) > known.Max(r => r.CreatedAt))
        {
            var m = Money(limit);
            return new OneSumState(true, limit, T(
                $"Остаток по чеку{which} уточняется — «одной суммой» не больше {m} (сервер гасит сначала старые чеки, этот — новее).",
                $"Чек{which} боюнча калдык такталууда — «бир суммада» {m} ашпайт (сервер адегенде эски чектерди жабат, бул — жаңыраак).",
                $"The balance of receipt{which} is being checked — “one amount” up to {m} (the server pays older receipts first; this one is newer).",
                $"Fiş{which} bakiyesi kontrol ediliyor — «tek tutar» en fazla {m} (sunucu önce eski fişleri kapatır, bu daha yeni).",
                $"Chek{which} bo'yicha qoldiq aniqlanmoqda — «bitta summada» {m} dan oshmaydi (server avval eski cheklarni yopadi, bu — yangiroq)."));
        }
        return new OneSumState(false, 0, T(
            $"Есть старый чек с неуточнённым остатком{which} — «одной суммой» закрыта: сервер погасил бы сначала его. Оплатите по чекам.",
            $"Калдыгы такталбаган эски чек бар{which} — «бир суммада» жабык: сервер адегенде аны жабат. Чектер боюнча төлөңүз.",
            $"There is an older receipt with an unconfirmed balance{which} — “one amount” is closed: the server would pay it first. Pay by receipt.",
            $"Bakiyesi doğrulanmamış eski bir fiş var{which} — «tek tutar» kapalı: sunucu önce onu kapatırdı. Fiş fiş ödeyin.",
            $"Qoldig'i aniqlanmagan eski chek bor{which} — «bitta summada» yopiq: server avval uni yopardi. Cheklar bo'yicha to'lang."));
    }

    /// <summary>2026-10-21 (ревью денег, A2): у долга без продажи нет чека — «по чеку» его не оплатить.</summary>
    public static string NoSaleByReceiptText => T(
        "Долг без продажи принимается только «одной суммой» (весь долг клиента) — у него нет чека.",
        "Сатуусуз карыз «бир суммада» гана кабыл алынат (кардардын бардык карызы) — анын чеги жок.",
        "A debt without a sale can only be taken as “one amount” (the client's whole debt) — it has no receipt.",
        "Satışsız borç yalnızca «tek tutar» olarak alınır (müşterinin tüm borcu) — fişi yoktur.",
        "Sotuvsiz qarz faqat «bitta summada» qabul qilinadi (mijozning butun qarzi) — uning cheki yo'q.");

    /// <summary>2026-10-21 (ревью денег, A2): есть долг без продажи — порядок, в котором сервер разложит «одну сумму» (по сроку),
    /// касса не знает точно: какие чеки закроются, в подтверждении не обещаем; остаток долга — верный.</summary>
    public static string NoSalePlanNote(double noSaleTotal) => T(
        $"Среди долгов есть долг без продажи ({Money(noSaleTotal)}). Сервер сам разложит сумму от старых долгов к новым — какие чеки закроются, покажет список после оплаты.",
        $"Карыздардын ичинде сатуусуз карыз бар ({Money(noSaleTotal)}). Сервер сумманы эски карыздан жаңысына карай өзү бөлөт — кайсы чектер жабылганын төлөмдөн кийинки тизме көрсөтөт.",
        $"The debts include a debt without a sale ({Money(noSaleTotal)}). The server spreads the amount from oldest to newest itself — the list after payment will show which receipts are paid off.",
        $"Borçlar arasında satışsız bir borç var ({Money(noSaleTotal)}). Sunucu tutarı eskiden yeniye kendisi dağıtır — hangi fişlerin kapandığını ödemeden sonraki liste gösterir.",
        $"Qarzlar orasida sotuvsiz qarz bor ({Money(noSaleTotal)}). Server summani eskisidan yangisiga o'zi taqsimlaydi — qaysi cheklar yopilganini to'lovdan keyingi ro'yxat ko'rsatadi.");

    /// <summary>2026-10-18, десятый круг (п.6): «+ остаток уточняется по N чекам» к итогу (0 — пусто).</summary>
    public static string CheckingSuffix(int count) => count <= 0 ? "" : T(
        $" + остаток уточняется по {count} чек.", $" + {count} чек боюнча калдык такталууда", $" + balance being checked for {count} receipt(s)",
        $" + {count} fişin bakiyesi kontrol ediliyor", $" + {count} ta chek bo'yicha qoldiq aniqlanmoqda");

    /// <summary>Вид события смены по способу: безнал — свой вид (в ящик не попадает).</summary>
    public static string ShiftKind(string method) =>
        method == DebtPaymentSettings.MethodTransfer ? ShiftEventsStore.KindDebtPaymentNonCash : ShiftEventsStore.KindDebtPayment;

    /// <summary>Как сумма ляжет на чеки при «одной сумме»: от старых к новым (сервер гасит по сроку взноса — у продажи в
    /// долг он совпадает с датой чека). (строка, сколько на неё, закроется ли полностью).</summary>
    public static List<(DebtSaleRow Row, double Part, bool Closed)> Allocate(IEnumerable<DebtSaleRow> rows, double amount)
    {
        var result = new List<(DebtSaleRow, double, bool)>();
        var left = Math.Round(amount, 2);
        foreach (var row in rows.Where(r => r.Amount > 0.005).OrderBy(r => r.CreatedAt))
        {
            if (left <= 0.005)
                break;
            var part = Math.Min(left, row.Amount);
            result.Add((row, part, part >= row.Amount - 0.005));
            left = Math.Round(left - part, 2);
        }
        return result;
    }
}
