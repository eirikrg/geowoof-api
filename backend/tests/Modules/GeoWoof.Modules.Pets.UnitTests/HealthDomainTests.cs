using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.UnitTests;

public sealed class HealthDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Luna = Guid.CreateVersion7();

    private static HealthEvent Vaccine(HealthRepeat? repeat, DateOnly? dueOn = null) => HealthEvent.Create(
        Guid.CreateVersion7(),
        Luna,
        new HealthEventDetails(HealthEventType.Vaccine, "  Rabia ", dueOn ?? new DateOnly(2026, 3, 1), null, null, repeat, HealthReminder.DayBefore, " Lote 1234 "),
        Now);

    [Fact]
    public void It_keeps_what_was_written_without_the_spaces_around()
    {
        var vaccine = Vaccine(repeat: null);

        vaccine.Title.ShouldBe("Rabia");
        vaccine.Notes.ShouldBe("Lote 1234");
        vaccine.CreatedAt.ShouldBe(Now);
        vaccine.Repeat.ShouldBeNull();
    }

    [Fact]
    public void Something_that_repeats_comes_back_counting_from_the_day_it_was_done()
    {
        var vaccine = Vaccine(new HealthRepeat(1, RepeatUnit.Years, null));
        var nextId = Guid.CreateVersion7();

        var next = vaccine.Complete(new DateOnly(2026, 3, 10), nextId, Now);

        vaccine.DoneOn.ShouldBe(new DateOnly(2026, 3, 10));
        next.ShouldNotBeNull();
        next.Id.ShouldBe(nextId);
        next.PetId.ShouldBe(Luna);
        next.DueOn.ShouldBe(new DateOnly(2027, 3, 10));
        next.DoneOn.ShouldBeNull();
        next.Title.ShouldBe("Rabia");
        next.Reminder.ShouldBe(HealthReminder.DayBefore);
        next.Repeat.ShouldBe(vaccine.Repeat);
        next.Notes.ShouldBeNull();
    }

    [Theory]
    [InlineData(RepeatUnit.Days, 3, "2026-03-13")]
    [InlineData(RepeatUnit.Weeks, 2, "2026-03-24")]
    [InlineData(RepeatUnit.Months, 3, "2026-06-10")]
    [InlineData(RepeatUnit.Years, 1, "2027-03-10")]
    public void Each_unit_counts_as_the_calendar_does(RepeatUnit unit, int every, string expected)
    {
        new HealthRepeat(every, unit, null).After(new DateOnly(2026, 3, 10)).ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_month_after_the_last_day_of_a_month_is_the_last_day_of_the_next_one()
    {
        new HealthRepeat(1, RepeatUnit.Months, null).After(new DateOnly(2026, 1, 31)).ShouldBe(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void A_repetition_that_has_ended_makes_no_next_one()
    {
        var course = Vaccine(new HealthRepeat(1, RepeatUnit.Days, new DateOnly(2026, 3, 10)));

        course.Complete(new DateOnly(2026, 3, 10), Guid.CreateVersion7(), Now).ShouldBeNull();
        course.DoneOn.ShouldBe(new DateOnly(2026, 3, 10));
    }

    [Fact]
    public void What_was_done_already_is_not_done_again()
    {
        var vaccine = Vaccine(new HealthRepeat(1, RepeatUnit.Years, null));
        vaccine.Complete(new DateOnly(2026, 3, 10), Guid.CreateVersion7(), Now);

        vaccine.Complete(new DateOnly(2026, 4, 1), Guid.CreateVersion7(), Now).ShouldBeNull();
        vaccine.DoneOn.ShouldBe(new DateOnly(2026, 3, 10));
    }

    [Fact]
    public void What_does_not_repeat_is_only_done()
    {
        var visit = Vaccine(repeat: null);

        visit.Complete(new DateOnly(2026, 3, 1), Guid.CreateVersion7(), Now).ShouldBeNull();
        visit.DoneOn.ShouldBe(new DateOnly(2026, 3, 1));
    }
}
