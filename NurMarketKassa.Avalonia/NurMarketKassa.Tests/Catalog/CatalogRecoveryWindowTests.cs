using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-06 (Б-9, живой тест Этапа 1): сразу после возврата связи касса скачала весь каталог (155 страниц, 100 с) без строки причины. Теперь в течение окна после
/// возврата полная загрузка не стартует — каталог догоняет лента изменений. Чистые тесты правила.</summary>
public sealed class CatalogRecoveryWindowTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Full_load_is_postponed_right_after_the_connection_came_back()
    {
        CatalogFullLoadMarks.IsRecoveryFeedOnly(Now.AddSeconds(-30), Now).Should().BeTrue();
        CatalogFullLoadMarks.IsRecoveryFeedOnly(Now.AddMinutes(-19), Now).Should().BeTrue();
    }

    [Fact]
    public void Window_ends_and_the_usual_rules_apply_again()
    {
        CatalogFullLoadMarks.IsRecoveryFeedOnly(Now.AddMinutes(-21), Now).Should().BeFalse();
        CatalogFullLoadMarks.IsRecoveryFeedOnly(Now - CatalogFullLoadMarks.RecoveryFeedOnlyWindow, Now).Should().BeFalse();
    }

    [Fact]
    public void No_connection_trouble_means_no_postponement()
    {
        CatalogFullLoadMarks.IsRecoveryFeedOnly(null, Now).Should().BeFalse("связь не пропадала — обычные правила (плановая сверка раз в сутки)");
        CatalogFullLoadMarks.IsRecoveryFeedOnly(DateTime.MinValue, Now).Should().BeFalse();
    }

    [Fact]
    public void Clock_moved_back_does_not_hold_the_full_load_forever()
    {
        CatalogFullLoadMarks.IsRecoveryFeedOnly(Now.AddHours(3), Now).Should().BeFalse("метка в будущем — окно не действует");
    }
}
