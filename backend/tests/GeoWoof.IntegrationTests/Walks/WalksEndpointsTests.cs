using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Walks.Domain.Routes;
using GeoWoof.Modules.Walks.Infrastructure.Persistence;
using GeoWoof.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Walks;

/// <summary>The phase 3 walks contract end to end: HTTP, handlers, PostgreSQL with PostGIS, the Outbox and storage.</summary>
public sealed class WalksEndpointsTests(TestInfrastructure infrastructure)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static object Segment(int points, long firstOffsetMs = 0, double latitude = 40.4)
    {
        var positions = Enumerable.Range(0, points).Select(i => new GeoPoint(latitude + (i * 0.0001), -3.7)).ToList();
        return new
        {
            polyline = Polyline.Encode(positions),
            timeOffsetsMs = Enumerable.Range(0, points).Select(i => firstOffsetMs + (i * 8000L)).ToArray(),
        };
    }

    private static WalkBody Body(Guid petId) => new(
        Start,
        Start.AddMinutes(10),
        "Europe/Madrid",
        [petId],
        "Friends",
        "Por el Retiro",
        [],
        [Segment(11)],
        new { distanceM = 999, movingTimeS = 1 },
        Start.AddMinutes(11));

    private static async Task<Guid> CreatePetAsync(TestUser user, string name = "Luna")
    {
        using var created = await user.Client.PostJsonAsync("/api/v1/pets", new { name, species = "Dog", sex = "Female", neutered = false });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PutNewAsync(TestUser user, WalkBody body)
    {
        var id = Guid.CreateVersion7();
        using var created = await user.Client.PutJsonAsync($"/api/v1/walks/{id}", body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    [Fact]
    public async Task A_walk_is_saved_once_with_the_numbers_of_the_server_and_its_route_in_postgis()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await CreatePetAsync(ana);
        var id = Guid.CreateVersion7();
        var body = Body(petId) with { Segments = [Segment(11), Segment(11, firstOffsetMs: 200_000, latitude: 40.5)] };

        using var created = await ana.Client.PutJsonAsync($"/api/v1/walks/{id}", body);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe($"/api/v1/walks/{id}");
        var walk = await created.JsonAsync();
        walk.GetProperty("id").GetGuid().ShouldBe(id);
        walk.GetProperty("ownerId").GetGuid().ShouldBe(ana.Id);
        walk.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(Start);
        walk.GetProperty("timeZone").GetString().ShouldBe("Europe/Madrid");
        walk.GetProperty("petIds")[0].GetGuid().ShouldBe(petId);
        walk.GetProperty("visibility").GetString().ShouldBe("Friends");
        walk.GetProperty("note").GetString().ShouldBe("Por el Retiro");
        walk.GetProperty("distanceM").GetInt32().ShouldBe(222);
        walk.GetProperty("durationS").GetInt32().ShouldBe(600);
        walk.GetProperty("movingTimeS").GetInt32().ShouldBe(160);
        walk.GetProperty("averageSpeedMps").GetDouble().ShouldBe(1.39);
        walk.GetProperty("paceSecondsPerKm").GetInt32().ShouldBe(719);
        walk.GetProperty("previewPolylines").GetArrayLength().ShouldBe(2);
        walk.GetProperty("segments").GetArrayLength().ShouldBe(2);
        walk.GetProperty("segments")[1].GetProperty("timeOffsetsMs")[0].GetInt64().ShouldBe(200_000);

        using var repeated = await ana.Client.PutJsonAsync($"/api/v1/walks/{id}", body);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await ana.Client.GetJsonAsync($"/api/v1/walks/{id}");
        read.GetProperty("distanceM").GetInt32().ShouldBe(222);
        read.GetProperty("segments").GetArrayLength().ShouldBe(2);

        var summary = (await ana.Client.GetJsonAsync("/api/v1/me/walks")).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        summary.GetProperty("id").GetGuid().ShouldBe(id);
        summary.TryGetProperty("segments", out _).ShouldBeFalse();

        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalksDbContext>();
        var outbox = await db.OutboxMessages.Select(message => message.Type).ToListAsync(Token);
        outbox.ShouldHaveSingleItem().ShouldBe("walks.walk_recorded.v1");

        var length = await db.Database
            .SqlQueryRaw<double>("""SELECT ST_Length(route) AS "Value" FROM walks.walks WHERE id = {0}""", id)
            .SingleAsync(Token);
        length.ShouldBe(222, 2, "The geography is measured on the spheroid, so it differs slightly from the haversine distance.");
        var lines = await db.Database
            .SqlQueryRaw<int>("""SELECT ST_NumGeometries(route::geometry) AS "Value" FROM walks.walks WHERE id = {0}""", id)
            .SingleAsync(Token);
        lines.ShouldBe(2);
        var startLatitude = await db.Database
            .SqlQueryRaw<double>("""SELECT ST_Y(start_point::geometry) AS "Value" FROM walks.walks WHERE id = {0}""", id)
            .SingleAsync(Token);
        startLatitude.ShouldBe(40.4, 0.00001);
    }

    [Fact]
    public async Task Two_uploads_at_the_same_time_save_a_single_walk()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var body = Body(await CreatePetAsync(ana));
        var id = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ana.Client.PutJsonAsync($"/api/v1/walks/{id}", body)));

        var statuses = responses.Select(response => response.StatusCode).ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        statuses.Count(status => status == HttpStatusCode.Created).ShouldBe(1, string.Join(", ", statuses));
        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(3, string.Join(", ", statuses));
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalksDbContext>();
        (await db.Walks.CountAsync(Token)).ShouldBe(1);
        (await db.OutboxMessages.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_note_visibility_and_photos_of_a_saved_walk_can_change()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await CreatePetAsync(ana);
        var photo = (await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(300, 200), "WalkPhoto")).GetProperty("id").GetGuid();
        var replacement = (await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(300, 200), "WalkPhoto")).GetProperty("id").GetGuid();
        var body = Body(petId) with { PhotoAssetIds = [photo] };
        var id = await PutNewAsync(ana, body);
        var path = $"/api/v1/walks/{id}";

        var saved = await ana.Client.GetJsonAsync(path);
        var image = saved.GetProperty("photos")[0];
        image.GetProperty("assetId").GetGuid().ShouldBe(photo);
        (await MediaTestClient.DownloadAsync(image.GetProperty("mediumUrl").GetString()!)).ShouldNotBeEmpty();

        var later = Start.AddHours(1);
        using var edited = await ana.Client.PutJsonAsync(path, body with { Note = "  ", Visibility = "OnlyMe", PhotoAssetIds = [replacement], UpdatedAt = later });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await edited.JsonAsync();
        after.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("visibility").GetString().ShouldBe("OnlyMe");
        after.GetProperty("photos")[0].GetProperty("assetId").GetGuid().ShouldBe(replacement);

        // The replaced photo is free again, so it can be used somewhere else.
        await Eventually.Until(
            async () =>
            {
                using var response = await ana.Client.GetAsync(new Uri($"/api/v1/media/uploads/{photo}", UriKind.Relative), Token);
                return response.StatusCode;
            },
            status => status is HttpStatusCode.OK or HttpStatusCode.NotFound,
            "the replaced photo is released");

        using var late = await ana.Client.PutJsonAsync(path, body with { Note = "Antigua", UpdatedAt = Start.AddMinutes(30) });
        late.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await late.JsonAsync()).GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);

        using var reorderedAgain = await ana.Client.PutJsonAsync(path, body with { PhotoAssetIds = [replacement], Visibility = "Everyone", UpdatedAt = later.AddMinutes(1) });
        reorderedAgain.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var otherRoute = await ana.Client.PutJsonAsync(path, body with { Segments = [Segment(5)], UpdatedAt = later.AddHours(1) });
        otherRoute.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(otherRoute)).ShouldBe("walks.route_immutable");
        using var otherDates = await ana.Client.PutJsonAsync(path, body with { EndedAt = Start.AddMinutes(9), UpdatedAt = later.AddHours(1) });
        otherDates.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var otherPet = await CreatePetAsync(ana, "Toby");
        using var morePets = await ana.Client.PutJsonAsync(path, body with { PetIds = [petId, otherPet], UpdatedAt = later.AddHours(1) });
        morePets.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Every_rule_of_the_contract_has_its_error()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var petId = await CreatePetAsync(ana);
        var petOfBea = await CreatePetAsync(bea);
        var body = Body(petId);

        using var fields = await ana.Client.PutJsonAsync($"/api/v1/walks/{Guid.CreateVersion7()}", body with
        {
            Visibility = "Nobody",
            Note = new string('a', 1001),
            TimeZone = "Marte/Olympus",
            PetIds = [],
            PhotoAssetIds = [petId, petId],
        });
        fields.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await fields.JsonAsync();
        errors.GetProperty("code").GetString().ShouldBe("validation.failed");
        foreach (var field in new[] { "visibility", "note", "timeZone", "petIds", "photoAssetIds" })
        {
            errors.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue($"Missing the field {field} in {errors}");
        }

        var cases = new (WalkBody Body, string Code)[]
        {
            (body with { PetIds = [petOfBea] }, "walks.pet_not_owned"),
            (body with { EndedAt = Start }, "walks.invalid_time_range"),
            (body with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(10), EndedAt = DateTimeOffset.UtcNow.AddMinutes(20) }, "walks.invalid_time_range"),
            (body with { EndedAt = Start.AddHours(13) }, "walks.too_long"),
            (body with { Segments = [new { polyline = "_", timeOffsetsMs = new[] { 0L } }] }, "walks.invalid_track"),
            (body with { Segments = [null] }, "walks.invalid_track"),
            (body with { Segments = [] }, "walks.invalid_track"),
            (body with { Segments = [Segment(1)] }, "walks.invalid_track"),
            (body with { PhotoAssetIds = [Guid.CreateVersion7()] }, "media.not_found"),
        };
        foreach (var (invalid, code) in cases)
        {
            using var response = await ana.Client.PutJsonAsync($"/api/v1/walks/{Guid.CreateVersion7()}", invalid);
            (await CodeAsync(response)).ShouldBe(code);
        }

        var tooLong = Start.AddHours(12);
        var allPoints = Enumerable.Range(0, 20_001).Select(i => new GeoPoint(i * 0.00001, 0)).ToList();
        using var tooManyPoints = await ana.Client.PutJsonAsync($"/api/v1/walks/{Guid.CreateVersion7()}", body with
        {
            EndedAt = tooLong,
            Segments = [new { polyline = Polyline.Encode(allPoints), timeOffsetsMs = Enumerable.Range(0, 20_001).Select(i => i * 2000L).ToArray() }],
        });
        tooManyPoints.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(tooManyPoints)).ShouldBe("walks.too_many_points");

        using var malformed = await ana.Client.PutJsonAsync($"/api/v1/walks/{Guid.CreateVersion7()}", new { startedAt = "ayer" });
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await ana.Client.GetJsonAsync("/api/v1/me/walks")).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_walk_without_movement_is_valid()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var still = new GeoPoint(40.4, -3.7);
        var body = Body(await CreatePetAsync(ana)) with
        {
            Segments = [new { polyline = Polyline.Encode([still, still]), timeOffsetsMs = new[] { 0L, 4000L } }],
        };
        var id = await PutNewAsync(ana, body);

        var walk = await ana.Client.GetJsonAsync($"/api/v1/walks/{id}");

        walk.GetProperty("distanceM").GetInt32().ShouldBe(0);
        walk.GetProperty("averageSpeedMps").GetDouble().ShouldBe(0);
        walk.GetProperty("paceSecondsPerKm").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task For_other_people_a_walk_does_not_exist()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var petId = await CreatePetAsync(ana);
        var id = await PutNewAsync(ana, Body(petId));
        var path = $"/api/v1/walks/{id}";

        using var read = await bea.Client.GetAsync(new Uri(path, UriKind.Relative), Token);
        using var overwritten = await bea.Client.PutJsonAsync(path, Body(await CreatePetAsync(bea)));
        using var deleted = await bea.Client.DeleteAsync(new Uri(path, UriKind.Relative), Token);
        foreach (var response in new[] { read, overwritten, deleted })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeAsync(response)).ShouldBe("walks.not_found");
        }

        (await bea.Client.GetJsonAsync("/api/v1/me/walks")).GetProperty("items").GetArrayLength().ShouldBe(0);
        using var ofAnotherPet = await bea.Client.GetAsync(new Uri($"/api/v1/pets/{petId}/walks", UriKind.Relative), Token);
        ofAnotherPet.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(ofAnotherPet)).ShouldBe("pets.not_owner");
        using var statsOfAnotherPet = await bea.Client.GetAsync(new Uri($"/api/v1/pets/{petId}/walk-stats", UriKind.Relative), Token);
        statsOfAnotherPet.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var missingPet = await ana.Client.GetAsync(new Uri($"/api/v1/pets/{Guid.CreateVersion7()}/walks", UriKind.Relative), Token);
        missingPet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(missingPet)).ShouldBe("pets.not_found");

        using var anonymous = api.CreateClient();
        using var unauthorized = await anonymous.GetAsync(new Uri("/api/v1/me/walks", UriKind.Relative), Token);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Lists_and_stats_can_be_for_a_person_or_a_pet()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var luna = await CreatePetAsync(ana);
        var toby = await CreatePetAsync(ana, "Toby");
        var ids = new List<Guid>();

        // The three go back in time from now, each a week before the previous one, so they make a streak.
        var thisWeek = DateTimeOffset.UtcNow.AddHours(-1);
        for (var week = 0; week < 3; week++)
        {
            var started = thisWeek.AddDays(-7 * week);
            ids.Add(await PutNewAsync(ana, Body(week == 0 ? luna : toby) with { StartedAt = started, EndedAt = started.AddMinutes(10) }));
        }

        // Same start as the first one: the id breaks the tie and no walk is lost between pages.
        ids.Add(await PutNewAsync(ana, Body(toby) with { StartedAt = thisWeek, EndedAt = thisWeek.AddMinutes(10) }));

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var query = cursor is null ? "/api/v1/me/walks?limit=2" : $"/api/v1/me/walks?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await ana.Client.GetJsonAsync(query);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        }
        while (cursor is not null);

        seen.Count.ShouldBe(4);
        seen.ToHashSet().SetEquals(ids).ShouldBeTrue();
        seen[^1].ShouldBe(ids[2]);

        var ofLuna = await ana.Client.GetJsonAsync($"/api/v1/pets/{luna}/walks");
        ofLuna.GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(ids[0]);

        var total = await ana.Client.GetJsonAsync("/api/v1/me/walk-stats");
        total.GetProperty("walkCount").GetInt32().ShouldBe(4);
        total.GetProperty("distanceM").GetInt32().ShouldBe(444);
        total.GetProperty("durationS").GetInt32().ShouldBe(2400);
        total.GetProperty("movingTimeS").GetInt32().ShouldBe(320);
        total.GetProperty("weeklyStreak").GetInt32().ShouldBe(3);

        var statsOfLuna = await ana.Client.GetJsonAsync($"/api/v1/pets/{luna}/walk-stats");
        statsOfLuna.GetProperty("walkCount").GetInt32().ShouldBe(1);
        statsOfLuna.GetProperty("weeklyStreak").GetInt32().ShouldBe(1);

        using var badCursor = await ana.Client.GetAsync(new Uri("/api/v1/me/walks?cursor=nope", UriKind.Relative), Token);
        badCursor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(badCursor)).ShouldBe("request.invalid");
    }

    [Fact]
    public async Task Deleting_a_walk_publishes_it_and_frees_its_photos()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var photo = (await MediaTestClient.UploadReadyAsync(api, ana.Client, TestImages.Jpeg(300, 200), "WalkPhoto")).GetProperty("id").GetGuid();
        var id = await PutNewAsync(ana, Body(await CreatePetAsync(ana)) with { PhotoAssetIds = [photo] });

        using var deleted = await ana.Client.DeleteAsync(new Uri($"/api/v1/walks/{id}", UriKind.Relative), Token);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var missing = await ana.Client.GetAsync(new Uri($"/api/v1/walks/{id}", UriKind.Relative), Token);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalksDbContext>();
        (await db.OutboxMessages.Select(message => message.Type).ToListAsync(Token)).ShouldContain("walks.walk_deleted.v1");
    }

    [Fact]
    public async Task Deleting_a_pet_keeps_its_walks_without_it()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var luna = await CreatePetAsync(ana);
        var toby = await CreatePetAsync(ana, "Toby");
        var body = Body(luna) with { PetIds = [luna, toby] };
        var id = await PutNewAsync(ana, body);

        using var deleted = await ana.Client.DeleteAsync(new Uri($"/api/v1/pets/{luna}", UriKind.Relative), Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var walk = await Eventually.Until(
            () => ana.Client.GetJsonAsync($"/api/v1/walks/{id}"),
            current => current.GetProperty("petIds").GetArrayLength() == 1,
            "the deleted pet leaves its walks");
        walk.GetProperty("petIds")[0].GetGuid().ShouldBe(toby);

        // The phone did not know about it yet: its retry is not a change.
        using var retry = await ana.Client.PutJsonAsync($"/api/v1/walks/{id}", body);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.JsonAsync()).GetProperty("petIds").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_an_account_erases_its_walks()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        await PutNewAsync(ana, Body(await CreatePetAsync(ana)));
        await PutNewAsync(bea, Body(await CreatePetAsync(bea)));

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/me", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { password = TestUser.Password }),
        };
        using var deleted = await ana.Client.SendAsync(request, Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Eventually.Until(
            async () =>
            {
                await using var scope = api.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<WalksDbContext>().Walks.CountAsync(walk => walk.OwnerId == ana.Id, Token);
            },
            count => count == 0,
            "the walks of the deleted account are erased");
        (await bea.Client.GetJsonAsync("/api/v1/me/walks")).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    private sealed record WalkBody(
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        string TimeZone,
        Guid[] PetIds,
        string Visibility,
        string? Note,
        Guid[] PhotoAssetIds,
        object?[] Segments,
        object ClientStats,
        DateTimeOffset UpdatedAt);
}
