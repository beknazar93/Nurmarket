using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (список владельца, п. 7: «Старая загрузка» плодила окна при каждом нажатии): правило «одно окно — один экземпляр».
/// Повторное открытие не создаёт второго окна, а двойное нажатие на модальный диалог не создаёт второй диалог (слои затемнения не накапливаются).</summary>
public sealed class WindowSingletonUiTests
{
    [AvaloniaFact]
    public async Task BulkImportWindow_OpenTwice_KeepsSingleWindow()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var before = WindowSingleton.Count;
        BulkImportWindow.Open(null);
        WindowSingleton.Count.Should().Be(before + 1);
        BulkImportWindow.Open(null);
        BulkImportWindow.Open(null);
        WindowSingleton.Count.Should().Be(before + 1, "повторное «Старая загрузка» выводит открытое окно вперёд, а не открывает новое");
        WindowSingleton.TryActivate("BulkImportWindow").Should().BeTrue();
        // Закрытое окно из учёта убирается — следующее открытие создаёт новое.
        WindowSingleton.Find("BulkImportWindow")!.Close();
        UiShot.Pump();
        WindowSingleton.Count.Should().Be(before, "закрытое окно снято с учёта");
    }

    [AvaloniaFact]
    public async Task ModalDialog_DoubleOpen_IsDetectedAsDuplicate()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var first = new BulkImportWindow();
        PosDialogHost.IsDuplicateOpen(first).Should().BeFalse("первое окно открывается");
        first.Show();
        var second = new BulkImportWindow();
        PosDialogHost.IsDuplicateOpen(second).Should().BeTrue("второй такой же диалог в течение 0,7 с — двойное нажатие");
        first.Close();
        UiShot.Pump();
        var third = new BulkImportWindow();
        PosDialogHost.IsDuplicateOpen(third).Should().BeFalse("первое окно закрыто — новое открывать можно");
    }

    /// <summary>2026-11-19: диалог того же типа у ДРУГОГО владельца (другое окно, следующий сценарий подряд) — не двойное нажатие; у того же владельца — двойное.</summary>
    [AvaloniaFact]
    public async Task ModalDialog_SameTypeOtherOwner_IsNotDuplicate_SameOwnerIs()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        var ownerA = new Window();
        var ownerB = new Window();
        var first = new BulkImportWindow();
        PosDialogHost.IsDuplicateOpen(first, ownerA).Should().BeFalse();
        first.Show();
        PosDialogHost.IsDuplicateOpen(new BulkImportWindow(), ownerB).Should().BeFalse("другой владелец — легитимный повторный диалог, а не «отмена»");
        PosDialogHost.ResetDuplicateGuardForTests();
        PosDialogHost.IsDuplicateOpen(first, ownerA).Should().BeFalse();
        PosDialogHost.IsDuplicateOpen(new BulkImportWindow(), ownerA).Should().BeTrue("тот же владелец за 0,7 с — двойное нажатие");
        first.Close();
        UiShot.Pump();
    }
}
