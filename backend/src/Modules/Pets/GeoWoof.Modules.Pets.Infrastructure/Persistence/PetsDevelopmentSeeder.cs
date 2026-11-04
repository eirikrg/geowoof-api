using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GeoWoof.Modules.Pets.Infrastructure.Persistence;

/// <summary>Sample pets of the sample accounts, with photos and a weight history (docs/api.md). Idempotent.</summary>
internal sealed partial class PetsDevelopmentSeeder(
    PetsDbContext dbContext,
    IIdentityApi identity,
    IMediaApi media,
    TimeProvider timeProvider,
    ILogger<PetsDevelopmentSeeder> logger) : IDevelopmentDataSeeder
{
    private const int PhotoSize = 1200;
    private const int WeightMonths = 8;

    private static readonly (string OwnerEmail, PetDetails Details, decimal StartWeightKg, SKColor Color)[] Samples =
    [
        (
            "ana@geowoof.dev",
            new PetDetails("Luna", PetSpecies.Dog, null, "Border collie", new DateOnly(2021, 4, 12), PetSex.Female, PetSize.Medium, "Blanco y negro", true, "941000024567890", "Le encanta el agua"),
            17.6m,
            new SKColor(0xF2, 0x8C, 0x38)),
        (
            "ana@geowoof.dev",
            new PetDetails("Toby", PetSpecies.Dog, null, "Teckel", new DateOnly(2023, 9, 1), PetSex.Male, PetSize.Small, "Marrón", false, null, null),
            7.2m,
            new SKColor(0x4C, 0x8B, 0xB0)),
        (
            "bea@geowoof.dev",
            new PetDetails("Mishi", PetSpecies.Cat, null, "Común europeo", new DateOnly(2020, 2, 20), PetSex.Female, PetSize.Small, "Atigrada", true, null, "Pasea con arnés"),
            3.9m,
            new SKColor(0x7A, 0x9E, 0x5F)),
        (
            "carlos@geowoof.dev",
            new PetDetails("Rocky", PetSpecies.Dog, null, "Pastor alemán", new DateOnly(2019, 11, 5), PetSex.Male, PetSize.Large, "Negro y fuego", true, "941000098765432", null),
            34.5m,
            new SKColor(0xA8, 0x6A, 0xB8)),
    ];

    public int Order => 10;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var sample in Samples)
        {
            var ownerId = await identity.FindUserIdByEmailAsync(sample.OwnerEmail, cancellationToken);
            if (ownerId is not { } owner
                || await dbContext.Pets.AnyAsync(pet => pet.OwnerId == owner && pet.Name == sample.Details.Name, cancellationToken))
            {
                continue;
            }

            await AddSampleAsync(owner, sample.Details, sample.StartWeightKg, sample.Color, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AddSampleAsync(Guid ownerId, PetDetails details, decimal startWeightKg, SKColor color, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pet = Pet.Create(Guid.CreateVersion7(now), ownerId, details, await ImportPhotoAsync(ownerId, details.Name, color, cancellationToken), now);
        dbContext.Pets.Add(pet);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        for (var monthsAgo = WeightMonths - 1; monthsAgo >= 0; monthsAgo--)
        {
            var step = WeightMonths - monthsAgo;
            var weight = decimal.Round(startWeightKg * (1 + (0.01m * step)), 2);
            dbContext.WeightEntries.Add(PetWeightEntry.Record(Guid.CreateVersion7(now), pet.Id, today.AddMonths(-monthsAgo), weight));
        }
    }

    /// <summary>Sample data must never stop the API from starting: without storage, the pet just has no photo.</summary>
    private async Task<Guid?> ImportPhotoAsync(Guid ownerId, string name, SKColor color, CancellationToken cancellationToken)
    {
        try
        {
            var assetId = await media.ImportAsync(ownerId, MediaUsage.PetAvatar, DrawPhoto(name, color), "image/png", cancellationToken);
            await media.AttachAsync(assetId, ownerId, MediaUsage.PetAvatar, cancellationToken);
            return assetId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPhotoFailed(logger, exception, name);
            return null;
        }
    }

    /// <summary>A plain placeholder portrait: a coloured background with a paw print.</summary>
    private static byte[] DrawPhoto(string name, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(PhotoSize, PhotoSize));
        var canvas = surface.Canvas;
        canvas.Clear(color);

        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(230), IsAntialias = true };
        var center = PhotoSize / 2f;
        var seed = Math.Abs(name.Length - 4) * 8f;
        canvas.DrawOval(center, center + 140, 240, 200, paint);
        canvas.DrawOval(center - 290, center - 60 + seed, 85, 120, paint);
        canvas.DrawOval(center - 105, center - 220, 85, 130, paint);
        canvas.DrawOval(center + 105, center - 220, 85, 130, paint);
        canvas.DrawOval(center + 290, center - 60 + seed, 85, 120, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sample photo of {Name} could not be imported")]
    private static partial void LogPhotoFailed(ILogger logger, Exception exception, string name);
}
