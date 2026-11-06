using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave6;

/// <summary>2026-11-06: В6 Д4-44 — у новых настроек экрана покупателя нет русской заглушки «Специальное предложение» (она висела на втором мониторе у любой кассы
/// без настроенной акции). Реклама по умолчанию выключена, название и описание пустые. Без сети и без боевых данных.</summary>
public sealed class CustomerDisplayAdvertisementDefaultTests
{
    [Fact]
    public void New_settings_have_no_stub_advertisement_title()
    {
        var settings = new CustomerDisplaySettings();

        settings.AdvertisementEnabled.Should().BeFalse();
        settings.AdvertisementTitle.Should().BeEmpty();
        settings.AdvertisementDescription.Should().BeEmpty();
        settings.AdvertisementImagePath.Should().BeEmpty();
    }
}
