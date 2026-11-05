using System.Drawing;
using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using VerifyXunit;

namespace NurMarketKassa.Tests.Snapshots;

/// <summary>2026-11-05: снимки (Verify, MIT) того, что уходит на бумагу и принтер этикеток: текст товарного чека, текст чека возврата, задание TSPL.
/// Эталон — файлы в Snapshots/Verified; изменился вид чека намеренно — посмотреть *.received.txt и переименовать в *.verified.txt.
/// (Отдельного X/Z-отчёта в программе больше нет — удалён 27.10; смену закрывает серверный Z-отчёт, текста для печати нет.)
/// Настройки печати чека выставляются явно и возвращаются на место; общий замок — чтобы другие тесты не видели чужих значений.</summary>
[Collection("UserPreferences")]
public sealed class PrintTextSnapshotTests
{
    /// <summary>Выставляет настройки чека на известные значения и возвращает их после теста.</summary>
    private sealed class PrefsScope : IDisposable
    {
        private readonly UserPreferences _p = UserPreferences.Instance;
        // 2026-11-05: настройки антифрода/чека в Telegram (ссылка на бумаге) — отдельная пустая папка: эталон снят при выключенной ссылке, чужие тесты на него не влияют.
        private readonly string? _oldAntiFraudDir = AntiFraudSettingsStore.DataDirOverride;
        private readonly string? _oldBot = UserPreferences.Instance.TelegramBotUsername;
        private readonly string _antiFraudDir = Path.Combine(Path.GetTempPath(), "nurmarket-print-snap-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly (int Paper, string Store, string Inn, string Addr, bool Name, bool InnOn, bool AddrOn, bool No, bool Date, bool Items, bool Total, string? User) _old;

        public PrefsScope()
        {
            _old = (_p.ReceiptPaperWidthMm, _p.StoreName, _p.StoreInn, _p.StoreAddress, _p.ShowStoreName, _p.ShowInn, _p.ShowAddress,
                _p.ShowReceiptNumber, _p.ShowDate, _p.ShowItems, _p.ShowTotal, PosApp.CurrentUserDisplayName);
            _p.ReceiptPaperWidthMm = ReceiptPaperProfile.Paper58mm;
            _p.StoreName = "ТЕСТ МАРКЕТ";
            _p.StoreInn = "01234567890123";
            _p.StoreAddress = "г. Бишкек, ул. Тестовая, 1";
            _p.ShowStoreName = true;
            _p.ShowInn = true;
            _p.ShowAddress = true;
            _p.ShowReceiptNumber = true;
            _p.ShowDate = true;
            _p.ShowItems = true;
            _p.ShowTotal = true;
            PosApp.CurrentUserDisplayName = "Айгуль К.";
            Directory.CreateDirectory(_antiFraudDir);
            AntiFraudSettingsStore.DataDirOverride = _antiFraudDir;
            AntiFraudSettingsStore.Reset();
            _p.TelegramBotUsername = null;
        }

        public void Dispose()
        {
            _p.ReceiptPaperWidthMm = _old.Paper;
            _p.StoreName = _old.Store;
            _p.StoreInn = _old.Inn;
            _p.StoreAddress = _old.Addr;
            _p.ShowStoreName = _old.Name;
            _p.ShowInn = _old.InnOn;
            _p.ShowAddress = _old.AddrOn;
            _p.ShowReceiptNumber = _old.No;
            _p.ShowDate = _old.Date;
            _p.ShowItems = _old.Items;
            _p.ShowTotal = _old.Total;
            PosApp.CurrentUserDisplayName = _old.User;
            _p.TelegramBotUsername = _oldBot;
            AntiFraudSettingsStore.DataDirOverride = _oldAntiFraudDir;
            AntiFraudSettingsStore.Reset();
            try { Directory.Delete(_antiFraudDir, true); } catch (IOException) { /* временная папка — не страшно */ }
        }
    }

    private const string CartJson = """
        {
          "receipt_number": "20261105-0042",
          "items": [
            { "product_name": "Молоко Простоквашино 1 л", "quantity": 2, "unit_price": 95.5, "line_total": 191 },
            { "product_name": "Хлеб белый", "quantity": 1, "unit_price": 30, "line_total": 30 },
            { "product_name": "Колбаса Докторская (весовая)", "product_id": "p3", "is_weight": true, "quantity": 0.356, "unit_price": 520, "line_total": 185.12, "discount_total": 5.12 }
          ]
        }
        """;

    [Fact]
    public Task SaleReceipt_Cash()
    {
        using var _ = new PrefsScope();
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(CartJson, null, "cash", "500", new DateTime(2026, 11, 5, 14, 30, 0));
        return Verifier.Verify(text);
    }

    [Fact]
    public Task SaleReceipt_Debt()
    {
        using var _ = new PrefsScope();
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(CartJson, null, "debt", null, new DateTime(2026, 11, 5, 14, 30, 0));
        return Verifier.Verify(text);
    }

    [Fact]
    public Task SaleReceipt_Offline_Note()
    {
        using var _ = new PrefsScope();
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(CartJson, "офлайн-продажа", "card", null, new DateTime(2026, 11, 5, 14, 30, 0));
        return Verifier.Verify(text);
    }

    [Fact]
    public Task ReturnReceipt()
    {
        using var _ = new PrefsScope();
        var text = ReturnReceiptTextBuilder.Build(
            "20261105-0042",
            [("Молоко Простоквашино 1 л", 1, 95.5m, 95.5m), ("Хлеб белый", 1, 30m, 30m)],
            125.5m, "Брак", false, "Айгуль К.");
        // В чеке возврата стоит текущее время — для снимка заменяем на метку.
        return Verifier.Verify(text).ScrubLinesWithReplace(l => l.StartsWith("Дата:", StringComparison.Ordinal) ? "Дата: <сейчас>" : l);
    }

    [Fact]
    public Task Tspl_Job_Header_And_Bitmap()
    {
        // Этикетка 16×6 точек с рамкой и диагональю: видно порядок строк SIZE/GAP/CLS/BITMAP/PRINT и инверсию битов TSPL.
        using var bmp = new Bitmap(16, 6);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.White);
        for (var x = 0; x < 16; x++)
        {
            bmp.SetPixel(x, 0, Color.Black);
            bmp.SetPixel(x, 5, Color.Black);
        }
        for (var y = 0; y < 6; y++)
            bmp.SetPixel(y, y, Color.Black);

        var plain = BarcodeLabelService.BuildTsplJob(bmp, 58, 40, 2, 0, 0, 3);
        var extras = BarcodeLabelService.BuildTsplJob(bmp, 40, 30, 3, 1.5, 0.5, 1, new TsplExtras(8, 4, true, "gap"));
        return Verifier.Verify(Describe("без доп. настроек", plain) + "\n\n" + Describe("плотность, скорость, поворот, датчик «gap»", extras));
    }

    /// <summary>Задание TSPL: текстовые команды как есть, точечный рисунок — строкой шестнадцатеричных байтов.</summary>
    private static string Describe(string title, byte[] job)
    {
        var ascii = Encoding.Latin1.GetString(job);
        var start = ascii.IndexOf("BITMAP ", StringComparison.Ordinal);
        var comma = start;
        for (var i = 0; i < 5; i++)
            comma = ascii.IndexOf(',', comma + 1);
        var tail = ascii.LastIndexOf("\r\nPRINT", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append('\n');
        sb.Append(ascii[..(comma + 1)].Replace("\r\n", "\n"));
        sb.Append('\n').Append("<рисунок> ").Append(Convert.ToHexString(job[(comma + 1)..tail])).Append('\n');
        sb.Append(ascii[(tail + 2)..].Replace("\r\n", "\n"));
        return sb.ToString();
    }
}
