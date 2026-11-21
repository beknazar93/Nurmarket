using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.Tests.Fiscal;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): разбор capabilities.fiscal и службы FiscalSettings. Сеть не используется. Запускать только tools/qa/test-safe.ps1.</summary>
[Collection(nameof(FiscalStateCollection))]
public sealed class FiscalCapabilitiesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-fiscal-" + Guid.NewGuid().ToString("N")[..8]);

    public FiscalCapabilitiesTests()
    {
        Directory.CreateDirectory(_dir);
        FiscalSettings.DirOverride = _dir;
        FiscalSettings.AccountOverride = "acc-1|company-1";
        FiscalSettings.ResetMemoryForTests();
    }

    public void Dispose()
    {
        FiscalSettings.DirOverride = null;
        FiscalSettings.AccountOverride = null;
        FiscalSettings.ResetMemoryForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string KassaJson = "{\"features\":[\"fiscal_kassa\",\"receipt_delete\"],\"fiscal\":{\"mode\":\"kassa\",\"fiscal_enabled\":true,\"tax_regime\":\"general\",\"vat_payer\":true,\"vat_rates\":[\"0.00\",\"12.00\"],\"nsp_rate\":\"2.00\"}}";

    private static IReadOnlySet<string> Features(JsonElement root) => ServerCapabilities.Parse(root);

    [Fact]
    public void Kassa_mode_block_is_parsed_with_rates_and_flags()
    {
        var root = J(KassaJson);
        var caps = FiscalCapabilities.Parse(root, Features(root));
        caps.FeatureAvailable.Should().BeTrue();
        caps.Mode.Should().Be(FiscalMode.Kassa);
        caps.FiscalEnabled.Should().BeTrue();
        caps.TaxRegime.Should().Be("general");
        caps.VatPayer.Should().BeTrue();
        caps.Rates.Should().Equal(0m, 12m);
        caps.NspRate.Should().Be(2m);
        caps.ShowsFiscalUi.Should().BeTrue();
        caps.KassaMustFiscalize.Should().BeTrue();
    }

    [Theory]
    [InlineData("server", FiscalMode.Server)]
    [InlineData("off", FiscalMode.Off)]
    [InlineData("", FiscalMode.Off)]
    [InlineData("что-то новое", FiscalMode.Off)]
    public void Server_and_off_modes_do_not_let_the_kassa_fiscalize(string mode, FiscalMode expected)
    {
        var root = J("{\"features\":[\"fiscal_kassa\"],\"fiscal\":{\"mode\":\"" + mode + "\",\"fiscal_enabled\":true}}");
        var caps = FiscalCapabilities.Parse(root, Features(root));
        caps.Mode.Should().Be(expected);
        caps.KassaMustFiscalize.Should().BeFalse("при server пробивает сервер, при off — как сегодня");
        caps.ShowsFiscalUi.Should().Be(expected == FiscalMode.Server, "off — ничего не показываем");
    }

    [Fact]
    public void Server_mode_does_not_need_the_fiscal_kassa_feature_but_kassa_mode_does()
    {
        var server = J("{\"features\":[],\"fiscal\":{\"mode\":\"server\",\"fiscal_enabled\":true}}");
        var s = FiscalCapabilities.Parse(server, Features(server));
        s.ServerFiscalizes.Should().BeTrue("режим server сервер называет сам — печать фискального чека сервера не зависит от имени признака");
        s.ShowsFiscalUi.Should().BeTrue();
        s.KassaMustFiscalize.Should().BeFalse();
        var ekassa = FiscalRequisites.Parse(J("{\"ekassa\":{\"status\":\"ok\",\"fd_number\":\"123\"}}"));
        ekassa.Status.Should().Be(FiscalStatus.Accepted);
        ekassa.DocNumber.Should().Be("123");
        FiscalRequisites.Parse(J("{\"ekassa\":{\"status\":\"error\"}}")).Status.Should().Be(FiscalStatus.Failed);
    }

    [Fact]
    public void Without_the_fiscal_kassa_feature_nothing_is_enabled_even_if_block_says_kassa()
    {
        var root = J("{\"features\":[\"receipt_delete\"],\"fiscal\":{\"mode\":\"kassa\",\"fiscal_enabled\":true}}");
        var caps = FiscalCapabilities.Parse(root, Features(root));
        caps.FeatureAvailable.Should().BeFalse();
        caps.KassaMustFiscalize.Should().BeFalse("адреса fiscal/ без признака fiscal_kassa не работают");
        caps.ShowsFiscalUi.Should().BeFalse();
    }

    [Fact]
    public void Kassa_mode_with_fiscal_disabled_does_not_fiscalize()
    {
        var root = J("{\"features\":[\"fiscal_kassa\"],\"fiscal\":{\"mode\":\"kassa\",\"fiscal_enabled\":false}}");
        var caps = FiscalCapabilities.Parse(root, Features(root));
        caps.ShowsFiscalUi.Should().BeTrue();
        caps.KassaMustFiscalize.Should().BeFalse();
    }

    [Fact]
    public void Missing_or_broken_block_means_off()
    {
        FiscalCapabilities.Parse(J("{\"features\":[\"fiscal_kassa\"]}"), new HashSet<string> { "fiscal_kassa" }).Mode.Should().Be(FiscalMode.Off);
        FiscalCapabilities.Parse(J("[]"), new HashSet<string>()).Mode.Should().Be(FiscalMode.Off);
        FiscalCapabilities.ParseBlock(J("\"oops\""), true).Mode.Should().Be(FiscalMode.Off);
        // ставки числами и строками, мусор отбрасывается, повторы убираются
        var caps = FiscalCapabilities.ParseBlock(J("{\"mode\":\"kassa\",\"vat_rates\":[0,\"12.00\",12,\"abc\",null]}"), true);
        caps.Rates.Should().Equal(0m, 12m);
    }

    [Fact]
    public void Features_array_still_contains_fiscal_kassa_so_has_works()
    {
        var root = J(KassaJson);
        Features(root).Should().Contain("fiscal_kassa").And.Contain("receipt_delete");
    }

    [Fact]
    public void Settings_service_applies_capabilities_and_survives_restart_from_disk()
    {
        var root = J(KassaJson);
        FiscalSettings.Current.Mode.Should().Be(FiscalMode.Off, "до ответа сервера — выключено");
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        FiscalSettings.KassaMustFiscalize.Should().BeTrue();
        FiscalSettings.VatRates.Should().Equal(0m, 12m);
        File.Exists(Path.Combine(_dir, FiscalSettings.FileName)).Should().BeTrue();

        FiscalSettings.ResetMemoryForTests(); // перезапуск кассы без связи
        FiscalSettings.Current.Mode.Should().Be(FiscalMode.Kassa);
        FiscalSettings.Current.NspRate.Should().Be(2m);
        FiscalSettings.ShowsFiscalUi.Should().BeTrue();
    }

    [Fact]
    public void Settings_are_bound_to_the_account_other_account_sees_off()
    {
        var root = J(KassaJson);
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        FiscalSettings.AccountOverride = "acc-2|company-2";
        FiscalSettings.Current.Mode.Should().Be(FiscalMode.Off, "режим чужой компании не показывается");
        FiscalSettings.ShowsFiscalUi.Should().BeFalse();
        FiscalSettings.AccountOverride = "acc-1|company-1";
        FiscalSettings.Current.Mode.Should().Be(FiscalMode.Kassa);
    }

    [Fact]
    public void Server_mode_means_server_fiscalizes_and_kassa_does_not()
    {
        var root = J("{\"features\":[\"fiscal_kassa\"],\"fiscal\":{\"mode\":\"server\",\"fiscal_enabled\":true,\"vat_rates\":[\"12.00\"]}}");
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        FiscalSettings.ServerFiscalizes.Should().BeTrue();
        FiscalSettings.KassaMustFiscalize.Should().BeFalse();
    }

    [Fact]
    public void Off_mode_hides_vat_rates_for_product_card()
    {
        var root = J("{\"features\":[\"fiscal_kassa\"],\"fiscal\":{\"mode\":\"off\",\"vat_rates\":[\"12.00\"]}}");
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        FiscalSettings.VatRates.Should().BeEmpty();
    }

    [Fact]
    public void Requisites_are_parsed_from_sale_object_and_old_receipts_are_none()
    {
        var req = FiscalRequisites.Parse(J("{\"fiscal_status\":\"accepted\",\"fiscal_doc_number\":\"FD-1\",\"fiscal_memory_number\":\"FM-2\",\"kkm_reg_number\":\"RN-3\",\"fiscal_sign\":\"S\",\"fiscal_qr_url\":\"https://q\",\"fiscal_at\":\"2026-11-14T10:00:00+06:00\",\"kkm_shift_id\":\"7\"}"));
        req.Status.Should().Be(FiscalStatus.Accepted);
        req.IsLocked.Should().BeTrue();
        req.DocNumber.Should().Be("FD-1");
        req.MemoryNumber.Should().Be("FM-2");
        req.KkmRegNumber.Should().Be("RN-3");
        req.QrUrl.Should().Be("https://q");
        req.At!.Value.Hour.Should().Be(10);
        req.KkmShiftId.Should().Be("7");

        var old = FiscalRequisites.Parse(J("{\"id\":\"x\",\"fiscal_status\":\"none\"}"));
        old.Status.Should().Be(FiscalStatus.None);
        old.HasData.Should().BeFalse();
        old.IsLocked.Should().BeFalse();
        FiscalRequisites.Parse(J("{\"id\":\"x\"}")).Status.Should().Be(FiscalStatus.None, "старый сервер без полей");
        FiscalRequisites.Parse(J("[]")).Should().Be(FiscalRequisites.None);
    }

    // ───────────── 2026-11-20: строка «ККМ» в режиме server и сведения о ККМ с сервера ─────────────

    private sealed class ConnectedProvider : IFiscalProvider
    {
        public string Name => "eKassa";
        public bool IsConnected => true;
        public Task<FiscalResult> SaleAsync(FiscalReceiptRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FiscalResult> ReturnAsync(FiscalReceiptRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FiscalShiftResult> ShiftAsync(FiscalShiftAction action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FiscalReportResult> ReportAsync(FiscalReportKind kind, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Server_mode_without_kkm_data_says_server_fiscalizes_and_never_mentions_telemedia()
    {
        var caps = FiscalCapabilities.ParseBlock(J("{\"mode\":\"server\",\"fiscal_enabled\":true}"), false);
        caps.Kkm.Should().BeNull("сервер сведений о ККМ не отдал — ничего не выдумываем");
        var text = FiscalFormRules.KkmStatusText(caps, new NotConnectedFiscalProvider());
        text.Should().Be(FiscalFormRules.ServerFiscalizesText);
        text.Should().NotContain("Telemedia");
    }

    [Fact]
    public void Telemedia_wait_text_is_shown_only_when_the_kassa_must_fiscalize_and_has_no_local_kkm()
    {
        var kassa = FiscalCapabilities.ParseBlock(J("{\"mode\":\"kassa\",\"fiscal_enabled\":true}"), true);
        FiscalFormRules.KkmStatusText(kassa, new NotConnectedFiscalProvider()).Should().Be(NotConnectedFiscalProvider.NotConnectedText).And.Contain("Telemedia");
        FiscalFormRules.KkmStatusText(kassa, new ConnectedProvider()).Should().Be("eKassa");
        var off = FiscalCapabilities.ParseBlock(J("{\"mode\":\"off\"}"), true);
        FiscalFormRules.KkmStatusText(off, new NotConnectedFiscalProvider()).Should().NotContain("Telemedia");
    }

    [Fact]
    public void Server_mode_with_kkm_data_shows_registered_on_server()
    {
        var caps = FiscalCapabilities.ParseBlock(J("{\"mode\":\"server\",\"kkm\":{\"serial_number\":\"EK00044889\",\"reg_number\":\"0000000318879\",\"fm_number\":\"0000000002470706\"," +
            "\"fm_valid_until\":\"2029-11-18\",\"ofd_inn\":\"01102201510234\",\"version\":\"2.0\",\"shift_open\":true,\"last_receipt_number\":\"57\"}}"), false);
        caps.Kkm.Should().NotBeNull();
        FiscalFormRules.KkmStatusText(caps, new NotConnectedFiscalProvider()).Should().NotBe(FiscalFormRules.ServerFiscalizesText).And.NotContain("Telemedia");
        var rows = caps.Kkm!.Rows().ToDictionary(r => r.Key, r => r.Value);
        rows["Serial"].Should().Be("EK00044889");
        rows["Reg"].Should().Be("0000000318879");
        rows["Fm"].Should().Be("0000000002470706");
        rows["FmUntil"].Should().Be("18.11.2029");
        rows["Ofd"].Should().Be("01102201510234");
        rows["Version"].Should().Be("2.0");
        rows["Shift"].Should().Be(Tr.T("открыта", "ачык", "open", "açık", "ochiq"));
        rows["LastReceipt"].Should().Be("57");
    }

    [Fact]
    public void Kkm_info_is_read_from_flat_fields_skips_empty_values_and_returns_null_without_data()
    {
        var flat = FiscalKkmInfo.Parse(J("{\"mode\":\"server\",\"kkm_serial\":\"EK1\",\"kkm_reg_number\":\"\",\"fm_number\":null,\"shift\":{\"status\":\"closed\"}}"));
        flat.Should().NotBeNull();
        flat!.Serial.Should().Be("EK1");
        flat.RegNumber.Should().BeNull();
        flat.FmNumber.Should().BeNull();
        flat.ShiftOpen.Should().BeFalse();
        FiscalKkmInfo.Parse(J("{\"mode\":\"server\",\"fiscal_enabled\":true,\"vat_rates\":[\"12.00\"]}")).Should().BeNull();
        FiscalKkmInfo.Parse(J("[]")).Should().BeNull();
    }

    [Fact]
    public void Kkm_info_survives_restart_from_disk_and_a_put_without_kkm_keeps_it()
    {
        var root = J("{\"features\":[],\"fiscal\":{\"mode\":\"server\",\"fiscal_enabled\":true,\"kkm\":{\"serial_number\":\"EK00044889\",\"fm_valid_until\":\"2029-11-18\"}}}");
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        FiscalSettings.ResetMemoryForTests();
        FiscalSettings.Current.Kkm!.Serial.Should().Be("EK00044889");
        FiscalSettings.Current.Kkm!.FmValidUntil.Should().Be("2029-11-18");
        // тот же ответ capabilities ещё раз — «ничего не изменилось», обработчик не дёргается
        var changed = 0;
        void Handler() => changed++;
        FiscalSettings.Changed += Handler;
        try { FiscalSettings.Apply(root, Features(root), "acc-1|company-1"); }
        finally { FiscalSettings.Changed -= Handler; }
        changed.Should().Be(0);
    }

    [Fact]
    public async Task Not_connected_provider_does_not_scare_in_server_mode()
    {
        var root = J("{\"features\":[],\"fiscal\":{\"mode\":\"server\",\"fiscal_enabled\":true}}");
        FiscalSettings.Apply(root, Features(root), "acc-1|company-1");
        var shift = await new NotConnectedFiscalProvider().ShiftAsync(FiscalShiftAction.Open);
        shift.ErrorText.Should().Be(FiscalFormRules.ServerFiscalizesText).And.NotContain("Telemedia");
        var report = await new NotConnectedFiscalProvider().ReportAsync(FiscalReportKind.X);
        report.ErrorText.Should().NotContain("Telemedia");
    }

    [Fact]
    public void Receipt_template_is_off_without_a_connected_provider()
    {
        var accepted = new FiscalRequisites(FiscalStatus.Accepted, "FD-1", "FM-2", "RN-3", "SIGN", "https://q");
        FiscalReceiptTemplate.Enabled.Should().BeFalse("провайдера нет — в чеке ничего нового");
        FiscalReceiptTemplate.Lines(accepted).Should().BeEmpty();
        FiscalReceiptTemplate.QrPayload(accepted).Should().BeNull();

        // шаблон (когда включат): строки в ширину чека, только принятый чек
        var lines = FiscalReceiptTemplate.Lines(accepted, 20, enabled: true);
        lines.Should().HaveCountGreaterThan(3).And.OnlyContain(l => l.Length <= 20);
        lines.Should().Contain(l => l.Contains("FD-1"));
        FiscalReceiptTemplate.QrPayload(accepted, enabled: true).Should().Be("https://q");
        FiscalReceiptTemplate.Lines(new FiscalRequisites(FiscalStatus.Failed, Error: "x"), 32, enabled: true).Should().BeEmpty("нефискальный чек не выдаётся за фискальный");
    }
}

[CollectionDefinition(nameof(FiscalStateCollection), DisableParallelization = true)]
public sealed class FiscalStateCollection { }
