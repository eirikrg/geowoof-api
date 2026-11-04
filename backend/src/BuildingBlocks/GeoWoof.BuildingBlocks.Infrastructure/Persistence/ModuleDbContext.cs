using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Entities;
using GeoWoof.BuildingBlocks.Infrastructure.Inbox;
using GeoWoof.BuildingBlocks.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Base DbContext for a module. Each module owns one PostgreSQL schema that contains its tables and
/// its own <c>outbox_messages</c> and <c>inbox_messages</c>. Register it with
/// <see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>PostgreSQL schema owned by the module (for example <c>pets</c>).</summary>
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>
    /// Saves changes and translates provider exceptions into persistence exceptions that Application code
    /// can handle (<see cref="UniqueConstraintViolationException"/>, <see cref="ConcurrencyConflictException"/>).
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException("The data was modified by another operation.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            throw new UniqueConstraintViolationException(postgres.ConstraintName, "A unique constraint was violated.", exception);
        }
    }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModule(modelBuilder);

        // Domain events are transient: never map them.
        var aggregateTypes = modelBuilder.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Where(clrType => typeof(AggregateRoot).IsAssignableFrom(clrType))
            .ToList();
        foreach (var aggregateType in aggregateTypes)
        {
            modelBuilder.Entity(aggregateType).Ignore(nameof(AggregateRoot.DomainEvents));
        }

        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }

    /// <summary>Applies the module's entity configurations.</summary>
    protected abstract void ConfigureModule(ModelBuilder modelBuilder);
}
