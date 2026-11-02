using System.Text.Json;
using GeoWoof.BuildingBlocks.Presentation.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoWoof.BuildingBlocks.UnitTests.Presentation;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task Unhandled_exception_becomes_a_500_without_internal_details()
    {
        var (handled, status, body) = await HandleAsync(new InvalidOperationException("connection string secret=hunter2"));

        handled.ShouldBeTrue();
        status.ShouldBe(500);
        body.GetProperty("code").GetString().ShouldBe(ErrorCodes.InternalError);
        body.GetRawText().ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task Malformed_request_keeps_its_client_error_status()
    {
        var (handled, status, body) = await HandleAsync(new BadHttpRequestException("Invalid JSON.", StatusCodes.Status400BadRequest));

        handled.ShouldBeTrue();
        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe(ErrorCodes.InvalidRequest);
    }

    private static async Task<(bool Handled, int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Accept = "application/json";
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        responseBody.Position = 0;
        var json = (await JsonDocument.ParseAsync(responseBody, cancellationToken: TestContext.Current.CancellationToken)).RootElement.Clone();
        return (handled, context.Response.StatusCode, json);
    }
}
