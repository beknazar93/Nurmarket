using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (замечания владельца): настройки ИИ — только на вкладке «ИИ», а на «Операциях» одна подсказка с переходом; «Перенос данных из другой
/// программы» (1С, МойСклад, Poster…) — заметная карточка на «Операциях», видна владельцу и скрыта без права «Склад: редактирование»; тексты не обрезаются
/// на 1366×768 и 1024×600 (ru/ky/uz).</summary>
[Collection("UiScenarios")]
public sealed class TransferAndAiTabUiTests : IDisposable
{
    // 2026-11-21: «Перенос данных» — только в программе владельца (ImportAccess); карточка строится лишь при AppMode.IsOwner, поэтому тесты карточки идут в режиме владельца.
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _savedIsOwner = AppMode.IsOwner;

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _savedIsOwner);
        BulkImportWizardWindow.CanOpenTransferOverride = null;
    }

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task OperationsHasNoAi_AiTabHasAll_TransferCardVisibleForOwner_NoClip(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        BulkImportWizardWindow.CanOpenTransferOverride = null;
        IsOwnerProp.SetValue(null, true); // программа владельца: карточка «Перенос данных» есть
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        window.Width = w;
        window.Height = h;
        try
        {
            window.Show();
            UiShot.Settle(6);

            // «Операции»: ИИ-блоков нет, есть подсказка и карточка переноса данных
            window.SelectNavTab("operations").Should().BeTrue();
            UiShot.Settle(6);
            window.GetVisualDescendants().OfType<Border>().Should().NotContain(b => b.Name == "AiKeysCard", "ключи ИИ уехали на вкладку «ИИ»");
            window.GetVisualDescendants().OfType<AdvisorSettingsCard>().Should().BeEmpty("тумблеры Нур Советника — на вкладке «ИИ»");
            var hint = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AiMovedHintCard");
            var transfer = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TransferDataCard");
            transfer.IsVisible.Should().BeTrue("владелец видит «Перенос данных из другой программы»");
            transfer.GetVisualDescendants().OfType<Button>().Should().ContainSingle(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "TransferDataButton", "кнопка загрузки из файла");
            if (lang == AppLanguage.Russian)
            {
                var text = UiDrive.AllText(window);
                text.Should().Contain("Настройки ИИ — на вкладке «ИИ»").And.Contain("Перенос данных из другой программы");
                text.Should().NotContain("Нур Советник включён").And.NotContain("Запасные модели ИИ").And.NotContain("Проверить ИИ");
            }
            var ownTexts = hint.GetVisualDescendants().Concat(transfer.GetVisualDescendants()).OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            ownTexts.Should().NotBeEmpty();
            TextClipChecker.Find(window, "settings-operations", lang.ToString()).Where(v => ownTexts.Any(t => v.Key.Contains(t!)))
                .Should().BeEmpty($"карточки «ИИ перенесён» и «Перенос данных» не обрезаются ({w}x{h} {lang})");

            // «ИИ»: ключи и Нур Советник на месте
            window.SelectNavTab("ai").Should().BeTrue();
            UiShot.Settle(6);
            window.GetVisualDescendants().OfType<Border>().Should().Contain(b => b.Name == "AiKeysCard", "ключи Gemini/Groq/OpenRouter на вкладке «ИИ»");
            window.GetVisualDescendants().OfType<AdvisorSettingsCard>().Should().ContainSingle("карточка Нур Советника на вкладке «ИИ»");
            window.GetVisualDescendants().OfType<Border>().Should().NotContain(b => b.Name == "AiMovedHintCard");
            TextClipChecker.Find(window, "settings-ai", lang.ToString()).Where(v => v.Key.Contains("Нур") || v.Key.Contains("Ключ") || v.Key.Contains("ИИ"))
                .Should().BeEmpty($"вкладка «ИИ» не обрезается ({w}x{h} {lang})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            IsOwnerProp.SetValue(null, _savedIsOwner);
        }
    }

    [AvaloniaFact]
    public async Task TransferCard_HiddenWithoutRight_AndHintButtonSwitchesToAiTab()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        BulkImportWizardWindow.CanOpenTransferOverride = () => false;
        IsOwnerProp.SetValue(null, true); // карточка строится только в программе владельца; без права «Склад: редактирование» она скрыта
        try
        {
            var ops = new OperationsSettingsView();
            var host = new Window { Content = ops, Width = 1100, Height = 700 };
            try
            {
                host.Show();
                UiShot.Settle(4);
                ops.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TransferDataCard").IsVisible.Should().BeFalse("кассир без права «Склад: редактирование» «Перенос данных» не видит");
                BulkImportWizardWindow.CanOpenTransfer().Should().BeFalse();

                var opened = 0;
                ops.OpenAiTab = () => opened++;
                var hintButton = ops.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "OpenAiTabButton");
                hintButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); // кнопка может быть ниже видимой части прокрутки — нажатие событием
                UiShot.Settle(2);
                opened.Should().Be(1, "кнопка подсказки переключает на вкладку «ИИ»");
            }
            finally
            {
                host.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            BulkImportWizardWindow.CanOpenTransferOverride = null;
            IsOwnerProp.SetValue(null, _savedIsOwner);
        }
    }
}
