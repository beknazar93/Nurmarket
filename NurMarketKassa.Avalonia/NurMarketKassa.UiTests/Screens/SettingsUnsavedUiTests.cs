using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (Д-5, живой показ 1.17.85): «Есть несохранённые изменения» появлялось в кассе после простого переключения вкладок
/// «Настройки → Весы» и после переключателей, которые применяются сразу (размытие, режим отрисовки, правила штрихкода). Проверяем настоящее окно
/// в Avalonia.Headless: переключили вкладки и «сразу применяемые» переключатели — вопроса нет, окно закрывается; изменили поле, которое сохраняет
/// кнопка «Сохранить», — вопрос будет.</summary>
[Collection("UiScenarios")]
public sealed class SettingsUnsavedUiTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    public SettingsUnsavedUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    /// <summary>Как первое касание окна: исходное состояние полей запоминается (PosSettingsWindow делает это на первом нажатии мыши/клавиши).</summary>
    private static void TakeBaseline(PosSettingsWindow window)
    {
        var snapshot = typeof(PosSettingsWindow).GetMethod("SnapshotEditable", Flags)!.Invoke(window, null);
        typeof(PosSettingsWindow).GetField("_dirtyBaseline", Flags)!.SetValue(window, snapshot);
    }

    private static bool Pending(PosSettingsWindow window) =>
        (bool)typeof(PosSettingsWindow).GetProperty("HasPendingEditsForTests", Flags)!.GetValue(window)!;

    [AvaloniaFact]
    public async Task Switching_tabs_and_immediate_toggles_is_not_unsaved_but_a_real_edit_is()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
        var savedOwnerSections = AppMode.OwnerSectionsInKassa;
        AppMode.OwnerSectionsInKassa = true; // автономный режим: в кассе видны вкладки «Весы на кассе», «Штрих-код», «Табло»
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        bool? blurBefore = null;
        CheckBox? blur = null;
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            var scales = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            TakeBaseline(window);

            // переключение вкладок «Весов» — не правка
            scales.ShowSection(2);
            scales.ShowSection(3);
            scales.ShowSection(0);
            UiShot.Settle(2, 20);
            Pending(window).Should().BeFalse("вкладки — переход по странице, а не несохранённое значение");

            // переключатель «Размытие фона» применяется и сохраняется сразу — тоже не правка
            var screen = (ScreenSettingsView)typeof(PosSettingsWindow).GetField("_screenView", Flags)!.GetValue(window)!; // страница «Экран» (не на экране — не в дереве)
            blur = screen.GetLogicalDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == "GfxBlurCheck");
            if (blur is { IsEnabled: true })
            {
                blurBefore = blur.IsChecked;
                blur.IsChecked = blur.IsChecked != true;
                UiShot.Settle(1, 20);
                Pending(window).Should().BeFalse("размытие применяется сразу, без «Сохранить»");
            }

            // закрытие без правок — без вопроса
            window.Close();
            UiShot.Pump();
            window.IsVisible.Should().BeFalse("окно закрылось сразу, вопроса «Есть несохранённые изменения» нет");
        }
        finally
        {
            if (blur is not null && blurBefore is not null)
                UserPreferences.SetModalBackgroundBlur(blurBefore == true);
            if (window.IsVisible)
                window.Close();
            UiShot.Pump();
            AppMode.OwnerSectionsInKassa = savedOwnerSections;
        }

        // настоящая правка поля, которое сохраняет «Сохранить», — вопрос будет
        var second = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            second.Show();
            UiShot.Settle(3, 30);
            TakeBaseline(second);
            var scales = second.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            var weighing = scales.FindControl<Control>("WeighingSection")!;
            var field = weighing.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => !t.IsReadOnly);
            field.Should().NotBeNull("в разделе «Весы на кассе» есть поле, которое сохраняет кнопка «Сохранить»");
            field!.Text = (field.Text ?? "") + "7";
            UiShot.Settle(1, 20);
            Pending(second).Should().BeTrue("изменённое и не сохранённое значение — повод спросить при закрытии");
        }
        finally
        {
            typeof(PosSettingsWindow).GetField("_closeConfirmed", Flags)!.SetValue(second, true); // закрыть без вопроса в тесте
            second.Close();
            UiShot.Pump();
        }
    }
}
