using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Notifications.Application;
using GeoWoof.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GeoWoof.Modules.Notifications.Infrastructure.Persistence;

/// <summary>Schema <c>notifications</c>: what each person has to find out, and where to send a push.</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ModuleDbContext(options), INotificationsUnitOfWork
{
    public const string SchemaName = "notifications";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<DeviceToken> Devices => Set<DeviceToken>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(builder =>
        {
            builder.ToTable("notifications");
            builder.HasKey(notification => notification.Id);
            builder.Property(notification => notification.Id).ValueGeneratedNever();
            builder.Property(notification => notification.Type).HasConversion<string>().HasMaxLength(32);
            builder.Property(notification => notification.DedupeKey).HasMaxLength(256).IsRequired();
            builder.HasIndex(notification => notification.DedupeKey).IsUnique();

            // The ids grow with time: this serves the list and the unread count.
            builder.HasIndex(notification => new { notification.RecipientId, notification.Id });
            builder.HasIndex(notification => notification.ActorId);
        });

        modelBuilder.Entity<DeviceToken>(builder =>
        {
            builder.ToTable("device_tokens");
            builder.HasKey(device => device.Id);
            builder.Property(device => device.Id).ValueGeneratedNever();
            builder.Property(device => device.Token).HasMaxLength(DeviceToken.TokenMaxLength).IsRequired();
            builder.Property(device => device.Platform).HasMaxLength(16).IsRequired();
            builder.HasIndex(device => device.Token).IsUnique();
            builder.HasIndex(device => device.UserId);
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations</c>.</summary>
internal sealed class NotificationsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geowoof_design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.SchemaName).UseNetTopologySuite())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new NotificationsDbContext(options);
    }
}
