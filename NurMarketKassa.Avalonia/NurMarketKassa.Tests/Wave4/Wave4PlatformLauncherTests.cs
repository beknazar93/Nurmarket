// 2026-11-06: В4 — тесты файлов приложения (Av/Services). Они компилируются только после того, как В1 добавит в NurMarketKassa.Tests.csproj ссылки на
// PlatformLauncher.cs и PdfSupport.cs и определит WAVE4_LINKS (см. docs/Отчеты/Волна 1 — В4 заметки для В1 (06.11.2026).md). Пока константы нет — файл пуст.
#if WAVE4_LINKS
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.Tests.Wave4;

public sealed class Wave4PlatformLauncherTests
{
    [Theory]
    [InlineData("https://wa.me/996555123456?text=%D0%A7%D0%B5%D0%BA")]
    [InlineData("tg://msg?text=abc")]
    [InlineData("tel:+996555123456")]
    [InlineData("mailto:support@nurcrm.kg")]
    [InlineData("  https://www.nurcrm.kg  ")]
    public void IsLaunchableUrl_AcceptsMessengerAndWebLinks(string url) => PlatformLauncher.IsLaunchableUrl(url).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\Users\Public\x.pdf")]
    [InlineData("file:///etc/passwd")]
    [InlineData("relative/path.html")]
    [InlineData("https://nurcrm.kg\r\nHost: evil")]
    public void IsLaunchableUrl_RejectsFilesAndBrokenText(string? url) => PlatformLauncher.IsLaunchableUrl(url).Should().BeFalse();

    [Fact]
    public void OpenUrl_BadAddress_ReturnsFalseWithoutException() => PlatformLauncher.OpenUrl("not a url").Should().BeFalse();

    [Fact]
    public void OpenFile_EmptyPath_ReturnsFalseWithoutException() => PlatformLauncher.OpenFile("").Should().BeFalse();

    [Fact]
    public void PdfSupport_IsAvailableOnDesktop() => PdfSupport.IsAvailable.Should().BeTrue();
}
#endif
