using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (жалоба владельца «чек в модалке открывается дважды: подряд 2-е нажатие»): быстрое повторное нажатие (мышь, тач, Enter×2)
/// на строку/кнопку, открывающую модальное окно, не создаёт второго окна; после закрытия окно открывается снова; исключение при открытии
/// не оставляет «залипший» замок.</summary>
public sealed class DialogDoubleOpenUiTests
{
    private sealed class ReceiptLikeDialog : Window, IDialogOpenKey
    {
        public static readonly List<ReceiptLikeDialog> All = new();
        private readonly string _key;
        public ReceiptLikeDialog(string key)
        {
            _key = key;
            Width = 300;
            Height = 200;
            Content = new TextBlock { Text = "Чек " + key };
            All.Add(this);
        }

        string? IDialogOpenKey.DialogOpenKey => _key;
    }

    private static int VisibleCount() => ReceiptLikeDialog.All.Count(d => d.IsVisible);

    private static Window NewOwner()
    {
        var owner = new Window { Width = 800, Height = 600, Content = new TextBlock { Text = "Список чеков" } };
        owner.Show();
        UiShot.Pump();
        return owner;
    }

    /// <summary>То, что делает обработчик нажатия на чек: замок по ключу + показ окна.</summary>
    private static Task OpenReceipt(Window owner, string id) =>
        PosDialogHost.RunExclusiveAsync("receipt:" + id, () => PosDialogHost.ShowAsync(new ReceiptLikeDialog(id), owner));

    private static void Cleanup(Window owner)
    {
        foreach (var d in Enumerable.Reverse(ReceiptLikeDialog.All.Where(d => d.IsVisible).ToList()))
            d.Close();
        UiShot.Pump();
        owner.Close();
        UiShot.Pump();
        ReceiptLikeDialog.All.Clear();
        PosDialogHost.ResetDuplicateGuardForTests();
    }

    [AvaloniaFact]
    public async Task DoubleClick_OnReceipt_OpensExactlyOneWindow_AndReopensAfterClose()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        ReceiptLikeDialog.All.Clear();
        var owner = NewOwner();
        try
        {
            var first = OpenReceipt(owner, "A-1");
            var second = OpenReceipt(owner, "A-1"); // быстрое второе нажатие, пока первое открывается
            var third = OpenReceipt(owner, "A-1");  // и третье (Enter×2 + мышь)
            UiShot.Pump();
            UiShot.Pump();

            VisibleCount().Should().Be(1, "повторное нажатие на тот же чек не открывает второе окно");
            ReceiptLikeDialog.All.Count.Should().Be(1, "второй экземпляр окна даже не создаётся");
            second.IsCompleted.Should().BeTrue("повтор завершается сразу");
            third.IsCompleted.Should().BeTrue();

            ReceiptLikeDialog.All[0].Close();
            UiShot.Pump();
            await first;

            // После закрытия первого чек открывается снова.
            var again = OpenReceipt(owner, "A-1");
            UiShot.Pump();
            VisibleCount().Should().Be(1, "после закрытия окно открывается снова");
            ReceiptLikeDialog.All.Count.Should().Be(2);
            ReceiptLikeDialog.All[1].Close();
            UiShot.Pump();
            await again;
        }
        finally
        {
            Cleanup(owner);
        }
    }

    [AvaloniaFact]
    public async Task DifferentReceipts_AreNotBlockedByEachOther()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        ReceiptLikeDialog.All.Clear();
        var owner = NewOwner();
        try
        {
            var a = OpenReceipt(owner, "B-1");
            UiShot.Pump();
            var b = OpenReceipt(owner, "B-2"); // другой чек — вложенное окно допустимо
            UiShot.Pump();
            ReceiptLikeDialog.All.Count.Should().Be(2, "другой чек (другой ключ) открывается");
            VisibleCount().Should().Be(2);
            foreach (var d in Enumerable.Reverse(ReceiptLikeDialog.All.ToList()))
                d.Close();
            UiShot.Pump();
            await Task.WhenAll(a, b);
        }
        finally
        {
            Cleanup(owner);
        }
    }

    [AvaloniaFact]
    public async Task HostGuard_SecondShowOfSameWindowKey_IsIgnored_EvenWhenOwnerIsTheFirstModal()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        ReceiptLikeDialog.All.Clear();
        var owner = NewOwner();
        try
        {
            // Без замка по ключу: защита самого PosDialogHost (так открываются сотни окон через ShowAsync/ShowModalAsync).
            var first = PosDialogHost.ShowAsync(new ReceiptLikeDialog("C-1"), owner);
            UiShot.Pump();
            var second = PosDialogHost.ShowAsync(new ReceiptLikeDialog("C-1"), owner);
            UiShot.Pump();
            VisibleCount().Should().Be(1, "второе окно с тем же ключом не показывается");
            second.IsCompleted.Should().BeTrue();
            ReceiptLikeDialog.All[0].Close();
            UiShot.Pump();
            await first;
        }
        finally
        {
            Cleanup(owner);
        }
    }

    [AvaloniaFact]
    public async Task ShowDialogOnce_DirectShowDialogReplacement_IgnoresRepeat()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        ReceiptLikeDialog.All.Clear();
        var owner = NewOwner();
        try
        {
            var first = PosDialogHost.ShowDialogOnce<bool>(new ReceiptLikeDialog("E-1"), owner);
            UiShot.Pump();
            var second = PosDialogHost.ShowDialogOnce<bool>(new ReceiptLikeDialog("E-1"), owner);
            UiShot.Pump();
            VisibleCount().Should().Be(1, "повтор прямого ShowDialog через ShowDialogOnce не открывает второе окно");
            second.IsCompleted.Should().BeTrue();
            ReceiptLikeDialog.All[0].Close();
            UiShot.Pump();
            await first;
        }
        finally
        {
            Cleanup(owner);
        }
    }

    [AvaloniaFact]
    public async Task ExceptionOnOpen_DoesNotLeaveStuckLock()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PosDialogHost.ResetDuplicateGuardForTests();
        ReceiptLikeDialog.All.Clear();
        var owner = NewOwner();
        try
        {
            var failing = async () => await PosDialogHost.RunExclusiveAsync("receipt:D-1", () => throw new InvalidOperationException("сервер не ответил"));
            await failing.Should().ThrowAsync<InvalidOperationException>();

            // Замок снят: следующее нажатие открывает окно.
            var ok = OpenReceipt(owner, "D-1");
            UiShot.Pump();
            VisibleCount().Should().Be(1, "после исключения замок не залип");
            ReceiptLikeDialog.All[0].Close();
            UiShot.Pump();
            await ok;

            // Окно закрыто крестиком/Esc/потерей окна (Close) — повторное открытие снова возможно.
            var again = OpenReceipt(owner, "D-1");
            UiShot.Pump();
            VisibleCount().Should().Be(1);
            ReceiptLikeDialog.All[^1].Close();
            UiShot.Pump();
            await again;
            PosDialogHost.TryBeginExclusive("receipt:D-1").Should().BeTrue("замок снят после закрытия");
            PosDialogHost.TryBeginExclusive("receipt:D-1").Should().BeFalse("пока замок держится — повтор не проходит");
            PosDialogHost.EndExclusive("receipt:D-1");
        }
        finally
        {
            Cleanup(owner);
        }
    }
}
