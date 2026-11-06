using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave13;

/// <summary>2026-11-06 (Б-8): «Сессия истекла» открывалось поверх фонового анонса («Просрочка при продаже») — две модалки сразу. Общий механизм: фоновый анонс ждёт важное окно (не теряется),
/// а важное окно ждёт открытый фоновый анонс. Тесты чистые (окон нет): состояние задаётся делегатами.</summary>
[Collection("ModalAnnouncements")]
public sealed class ModalAnnouncementsTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Announcement_waits_while_a_more_important_window_is_open_and_is_not_lost()
    {
        var blocked = true;
        var shown = false;
        var task = ModalAnnouncements.ShowWhenFreeAsync(() => { shown = true; return Task.FromResult(42); }, "тест", () => blocked, Fast);

        await Task.Delay(120);
        shown.Should().BeFalse("пока важное окно открыто, анонс отложен");
        task.IsCompleted.Should().BeFalse();

        blocked = false;
        (await task).Should().Be(42);
        shown.Should().BeTrue("после закрытия важного окна анонс показан");
    }

    [Fact]
    public async Task Open_background_announcement_is_visible_to_important_windows_and_released_afterwards()
    {
        var gate = new TaskCompletionSource();
        bool? duringShow = null;
        var task = ModalAnnouncements.ShowWhenFreeAsync(async () =>
        {
            duringShow = ModalAnnouncements.BackgroundOpen;
            await gate.Task;
            return true;
        }, "тест", () => false, Fast);

        await Task.Delay(60);
        duringShow.Should().BeTrue();
        ModalAnnouncements.BackgroundOpen.Should().BeTrue("важные окна («Сессия истекла») видят открытый анонс и откладываются");

        gate.SetResult();
        await task;
        ModalAnnouncements.BackgroundOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Announcement_is_shown_after_the_maximum_wait_even_if_still_blocked()
    {
        var shown = await ModalAnnouncements.ShowWhenFreeAsync(() => Task.FromResult(true), "тест", () => true, Fast, TimeSpan.FromMilliseconds(80));
        shown.Should().BeTrue("не висим вечно");
        ModalAnnouncements.BackgroundOpen.Should().BeFalse();
    }

    [Fact]
    public void Important_window_scope_is_counted_and_released_once()
    {
        var scope = ModalAnnouncements.BeginImportant();
        ModalAnnouncements.ImportantShowing.Should().BeTrue();
        scope.Dispose();
        scope.Dispose(); // повторный Dispose не уводит счётчик в минус
        ModalAnnouncements.ImportantShowing.Should().BeFalse();
    }

    [Fact]
    public async Task Exception_in_the_announcement_still_releases_the_flag()
    {
        var act = () => ModalAnnouncements.ShowWhenFreeAsync<bool>(() => throw new InvalidOperationException("окно не открылось"), "тест", () => false, Fast);
        await act.Should().ThrowAsync<InvalidOperationException>();
        ModalAnnouncements.BackgroundOpen.Should().BeFalse();
    }
}
