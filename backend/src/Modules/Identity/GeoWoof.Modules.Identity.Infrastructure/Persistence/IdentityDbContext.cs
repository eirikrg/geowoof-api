using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence;

/// <summary>Schema <c>identity</c>: users with their external logins, refresh tokens and verification codes.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options), IIdentityUnitOfWork
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
        modelBuilder.ApplyConfiguration(new VerificationCodeConfiguration());
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        builder.Property(user => user.Email)
            .HasConversion(email => email.Value, value => Email.FromStorage(value))
            .HasMaxLength(Email.MaxLength)
            .IsRequired();
        builder.HasIndex(user => user.Email).IsUnique();

        // Unique among users who chose one; NULLs (onboarding pending) do not collide in PostgreSQL.
        builder.Property(user => user.Username)
            .HasConversion(username => username!.Value, value => Username.FromStorage(value))
            .HasMaxLength(Username.MaxLength);
        builder.HasIndex(user => user.Username).IsUnique();

        builder.Property(user => user.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(user => user.PasswordHash).HasMaxLength(512);
        builder.Property(user => user.Language).HasConversion<string>().HasMaxLength(8);

        builder.OwnsMany(user => user.ExternalLogins, logins =>
        {
            logins.ToTable("user_logins");
            logins.WithOwner().HasForeignKey("user_id");
            logins.HasKey(login => new { login.Provider, login.Subject });
            logins.Property(login => login.Provider).HasMaxLength(32);
            logins.Property(login => login.Subject).HasMaxLength(255);
        });
        builder.Navigation(user => user.ExternalLogins).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();
        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => token.UserId);
        builder.HasIndex(token => token.FamilyId);
        builder.Property(token => token.RevocationReason).HasConversion<string>().HasMaxLength(32);

        // Optimistic concurrency on PostgreSQL's xmin: two requests cannot rotate the same token.
        builder.Property<uint>("Version").IsRowVersion().HasColumnName("xmin");
    }
}

internal sealed class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> builder)
    {
        builder.ToTable("verification_codes");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();
        builder.Property(code => code.Purpose).HasConversion<string>().HasMaxLength(32);
        builder.Property(code => code.CodeHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(code => new { code.UserId, code.Purpose, code.CreatedAt });
    }
}
