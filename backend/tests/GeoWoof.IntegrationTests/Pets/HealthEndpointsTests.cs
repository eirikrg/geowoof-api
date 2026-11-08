using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;
using GeoWoof.Modules.Pets.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.IntegrationTests.Pets;

/// <summary>The health of a pet end to end: HTTP, handlers and PostgreSQL. Only its owner sees it.</summary>
public sealed class HealthEndpointsTests(TestInfrastructure infrastructure)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<Guid> PetAsync(TestUser owner, string name = "Luna")
    {
        using var created = await owner.Client.PostJsonAsync("/api/v1/pets", new { name, species = "Dog", sex = "Female", neutered = false });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static object Rabies(string dueOn = "2027-01-15", object? repeat = null, string? doneOn = null) => new
    {
        type = "Vaccine",
        title = "  Rabia  ",
        dueOn,
        dueAt = (string?)null,
        doneOn,
        repeat = repeat ?? new { every = 1, unit = "Years", until = (string?)null },
        reminder = "DayBefore",
        notes = "Lote 1234",
    };

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string path) =>
        user.Client.DeleteAsync(new Uri(path, UriKind.Relative), Token);

    [Fact]
    public async Task The_owner_writes_the_health_of_a_pet_with_the_identifier_of_the_app_and_repeating_it_creates_nothing_else()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await PetAsync(ana);
        var eventId = Guid.CreateVersion7();
        var appointment = Guid.CreateVersion7();

        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies());
        using var again = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies());
        using var visit = await ana.Client.PutJsonAsync(
            $"/api/v1/pets/{petId}/health/{appointment}",
            new { type = "VetVisit", title = "Revisión", dueOn = "2026-12-02", dueAt = "17:30", reminder = "OnTheDay" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe($"/api/v1/pets/{petId}/health/{eventId}");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        visit.StatusCode.ShouldBe(HttpStatusCode.Created);
        var vaccine = await created.JsonAsync();
        vaccine.GetProperty("title").GetString().ShouldBe("Rabia");
        vaccine.GetProperty("dueOn").GetString().ShouldBe("2027-01-15");
        vaccine.GetProperty("dueAt").ValueKind.ShouldBe(JsonValueKind.Null);
        vaccine.GetProperty("doneOn").ValueKind.ShouldBe(JsonValueKind.Null);
        vaccine.GetProperty("repeat").GetProperty("unit").GetString().ShouldBe("Years");
        vaccine.GetProperty("reminder").GetString().ShouldBe("DayBefore");
        (await visit.JsonAsync()).GetProperty("dueAt").GetString().ShouldBe("17:30");

        var items = (await ana.Client.GetJsonAsync($"/api/v1/pets/{petId}/health")).GetProperty("items");
        items.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([appointment, eventId]);
    }

    [Fact]
    public async Task Nobody_but_the_owner_sees_or_writes_the_health_of_a_pet()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        using var bea = await TestUser.RegisterAsync(api, "bea@example.com");
        var petId = await PetAsync(ana);
        var eventId = Guid.CreateVersion7();
        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies());

        using var read = await bea.Client.GetAsync(new Uri($"/api/v1/pets/{petId}/health", UriKind.Relative), Token);
        using var write = await bea.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{Guid.CreateVersion7()}", Rabies());
        using var complete = await bea.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{eventId}/complete", new { doneOn = "2026-03-10" });
        using var delete = await DeleteAsync(bea, $"/api/v1/pets/{petId}/health/{eventId}");
        using var missing = await ana.Client.GetAsync(new Uri($"/api/v1/pets/{Guid.CreateVersion7()}/health", UriKind.Relative), Token);

        foreach (var response in new[] { read, write, complete, delete })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeAsync(response)).ShouldBe("pets.not_owner");
        }

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(missing)).ShouldBe("pets.not_found");
    }

    [Fact]
    public async Task Completing_what_repeats_makes_the_next_one_from_the_day_it_was_done()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await PetAsync(ana);
        var eventId = Guid.CreateVersion7();
        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies(dueOn: "2026-03-01"));

        using var completed = await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{eventId}/complete", new { doneOn = "2026-03-10" });
        using var twice = await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{eventId}/complete", new { doneOn = "2026-03-11" });

        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await completed.JsonAsync();
        result.GetProperty("done").GetProperty("doneOn").GetString().ShouldBe("2026-03-10");
        var next = result.GetProperty("next");
        next.GetProperty("dueOn").GetString().ShouldBe("2027-03-10");
        next.GetProperty("doneOn").ValueKind.ShouldBe(JsonValueKind.Null);
        next.GetProperty("title").GetString().ShouldBe("Rabia");
        next.GetProperty("reminder").GetString().ShouldBe("DayBefore");
        next.GetProperty("notes").ValueKind.ShouldBe(JsonValueKind.Null);

        twice.StatusCode.ShouldBe(HttpStatusCode.OK);
        var repeated = await twice.JsonAsync();
        repeated.GetProperty("done").GetProperty("doneOn").GetString().ShouldBe("2026-03-10");
        repeated.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ana.Client.GetJsonAsync($"/api/v1/pets/{petId}/health")).GetProperty("items").GetArrayLength().ShouldBe(2);

        using var unknown = await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{Guid.CreateVersion7()}/complete", new { doneOn = "2026-03-10" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(unknown)).ShouldBe("pets.health_event_not_found");
    }

    [Fact]
    public async Task A_repetition_that_ends_makes_no_next_one_after_its_end()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await PetAsync(ana);
        var eventId = Guid.CreateVersion7();
        var course = new { every = 1, unit = "Days", until = "2026-03-11" };
        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies(dueOn: "2026-03-10", repeat: course));

        var first = await (await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{eventId}/complete", new { doneOn = "2026-03-10" })).JsonAsync();
        var nextId = first.GetProperty("next").GetProperty("id").GetGuid();
        var last = await (await ana.Client.PostJsonAsync($"/api/v1/pets/{petId}/health/{nextId}/complete", new { doneOn = "2026-03-11" })).JsonAsync();

        first.GetProperty("next").GetProperty("dueOn").GetString().ShouldBe("2026-03-11");
        last.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task What_is_not_valid_says_which_field()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await PetAsync(ana);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        async Task<string[]> FieldsAsync(object body)
        {
            using var response = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{Guid.CreateVersion7()}", body);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return (await response.JsonAsync()).GetProperty("errors").EnumerateObject().Select(error => error.Name).ToArray();
        }

        (await FieldsAsync(new { type = "Vaccine", title = " ", dueOn = "2027-01-15", reminder = "None" })).ShouldContain("title");
        (await FieldsAsync(new { type = "Surgery", title = "Algo", dueOn = "2027-01-15", reminder = "None" })).ShouldContain("type");
        (await FieldsAsync(new { type = "VetVisit", title = "Cita", dueOn = "2027-01-15", dueAt = "25:00", reminder = "None" })).ShouldContain("dueAt");
        (await FieldsAsync(new { type = "Vaccine", title = "Rabia", dueOn = "2027-01-15", doneOn = future, reminder = "None" })).ShouldContain("doneOn");
        (await FieldsAsync(new { type = "Vaccine", title = "Rabia", dueOn = "2050-01-15", reminder = "None" })).ShouldContain("dueOn");
        (await FieldsAsync(Rabies(repeat: new { every = 1, unit = "Years", until = "2026-01-01" }))).ShouldContain("repeat.until");
        (await FieldsAsync(Rabies(repeat: new { every = 0, unit = "Years" }))).ShouldContain("repeat.every");
        (await FieldsAsync(new { type = "Vaccine", title = "Rabia", dueOn = "2027-01-15", reminder = "Sometimes" })).ShouldContain("reminder");
    }

    [Fact]
    public async Task An_identifier_of_another_pet_is_not_taken()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var luna = await PetAsync(ana);
        var toby = await PetAsync(ana, "Toby");
        var eventId = Guid.CreateVersion7();
        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{luna}/health/{eventId}", Rabies());

        using var taken = await ana.Client.PutJsonAsync($"/api/v1/pets/{toby}/health/{eventId}", Rabies());

        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(taken)).ShouldBe("pets.health_event_id_taken");
        (await ana.Client.GetJsonAsync($"/api/v1/pets/{toby}/health")).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_is_idempotent_and_deleting_the_pet_takes_its_health()
    {
        await using var api = await ApiFactory.WithFreshDatabaseAsync(infrastructure);
        using var ana = await TestUser.RegisterAsync(api, "ana@example.com");
        var petId = await PetAsync(ana);
        var eventId = Guid.CreateVersion7();
        using var created = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{eventId}", Rabies());
        using var other = await ana.Client.PutJsonAsync($"/api/v1/pets/{petId}/health/{Guid.CreateVersion7()}", Rabies(dueOn: "2027-06-01"));

        using var deleted = await DeleteAsync(ana, $"/api/v1/pets/{petId}/health/{eventId}");
        using var deletedAgain = await DeleteAsync(ana, $"/api/v1/pets/{petId}/health/{eventId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deletedAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ana.Client.GetJsonAsync($"/api/v1/pets/{petId}/health")).GetProperty("items").GetArrayLength().ShouldBe(1);

        using var petDeleted = await DeleteAsync(ana, $"/api/v1/pets/{petId}");
        petDeleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var scope = api.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PetsDbContext>().HealthEvents.CountAsync(Token)).ShouldBe(0);
    }
}
