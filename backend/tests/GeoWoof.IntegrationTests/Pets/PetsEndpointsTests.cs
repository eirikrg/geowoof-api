using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Media.Application.Abstractions;
using GeoWoof.Modules.Pets.Infrastructure.Persistence;
using GeoWoof.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Pets;

/// <summary>The phase 2 pets contract (docs/api.md) end to end: HTTP, handlers, PostgreSQL, the Outbox and storage.</summary>
public sealed class PetsEndpointsTests(TestInfrastructure infrastructure)
{
    private static object Luna(Guid? avatarAssetId = null) => new
    {
        name = "Luna",
        species = "Dog",
        breed = "Border collie",
        birthDate = "2021-04-12",
        sex = "Female",
        size = "Medium",
        color = "Blanco y negro",
        neutered = true,
        microchip = "941000024567890",
        avatarAssetId,
        bio = "Le encanta el agua",
    };

    [Fact]
    public async Task A_pet_with_a_photo_can_be_created_read_edited_and_deleted()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var photo = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(600, 400), "PetAvatar");
        var assetId = photo.GetProperty("id").GetGuid();

        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", Luna(assetId));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pet = await created.JsonAsync();
        var petId = pet.GetProperty("id").GetGuid();
        created.Headers.Location!.ToString().ShouldBe($"/api/v1/pets/{petId}");
        pet.GetProperty("ownerId").GetGuid().ShouldBe(ana.Id);
        pet.GetProperty("species").GetString().ShouldBe("Dog");
        pet.GetProperty("birthDate").GetString().ShouldBe("2021-04-12");
        pet.GetProperty("size").GetString().ShouldBe("Medium");
        pet.GetProperty("avatar").GetProperty("assetId").GetGuid().ShouldBe(assetId);
        (await MediaTestClient.DownloadAsync(pet.GetProperty("avatar").GetProperty("mediumUrl").GetString()!)).ShouldNotBeEmpty();

        using var edited = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}", new { name = "Lunita", species = "Other", speciesOther = "Hurón", sex = "Unknown", neutered = false });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await edited.JsonAsync();
        after.GetProperty("name").GetString().ShouldBe("Lunita");
        after.GetProperty("speciesOther").GetString().ShouldBe("Hurón");
        after.GetProperty("breed").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("microchip").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("avatar").ValueKind.ShouldBe(JsonValueKind.Null);

        using var deleted = await ana.Client.DeleteAsync(new Uri($"/api/v1/pets/{petId}", UriKind.Relative), TestContext.Current.CancellationToken);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var missing = await ana.Client.GetAsync(new Uri($"/api/v1/pets/{petId}", UriKind.Relative), TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await missing.JsonAsync()).GetProperty("code").GetString().ShouldBe("pets.not_found");

        await using var scope = api.Services.CreateAsyncScope();
        var outbox = await scope.ServiceProvider.GetRequiredService<PetsDbContext>().OutboxMessages
            .Select(message => message.Type)
            .ToListAsync(TestContext.Current.CancellationToken);
        outbox.ShouldContain("pets.pet_created.v1");
        outbox.ShouldContain("pets.pet_updated.v1");
        outbox.ShouldContain("pets.pet_deleted.v1");
    }

    [Fact]
    public async Task Only_the_owner_sees_the_microchip_and_can_make_changes()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", Luna());
        var petId = (await created.JsonAsync()).GetProperty("id").GetGuid();

        (await ana.Client.GetJsonAsync($"/api/v1/pets/{petId}")).GetProperty("microchip").GetString().ShouldBe("941000024567890");
        (await bea.Client.GetJsonAsync($"/api/v1/pets/{petId}")).GetProperty("microchip").ValueKind.ShouldBe(JsonValueKind.Null);
        var ofAna = await bea.Client.GetJsonAsync($"/api/v1/pets?ownerId={ana.Id}");
        ofAna.GetProperty("items")[0].GetProperty("microchip").ValueKind.ShouldBe(JsonValueKind.Null);
        ofAna.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await bea.Client.GetJsonAsync("/api/v1/pets")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await ana.Client.GetJsonAsync("/api/v1/pets")).GetProperty("items")[0].GetProperty("microchip").GetString().ShouldBe("941000024567890");

        using var edit = await bea.Client.PutJsonAsync($"/api/v1/pets/{petId}", Luna());
        using var delete = await bea.Client.DeleteAsync(new Uri($"/api/v1/pets/{petId}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var weights = await bea.Client.GetAsync(new Uri($"/api/v1/pets/{petId}/weights", UriKind.Relative), TestContext.Current.CancellationToken);
        using var weigh = await bea.Client.PostJsonAsync($"/api/v1/pets/{petId}/weights", new { measuredOn = "2026-01-01", weightKg = 10 });
        foreach (var response in new[] { edit, delete, weights, weigh })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await response.JsonAsync()).GetProperty("code").GetString().ShouldBe("pets.not_owner");
        }
    }

    [Fact]
    public async Task The_fields_are_validated_with_the_names_of_the_contract()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");

        using var response = await ana.Client.PostJsonAsync("/api/v1/pets", new
        {
            name = " ",
            species = "Dragon",
            birthDate = "1900-01-01",
            sex = "Nope",
            size = "Huge",
            neutered = false,
            microchip = "con espacios",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.JsonAsync();
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        var errors = body.GetProperty("errors");
        foreach (var field in new[] { "name", "species", "birthDate", "sex", "size", "microchip" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue($"Missing the field {field} in {errors}");
        }
    }

    [Fact]
    public async Task The_weight_history_can_be_recorded_corrected_and_paged()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", Luna());
        var petId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        var path = $"/api/v1/pets/{petId}/weights";

        for (var day = 1; day <= 3; day++)
        {
            using var added = await ana.Client.PostJsonAsync(path, new { measuredOn = $"2026-01-0{day}", weightKg = 18 + (day / 10m) });
            added.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var sameDay = await ana.Client.PostJsonAsync(path, new { measuredOn = "2026-01-02", weightKg = 19 });
        sameDay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameDay.JsonAsync()).GetProperty("code").GetString().ShouldBe("pets.weight_date_taken");

        using var wrong = await ana.Client.PostJsonAsync(path, new { measuredOn = "2026-02-01", weightKg = 18.456 });
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.JsonAsync()).GetProperty("errors").TryGetProperty("weightKg", out _).ShouldBeTrue();

        var first = await ana.Client.GetJsonAsync($"{path}?limit=2");
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        first.GetProperty("items")[0].GetProperty("measuredOn").GetString().ShouldBe("2026-01-03");
        first.GetProperty("items")[0].GetProperty("weightKg").GetDecimal().ShouldBe(18.3m);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ana.Client.GetJsonAsync($"{path}?limit=2&cursor={cursor}");
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var entryId = second.GetProperty("items")[0].GetProperty("id").GetGuid();
        using var movedOntoAnother = await ana.Client.PutJsonAsync($"{path}/{entryId}", new { measuredOn = "2026-01-03", weightKg = 18.1 });
        movedOntoAnother.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var corrected = await ana.Client.PutJsonAsync($"{path}/{entryId}", new { measuredOn = "2026-01-01", weightKg = 17.95 });
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await corrected.JsonAsync()).GetProperty("weightKg").GetDecimal().ShouldBe(17.95m);

        using var removed = await ana.Client.DeleteAsync(new Uri($"{path}/{entryId}", UriKind.Relative), TestContext.Current.CancellationToken);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var again = await ana.Client.DeleteAsync(new Uri($"{path}/{entryId}", UriKind.Relative), TestContext.Current.CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await again.JsonAsync()).GetProperty("code").GetString().ShouldBe("pets.weight_not_found");
    }

    [Fact]
    public async Task Deleting_a_pet_also_deletes_its_weights()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var created = await ana.Client.PostJsonAsync("/api/v1/pets", Luna());
        var petId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        using var added = await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/weights", new { measuredOn = "2026-01-01", weightKg = 18 });

        using var deleted = await ana.Client.DeleteAsync(new Uri($"/api/v1/pets/{petId}", UriKind.Relative), TestContext.Current.CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = api.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<PetsDbContext>().WeightEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task A_person_has_a_limited_number_of_pets()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        for (var i = 0; i < 20; i++)
        {
            using var created = await ana.Client.PostJsonAsync("/api/v1/pets", Luna());
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var tooMany = await ana.Client.PostJsonAsync("/api/v1/pets", Luna());

        tooMany.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await tooMany.JsonAsync()).GetProperty("code").GetString().ShouldBe("pets.limit_reached");
    }

    [Fact]
    public async Task Deleting_an_account_erases_its_pets_and_their_photos()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var photo = await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(100, 100), "PetAvatar");
        var assetId = photo.GetProperty("id").GetGuid();
        using var anaPet = await ana.Client.PostJsonAsync("/api/v1/pets", Luna(assetId));
        using var beaPet = await bea.Client.PostJsonAsync("/api/v1/pets", Luna());

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await ana.Client.SendAsync(request, TestContext.Current.CancellationToken);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Eventually.Until(
            async () => (await bea.Client.GetJsonAsync($"/api/v1/pets?ownerId={ana.Id}")).GetProperty("items").GetArrayLength(),
            count => count == 0,
            "the pets of the deleted account are erased");
        var storage = api.Services.GetRequiredService<IMediaStorage>();
        await Eventually.Until(
            () => storage.GetInfoAsync($"variants/{assetId:N}/medium.webp", TestContext.Current.CancellationToken),
            info => info is null,
            "the photos of the deleted account are erased");
        (await bea.Client.GetJsonAsync("/api/v1/pets")).GetProperty("items").GetArrayLength().ShouldBe(1);
    }
}
