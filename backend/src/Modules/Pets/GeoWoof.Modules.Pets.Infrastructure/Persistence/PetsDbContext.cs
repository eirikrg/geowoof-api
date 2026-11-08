using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Pets.Infrastructure.Persistence;

/// <summary>Schema <c>pets</c>: pets and their weight history.</summary>
public sealed class PetsDbContext(DbContextOptions<PetsDbContext> options) : ModuleDbContext(options), IPetsUnitOfWork
{
    public const string SchemaName = "pets";

    public override string Schema => SchemaName;

    public DbSet<Pet> Pets => Set<Pet>();

    public DbSet<PetWeightEntry> WeightEntries => Set<PetWeightEntry>();

    public DbSet<HealthEvent> HealthEvents => Set<HealthEvent>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasPostgresExtension("unaccent");
        modelBuilder.ApplyConfiguration(new PetConfiguration());
        modelBuilder.ApplyConfiguration(new PetWeightEntryConfiguration());
        modelBuilder.ApplyConfiguration(new HealthEventConfiguration());
    }
}

internal sealed class PetConfiguration : IEntityTypeConfiguration<Pet>
{
    public void Configure(EntityTypeBuilder<Pet> builder)
    {
        builder.ToTable("pets");
        builder.HasKey(pet => pet.Id);
        builder.Property(pet => pet.Id).ValueGeneratedNever();
        builder.Property(pet => pet.Name).HasMaxLength(Pet.NameMaxLength).IsRequired();
        builder.Property(pet => pet.Species).HasConversion<string>().HasMaxLength(16);
        builder.Property(pet => pet.SpeciesOther).HasMaxLength(Pet.SpeciesOtherMaxLength);
        builder.Property(pet => pet.Breed).HasMaxLength(Pet.BreedMaxLength);
        builder.Property(pet => pet.Sex).HasConversion<string>().HasMaxLength(16);
        builder.Property(pet => pet.Size).HasConversion<string>().HasMaxLength(16);
        builder.Property(pet => pet.Color).HasMaxLength(Pet.ColorMaxLength);
        builder.Property(pet => pet.Microchip).HasMaxLength(Pet.MicrochipMaxLength);
        builder.Property(pet => pet.Bio).HasMaxLength(Pet.BioMaxLength);
        builder.HasIndex(pet => new { pet.OwnerId, pet.CreatedAt });
    }
}

internal sealed class PetWeightEntryConfiguration : IEntityTypeConfiguration<PetWeightEntry>
{
    public void Configure(EntityTypeBuilder<PetWeightEntry> builder)
    {
        builder.ToTable("pet_weight_entries");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.WeightKg).HasPrecision(5, 2);

        // One measurement per day; it also serves the history, most recent first.
        builder.HasIndex(entry => new { entry.PetId, entry.MeasuredOn }).IsUnique();
        builder.HasOne<Pet>().WithMany().HasForeignKey(entry => entry.PetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HealthEventConfiguration : IEntityTypeConfiguration<HealthEvent>
{
    public void Configure(EntityTypeBuilder<HealthEvent> builder)
    {
        builder.ToTable("health_events");
        builder.HasKey(healthEvent => healthEvent.Id);
        builder.Property(healthEvent => healthEvent.Id).ValueGeneratedNever();
        builder.Property(healthEvent => healthEvent.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(healthEvent => healthEvent.Title).HasMaxLength(HealthEvent.TitleMaxLength).IsRequired();
        builder.Property(healthEvent => healthEvent.RepeatUnit).HasConversion<string>().HasMaxLength(16);
        builder.Property(healthEvent => healthEvent.Reminder).HasConversion<string>().HasMaxLength(16);
        builder.Property(healthEvent => healthEvent.Notes).HasMaxLength(HealthEvent.NotesMaxLength);
        builder.Ignore(healthEvent => healthEvent.Repeat);

        // The health of a pet is read whole, by the day it is due.
        builder.HasIndex(healthEvent => new { healthEvent.PetId, healthEvent.DueOn });
        builder.HasOne<Pet>().WithMany().HasForeignKey(healthEvent => healthEvent.PetId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Used by <c>dotnet ef migrations</c> only (no database connection is opened):
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Modules/Pets/GeoWoof.Modules.Pets.Infrastructure --startup-project src/Modules/Pets/GeoWoof.Modules.Pets.Infrastructure --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class PetsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PetsDbContext>
{
    public PetsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PetsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PetsDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PetsDbContext(options);
    }
}

internal sealed class PetRepository(PetsDbContext dbContext) : IPetRepository
{
    public Task<Pet?> GetByIdAsync(Guid petId, CancellationToken cancellationToken) =>
        dbContext.Pets.FirstOrDefaultAsync(pet => pet.Id == petId, cancellationToken);

    public async Task<IReadOnlyList<Pet>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        await dbContext.Pets.Where(pet => pet.OwnerId == ownerId).OrderBy(pet => pet.CreatedAt).ThenBy(pet => pet.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Pet>> GetByIdsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken) =>
        await dbContext.Pets.AsNoTracking().Where(pet => petIds.Contains(pet.Id)).ToListAsync(cancellationToken);

    public Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedOwnerIds, RelevancePosition after, int take, CancellationToken cancellationToken)
    {
        var pattern = TextSearch.ContainsPattern(text);
        var excluded = excludedOwnerIds.ToArray();
        return TextSearch.RunAsync(
            dbContext,
            $"""
            SELECT r.id, r.score
            FROM (
                SELECT p.id, round(GREATEST(
                    word_similarity(pets.normalize_search_text({text}), pets.normalize_search_text(p.name)),
                    word_similarity(pets.normalize_search_text({text}), pets.normalize_search_text(COALESCE(p.breed, ''))))::numeric, 4) AS score
                FROM pets.pets AS p
                WHERE NOT (p.owner_id = ANY({excluded}))
                    AND (pets.normalize_search_text({text}) <% pets.normalize_search_text(p.name)
                        OR pets.normalize_search_text({text}) <% pets.normalize_search_text(p.breed)
                        OR pets.normalize_search_text(p.name) LIKE pets.normalize_search_text({pattern})
                        OR pets.normalize_search_text(p.breed) LIKE pets.normalize_search_text({pattern}))
            ) AS r
            WHERE (r.score, r.id) < ({after.Score}, {after.Id})
            ORDER BY r.score DESC, r.id DESC
            LIMIT {take}
            """,
            cancellationToken);
    }

    public Task<int> CountByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        dbContext.Pets.CountAsync(pet => pet.OwnerId == ownerId, cancellationToken);

    public void Add(Pet pet) => dbContext.Pets.Add(pet);

    public void Remove(Pet pet) => dbContext.Pets.Remove(pet);
}

internal sealed class PetWeightRepository(PetsDbContext dbContext) : IPetWeightRepository
{
    public Task<PetWeightEntry?> GetByIdAsync(Guid petId, Guid entryId, CancellationToken cancellationToken) =>
        dbContext.WeightEntries.FirstOrDefaultAsync(entry => entry.PetId == petId && entry.Id == entryId, cancellationToken);

    public async Task<IReadOnlyList<PetWeightEntry>> GetPageAsync(Guid petId, DateOnly? before, int take, CancellationToken cancellationToken) =>
        await dbContext.WeightEntries
            .Where(entry => entry.PetId == petId && (before == null || entry.MeasuredOn < before))
            .OrderByDescending(entry => entry.MeasuredOn)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Add(PetWeightEntry entry) => dbContext.WeightEntries.Add(entry);

    public void Remove(PetWeightEntry entry) => dbContext.WeightEntries.Remove(entry);
}

internal sealed class HealthEventRepository(PetsDbContext dbContext) : IHealthEventRepository
{
    public Task<HealthEvent?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken) =>
        dbContext.HealthEvents.FirstOrDefaultAsync(healthEvent => healthEvent.Id == eventId, cancellationToken);

    public async Task<IReadOnlyList<HealthEvent>> GetByPetAsync(Guid petId, CancellationToken cancellationToken) =>
        await dbContext.HealthEvents.AsNoTracking()
            .Where(healthEvent => healthEvent.PetId == petId)
            .OrderBy(healthEvent => healthEvent.DueOn).ThenBy(healthEvent => healthEvent.DueAt).ThenBy(healthEvent => healthEvent.Id)
            .ToListAsync(cancellationToken);

    public Task<int> CountByPetAsync(Guid petId, CancellationToken cancellationToken) =>
        dbContext.HealthEvents.CountAsync(healthEvent => healthEvent.PetId == petId, cancellationToken);

    public void Add(HealthEvent healthEvent) => dbContext.HealthEvents.Add(healthEvent);

    public void Remove(HealthEvent healthEvent) => dbContext.HealthEvents.Remove(healthEvent);
}
