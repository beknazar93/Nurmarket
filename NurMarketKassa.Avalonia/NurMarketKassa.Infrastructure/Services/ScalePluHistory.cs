using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (сервер «ev1311»: товар в архиве освобождает PLU; при возврате из архива весовой товар получает НОВЫЙ PLU): память «PLU, который у товара был раньше».
/// Источник правды по PLU — карточка товара с сервера (лента, точечное обновление, полная загрузка кладут её PLU в базу и кэш плиток). Но этикетки на полках и ячейки на весах
/// остаются со старым номером: пока товар не отправлен на весы заново, весы печатают прежний код. Отсюда два риска, которые закрывает этот список:
/// • скан этикетки со старым PLU, когда он освобождён или перешёл к другому товару, не должен молча продать чужой товар или сказать просто «не найден» —
///   <see cref="CheckLabel"/> даёт понятное предупреждение (свободен — сообщение и отказ; у другого товара — вопрос кассиру);
/// • окно «Весы» помечает такие товары «PLU изменился — отправьте на весы» (<see cref="HasChange"/>) и не держит закреплённый «Код в ШК» со старым номером.
/// Файл scale-plu-history.json рядом с scale-label-codes.json; запись живёт <see cref="Keep"/> (этикетки на полках не вечны), список маленький.</summary>
public static class ScalePluHistory
{
    private const string FileName = "scale-plu-history.json";

    /// <summary>Сколько помним старый PLU: этикетки и ячейки весов за месяц обновляются.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>Только для тестов: другая папка данных вместо %APPDATA%.</summary>
    public static string? DirOverride { get; set; }

    /// <summary>Только для тестов: «сейчас» (UTC).</summary>
    public static Func<DateTime>? NowUtcOverride { get; set; }

    private static DateTime NowUtc => NowUtcOverride?.Invoke() ?? DateTime.UtcNow;

    /// <summary>Прежний PLU товара. <see cref="NewPlu"/> null — PLU освобождён (товар в архиве или удалён) и нового пока нет.</summary>
    public sealed record Retired(int Plu, string ProductId, string Name, int? NewPlu, DateTime AtUtc);

    private sealed class Entry
    {
        public int Plu { get; set; }
        public string ProductId { get; set; } = "";
        public string Name { get; set; } = "";
        public int? NewPlu { get; set; }
        public DateTime AtUtc { get; set; }
    }

    private static readonly object Gate = new();
    private static List<Entry>? _list;
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal);

    private static string FilePath => Path.Combine(
        DirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), FileName);

    private static List<Entry> List()
    {
        if (_list is not null)
            return _list;
        var list = new List<Entry>();
        try
        {
            if (File.Exists(FilePath))
                list = JsonSerializer.Deserialize<List<Entry>>(AtomicFile.ReadAllText(FilePath) ?? "[]") ?? new List<Entry>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"PLU: список прежних PLU не прочитан ({ex.Message}).", "WARNING");
        }
        _list = list;
        return list;
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_list ?? new List<Entry>()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"PLU: список прежних PLU не сохранён ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Товар сменил PLU (<paramref name="newPlu"/> — новый) или освободил его (null: архив, удаление). Без прежнего PLU или при том же номере — ничего.</summary>
    public static void NoteChange(string productId, string? name, int? oldPlu, int? newPlu)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        if (oldPlu is not > 0)
        {
            if (newPlu is > 0)
                NoteGained(productId, newPlu.Value); // вернулся из архива и получил PLU
            return;
        }
        if (oldPlu == newPlu)
            return;
        try
        {
            lock (Gate)
            {
                var list = List();
                var now = NowUtc;
                list.RemoveAll(e => now - e.AtUtc > Keep
                    || (e.Plu == oldPlu && e.ProductId == productId)
                    || (newPlu is > 0 && e.Plu == newPlu && e.ProductId == productId)); // вернул себе прежний номер — он снова не «старый»
                list.Add(new Entry { Plu = oldPlu.Value, ProductId = productId, Name = (name ?? "").Trim(), NewPlu = newPlu is > 0 ? newPlu : null, AtUtc = now });
                Accepted.RemoveWhere(k => k.StartsWith(oldPlu.Value.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal));
                Save();
            }
            PosLogger.Log($"PLU: у товара {productId} PLU {oldPlu} " + (newPlu is > 0 ? $"заменён на {newPlu}" : "освобождён") + " — этикетки и ячейки весов со старым номером устарели.", "SCALES");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"PLU: смена PLU не записана ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Товар без PLU получил его (вернулся из архива): у его прежних записей появляется «теперь у него PLU …»; тот же номер, что был, — запись больше не нужна.</summary>
    public static void NoteGained(string productId, int newPlu)
    {
        try
        {
            lock (Gate)
            {
                var list = List();
                var mine = list.Where(e => e.ProductId == productId).ToList();
                if (mine.Count == 0)
                    return;
                foreach (var e in mine)
                {
                    if (e.Plu == newPlu)
                        list.Remove(e);
                    else
                        e.NewPlu = newPlu;
                }
                Save();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"PLU: новый PLU товара не записан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Есть ли хоть одна запись (дёшево проверить, нужно ли что-то отслеживать).</summary>
    public static bool Any
    {
        get
        {
            lock (Gate)
                return List().Count > 0;
        }
    }

    /// <summary>Прежний PLU, равный коду из этикетки (ведущие нули не важны), если он ещё помнится; иначе null.</summary>
    public static Retired? Find(string? embeddedCode)
    {
        if (!int.TryParse((embeddedCode ?? "").Trim().TrimStart('0'), NumberStyles.None, CultureInfo.InvariantCulture, out var plu) || plu <= 0)
            return null;
        lock (Gate)
        {
            var now = NowUtc;
            var e = List().Where(x => x.Plu == plu && now - x.AtUtc <= Keep).OrderByDescending(x => x.AtUtc).FirstOrDefault();
            return e is null ? null : new Retired(e.Plu, e.ProductId, e.Name, e.NewPlu, e.AtUtc);
        }
    }

    /// <summary>У товара PLU менялся недавно — на весы его нужно отправить заново (если он там уже был).</summary>
    public static bool HasChange(string productId)
    {
        lock (Gate)
        {
            var now = NowUtc;
            return List().Any(e => e.ProductId == productId && now - e.AtUtc <= Keep);
        }
    }

    /// <summary>Код — прежний PLU именно этого товара (закреплённый «Код в ШК» с таким номером устарел).</summary>
    public static bool IsRetiredCodeOf(string productId, string? code)
    {
        if (!int.TryParse((code ?? "").Trim().TrimStart('0'), NumberStyles.None, CultureInfo.InvariantCulture, out var plu) || plu <= 0)
            return false;
        lock (Gate)
        {
            var now = NowUtc;
            return List().Any(e => e.ProductId == productId && e.Plu == plu && now - e.AtUtc <= Keep);
        }
    }

    public enum LabelProblemKind
    {
        /// <summary>Старый PLU свободен (или товар в архиве): по этикетке продавать нечего.</summary>
        Freed,
        /// <summary>Старый PLU теперь у другого товара: спросить кассира.</summary>
        TakenByOther,
    }

    /// <param name="Message">Текст для кассира (5 языков).</param>
    public sealed record LabelProblem(LabelProblemKind Kind, string Message, string AcceptKey);

    /// <summary>Проверка скана весовой этикетки (<paramref name="embeddedCode"/> — код из штрихкода, <paramref name="found"/> — что нашла касса).
    /// null — всё в порядке (PLU не менялся, найден тот же товар, либо кассир уже подтвердил этот товар для этого номера).</summary>
    public static LabelProblem? CheckLabel(string? embeddedCode, CatalogProductTileVm? found)
    {
        var retired = Find(embeddedCode);
        if (retired is null)
            return null;
        if (found is not null && string.Equals(found.Id, retired.ProductId, StringComparison.Ordinal))
            return null; // тот же товар (нашёлся по артикулу или по списку кодов весов) — этикетка верна по существу
        var plu = retired.Plu.ToString(CultureInfo.InvariantCulture);
        var was = string.IsNullOrWhiteSpace(retired.Name) ? "—" : retired.Name;
        if (found is null)
            return new LabelProblem(LabelProblemKind.Freed, FreedText(plu, was, retired.NewPlu), plu + "|");
        var key = plu + "|" + found.Id;
        lock (Gate)
        {
            if (Accepted.Contains(key))
                return null;
        }
        return new LabelProblem(LabelProblemKind.TakenByOther, TakenText(plu, was, found.Title, retired.NewPlu), key);
    }

    /// <summary>Кассир подтвердил: этикетка с этим номером — именно найденный товар (до перезапуска не спрашивать снова).</summary>
    public static void Accept(LabelProblem problem)
    {
        lock (Gate)
            Accepted.Add(problem.AcceptKey);
    }

    private static string NewPluPart(int? newPlu) => newPlu is > 0
        ? Tr.T($" Теперь у него PLU {newPlu}.", $" Эми анын PLU коду {newPlu}.", $" It now has PLU {newPlu}.", $" Şimdi PLU kodu {newPlu}.", $" Endi uning PLU kodi {newPlu}.")
        : Tr.T(" Сейчас PLU у него нет (товар в архиве или удалён).", " Азыр анын PLU коду жок (товар архивде же өчүрүлгөн).",
            " It has no PLU now (the product is archived or deleted).", " Şu an PLU kodu yok (ürün arşivde veya silinmiş).", " Hozir uning PLU kodi yo'q (mahsulot arxivda yoki o'chirilgan).");

    private static string FreedText(string plu, string was, int? newPlu) => Tr.T(
        $"Этикетка со старым PLU {plu}: так был помечен товар «{was}».{NewPluPart(newPlu)} Товар не добавлен. Отправьте товар на весы заново («Весы» → «Отправить») и напечатайте новую этикетку.",
        $"PLU {plu} эски этикетка: «{was}» товары ушундай белгиленген.{NewPluPart(newPlu)} Товар кошулган жок. Товарды таразага кайра жөнөтүңүз («Тараза» → «Жөнөтүү») жана жаңы этикетка басыңыз.",
        $"A label with the old PLU {plu}: it marked “{was}”.{NewPluPart(newPlu)} The product was not added. Send the product to the scale again (Scales → Send) and print a new label.",
        $"Eski PLU {plu} etiketi: «{was}» ürününü işaretliyordu.{NewPluPart(newPlu)} Ürün eklenmedi. Ürünü teraziye yeniden gönderin (Teraziler → Gönder) ve yeni etiket yazdırın.",
        $"Eski PLU {plu} yorlig'i: «{was}» mahsulotini belgilagan.{NewPluPart(newPlu)} Mahsulot qo'shilmadi. Mahsulotni taroziga qayta yuboring (Tarozilar → Yuborish) va yangi yorliq chop eting.");

    private static string TakenText(string plu, string was, string now, int? newPlu) => Tr.T(
        $"PLU {plu} раньше был у товара «{was}», а теперь у «{now}».{NewPluPart(newPlu)} Если этикетка старая, это «{was}», а не «{now}». Добавить «{now}»?",
        $"PLU {plu} мурда «{was}» товарында болгон, азыр «{now}» товарында.{NewPluPart(newPlu)} Этикетка эски болсо, бул «{was}», «{now}» эмес. «{now}» кошулсунбу?",
        $"PLU {plu} used to belong to “{was}” and now belongs to “{now}”.{NewPluPart(newPlu)} If the label is old, it is “{was}”, not “{now}”. Add “{now}”?",
        $"PLU {plu} eskiden «{was}» ürününündü, şimdi «{now}» ürününde.{NewPluPart(newPlu)} Etiket eskiyse bu «{was}», «{now}» değil. «{now}» eklensin mi?",
        $"PLU {plu} avval «{was}» mahsulotida edi, endi «{now}» da.{NewPluPart(newPlu)} Yorliq eski bo'lsa, bu «{was}», «{now}» emas. «{now}» qo'shilsinmi?");

    /// <summary>Только для тестов: забыть всё в памяти (файл остаётся).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _list = null;
            Accepted.Clear();
        }
    }
}
