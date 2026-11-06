using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06 (Б-11): у офлайн-приходов на сервере comment оказывался пустым — первая попытка сразу после приёма (запрос не прошёл) закрепляла ПУСТУЮ пометку «Принято…»
/// навсегда, и приход, дошедший через минуты, уходил без времени приёма. Чистые тесты состояния прихода: ни сеть, ни база не нужны.</summary>
public sealed class ReceiptDelayNoteTests
{
    private static readonly DateTime Received = new(2026, 11, 6, 13, 35, 0, DateTimeKind.Local);

    [Fact]
    public void Empty_note_from_a_quick_first_attempt_is_not_frozen()
    {
        var st = new ReceivingPostState { ReceivedAt = Received };

        st.GetOrMakeDelayNote(Received.AddSeconds(20)).Should().BeNull("сразу после приёма пометка не нужна");
        st.DelayNote.Should().BeNull("пустая пометка не запоминается");

        var later = st.GetOrMakeDelayNote(Received.AddMinutes(7));
        later.Should().Be("Принято 06.11.2026 13:35, отправлено после восстановления связи 13:42");
    }

    [Fact]
    public void Non_empty_note_is_computed_once_and_stays_identical_on_retry()
    {
        var st = new ReceivingPostState { ReceivedAt = Received };

        var first = st.GetOrMakeDelayNote(Received.AddMinutes(7));
        var second = st.GetOrMakeDelayNote(Received.AddMinutes(40));

        first.Should().NotBeNull();
        second.Should().Be(first, "тот же ключ повтора — то же тело запроса");
    }

    [Fact]
    public void No_receiving_time_means_no_note()
    {
        new ReceivingPostState().GetOrMakeDelayNote(DateTime.Now).Should().BeNull();
    }
}
