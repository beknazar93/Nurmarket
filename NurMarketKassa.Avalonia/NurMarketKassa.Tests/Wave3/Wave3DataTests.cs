using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave3;

/// <summary>2026-11-06: В3 «Данные, надёжность, безопасность» — чистые правила: реестр хранилищ (Л4-05, Л4-03), предел наборов карантина (Л5-17), адрес API только по https (Л4-21),
/// номер телефона целиком (Л5-19), слова «сохранить» (Л5-18), межпроцессный замок (Л5-22). Без сети и без боевых данных.</summary>
public sealed class Wave3DataTests
{
    // ───────── реестр хранилищ (Л4-05, Л4-03) ─────────

    [Theory]
    [InlineData("state.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("state.json.bak", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("pending_unknown_checkout.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("loyalty-imported-4b759359.flag", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("telegram-updates-123.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("offline_pos_state.json.pending", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("fraud-events.jsonl", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("debt-payments", CompanyStoreRegistry.StoreKind.SharedByCompanyField)]
    [InlineData("pending_shift_closes.json.bak", CompanyStoreRegistry.StoreKind.SharedByCompanyField)]
    [InlineData("auth.dat", CompanyStoreRegistry.StoreKind.Terminal)]
    [InlineData("WebView2", CompanyStoreRegistry.StoreKind.Terminal)]
    [InlineData("global-products.sqlite", CompanyStoreRegistry.StoreKind.Terminal)]
    [InlineData("brand-new-store.json", CompanyStoreRegistry.StoreKind.Unknown)]
    [InlineData("", CompanyStoreRegistry.StoreKind.Unknown)]
    public void Registry_classifies_files(string name, CompanyStoreRegistry.StoreKind expected) =>
        CompanyStoreRegistry.Classify(name).Should().Be(expected);

    [Fact]
    public void Debt_payments_folder_is_not_carried_away_with_the_company()
    {
        // Л4-03: общий журнал неподтверждённых оплат долга (с замком) касса целиком в карантин не уносит.
        CompanyStoreRegistry.CompanyItems.Should().NotContain("debt-payments");
        CompanyStoreRegistry.SharedByCompanyItems.Should().Contain("debt-payments");
    }

    [Fact]
    public void Registry_lists_do_not_overlap()
    {
        var company = CompanyStoreRegistry.CompanyItems.Concat(CompanyStoreRegistry.CompanyLocalItems).ToHashSet(StringComparer.OrdinalIgnoreCase);
        company.Intersect(CompanyStoreRegistry.SharedByCompanyItems, StringComparer.OrdinalIgnoreCase).Should().BeEmpty("файл либо компании, либо общий");
        company.Intersect(CompanyStoreRegistry.TerminalItems, StringComparer.OrdinalIgnoreCase).Should().BeEmpty("файл либо компании, либо терминала");
        foreach (var item in CompanyStoreRegistry.CompanyItems.Concat(CompanyStoreRegistry.CompanyLocalItems))
            CompanyStoreRegistry.Classify(item).Should().Be(CompanyStoreRegistry.StoreKind.Company, item);
    }

    // ───────── предел наборов карантина (Л5-17) ─────────

    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0);

    [Fact]
    public void Quarantine_keeps_at_most_two_newest_sets()
    {
        var sets = new[] { ("a", Now.AddDays(-1)), ("b", Now.AddDays(-2)), ("c", Now.AddDays(-3)), ("d", Now.AddDays(-4)) };
        QuarantineRetention.SelectToDelete(sets, Now, keepDays: 14).Should().BeEquivalentTo(new[] { "c", "d" });
    }

    [Fact]
    public void Quarantine_drops_sets_older_than_keep_days_even_if_few()
    {
        var sets = new[] { ("new", Now.AddDays(-2)), ("old", Now.AddDays(-15)) };
        QuarantineRetention.SelectToDelete(sets, Now, keepDays: 14).Should().BeEquivalentTo(new[] { "old" });
        QuarantineRetention.SelectToDelete(new[] { ("only", Now.AddDays(-1)) }, Now, 14).Should().BeEmpty();
        QuarantineRetention.SelectToDelete(Array.Empty<(string, DateTime)>(), Now, 14).Should().BeEmpty();
    }

    // ───────── адрес API только по https (Л4-21) ─────────

    [Theory]
    [InlineData("http://app.nurcrm.kg", "https://app.nurcrm.kg")]
    [InlineData("http://app.nurcrm.kg/", "https://app.nurcrm.kg")]
    [InlineData("HTTP://app.nurcrm.kg:8080", "https://app.nurcrm.kg:8080")]
    [InlineData("http://172.32.0.1", "https://172.32.0.1")]
    [InlineData("https://app.nurcrm.kg", "https://app.nurcrm.kg")]
    [InlineData("http://127.0.0.1:9", "http://127.0.0.1:9")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://192.168.1.5:8000", "http://192.168.1.5:8000")]
    [InlineData("http://10.0.0.7", "http://10.0.0.7")]
    [InlineData("http://172.16.4.4", "http://172.16.4.4")]
    [InlineData("", "")]
    [InlineData("app.nurcrm.kg", "app.nurcrm.kg")]
    public void Api_url_is_https_unless_local(string input, string expected) =>
        AppSettings.NormalizeApiUrl(input).Should().Be(expected);

    // ───────── номер телефона целиком (Л5-19) ─────────

    [Theory]
    [InlineData("0555 123 456", "996555123456")]
    [InlineData("555123456", "996555123456")]
    [InlineData("+996 (555) 123-456", "996555123456")]
    [InlineData("00996555123456", "996555123456")]
    [InlineData("+998 55 512 34 56", "998555123456")]
    [InlineData("+7 900 123-45-67", "79001234567")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Phone_key_keeps_the_country_code(string? phone, string expected) =>
        TelegramReceiptLinks.PhoneKey(phone).Should().Be(expected);

    [Fact]
    public void Legacy_nine_digit_link_matches_only_kyrgyz_numbers()
    {
        TelegramReceiptLinks.LegacyLinkMatches("555123456", "996555123456").Should().BeTrue();
        TelegramReceiptLinks.LegacyLinkMatches("555123456", "998555123456").Should().BeFalse("+998 с тем же хвостом — другой человек");
        TelegramReceiptLinks.LegacyLinkMatches("996555123456", "996555123456").Should().BeFalse("новая запись сравнивается целиком, а не как старая");
    }

    // ───────── слова «сохранить» (Л5-18) ─────────

    [Theory]
    [InlineData("Сохранить", true)]
    [InlineData("Сакта", true)]
    [InlineData("Apply", true)]
    [InlineData("Колдонуу", true)]
    [InlineData("Провести", true)]
    [InlineData("Колдонуучу кошуу", false)]
    [InlineData("Колдонуучулар", false)]
    [InlineData("Отправить в WhatsApp", false)]
    [InlineData("Создать резервную копию", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Save_words_do_not_match_user_buttons(string? text, bool expected) =>
        UnsavedInputRules.IsSaveText(text).Should().Be(expected);

    // ───────── межпроцессный замок (Л5-22) ─────────

    [Fact]
    public void Cross_process_lock_is_exclusive_until_released()
    {
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "wave3-lock-" + Guid.NewGuid().ToString("N")[..8] + ".lock");
        try
        {
            using var first = CrossProcessLock.TryAcquire(path, TimeSpan.Zero);
            first.Should().NotBeNull();
            CrossProcessLock.TryAcquire(path, TimeSpan.FromMilliseconds(120)).Should().BeNull("замок держит «другая программа»");
            first!.Dispose();
            using var again = CrossProcessLock.TryAcquire(path, TimeSpan.Zero);
            again.Should().NotBeNull("после освобождения замок снова свободен");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* временный файл */ }
        }
    }

    [Fact]
    public async Task Cross_process_lock_async_waits_for_release()
    {
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "wave3-lock-" + Guid.NewGuid().ToString("N")[..8] + ".lock");
        try
        {
            var first = CrossProcessLock.TryAcquire(path, TimeSpan.Zero)!;
            var waiting = CrossProcessLock.TryAcquireAsync(path, TimeSpan.FromSeconds(3));
            await Task.Delay(150);
            first.Dispose();
            using var second = await waiting;
            second.Should().NotBeNull();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* временный файл */ }
        }
    }
}
