using GeoWoof.BuildingBlocks.Domain.Entities;

namespace GeoWoof.Modules.Pets.Domain;

public enum HealthEventType
{
    Vaccine,
    Deworming,
    VetVisit,
    Medication,
    Other,
}

public enum RepeatUnit
{
    Days,
    Weeks,
    Months,
    Years,
}

/// <summary>When the phone of the owner reminds of it. The phone schedules it; the server only keeps the choice.</summary>
public enum HealthReminder
{
    None,
    OnTheDay,
    DayBefore,
    WeekBefore,
}

/// <summary>How often something comes back, and until when, if it ends.</summary>
public sealed record HealthRepeat(int Every, RepeatUnit Unit, DateOnly? Until)
{
    public const int MaxEvery = 365;

    /// <summary>The next time after <paramref name="date"/>. A month after the 31st is the last day of the next month.</summary>
    public DateOnly After(DateOnly date) => Unit switch
    {
        RepeatUnit.Days => date.AddDays(Every),
        RepeatUnit.Weeks => date.AddDays(Every * 7),
        RepeatUnit.Months => date.AddMonths(Every),
        RepeatUnit.Years => date.AddYears(Every),
        _ => throw new InvalidOperationException($"Unknown unit {Unit}."),
    };
}

/// <summary>What can be changed of a health event: all of it at once, as the API replaces it.</summary>
public sealed record HealthEventDetails(
    HealthEventType Type,
    string Title,
    DateOnly DueOn,
    TimeOnly? DueAt,
    DateOnly? DoneOn,
    HealthRepeat? Repeat,
    HealthReminder Reminder,
    string? Notes);

/// <summary>
/// Something of the health of a pet that is due on a day, such as a vaccine or an appointment, and that is done or
/// still pending. Only its owner sees it.
/// </summary>
public sealed class HealthEvent : Entity
{
    public const int TitleMaxLength = 100;
    public const int NotesMaxLength = 1000;
    public const int MaxPerPet = 500;
    public const int MaxYearsAhead = 10;

    /// <summary>Constructor for EF Core.</summary>
    private HealthEvent()
    {
    }

    private HealthEvent(Guid id, Guid petId, DateTimeOffset now)
        : base(id)
    {
        PetId = petId;
        CreatedAt = now;
    }

    public Guid PetId { get; private set; }

    public HealthEventType Type { get; private set; }

    public string Title { get; private set; } = null!;

    /// <summary>The day it is due, local to the owner.</summary>
    public DateOnly DueOn { get; private set; }

    /// <summary>The local time, for what has one, such as an appointment.</summary>
    public TimeOnly? DueAt { get; private set; }

    /// <summary>The day it was done. Pending while it is null.</summary>
    public DateOnly? DoneOn { get; private set; }

    public int? RepeatEvery { get; private set; }

    public RepeatUnit? RepeatUnit { get; private set; }

    public DateOnly? RepeatUntil { get; private set; }

    public HealthReminder Reminder { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public HealthRepeat? Repeat => RepeatEvery is { } every && RepeatUnit is { } unit ? new HealthRepeat(every, unit, RepeatUntil) : null;

    public static HealthEvent Create(Guid id, Guid petId, HealthEventDetails details, DateTimeOffset now)
    {
        var healthEvent = new HealthEvent(id, petId, now);
        healthEvent.Replace(details, now);
        return healthEvent;
    }

    public void Replace(HealthEventDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        Type = details.Type;
        Title = details.Title.Trim();
        DueOn = details.DueOn;
        DueAt = details.DueAt;
        DoneOn = details.DoneOn;
        RepeatEvery = details.Repeat?.Every;
        RepeatUnit = details.Repeat?.Unit;
        RepeatUntil = details.Repeat?.Until;
        Reminder = details.Reminder;
        Notes = string.IsNullOrWhiteSpace(details.Notes) ? null : details.Notes.Trim();
        UpdatedAt = now;
    }

    /// <summary>
    /// Marks it as done on <paramref name="doneOn"/>. If it repeats, the next one is due that interval after the day it
    /// was done, as a vaccine counts from when it was given, unless that passes the end of the repetition. Something
    /// that was done already does not make another one.
    /// </summary>
    public HealthEvent? Complete(DateOnly doneOn, Guid nextId, DateTimeOffset now)
    {
        if (DoneOn is not null)
        {
            return null;
        }

        DoneOn = doneOn;
        UpdatedAt = now;
        if (Repeat is not { } repeat)
        {
            return null;
        }

        var nextDue = repeat.After(doneOn);
        if (repeat.Until is { } until && nextDue > until)
        {
            return null;
        }

        return Create(nextId, PetId, new HealthEventDetails(Type, Title, nextDue, DueAt, DoneOn: null, repeat, Reminder, Notes: null), now);
    }
}
