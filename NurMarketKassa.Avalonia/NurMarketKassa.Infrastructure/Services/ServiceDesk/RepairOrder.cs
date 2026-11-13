using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (решение владельца: «Ремонт и trade-in — как доп. услуги в маркетплейсе»): статус квитанции «Ремонта и сервиса».
/// Порядок: принят → в работе → готов → выдан; «Отменён» — клиент отказался (предоплата возвращается из кассы).</summary>
public enum RepairStatus
{
    Accepted,
    InWork,
    Ready,
    Issued,
    Cancelled,
}

/// <summary>Запись журнала квитанции: кто и когда поменял статус или деньги.</summary>
public sealed class RepairLogEntry
{
    public DateTime At { get; set; }
    public string By { get; set; } = "";
    public string Action { get; set; } = "";
}

/// <summary>Квитанция приёма в ремонт. Деньги — только decimal; сумма предоплаты — то, что УЖЕ внесено в кассу (внесение со ссылкой на номер квитанции).</summary>
public sealed class RepairOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Сквозной номер квитанции (в пределах файла данных этой кассы).</summary>
    public int Number { get; set; }
    public DateTime AcceptedAt { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientPhone { get; set; } = "";
    /// <summary>Устройство: «Samsung A54», «Часы Casio», «Туфли»…</summary>
    public string Device { get; set; } = "";
    /// <summary>Серийный номер или IMEI (нормализованный, <see cref="ImeiValidator"/>); пусто — нет.</summary>
    public string Serial { get; set; } = "";
    public string Fault { get; set; } = "";
    /// <summary>Комплектация: зарядка, чехол, коробка, SIM…</summary>
    public string Equipment { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Ориентировочная стоимость работ при приёме (может быть 0 — «после диагностики»).</summary>
    public decimal Estimate { get; set; }
    /// <summary>Предоплата, внесённая в кассу при приёме.</summary>
    public decimal Prepay { get; set; }
    /// <summary>Предоплата возвращена клиенту (отмена или переплата при выдаче) — сколько уже ушло из кассы.</summary>
    public decimal Refunded { get; set; }
    public DateTime? DueDate { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RepairStatus Status { get; set; }
    /// <summary>Итоговая стоимость работ (задаётся при «Готов»).</summary>
    public decimal FinalPrice { get; set; }
    public DateTime? ReadyAt { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public string AcceptedBy { get; set; } = "";
    public string IssuedBy { get; set; } = "";
    /// <summary>Строка оплаты работ добавлена в чек кассы и ждёт подтверждения «чек оплачен» (деньги проходят обычной продажей услуги).</summary>
    public decimal PaymentInCart { get; set; }
    /// <summary>Сколько оплачено при выдаче продажей услуги (после подтверждения «чек оплачен»).</summary>
    public decimal PaidAtIssue { get; set; }
    public List<RepairLogEntry> Log { get; set; } = new();
}

/// <summary>Файл квитанций.</summary>
public sealed class RepairData
{
    public int Version { get; set; } = 1;
    public int LastNumber { get; set; }
    public List<RepairOrder> Orders { get; set; } = new();
}

/// <summary>Чистая логика «Ремонта и сервиса»: переходы статусов, суммы, тексты, печатные формы. Без окон и файлов — проверяется тестами.</summary>
public static class RepairRules
{
    public static string Code(int number) => "Р-" + number.ToString("0000", CultureInfo.InvariantCulture);

    public static string Code(RepairOrder o) => Code(o.Number);

    /// <summary>Деньги — до копеек, от нуля вверх (как на кассе).</summary>
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сумма из поля ввода: пробелы и запятая/точка допустимы; пусто — 0; не число или отрицательное — null.</summary>
    public static decimal? TryParseMoney(string? text)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim();
        if (t.Length == 0)
            return 0m;
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 100_000_000m ? Money(v) : null;
    }

    /// <summary>Сколько клиент должен доплатить при выдаче: итог работ минус предоплата (не меньше нуля).</summary>
    public static decimal AmountDue(decimal finalPrice, decimal prepay) => Math.Max(0m, Money(finalPrice) - Money(prepay));

    /// <summary>Переплата: предоплата больше итога работ — разницу возвращают клиенту из кассы.</summary>
    public static decimal Overpaid(decimal finalPrice, decimal prepay) => Math.Max(0m, Money(prepay) - Money(finalPrice));

    /// <summary>Сколько предоплаты ещё не возвращено клиенту.</summary>
    public static decimal PrepayToRefund(RepairOrder o) => Math.Max(0m, Money(o.Prepay) - Money(o.Refunded));

    public static bool IsFinal(RepairStatus s) => s is RepairStatus.Issued or RepairStatus.Cancelled;

    /// <summary>Можно ли перевести квитанцию из одного статуса в другой кнопками окна (выдача и отмена идут своими путями с деньгами, но допустимость та же).</summary>
    public static bool CanMove(RepairStatus from, RepairStatus to) => (from, to) switch
    {
        (RepairStatus.Accepted, RepairStatus.InWork) => true,
        (RepairStatus.InWork, RepairStatus.Ready) => true,
        (RepairStatus.Accepted, RepairStatus.Ready) => true,   // мелкий ремонт «на месте»
        (RepairStatus.Ready, RepairStatus.InWork) => true,      // вернули в работу (клиент передумал, недоделка)
        (RepairStatus.Ready, RepairStatus.Issued) => true,
        (RepairStatus.Accepted, RepairStatus.Cancelled) => true,
        (RepairStatus.InWork, RepairStatus.Cancelled) => true,
        (RepairStatus.Ready, RepairStatus.Cancelled) => true,
        _ => false,
    };

    /// <summary>Срок просрочен: не готов и дата срока раньше сегодняшней.</summary>
    public static bool IsOverdue(RepairOrder o, DateTime today) =>
        o.DueDate is { } due && due.Date < today.Date && o.Status is RepairStatus.Accepted or RepairStatus.InWork;

    public static string StatusName(RepairStatus s) => s switch
    {
        RepairStatus.Accepted => Tr.T("Принят", "Кабыл алынды", "Accepted", "Kabul edildi", "Qabul qilindi"),
        RepairStatus.InWork => Tr.T("В работе", "Иште", "In progress", "İşlemde", "Ishda"),
        RepairStatus.Ready => Tr.T("Готов", "Даяр", "Ready", "Hazır", "Tayyor"),
        RepairStatus.Issued => Tr.T("Выдан", "Берилди", "Handed over", "Teslim edildi", "Topshirildi"),
        _ => Tr.T("Отменён", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"),
    };

    /// <summary>Что не заполнено при приёме (пусто — можно сохранять). Устройство и неисправность обязательны; телефон нужен, чтобы позвать клиента; предоплата не больше ориентира, если ориентир задан.</summary>
    public static List<string> ValidateNew(RepairOrder o)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(o.ClientName))
            problems.Add(Tr.T("Впишите имя клиента.", "Кардардын атын жазыңыз.", "Enter the customer's name.", "Müşterinin adını girin.", "Mijoz ismini kiriting."));
        if (!ServerAddons.PhoneLooksValid(o.ClientPhone))
            problems.Add(Tr.T("Впишите телефон клиента (не меньше 9 цифр).", "Кардардын телефонун жазыңыз (кеминде 9 сан).", "Enter the customer's phone (at least 9 digits).", "Müşterinin telefonunu girin (en az 9 hane).", "Mijoz telefonini kiriting (kamida 9 raqam)."));
        if (string.IsNullOrWhiteSpace(o.Device))
            problems.Add(Tr.T("Впишите, что принимаем (устройство).", "Эмнени кабыл алганыңызды жазыңыз (түзмөк).", "Enter what is being accepted (the device).", "Neyin kabul edildiğini yazın (cihaz).", "Nima qabul qilinayotganini yozing (qurilma)."));
        if (string.IsNullOrWhiteSpace(o.Fault))
            problems.Add(Tr.T("Опишите неисправность.", "Бузулууну сүрөттөңүз.", "Describe the fault.", "Arızayı açıklayın.", "Nosozlikni tavsiflang."));
        if (o.Estimate < 0 || o.Prepay < 0)
            problems.Add(Tr.T("Суммы не могут быть отрицательными.", "Суммалар терс болушу мүмкүн эмес.", "Amounts cannot be negative.", "Tutarlar negatif olamaz.", "Summalar manfiy bo'lishi mumkin emas."));
        else if (o.Estimate > 0 && Money(o.Prepay) > Money(o.Estimate))
            problems.Add(Tr.T("Предоплата больше ориентировочной стоимости.", "Алдын ала төлөм болжолдуу баадан чоң.", "The prepayment is larger than the estimate.", "Ön ödeme tahmini tutardan büyük.", "Oldindan to'lov taxminiy narxdan katta."));
        if (!string.IsNullOrWhiteSpace(o.Serial))
        {
            var v = ImeiValidator.Check(o.Serial, out _);
            if (v is ImeiValidator.Verdict.BadFormat or ImeiValidator.Verdict.BadImei)
                problems.Add(Tr.T("Серийный номер / IMEI не прошёл проверку — проверьте цифры или оставьте поле пустым.", "Сериялык номер / IMEI текшерүүдөн өткөн жок — сандарды текшериңиз же талааны бош калтырыңыз.", "The serial number / IMEI failed the check — check the digits or leave the field empty.", "Seri numarası / IMEI kontrolü geçemedi — rakamları kontrol edin veya alanı boş bırakın.", "Seriya raqami / IMEI tekshiruvdan o'tmadi — raqamlarni tekshiring yoki maydonni bo'sh qoldiring."));
        }
        return problems;
    }

    // ───────────────────────── печатные формы (текст для ленты чеков 58/80 мм) ─────────────────────────

    private static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Sum(decimal v) => ReceiptLineLayout.WithSom(Money(v).ToString("N2", CultureInfo.CurrentCulture));

    private static void Header(StringBuilder sb, string title, int width)
    {
        var prefs = UserPreferences.Instance;
        sb.Append(ReceiptLineLayout.Center(title, width)).Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreName))
            foreach (var p in ReceiptLineLayout.WrapLeft(prefs.StoreName.Trim(), width)) sb.Append(p).Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreInn))
            sb.Append($"{Tr.T("ИНН", "ИНН", "TIN", "Vergi no", "STIR")}: {prefs.StoreInn.Trim()}").Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreAddress))
            foreach (var p in ReceiptLineLayout.WrapLeft(prefs.StoreAddress.Trim(), width)) sb.Append(p).Append('\n');
        sb.Append(new string('-', width)).Append('\n');
    }

    private static void Field(StringBuilder sb, string label, string? value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        foreach (var p in ReceiptLineLayout.WrapLeft(label + ": " + value.Trim(), width))
            sb.Append(p).Append('\n');
    }

    /// <summary>Квитанция приёма: клиент, устройство, серийный номер, неисправность, комплектация, предоплата, срок, условия и две подписи.</summary>
    public static string BuildReceipt(RepairOrder o, int width)
    {
        var sb = new StringBuilder(700);
        Header(sb, Tr.T("КВИТАНЦИЯ ПРИЁМА В РЕМОНТ", "ОҢДООГО КАБЫЛ АЛУУ КВИТАНЦИЯСЫ", "REPAIR INTAKE RECEIPT", "TAMİR KABUL MAKBUZU", "TA'MIRLASHGA QABUL QILISH KVITANSIYASI"), width);
        sb.Append($"{Tr.T("Квитанция №", "Квитанция №", "Receipt No.", "Makbuz No", "Kvitansiya №")} {Code(o)}").Append('\n');
        sb.Append($"{Tr.T("Принято", "Кабыл алынды", "Accepted", "Kabul", "Qabul")}: {Day(o.AcceptedAt)} {o.AcceptedAt:HH:mm}").Append('\n');
        Field(sb, Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), o.ClientName, width);
        Field(sb, Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), o.ClientPhone, width);
        sb.Append(new string('-', width)).Append('\n');
        Field(sb, Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), o.Device, width);
        if (!string.IsNullOrWhiteSpace(o.Serial))
            Field(sb, ImeiValidator.LabelFor(o.Serial), o.Serial, width);
        Field(sb, Tr.T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"), o.Fault, width);
        Field(sb, Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), o.Equipment, width);
        Field(sb, Tr.T("Заметка", "Эскертүү", "Note", "Not", "Izoh"), o.Note, width);
        sb.Append(new string('-', width)).Append('\n');
        if (o.Estimate > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Ориентир", "Болжол", "Estimate", "Tahmin", "Taxmin"), Sum(o.Estimate), width)).Append('\n');
        if (o.Prepay > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), Sum(o.Prepay), width)).Append('\n');
        if (o.DueDate is { } due)
            sb.Append($"{Tr.T("Срок", "Мөөнөт", "Due", "Süre", "Muddat")}: {Day(due)}").Append('\n');
        sb.Append('\n');
        foreach (var p in ReceiptLineLayout.WrapLeft(Tr.T(
            "Окончательная стоимость определяется после диагностики. Устройство выдаётся по этой квитанции и документу клиента. Не забранное в течение 30 дней после уведомления может быть удержано в счёт хранения.",
            "Акыркы баа диагностикадан кийин аныкталат. Түзмөк ушул квитанция жана кардардын документи боюнча берилет. Эскертүүдөн кийин 30 күндүн ичинде алынбаса, сактоо үчүн кармалышы мүмкүн.",
            "The final price is set after diagnostics. The device is handed over against this receipt and the customer's ID. Items not collected within 30 days after the notice may be kept against storage.",
            "Nihai ücret teşhisten sonra belirlenir. Cihaz bu makbuz ve müşterinin kimliğiyle teslim edilir. Bildirimden sonra 30 gün içinde alınmayanlar saklama bedeli karşılığında tutulabilir.",
            "Yakuniy narx diagnostikadan keyin belgilanadi. Qurilma shu kvitansiya va mijoz hujjati bo'yicha topshiriladi. Xabardan keyin 30 kun ichida olinmaganlar saqlash evaziga ushlab qolinishi mumkin."), width))
            sb.Append(p).Append('\n');
        sb.Append('\n');
        sb.Append(Tr.T("Подпись клиента: ____________", "Кардардын колу: ____________", "Customer's signature: ____________", "Müşteri imzası: ____________", "Mijoz imzosi: ____________")).Append('\n');
        sb.Append(Tr.T("Принял: ____________", "Кабыл алды: ____________", "Accepted by: ____________", "Teslim alan: ____________", "Qabul qildi: ____________")).Append('\n');
        return sb.ToString();
    }

    /// <summary>Акт выдачи: что выдано, итог работ, предоплата, доплата (оплачена продажей услуги), подпись клиента «претензий нет».</summary>
    public static string BuildIssueAct(RepairOrder o, int width)
    {
        var sb = new StringBuilder(600);
        Header(sb, Tr.T("АКТ ВЫДАЧИ ИЗ РЕМОНТА", "ОҢДООДОН БЕРҮҮ АКТЫСЫ", "REPAIR HAND-OVER ACT", "TAMİRDEN TESLİM TUTANAĞI", "TA'MIRDAN TOPSHIRISH DALOLATNOMASI"), width);
        sb.Append($"{Tr.T("Квитанция №", "Квитанция №", "Receipt No.", "Makbuz No", "Kvitansiya №")} {Code(o)} ({Day(o.AcceptedAt)})").Append('\n');
        sb.Append($"{Tr.T("Выдано", "Берилди", "Handed over", "Teslim", "Topshirildi")}: {Day(o.IssuedAt ?? DateTime.Now)} {(o.IssuedAt ?? DateTime.Now):HH:mm}").Append('\n');
        Field(sb, Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), o.ClientName, width);
        Field(sb, Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), o.Device, width);
        if (!string.IsNullOrWhiteSpace(o.Serial))
            Field(sb, ImeiValidator.LabelFor(o.Serial), o.Serial, width);
        Field(sb, Tr.T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"), o.Fault, width);
        sb.Append(new string('-', width)).Append('\n');
        sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Стоимость работ", "Иштин баасы", "Cost of work", "İş bedeli", "Ish narxi"), Sum(o.FinalPrice), width)).Append('\n');
        if (o.Prepay > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), Sum(o.Prepay), width)).Append('\n');
        if (o.PaidAtIssue > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Доплата при выдаче", "Берүүдө кошумча төлөм", "Paid at hand-over", "Tesliminde ödenen", "Topshirishda to'langan"), Sum(o.PaidAtIssue), width)).Append('\n');
        if (o.Refunded > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Возвращено клиенту", "Кардарга кайтарылды", "Refunded to the customer", "Müşteriye iade edildi", "Mijozga qaytarildi"), Sum(o.Refunded), width)).Append('\n');
        sb.Append('\n');
        foreach (var p in ReceiptLineLayout.WrapLeft(Tr.T(
            "Устройство получено в исправном состоянии, работа выполнена, претензий по комплектности и внешнему виду нет.",
            "Түзмөк жакшы абалда алынды, иш аткарылды, комплектине жана сырткы көрүнүшүнө доомат жок.",
            "The device was received in working order, the work is done, there are no claims about the items or appearance.",
            "Cihaz çalışır durumda teslim alındı, iş tamamlandı, eksiklik veya görünüm konusunda itiraz yok.",
            "Qurilma soz holatda olindi, ish bajarildi, komplektlik va tashqi ko'rinish bo'yicha e'tiroz yo'q."), width))
            sb.Append(p).Append('\n');
        sb.Append('\n');
        sb.Append(Tr.T("Подпись клиента: ____________", "Кардардын колу: ____________", "Customer's signature: ____________", "Müşteri imzası: ____________", "Mijoz imzosi: ____________")).Append('\n');
        return sb.ToString();
    }

    public static string BuildReceipt(RepairOrder o) => BuildReceipt(o, ReceiptLayout.CharWidth);

    public static string BuildIssueAct(RepairOrder o) => BuildIssueAct(o, ReceiptLayout.CharWidth);

    /// <summary>Текст клиенту «готово» для WhatsApp (клиент открывает чат и сам жмёт «Отправить»).</summary>
    public static string ReadyMessage(RepairOrder o)
    {
        var shop = UserPreferences.Instance.StoreName?.Trim();
        var due = AmountDue(o.FinalPrice, o.Prepay);
        var pay = due > 0
            ? Tr.T($" К оплате: {Money(due):0.##} сом.", $" Төлөнүүчү сумма: {Money(due):0.##} сом.", $" Amount due: {Money(due):0.##} som.", $" Ödenecek tutar: {Money(due):0.##} som.", $" To'lov: {Money(due):0.##} so'm.")
            : "";
        return Tr.T(
            $"Здравствуйте, {o.ClientName}! Ваше устройство «{o.Device}» (квитанция {Code(o)}) готово — можно забирать.{pay}{(string.IsNullOrEmpty(shop) ? "" : " " + shop)}",
            $"Саламатсызбы, {o.ClientName}! «{o.Device}» түзмөгүңүз (квитанция {Code(o)}) даяр — алып кетсеңиз болот.{pay}{(string.IsNullOrEmpty(shop) ? "" : " " + shop)}",
            $"Hello, {o.ClientName}! Your device “{o.Device}” (receipt {Code(o)}) is ready for pickup.{pay}{(string.IsNullOrEmpty(shop) ? "" : " " + shop)}",
            $"Merhaba {o.ClientName}! «{o.Device}» cihazınız (makbuz {Code(o)}) hazır, teslim alabilirsiniz.{pay}{(string.IsNullOrEmpty(shop) ? "" : " " + shop)}",
            $"Assalomu alaykum, {o.ClientName}! «{o.Device}» qurilmangiz (kvitansiya {Code(o)}) tayyor — olib ketishingiz mumkin.{pay}{(string.IsNullOrEmpty(shop) ? "" : " " + shop)}");
    }
}
