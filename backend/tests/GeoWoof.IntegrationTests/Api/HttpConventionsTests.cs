using System.Net;
using System.Text.Json;
using GeoWoof.IntegrationTests.Infrastructure;

namespace GeoWoof.IntegrationTests.Api;

public sealed class HttpConventionsTests(TestInfrastructure infrastructure)
{
    [Fact]
    public async Task Unknown_routes_return_problem_details_with_a_stable_code()
    {
        await using var factory = new ApiFactory(infrastructure);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        body.GetProperty("status").GetInt32().ShouldBe(404);
        body.GetProperty("code").GetString().ShouldBe("request.not_found");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Documentation_is_served_when_enabled()
    {
        await using var factory = new ApiFactory(infrastructure, new Dictionary<string, string?> { ["Api:ExposeDocumentation"] = "true" });
        using var client = factory.CreateClient();

        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var scalar = await client.GetAsync(new Uri("/scalar", UriKind.Relative), TestContext.Current.CancellationToken);

        openApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await openApi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("GeoWoof API");
        scalar.StatusCode.ShouldBe(HttpStatusCode.OK);
        scalar.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task Documentation_is_hidden_outside_development_by_default()
    {
        await using var factory = new ApiFactory(infrastructure);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
