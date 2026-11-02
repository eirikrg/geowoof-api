namespace GeoWoof.BuildingBlocks.Contracts;

/// <summary>
/// Stable, versioned name of an integration event as stored in the Outbox
/// (format <c>module.event_name.vN</c>, for example <c>social.post_liked.v1</c>).
/// Renaming the CLR type never breaks stored messages as long as this name is kept.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventTypeAttribute : Attribute
{
    public EventTypeAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
