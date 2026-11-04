using System.Text.Json;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Presentation.Errors;
using GeoWoof.BuildingBlocks.Presentation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.BuildingBlocks.UnitTests.Presentation;

public sealed class ResultHttpExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unprocessable, 422)]
    [InlineData(ErrorType.TooManyRequests, 429)]
    [InlineData(ErrorType.Unavailable, 503)]
    [InlineData(ErrorType.Failure, 500)]
    public void Error_types_map_to_status_codes(ErrorType type, int expectedStatus)
    {
        ResultHttpExtensions.StatusCodeFor(type).ShouldBe(expectedStatus);
    }

    [Fact]
    public async Task Failure_is_written_as_problem_details_with_code_and_trace_id()
    {
        var result = Result.Failure<string>(Error.NotFound("pets.not_found", "The pet does not exist."));

        var (status, contentType, body) = await ExecuteAsync(result.ToHttpResult(TypedResults.Ok));

        status.ShouldBe(404);
        contentType.ShouldStartWith("application/problem+json");
        body.GetProperty("code").GetString().ShouldBe("pets.not_found");
        body.GetProperty("detail").GetString().ShouldBe("The pet does not exist.");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Validation_failure_includes_field_errors()
    {
        var error = Error.Validation(
            "validation.failed",
            "One or more validation errors occurred.",
            new Dictionary<string, string[]> { ["name"] = ["'Name' must not be empty."] });

        var (status, _, body) = await ExecuteAsync(Result.Failure(error).ToHttpResult());

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("'Name' must not be empty.");
    }

    [Fact]
    public async Task Errors_with_a_retry_delay_send_the_retry_after_header()
    {
        var error = Error.TooManyRequests("auth.code_cooldown", "Wait.", TimeSpan.FromSeconds(41.2));

        var (status, _, body, headers) = await ExecuteWithHeadersAsync(Result.Failure(error).ToHttpResult());

        status.ShouldBe(429);
        headers.RetryAfter.ToString().ShouldBe("42");
        body.GetProperty("code").GetString().ShouldBe("auth.code_cooldown");
    }

    [Fact]
    public async Task Success_without_value_is_no_content()
    {
        var (status, _, _) = await ExecuteAsync(Result.Success().ToHttpResult());

        status.ShouldBe(204);
    }

    [Theory]
    [InlineData(400, ErrorCodes.InvalidRequest)]
    [InlineData(401, ErrorCodes.Unauthorized)]
    [InlineData(404, ErrorCodes.NotFound)]
    [InlineData(405, ErrorCodes.MethodNotAllowed)]
    [InlineData(429, ErrorCodes.RateLimited)]
    [InlineData(500, ErrorCodes.InternalError)]
    [InlineData(503, ErrorCodes.Unavailable)]
    [InlineData(418, ErrorCodes.Failed)]
    public void Bare_status_codes_get_a_default_code(int status, string expectedCode)
    {
        ErrorCodes.ForStatus(status).ShouldBe(expectedCode);
    }

    internal static async Task<(int Status, string ContentType, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var (status, contentType, body, _) = await ExecuteWithHeadersAsync(result);
        return (status, contentType, body);
    }

    internal static async Task<(int Status, string ContentType, JsonElement Body, IHeaderDictionary Headers)> ExecuteWithHeadersAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/v1/pets/1";
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var json = body.Length == 0 ? default : (await JsonDocument.ParseAsync(body)).RootElement.Clone();
        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, json, context.Response.Headers);
    }
}
