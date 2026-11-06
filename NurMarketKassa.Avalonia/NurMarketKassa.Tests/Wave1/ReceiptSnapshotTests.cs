using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using VerifyXunit;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — снимки Verify текста чека продажи (смешанная оплата со скидками, предоплата долга с рассрочкой, безнал) и чека возврата
/// (весь чек, части с дробным весом, без причины и кассира). Любое изменение денежного вывода на бумаге видно в разнице снимков. Эталоны — в Wave1/Verified;
/// изменили вид чека намеренно — посмотреть *.received.txt и переименовать в *.verified.txt.
/// Весовых строк в чеках нет: их итог зависит от общего признака сервера weight_line_total_floor (его покрывают property-тесты WeightLineFloorPropertyTests).
/// Настройки печати выставляются явно и возвращаются на место; коллекция общая с остальными тестами UserPreferences.</summary>
[Collection("UserPreferences")]
public sealed class ReceiptSnapshotTests
{
    private sealed class PrefsScope : IDisposable
    {
        private readonly UserPreferences _p = UserPreferences.Instance;
        private readonly string? _oldAntiFraudDir = AntiFraudSettingsStore.DataDirOverride;
        private readonly string? _oldBot = UserPreferences.Instance.TelegramBotUsername;
        private readonly string _antiFraudDir = Path.Combine(Path.GetTempPath(), "nurmarket-w1-snap-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly (int Paper, string Store, string Inn, string Addr, bool Name, bool InnOn, bool AddrOn, bool No, bool Date, bool Items, bool Total, string? User, string Penalty) _old;

        public PrefsScope()
        {
            _old = (_p.ReceiptPaperWidthMm, _p.StoreName, _p.StoreInn, _p.StoreAddress, _p.ShowStoreName, _p.ShowInn, _p.ShowAddress,
                _p.ShowReceiptNumber, _p.ShowDate, _p.ShowItems, _p.ShowTotal, PosApp.CurrentUserDisplayName, _p.DebtLatePenaltyText);
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
            _p.DebtLatePenaltyText = "";
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
            _p.DebtLatePenaltyText = _old.Penalty;
            PosApp.CurrentUserDisplayName = _old.User;
            _p.TelegramBotUsername = _oldBot;
            AntiFraudSettingsStore.DataDirOverride = _oldAntiFraudDir;
            AntiFraudSettingsStore.Reset();
            try { Directory.Delete(_antiFraudDir, true); } catch (IOException) { /* временная папка — не страшно */ }
        }
    }

    private static readonly DateTime Moment = new(2026, 11, 6, 10, 15, 0);

    /// <summary>Молоко 2 × 95,50 = 191; Хлеб 30 со скидкой строки 5; скидка чека 10 %: (221 − 5) × 10 % = 21,60; итог 194,40.</summary>
    private const string DiscountedCart = """
        {
          "receipt_number": "20261106-0007",
          "order_discount_percent": 10,
          "items": [
            { "product_id": "p1", "server_is_weight": false, "product_name": "Молоко Простоквашино 1 л", "quantity": 2, "unit_price": 95.5, "line_total": 191 },
            { "product_id": "p2", "server_is_weight": false, "product_name": "Хлеб белый", "quantity": 1, "unit_price": 30, "line_total": 30, "discount_total": 5 }
          ]
        }
        """;

    private const string DebtInstallmentCart = """
        {
          "receipt_number": "20261106-0008",
          "debt_client_name": "Бекназар Тестов",
          "debt_client_phone": "+996700123456",
          "debt_schedule": {
            "unit": "month", "interval": 1,
            "payments": [
              { "number": 1, "due_date": "2026-12-06", "amount": "450.00" },
              { "number": 2, "due_date": "2027-01-06", "amount": "450.00" },
              { "number": 3, "due_date": "2027-02-06", "amount": "400.00" }
            ]
          },
          "items": [
            { "product_id": "p3", "server_is_weight": false, "product_name": "Чайник электрический", "quantity": 1, "unit_price": 1200, "line_total": 1200 },
            { "product_id": "p4", "server_is_weight": false, "product_name": "Кружка", "quantity": 4, "unit_price": 50, "line_total": 200 }
          ]
        }
        """;

    [Fact]
    public Task SaleReceipt_Mixed_WithLineAndOrderDiscount()
    {
        using var _ = new PrefsScope();
        // наличных 100, остальное (94,40) безналом
        return Verifier.Verify(CartReceiptTextBuilder.BuildSimpleReceipt(DiscountedCart, null, "mixed", "100", Moment)).UseDirectory("Verified");
    }

    [Fact]
    public Task SaleReceipt_Cash_WithDiscounts_ShowsChange()
    {
        using var _ = new PrefsScope();
        return Verifier.Verify(CartReceiptTextBuilder.BuildSimpleReceipt(DiscountedCart, null, "cash", "200", Moment)).UseDirectory("Verified");
    }

    [Fact]
    public Task SaleReceipt_Card_Transfer()
    {
        using var _ = new PrefsScope();
        return Verifier.Verify(CartReceiptTextBuilder.BuildSimpleReceipt(DiscountedCart, null, "transfer", "194.40", Moment)).UseDirectory("Verified");
    }

    [Fact]
    public Task SaleReceipt_Debt_NonCashPrepayment_Installments()
    {
        using var _ = new PrefsScope();
        return Verifier.Verify(CartReceiptTextBuilder.BuildSimpleReceipt(DebtInstallmentCart, null, "debt-noncash", "100", Moment)).UseDirectory("Verified");
    }

    [Fact]
    public Task SaleReceipt_Reprint_Marked()
    {
        using var _ = new PrefsScope();
        // в повторной печати стоит текущее время — заменяем на метку
        return Verifier.Verify(CartReceiptTextBuilder.BuildSimpleReceipt(DiscountedCart, null, "cash", "200", Moment, isReprint: true))
            .UseDirectory("Verified")
            .ScrubLinesWithReplace(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$") ? "<время печати>" : l);
    }

    // ───────────── возврат ─────────────

    private static string ScrubDate(string text) =>
        string.Join('\n', text.Split('\n').Select(l => l.StartsWith("Дата:", StringComparison.Ordinal) ? "Дата: <сейчас>" : l));

    [Fact]
    public Task ReturnReceipt_WholeSale_WithReason()
    {
        using var _ = new PrefsScope();
        var text = ReturnReceiptTextBuilder.Build("20261106-0007",
            [("Молоко Простоквашино 1 л", 2, 95.5m, 191m), ("Хлеб белый", 1, 25m, 25m)],
            216m, "Покупатель передумал", true, "Айгуль К.");
        return Verifier.Verify(ScrubDate(text)).UseDirectory("Verified");
    }

    [Fact]
    public Task ReturnReceipt_Partial_FractionalWeight_NoReason_NoCashier()
    {
        using var _ = new PrefsScope();
        var text = ReturnReceiptTextBuilder.Build("20261106-0009",
            [("Яблоки (весовые)", 0.5, 120m, 60m), ("Сыр Российский", 0.25, 640m, 160m)],
            220m, null, false, null);
        return Verifier.Verify(ScrubDate(text)).UseDirectory("Verified");
    }

    [Fact]
    public Task ReturnReceipt_NoOriginalNumber()
    {
        using var _ = new PrefsScope();
        var text = ReturnReceiptTextBuilder.Build(null, [("Кружка", 1, 50m, 50m)], 50m, "Брак", false, "Айгуль К.");
        return Verifier.Verify(ScrubDate(text)).UseDirectory("Verified");
    }
}
