namespace GeoWoof.BuildingBlocks.Application.Persistence;

/// <summary>
/// A unique constraint was violated while saving (for example, two requests claimed the same username
/// at the same time). Thrown by the module unit of work so that Application code never sees EF Core or
/// Npgsql exceptions.
/// </summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException()
    {
    }

    public UniqueConstraintViolationException(string message)
        : base(message)
    {
    }

    public UniqueConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UniqueConstraintViolationException(string? constraintName, string message, Exception innerException)
        : base(message, innerException)
    {
        ConstraintName = constraintName;
    }

    /// <summary>Name of the violated constraint or index, when the database reports it.</summary>
    public string? ConstraintName { get; }
}

/// <summary>An optimistic concurrency check failed: the row changed since it was read.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
