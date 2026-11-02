namespace GeoWoof.BuildingBlocks.Domain.Entities;

/// <summary>Base class for entities identified by a UUID (v7 by convention).</summary>
public abstract class Entity
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An entity id cannot be empty.", nameof(id));
        }

        Id = id;
    }

    /// <summary>Constructor for the persistence layer.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; private init; }
}
