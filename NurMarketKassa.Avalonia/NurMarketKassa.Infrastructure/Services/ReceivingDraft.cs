using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-11-11 («Маркет ОРУК Ош», О-5: «Неоконченная приёмка пропадает» — после переустановки и отключения света): ЧЕРНОВИК ПРИЁМКИ.
// Набранные строки «Склад → Приёмка» и «Закупки → Приём товара» и шапка (поставщик, оплата/долг, срок, № накладной, доп. расходы) сами пишутся в файл
// %APPDATA%\<NurMarketKassa|NurMarketOwner>\receiving-draft\{warehouse|purchases}.json (атомарно, AtomicFile.Update — с межпроцессным замком; дебаунс 1,5 с,
// сразу при закрытии окна), а при следующем открытии приёмки кассе предлагают «Продолжить / Начать заново». Черновик ничего не проводит: цены, остатки и
// проверки — как обычно при «Оприходовать»; остаток и «прежние» цены существующих товаров перечитываются из каталога при восстановлении.
// Секретов в файле нет (названия, штрихкоды, количества, цены, поставщик). Новый вид хранилища — в CompanyStoreRegistry.CompanyItems («receiving-draft»).

/// <summary>Окно, чья приёмка пишется в черновик.</summary>
public static class ReceivingDraftSlots
{
    public const string Warehouse = "warehouse";
    public const string Purchases = "purchases";

    public static bool IsKnown(string? slot) => slot is Warehouse or Purchases;
}

/// <summary>Одна строка черновика: все значения <see cref="ReceivingLineVm"/>, которые нельзя вывести из других (см. ReceivingLineVm.Draft.cs).</summary>
public sealed class ReceivingDraftLine
{
    public int LineNo { get; set; }
    public string? ProductId { get; set; }
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
    public string? Unit { get; set; } = "шт";
    [JsonConverter(typeof(JsonStringEnumConverter))] public ReceivingSource Source { get; set; }
    public double StockBefore { get; set; }
    public double Quantity { get; set; }
    public double PurchasePrice { get; set; }
    public double SalePrice { get; set; }
    public double LandedExtraPerUnit { get; set; }
    public string? OriginalName { get; set; }
    public string? OriginalUnit { get; set; }
    public double OriginalPurchasePrice { get; set; }
    public double OriginalSalePrice { get; set; }
    public double OriginalMarkupPercent { get; set; }
    public string? CategoryName { get; set; }
    public bool IsWeight { get; set; }

    // упаковки («упак.», штук в упаковке, поштучная продажа)
    public bool PackMode { get; set; }
    public bool PackSale { get; set; }
    public double PackCount { get; set; }
    public double PiecesPerPack { get; set; }
    public double PackPrice { get; set; }
    public bool PackPriceTyped { get; set; }
    public bool PackSaleTyped { get; set; }
    public bool ScaleFromPiece { get; set; }
    public double PiecePurchaseBase { get; set; }
    public double PieceSaleBase { get; set; }
    public string? PackName { get; set; }

    // как считается цена: вручную / по наценке / из карточки; откуда закупка
    [JsonConverter(typeof(JsonStringEnumConverter))] public SalePriceMode SaleMode { get; set; }
    public bool PurchaseManual { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public PurchaseDefaultSource PurchaseSource { get; set; }
    public bool SaleFromHistory { get; set; }

    public Dictionary<string, double>? VariantQuantities { get; set; }
    public Dictionary<string, string>? VariantLabels { get; set; }
}

/// <summary>Файл черновика: шапка приёмки и строки.</summary>
public sealed class ReceivingDraftFile
{
    /// <summary>Версия формата. Файл новее, чем знает программа, не читается (черновик не ломается, а игнорируется с записью в журнал).</summary>
    public const int CurrentVersion = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int Version { get; set; } = CurrentVersion;
    public string? Slot { get; set; }
    public string? CompanyId { get; set; }
    /// <summary>Когда начата эта приёмка (первая запись) — не меняется при дальнейших записях.</summary>
    public DateTime StartedAt { get; set; }
    /// <summary>Когда записан файл (локальное время).</summary>
    public DateTime SavedAt { get; set; }

    public string? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierInn { get; set; }
    public string? SupplierPhone { get; set; }
    /// <summary>«Склад → Приёмка»: оплачено сразу (true) или в долг (false). В «Закупках» способ оплаты выбирают при проведении — там не используется.</summary>
    public bool PaidNow { get; set; }
    public int DebtDays { get; set; } = 30;
    public string? DocNumber { get; set; }
    public DateTime? DocDate { get; set; }
    // «Закупки → Приём товара»: по заказу и доп. расходы
    public string? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public double LandedTotal { get; set; }
    public string? LandedMode { get; set; }

    public List<ReceivingDraftLine> Lines { get; set; } = new();
}

/// <summary>Что показать в вопросе «Продолжить / Начать заново».</summary>
public sealed record ReceivingDraftInfo(string Slot, int Positions, DateTime StartedAt, DateTime SavedAt, bool Stale);

/// <summary>Хранилище черновиков (файлы на этом компьютере). Все ошибки чтения/записи — в журнал, приёмку они не роняют.</summary>
public static class ReceivingDraftStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для проверок окон: черновик выключен (сквозные тесты окон не должны находить чужой черновик и спрашивать «Продолжить?»).</summary>
    public static bool Disabled { get; set; }

    /// <summary>Черновик старше этого предлагается удалить (цены и остатки за такой срок уже не те).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);
    /// <summary>Предел размера файла и числа строк: огромный список не пишем (в журнал), чтобы запись не тормозила и не пухла.</summary>
    public const int MaxLines = 5000;
    public const int MaxBytes = 8 * 1024 * 1024;

    internal static string Dir => DataDirOverride
        ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "receiving-draft");

    public static string PathFor(string slot) => Path.Combine(Dir, (ReceivingDraftSlots.IsKnown(slot) ? slot : "other") + ".json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, // нули, false и пустые поля в файл не пишем — строки короче в разы
        WriteIndented = false,
    };

    private static long _seq;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Floor = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LastWritten = new(StringComparer.Ordinal);

    internal static long NextSeq() => Interlocked.Increment(ref _seq);

    internal static string CurrentCompany
    {
        get
        {
            try { return CompanyInfoService.LastCompany?.Id ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>В черновике есть что хранить: хотя бы у одной строки количество больше нуля (пустую приёмку не пишем и не предлагаем).</summary>
    public static bool IsMeaningful(ReceivingDraftFile? file) =>
        file is { Lines.Count: > 0 } && file.Lines.Any(l => l.Quantity > 0);

    public static bool IsStale(ReceivingDraftFile file, DateTime? now = null) =>
        (now ?? DateTime.Now) - (file.SavedAt == default ? file.StartedAt : file.SavedAt) > StaleAfter;

    public static string Serialize(ReceivingDraftFile file) => JsonSerializer.Serialize(file, Json);

    /// <summary>Разобрать текст файла. null — не черновик (пусто, битый JSON, версия новее, нет строк): причина — в <paramref name="problem"/>.</summary>
    public static ReceivingDraftFile? TryParse(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "пустой файл";
            return null;
        }
        try
        {
            var file = JsonSerializer.Deserialize<ReceivingDraftFile>(text, Json);
            if (file is null)
            {
                problem = "нет данных";
                return null;
            }
            if (file.Version > ReceivingDraftFile.CurrentVersion || file.Version < 1)
            {
                problem = $"версия формата {file.Version} не поддерживается";
                return null;
            }
            file.Lines ??= new List<ReceivingDraftLine>();
            file.Lines.RemoveAll(l => l is null);
            return file;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static bool IsValidText(string text) => TryParse(text, out _) is not null;

    /// <summary>Прочитать черновик окна. null — его нет, он битый, пустой, чужой компании или другой версии (в журнале — почему). Исключений не бросает.</summary>
    public static ReceivingDraftFile? Load(string slot)
    {
        try
        {
            var text = AtomicFile.ReadAllText(PathFor(slot), IsValidText);
            if (text is null)
                return null;
            var file = TryParse(text, out var problem);
            if (file is null)
            {
                PosLogger.Log($"Черновик приёмки ({slot}) не прочитан: {problem}.", "WARNING");
                return null;
            }
            // Черновик другого магазина (вход под другим аккаунтом) не показываем и не трогаем.
            if (!string.IsNullOrEmpty(file.CompanyId) && CurrentCompany.Length > 0
                && !string.Equals(file.CompanyId, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                return null;
            return IsMeaningful(file) ? file : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Черновик приёмки ({slot}) не прочитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
    }

    public static ReceivingDraftInfo? Peek(string slot)
    {
        var f = Load(slot);
        return f is null ? null : new ReceivingDraftInfo(slot, f.Lines.Count, f.StartedAt, f.SavedAt, IsStale(f));
    }

    /// <summary>Записать текст черновика. <paramref name="seq"/> — порядковый номер записи: запоздавшая старая запись не затирает новую и не воскрешает удалённый
    /// черновик. true — записано.</summary>
    internal static bool Save(string slot, string text, long seq)
    {
        lock (SlotLock(slot))
        {
            if (seq <= FloorOf(slot) || seq < LastWrittenOf(slot))
                return false;
            try
            {
                AtomicFile.Update(PathFor(slot), _ => text); // межпроцессный замок (касса и программа владельца на одном ПК), запись через временный файл
                lock (Gate)
                    LastWritten[slot] = seq;
                return true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Черновик приёмки ({slot}) не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                return false;
            }
        }
    }

    /// <summary>Поднять «пол» записей: записи с номером не больше <paramref name="seq"/> (начатые до удаления) ничего не воскресят.</summary>
    internal static void RaiseFloor(string slot, long seq)
    {
        lock (Gate)
            Floor[slot] = Math.Max(FloorOf(slot), seq);
    }

    /// <summary>Удалить черновик (и копию .bak). Записи, начатые ДО этого вызова, больше ничего не воскресят.</summary>
    internal static void Delete(string slot, long seq)
    {
        RaiseFloor(slot, seq);
        lock (SlotLock(slot))
        {
            try
            {
                var path = PathFor(slot);
                foreach (var p in new[] { path, path + ".bak" })
                {
                    for (var attempt = 0; attempt < 4 && File.Exists(p); attempt++)
                    {
                        try { File.Delete(p); }
                        catch (IOException) when (attempt < 3) { Thread.Sleep(40); } // файл на миг занят (антивирус, индексатор)
                    }
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Черновик приёмки ({slot}) не удалён ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
    }

    private static readonly Dictionary<string, object> Locks = new(StringComparer.Ordinal);

    private static object SlotLock(string slot)
    {
        lock (Gate)
        {
            if (!Locks.TryGetValue(slot, out var l))
                Locks[slot] = l = new object();
            return l;
        }
    }

    private static long FloorOf(string slot)
    {
        lock (Gate)
            return Floor.TryGetValue(slot, out var v) ? v : 0;
    }

    private static long LastWrittenOf(string slot)
    {
        lock (Gate)
            return LastWritten.TryGetValue(slot, out var v) ? v : 0;
    }

    /// <summary>Записать готовый черновик (для проверок окон: подготовить файл «от прошлого запуска»).</summary>
    public static bool WriteForTests(ReceivingDraftFile file) => Save(file.Slot ?? ReceivingDraftSlots.Warehouse, Serialize(file), NextSeq());

    /// <summary>Сбросить память о записях (только для тестов: каждый тест начинает с чистого листа).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Floor.Clear();
            LastWritten.Clear();
        }
    }
}

/// <summary>Тексты черновика на 5 языках (вопрос при открытии, подпись «Черновик сохранён»).</summary>
public static class ReceivingDraftTexts
{
    public static string Title => Tr.T("Неоконченная приёмка", "Бүтпөгөн кабыл алуу", "Unfinished receiving", "Yarım kalan mal kabul", "Tugallanmagan qabul");

    /// <summary>«12 позиций» с правильным склонением по-русски; в остальных языках — без склонения.</summary>
    public static string Positions(int n)
    {
        var ru = RuPlural(n, "позиция", "позиции", "позиций");
        return Tr.T($"{n} {ru}", $"{n} позиция", n == 1 ? "1 item" : $"{n} items", $"{n} kalem", $"{n} ta pozitsiya");
    }

    private static string RuPlural(int n, string one, string few, string many)
    {
        var m100 = Math.Abs(n) % 100;
        var m10 = m100 % 10;
        if (m100 is >= 11 and <= 14)
            return many;
        return m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
    }

    private static string When(DateTime t) => t.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Вопрос при открытии приёмки.</summary>
    public static string Question(ReceivingDraftInfo info)
    {
        var count = Positions(info.Positions);
        var when = When(info.SavedAt == default ? info.StartedAt : info.SavedAt);
        if (info.Stale)
            return Tr.T(
                $"Найден старый черновик приёмки ({count}, от {when}). Ему больше 30 дней: цены и остатки давно могли измениться. Удалить его или всё же продолжить?",
                $"Эски кабыл алуу долбоору табылды ({count}, {when}). Ал 30 күндөн ашык: баалар жана калдык көптөн бери өзгөргөн болушу мүмкүн. Өчүрөсүзбү же болбосо улантасызбы?",
                $"An old receiving draft was found ({count}, from {when}). It is over 30 days old: prices and stock may have changed long ago. Delete it or continue anyway?",
                $"Eski bir mal kabul taslağı bulundu ({count}, {when}). 30 günden eski: fiyatlar ve stok çoktan değişmiş olabilir. Silinsin mi, yoksa yine de devam edilsin mi?",
                $"Eski qabul qoralamasi topildi ({count}, {when}). U 30 kundan eski: narxlar va qoldiq allaqachon o'zgargan bo'lishi mumkin. O'chirasizmi yoki baribir davom etasizmi?");
        return Tr.T(
            $"Найдена неоконченная приёмка ({count}, от {when}). Продолжить с того места, где остановились, или начать заново?",
            $"Бүтпөгөн кабыл алуу табылды ({count}, {when}). Токтогон жерден улантасызбы же кайра баштайсызбы?",
            $"An unfinished receiving was found ({count}, from {when}). Continue where you stopped, or start over?",
            $"Yarım kalan bir mal kabul bulundu ({count}, {when}). Kaldığınız yerden devam edilsin mi, yoksa baştan mı başlansın?",
            $"Tugallanmagan qabul topildi ({count}, {when}). To'xtagan joyingizdan davom etasizmi yoki qaytadan boshlaysizmi?");
    }

    public static string ContinueText => Tr.T("Продолжить", "Улантуу", "Continue", "Devam et", "Davom etish");
    public static string FreshText => Tr.T("Начать заново", "Кайра баштоо", "Start over", "Baştan başla", "Qaytadan boshlash");
    public static string DeleteDraftText => Tr.T("Удалить черновик", "Долбоорду өчүрүү", "Delete the draft", "Taslağı sil", "Qoralamani o'chirish");
    public static string ContinueAnywayText => Tr.T("Всё же продолжить", "Баары бир улантуу", "Continue anyway", "Yine de devam et", "Baribir davom etish");

    public static string DeleteTitle => Tr.T("Удалить черновик?", "Долбоорду өчүрөсүзбү?", "Delete the draft?", "Taslak silinsin mi?", "Qoralama o'chirilsinmi?");

    public static string DeleteQuestion(ReceivingDraftInfo info) => Tr.T(
        $"Удалить неоконченную приёмку ({Positions(info.Positions)})? Набранное вернуть будет нельзя.",
        $"Бүтпөгөн кабыл алууну ({Positions(info.Positions)}) өчүрөсүзбү? Терилгенди кайтарууга болбойт.",
        $"Delete the unfinished receiving ({Positions(info.Positions)})? The entered data can't be restored.",
        $"Yarım kalan mal kabul ({Positions(info.Positions)}) silinsin mi? Girilenler geri getirilemez.",
        $"Tugallanmagan qabul ({Positions(info.Positions)}) o'chirilsinmi? Kiritilganni qaytarib bo'lmaydi.");

    public static string BackText => Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga");
    public static string DeleteText => Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish");

    /// <summary>Тихая подпись у приёмки.</summary>
    public static string SavedCaption(DateTime at) => Tr.T(
        $"Черновик сохранён {at:HH:mm}", $"Долбоор сакталды {at:HH:mm}", $"Draft saved {at:HH:mm}", $"Taslak kaydedildi {at:HH:mm}", $"Qoralama saqlandi {at:HH:mm}");

    public static string SavedTip => Tr.T(
        "Набранная приёмка сохраняется на этом компьютере сама: после сбоя или отключения света её можно продолжить при следующем открытии.",
        "Терилген кабыл алуу бул компьютерде өзү сакталат: бузулуудан же жарык өчкөндөн кийин кийинки ачканда улантса болот.",
        "The entered receiving is saved on this computer automatically: after a crash or a power cut you can continue it the next time you open it.",
        "Girilen mal kabul bu bilgisayarda kendiliğinden kaydedilir: çökme veya elektrik kesintisinden sonra bir sonraki açılışta devam edebilirsiniz.",
        "Kiritilgan qabul shu kompyuterda o'zi saqlanadi: nosozlik yoki elektr o'chganidan keyin keyingi ochishda davom ettirish mumkin.");

    /// <summary>После восстановления: у стольких товаров остаток на складе изменился, пока лежал черновик.</summary>
    public static string StockChanged(int n) => Tr.T(
        $"Черновик восстановлен. У товаров, у которых остаток изменился с момента сохранения ({n}), остаток обновлён — проверьте количество.",
        $"Долбоор калыбына келтирилди. Сакталгандан бери калдыгы өзгөргөн товарлардын ({n}) калдыгы жаңыртылды — санын текшериңиз.",
        $"The draft was restored. Stock was refreshed for the products whose stock changed since it was saved ({n}) — check the quantities.",
        $"Taslak geri yüklendi. Kaydedildikten sonra stoğu değişen ürünlerin ({n}) stoğu güncellendi — miktarları kontrol edin.",
        $"Qoralama tiklandi. Saqlangandan beri qoldig'i o'zgargan mahsulotlar ({n}) qoldig'i yangilandi — miqdorni tekshiring.");

    public static string Restored(int n) => Tr.T(
        $"Продолжаем неоконченную приёмку: {Positions(n)}.", $"Бүтпөгөн кабыл алууну улантабыз: {Positions(n)}.",
        $"Continuing the unfinished receiving: {Positions(n)}.", $"Yarım kalan mal kabul devam ediyor: {Positions(n)}.", $"Tugallanmagan qabul davom etadi: {Positions(n)}.");
}

/// <summary>Автосохранение черновика одной приёмки: дебаунс 1,5 с после любого изменения (максимум 6 с ожидания при непрерывном вводе), немедленная запись при закрытии,
/// удаление после проведения. Снимок строк берётся в потоке UI (<see cref="SynchronizationContext"/>, захваченный при создании), запись на диск — в фоне.</summary>
public sealed class ReceivingDraftSession : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(6);

    private static readonly Dictionary<string, int> ActiveSlots = new(StringComparer.Ordinal);

    /// <summary>Приёмка этого окна уже ведёт черновик в этой программе (второе окно того же раздела свой черновик не пишет — иначе они затирали бы друг друга).</summary>
    public static bool IsSlotActive(string slot)
    {
        lock (ActiveSlots)
            return ActiveSlots.TryGetValue(slot, out var n) && n > 0;
    }

    private readonly string _slot;
    private readonly Func<ReceivingDraftFile?> _capture;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _maxWait;
    private readonly SynchronizationContext? _ui;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private DateTime? _dirtySince;
    private int _suspended;
    private bool _armed;
    private bool _disposed;
    private bool _everMeaningful;
    private string? _lastJson;
    private DateTime _startedAt;
    private int _pendingWrites;

    /// <summary>Записано (в потоке UI): время записи — для подписи «Черновик сохранён чч:мм».</summary>
    public event Action<DateTime>? Saved;
    /// <summary>Черновик удалён или стал пустым (в потоке UI) — подпись убрать.</summary>
    public event Action? Cleared;

    public DateTime? LastSavedAt { get; private set; }

    /// <summary>2026-11-11 (Д-5, живая проверка «Склада»): последняя попытка записи не удалась (снимок строк, диск, слишком много строк / большой файл) —
    /// набранное НЕ сохранится, «Закрыть склад?» предупреждает по-старому («строки пропадут»).</summary>
    public bool LastWriteFailed { get; private set; }

    /// <summary>2026-11-11 (Д-5): записать сейчас (с ожиданием) и ответить, лежит ли набранное в черновике — для текста вопроса при закрытии окна.
    /// false — автосохранение ещё не включено (не ответили «Продолжить / Начать заново»), окно закрыто или запись не удалась. Вызывать в потоке UI.</summary>
    public bool SaveNowAndCheck()
    {
        Flush(wait: true);
        lock (_gate)
        {
            if (_disposed || !_armed)
                return false;
        }
        return !LastWriteFailed && LastSavedAt is not null;
    }

    public ReceivingDraftSession(string slot, Func<ReceivingDraftFile?> capture, TimeSpan? debounce = null, TimeSpan? maxWait = null)
    {
        _slot = slot;
        _capture = capture;
        _debounce = debounce ?? DefaultDebounce;
        _maxWait = maxWait ?? DefaultMaxWait;
        _ui = SynchronizationContext.Current;
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        lock (ActiveSlots)
            ActiveSlots[slot] = ActiveSlots.GetValueOrDefault(slot) + 1;
    }

    /// <summary>Включить автосохранение. Вызывается ПОСЛЕ ответа на вопрос «Продолжить / Начать заново»: до него старый черновик на диске не трогаем
    /// (пустой список приёмки не должен стереть то, что ещё не восстановили).</summary>
    public void Arm()
    {
        lock (_gate)
            _armed = true;
    }

    /// <summary>Черновик загружен в приёмку («Продолжить»): запомнить, когда он начат, и считать, что файл уже содержит это состояние.</summary>
    public void Adopt(ReceivingDraftFile loaded)
    {
        lock (_gate)
        {
            _startedAt = loaded.StartedAt == default ? DateTime.Now : loaded.StartedAt;
            _everMeaningful = true;
            _armed = true;
        }
    }

    /// <summary>Пока объект не освобождён, изменения (загрузка строк из черновика) не ставят запись в очередь.</summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        return new SuspendScope(this);
    }

    private sealed class SuspendScope(ReceivingDraftSession owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref owner._suspended);
        }
    }

    /// <summary>Что-то изменилось: перезапустить отсчёт. Потокобезопасно и дёшево (вызывается на каждую букву).</summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed || !_armed || _suspended > 0)
                return;
            var now = DateTime.UtcNow;
            _dirtySince ??= now;
            var left = _maxWait - (now - _dirtySince.Value);
            var delay = left <= TimeSpan.Zero ? TimeSpan.Zero : left < _debounce ? left : _debounce;
            try { _timer.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* закрыли */ }
        }
    }

    private void OnTimer()
    {
        if (_ui is not null)
            _ui.Post(_ => Flush(wait: false), null);
        else
            Flush(wait: false);
    }

    /// <summary>Записать сейчас (закрытие окна, сворачивание, Deactivated). <paramref name="wait"/> = true — дождаться записи (закрытие: программа может завершиться).
    /// Вызывать в потоке UI.</summary>
    public void Flush(bool wait)
    {
        ReceivingDraftFile? file;
        lock (_gate)
        {
            if (_disposed || !_armed)
                return;
            _dirtySince = null;
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { return; }
            if (_suspended > 0)
                return;
        }
        try
        {
            file = _capture();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Черновик приёмки ({_slot}): снимок строк не снят ({ex.GetType().Name}: {ex.Message}) — повторим.", "WARNING");
            LastWriteFailed = true; // 2026-11-11 (Д-5)
            Touch();
            return;
        }

        if (!ReceivingDraftStore.IsMeaningful(file))
        {
            // Пустое состояние не пишем. Если прежде в этой приёмке уже что-то было (или мы продолжали черновик), а теперь пусто — файл убираем.
            bool had;
            lock (_gate)
            {
                had = _everMeaningful;
                _everMeaningful = false;
                _lastJson = null;
            }
            if (had)
                DeleteFile(wait);
            return;
        }

        var f = file!;
        var now = DateTime.Now;
        f.Version = ReceivingDraftFile.CurrentVersion;
        f.Slot = _slot;
        f.CompanyId = ReceivingDraftStore.CurrentCompany;
        if (f.Lines.Count > ReceivingDraftStore.MaxLines)
        {
            PosLogger.Log($"Черновик приёмки ({_slot}): строк {f.Lines.Count} — больше {ReceivingDraftStore.MaxLines}, черновик не пишется.", "WARNING");
            LastWriteFailed = true; // 2026-11-11 (Д-5)
            return;
        }
        string compare;
        lock (_gate)
        {
            _everMeaningful = true;
            if (_startedAt == default)
                _startedAt = now;
            f.StartedAt = _startedAt;
            f.SavedAt = default;
            compare = ReceivingDraftStore.Serialize(f);
            if (compare == _lastJson)
                return; // с прошлой записи ничего не изменилось
            _lastJson = compare;
        }
        f.SavedAt = now;
        var text = ReceivingDraftStore.Serialize(f);
        if (text.Length * 2 > ReceivingDraftStore.MaxBytes)
        {
            PosLogger.Log($"Черновик приёмки ({_slot}): файл слишком большой ({text.Length} знаков) — не пишется.", "WARNING");
            lock (_gate)
                _lastJson = null;
            LastWriteFailed = true; // 2026-11-11 (Д-5)
            return;
        }

        var seq = ReceivingDraftStore.NextSeq();
        void Write()
        {
            try
            {
                if (ReceivingDraftStore.Save(_slot, text, seq))
                    OnSaved(now);
                else
                {
                    LastWriteFailed = true; // 2026-11-11 (Д-5): набранное не сохранилось — при закрытии прежнее предупреждение
                    lock (_gate)
                        _lastJson = null; // не записалось — следующая попытка не пропустит «без изменений»
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingWrites);
            }
        }

        Interlocked.Increment(ref _pendingWrites);
        if (wait)
            Write();
        else
            _ = Task.Run(Write);
    }

    private void OnSaved(DateTime at)
    {
        LastSavedAt = at;
        LastWriteFailed = false; // 2026-11-11 (Д-5)
        var handler = Saved;
        if (handler is null)
            return;
        if (_ui is not null)
            _ui.Post(_ => handler(at), null);
        else
            handler(at);
    }

    /// <summary>Приёмка проведена / очищена / «Начать заново»: черновик удалить, подпись убрать. Запись, начатая раньше, файл не воскресит.</summary>
    public void Discard(bool wait = false)
    {
        lock (_gate)
        {
            _dirtySince = null;
            _lastJson = null;
            _everMeaningful = false;
            _startedAt = default;
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* закрыли */ }
        }
        DeleteFile(wait);
    }

    private void DeleteFile(bool wait)
    {
        var seq = ReceivingDraftStore.NextSeq(); // пол записей выставляется сразу, файл удаляется в фоне
        LastSavedAt = null;
        var cleared = Cleared;
        void Del()
        {
            ReceivingDraftStore.Delete(_slot, seq);
        }

        if (wait)
            Del();
        else
        {
            // пол — синхронно (запоздавшая запись не воскресит файл), сам файл — в фоне
            ReceivingDraftStore.RaiseFloor(_slot, seq);
            _ = Task.Run(Del);
        }
        if (cleared is not null)
        {
            if (_ui is not null)
                _ui.Post(_ => cleared(), null);
            else
                cleared();
        }
    }

    /// <summary>Для тестов: дождаться записей, начатых в фоне.</summary>
    internal bool WaitIdle(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pendingWrites) > 0 && DateTime.UtcNow < until)
            Thread.Sleep(5);
        return Volatile.Read(ref _pendingWrites) == 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _timer.Dispose(); } catch (ObjectDisposedException) { /* уже */ }
        }
        lock (ActiveSlots)
        {
            var n = ActiveSlots.GetValueOrDefault(_slot) - 1;
            if (n <= 0)
                ActiveSlots.Remove(_slot);
            else
                ActiveSlots[_slot] = n;
        }
    }
}
