using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-12 (владелец: «редактирование накладной возврата поставщику — стресс-тест»): расчёт и тело правки возврата поставщику на 1 / 30 / 500 / 2000 строках —
/// время, точность суммы до копейки (decimal, от нуля), дробные количества (кг/л не округляются), штучные — только целые, подпись ключа повтора стабильна
/// (тот же состав — тот же Idempotency-Key, любое изменение — другой), строки с 0 не уходят. Окно и сервер — в UiTests (SupplierReturnEditStressUiTests).</summary>
public sealed class SupplierReturnEditStressTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public SupplierReturnEditStressTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static List<SupplierReturnEditLine> Lines(int n) => Enumerable.Range(0, n).Select(i => i % 3 == 2
        ? new SupplierReturnEditLine("l" + i, "p" + i, "Сыр №" + i, "кг", 1.255, 150.5, 0, "")
        : new SupplierReturnEditLine("l" + i, "p" + i, "Молоко №" + i, "шт", 2, 150.5, 0, "")).ToList();

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(500)]
    [InlineData(2000)]
    public void ManyLines_TotalExact_BodyComplete_FastEnough(int n)
    {
        var lines = Lines(n);
        var sw = Stopwatch.StartNew();
        SupplierReturnEdit.Validate(lines, "ошиблись количеством").Should().BeNull();
        var body = SupplierReturnEdit.BuildPatchBody(lines, " ошиблись количеством ", " коммент ");
        var sig = SupplierReturnEdit.Signature("r1", 3, lines, "ошиблись количеством", "коммент");
        var total = SupplierReturnEdit.Total(lines);
        sw.Stop();

        var items = (List<Dictionary<string, object?>>)body["items"]!;
        items.Should().HaveCount(n);
        items[0].Keys.Should().BeEquivalentTo(new[] { "id", "qty", "purchase_price" }, "реальный контракт: только id, qty, purchase_price");
        if (n >= 3)
            items[2]["qty"].Should().Be("1.255", "кг не округляются");
        body["edit_reason"].Should().Be("ошиблись количеством");
        // сумма до копейки: штучная строка 301,00; весовая 1,255 × 150,5 = 188,8775 → 188,88
        var kg = n / 3;
        var expected = (decimal)(n - kg) * 301.00m + kg * 188.88m;
        ((decimal)total).Should().Be(expected);
        sig.Should().Contain("|v3|");
        sw.ElapsedMilliseconds.Should().BeLessThan(n >= 2000 ? 1500 : 300, $"расчёт {n} строк не тормозит окно");
    }

    [Fact]
    public void Signature_SameComposition_SameKey_AnyChange_NewKey()
    {
        var lines = Lines(30);
        var a = SupplierReturnEdit.Signature("r1", 3, lines, "причина", "");
        SupplierReturnEdit.Signature("r1", 3, Lines(30), "причина", "").Should().Be(a, "тот же состав после обрыва — тот же ключ");
        var changed = Lines(30);
        changed[29] = changed[29] with { Qty = 1 };
        SupplierReturnEdit.Signature("r1", 3, changed, "причина", "").Should().NotBe(a);
        SupplierReturnEdit.Signature("r1", 4, lines, "причина", "").Should().NotBe(a, "другая версия — другая операция");
        var price = Lines(30);
        price[0] = price[0] with { Price = 150.49 };
        SupplierReturnEdit.Signature("r1", 3, price, "причина", "").Should().NotBe(a);

        var key = new PendingIdempotencyKey();
        var k1 = key.Get(a);
        key.OnError(new HttpRequestException("обрыв")).Should().BeTrue("исход неизвестен — ключ остаётся");
        key.Get(a).Should().Be(k1, "повтор того же состава — тот же ключ");
        key.HasUnansweredAttempt.Should().BeTrue();
    }

    [Fact]
    public void ZeroLinesDropped_PieceMustBeWhole_KgFractionOk_AllZeroRefused()
    {
        var lines = Lines(3);
        lines[1] = lines[1] with { Qty = 0 };
        var items = (List<Dictionary<string, object?>>)SupplierReturnEdit.BuildPatchBody(lines, "причина правки", "")["items"]!;
        items.Select(i => i["id"]).Should().Equal("l0", "l2");

        var piece = Lines(1);
        piece[0] = piece[0] with { Qty = 1.5 };
        SupplierReturnEdit.Validate(piece, "причина правки").Should().Contain("целое");
        var kg = Lines(3);
        kg[2] = kg[2] with { Qty = 0.001 };
        SupplierReturnEdit.Validate(kg, "причина правки").Should().BeNull("1 грамм — допустимо");
        SupplierReturnEdit.Validate(Lines(2).Select(l => l with { Qty = 0 }).ToList(), "причина правки").Should().Contain("хотя бы одна строка");
        SupplierReturnEdit.Validate(Lines(2), "abc").Should().Contain("причину");
        SupplierReturnEdit.ParseNumber("0,755").Should().Be(0.755);
        SupplierReturnEdit.ParseNumber("1 255,5").Should().Be(1255.5);
        SupplierReturnEdit.ParseNumber("abc").Should().BeNull();
    }

    [Fact]
    public void ParseReturn_2000Lines_KeepsIdsAndFractions()
    {
        var json = "{\"id\":\"r1\",\"version\":3,\"can_edit\":true,\"can_void\":true,\"items\":["
                   + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":\"l{i}\",\"product_id\":\"p{i}\",\"product_name\":\"Т{i}\",\"qty\":\"{(i % 2 == 0 ? "0.125" : "3")}\",\"unit\":\"{(i % 2 == 0 ? "л" : "шт")}\",\"purchase_price\":\"10.10\"}}"))
                   + "]}";
        var sw = Stopwatch.StartNew();
        var info = SupplierApiService.ParseReturn(JsonDocument.Parse(json).RootElement.Clone());
        var lines = SupplierReturnEdit.FromInfo(info);
        sw.Stop();
        lines.Should().HaveCount(2000);
        lines[0].Qty.Should().Be(0.125, "литры не округляются");
        lines[1999].Id.Should().Be("l1999");
        info.EditState.Version.Should().Be(3);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }
}

// 2026-11-21 (владелец: «редактор возвраты поставщикам нету!»): предпросмотр «что изменится» при правке накладной возврата поставщику
public sealed class SupplierReturnEditPreviewTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public SupplierReturnEditPreviewTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static List<SupplierReturnEditLine> Two() => new()
    {
        new SupplierReturnEditLine("l1", "p1", "Молоко", "шт", 10, 100, 0, ""),
        new SupplierReturnEditLine("l2", "p2", "Сыр", "кг", 2.5, 400, 0, ""),
    };

    [Fact]
    public void NoChanges_NoPreview() =>
        SupplierReturnEdit.Preview(Two(), Two(), "cash").Should().BeEmpty();

    [Fact]
    public void LessQty_GoesBackToStock_AndCashIsLess()
    {
        var edited = Two();
        edited[0] = edited[0] with { Qty = 7 }; // −3 шт × 100 = −300 сом
        var p = SupplierReturnEdit.Preview(Two(), edited, "cash");
        p.Should().Contain(x => x.Contains("Молоко") && x.Contains("+3 шт") && x.Contains("вернётся на склад"));
        p.Should().Contain(x => x.StartsWith("Деньги:") && x.Contains("меньше") && x.Replace(' ', ' ').Contains("300,00"));
    }

    [Fact]
    public void RemovedLine_AndDebtOffset_DebtGrows()
    {
        var edited = Two();
        edited[1] = edited[1] with { Qty = 0 }; // убрали сыр: +2,5 кг на склад, −1000 сом
        var p = SupplierReturnEdit.Preview(Two(), edited, "debt_offset");
        p.Should().Contain(x => x.Contains("Сыр") && x.Contains("+2,5 кг") || x.Contains("Сыр") && x.Contains("+2.5 кг"));
        p.Should().Contain(x => x.StartsWith("Долг поставщику:") && x.Contains("долг станет больше") && x.Replace(' ', ' ').Contains("1 000,00"));
    }

    [Fact]
    public void MoreQty_LeavesStock_StockOnlyReturnHasNoMoney()
    {
        var edited = Two();
        edited[0] = edited[0] with { Qty = 12 };
        var p = SupplierReturnEdit.Preview(Two(), edited, "none");
        p.Should().Contain(x => x.Contains("уйдёт со склада") && x.Contains("2 шт"));
        p.Should().Contain(x => x.Contains("Деньги и долг не затрагиваются"));
    }

    [Fact]
    public void PriceChangeOnly_ChangesMoneyNotStock()
    {
        var edited = Two();
        edited[0] = edited[0] with { Price = 90 }; // −100 сом
        var p = SupplierReturnEdit.Preview(Two(), edited, "cash");
        p.Should().NotContain(x => x.StartsWith("Склад:"));
        p.Should().ContainSingle(x => x.StartsWith("Деньги:") && x.Contains("100,00"));
    }

    [Fact]
    public void ManyLines_StockListIsCapped()
    {
        var a = Enumerable.Range(0, 12).Select(i => new SupplierReturnEditLine("l" + i, "p" + i, "Т" + i, "шт", 5, 10, 0, "")).ToList();
        var b = a.Select(l => l with { Qty = 1 }).ToList();
        var p = SupplierReturnEdit.Preview(a, b, "cash");
        p.Count(x => x.StartsWith("Склад:")).Should().Be(6, "пять строк и «и ещё N»");
        p.Should().Contain(x => x.Contains("и ещё 7"));
    }
}
