using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave3;

/// <summary>2026-11-06: В3 — безопасность: права на правку цены и склада только при явном true (Л4-20), фильтр журнала закрывает токен Telegram-бота (Б-4),
/// токен бота поддержки в файле шифруется (Б-3), чеки в Telegram по номеру целиком (Л5-19), запись «кто выключил защиту» (В3 хвост).
/// Статические хранилища — по очереди с остальными тестами защиты (коллекция UserPreferences).</summary>
[Collection("UserPreferences")]
public sealed class Wave3SecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-wave3-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _oldBot;

    public Wave3SecurityTests()
    {
        Directory.CreateDirectory(_dir);
        AntiFraudSettingsStore.DataDirOverride = _dir;
        AntiFraudSettingsStore.Reset();
        FraudEventStore.PathOverride = Path.Combine(_dir, "events.jsonl");
        TelegramReceiptLinks.ResetForTests();
        _oldBot = UserPreferences.Instance.TelegramBotUsername;
        UserPreferences.Instance.TelegramBotUsername = "TestShopBot";
    }

    public void Dispose()
    {
        UserPreferences.Instance.TelegramBotUsername = _oldBot;
        AntiFraudSettingsStore.DataDirOverride = null;
        AntiFraudSettingsStore.Reset();
        FraudEventStore.PathOverride = null;
        TelegramReceiptLinks.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ───────── Л4-20: права строже ─────────

    [Theory]
    [InlineData("{\"role\":\"cashier\"}", false)]
    [InlineData("{\"role\":\"cashier\",\"can_view_market_edit_price\":false}", false)]
    [InlineData("{\"role\":\"cashier\",\"can_view_market_edit_price\":true}", true)]
    [InlineData("{\"role\":\"cashier\",\"permissions\":[\"can_view_market_edit_price\"]}", true)]
    [InlineData("{\"role\":\"owner\"}", true)]
    [InlineData("{\"role\":\"admin\",\"can_view_market_edit_price\":false}", true)]
    public void Price_edit_needs_explicit_true_for_staff(string payload, bool expected) =>
        PermissionService.IsPriceEditAllowed(Json(payload)).Should().Be(expected);

    [Theory]
    [InlineData("{\"role\":\"cashier\"}", false)]
    [InlineData("{\"role\":\"manager\",\"can_edit_products\":false}", false)]
    [InlineData("{\"role\":\"manager\",\"can_edit_products\":true}", true)]
    [InlineData("{\"role\":\"cashier\",\"user\":{\"permissions\":[\"can_edit_products\"]}}", true)]
    [InlineData("{\"role\":\"owner\"}", true)]
    public void Product_edit_needs_explicit_true_for_staff(string payload, bool expected) =>
        PermissionService.IsProductEditAllowed(Json(payload)).Should().Be(expected);

    [Fact]
    public void Label_print_stays_open_when_the_server_sends_nothing() =>
        PermissionService.IsLabelPrintAllowed(Json("{\"role\":\"cashier\"}")).Should().BeTrue("строже только правка цены и склада, ярлыки прежние");

    // ───────── Б-4: фильтр журнала ─────────

    [Fact]
    public void Redactor_hides_telegram_bot_token_even_without_label()
    {
        const string secret = "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";
        var url = SensitiveDataRedactor.Redact("GET https://api.telegram.org/bot123456789:" + secret + "/getMe failed");
        url.Should().NotContain(secret);
        url.Should().Contain("bot***");
        SensitiveDataRedactor.Redact("Ошибка: 987654321:" + secret + " отклонён").Should().NotContain(secret);
    }

    [Fact]
    public void Redactor_hides_google_key_and_keeps_times_and_sums()
    {
        SensitiveDataRedactor.Redact("key=AIzaSyA1234567890abcdefghijklmnopqrstuv").Should().NotContain("AIzaSyA1234567890");
        const string harmless = "Время 12:34:56, отношение 100:25, итог 1000:5";
        SensitiveDataRedactor.Redact(harmless).Should().Be(harmless);
    }

    // ───────── Б-3: токен бота поддержки в файле ─────────

    [Fact]
    public void Support_bot_token_is_encrypted_in_its_file()
    {
        var path = Path.Combine(_dir, "support-bot.json");
        File.WriteAllText(path, "{\"token\":\"123456:SECRET-TOKEN-VALUE\",\"chat_id\":\"-100500\"}");
        File.WriteAllText(path + ".bak", "{\"token\":\"123456:SECRET-TOKEN-VALUE\"}");

        SupportLogService.TryProtectInFile(path, "123456:SECRET-TOKEN-VALUE", "-100500");

        var text = File.ReadAllText(path);
        text.Should().NotContain("SECRET-TOKEN-VALUE");
        File.Exists(path + ".bak").Should().BeFalse("копия с открытым токеном не остаётся");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.TryGetProperty("token", out _).Should().BeFalse();
        doc.RootElement.GetProperty("chat_id").GetString().Should().Be("-100500");
        WindowsDpapiHelper.UnprotectFromBase64(doc.RootElement.GetProperty("token_protected").GetString()).Should().Be("123456:SECRET-TOKEN-VALUE");
    }

    // ───────── Л5-19: чеки в Telegram — номер целиком ─────────

    [Fact]
    public void Telegram_receipts_do_not_mix_kyrgyz_and_uzbek_numbers_with_same_tail()
    {
        TelegramReceiptLinks.Link("+998 55 512 34 56", "chat-uz", "Uz");
        TelegramReceiptLinks.Link("+996 555 123 456", "chat-kg", "Kg");

        TelegramReceiptLinks.ChatsForPhone("0555123456").Should().Equal("chat-kg");
        TelegramReceiptLinks.ChatsForPhone("+996555123456").Should().Equal("chat-kg");
        TelegramReceiptLinks.ChatsForPhone("+998555123456").Should().Equal("chat-uz");
        TelegramReceiptLinks.ChatsForPhone("+7 555 123 456").Should().BeEmpty();
    }

    [Fact]
    public void Old_nine_digit_links_still_work_for_local_numbers_only()
    {
        File.WriteAllText(Path.Combine(_dir, "telegram-receipt-links.json"),
            "[{\"Phone\":\"555123456\",\"ChatId\":\"old\",\"Name\":null,\"Bot\":null,\"LinkedAt\":\"2026-11-01T10:00:00+06:00\"}]");
        TelegramReceiptLinks.ResetForTests();

        TelegramReceiptLinks.ChatsForPhone("0555 123 456").Should().Equal("old");
        TelegramReceiptLinks.ChatsForPhone("+998 555 123 456").Should().BeEmpty("старая запись не должна уходить на чужой код страны");
    }

    // ───────── В3 хвост: кто выключил защиту ─────────

    [Fact]
    public void Switching_protection_off_is_written_to_the_fraud_journal()
    {
        AntiFraudSettingsStore.Update(d => { d.Guard.Enabled = true; d.Fraud.Enabled = true; });
        FraudEventStore.ReadAll().Should().BeEmpty("включение защиты событий не пишет");

        AntiFraudSettingsStore.Update(d => d.Guard.Enabled = false);
        AntiFraudSettingsStore.Update(d => d.Guard.Enabled = false); // повторное «выключение» — уже выключено, второй записи нет
        AntiFraudSettingsStore.Update(d => d.Fraud.Enabled = false);

        var events = FraudEventStore.ReadAll().Where(e => e.Kind == FraudKinds.ProtectionOff).ToList();
        events.Select(e => e.Subject).Should().BeEquivalentTo(new[] { FraudMonitor.ProtectionGuard, FraudMonitor.ProtectionFraud });
        FraudKinds.Weight(FraudKinds.ProtectionOff).Should().BeGreaterThan(0, "владелец должен это заметить");
        FraudText.Describe(events[0]).Should().NotBeNullOrWhiteSpace();
        FraudText.Title(FraudKinds.ProtectionOff).Should().NotBe(FraudKinds.ProtectionOff);
    }
}
