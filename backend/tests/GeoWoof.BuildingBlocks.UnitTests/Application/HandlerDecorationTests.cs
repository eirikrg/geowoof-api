using FluentValidation;
using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Behaviors;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.BuildingBlocks.UnitTests.Application;

public sealed class HandlerDecorationTests
{
    [Fact]
    public async Task Invalid_command_is_rejected_before_the_handler_runs()
    {
        await using var root = BuildProvider();
        await using var scope = root.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var handler = provider.GetRequiredService<ICommandHandler<RenamePetCommand, string>>();

        var result = await handler.Handle(new RenamePetCommand(new PetName(string.Empty)), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe(RequestValidation.ErrorCode);
        result.Error.FieldErrors.ShouldNotBeNull();
        result.Error.FieldErrors.Keys.ShouldBe(["newName.value"]);
        provider.GetRequiredService<CallCounter>().Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Valid_command_reaches_the_handler()
    {
        await using var root = BuildProvider();
        await using var scope = root.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var handler = provider.GetRequiredService<ICommandHandler<RenamePetCommand, string>>();

        var result = await handler.Handle(new RenamePetCommand(new PetName("Luna")), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("LUNA");
        provider.GetRequiredService<CallCounter>().Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Queries_and_void_commands_are_decorated_too()
    {
        await using var root = BuildProvider();
        await using var scope = root.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var query = await provider.GetRequiredService<IQueryHandler<FindPetQuery, string>>()
            .Handle(new FindPetQuery(0), TestContext.Current.CancellationToken);
        var command = await provider.GetRequiredService<ICommandHandler<ArchivePetCommand>>()
            .Handle(new ArchivePetCommand(string.Empty), TestContext.Current.CancellationToken);

        query.Error.FieldErrors!.Keys.ShouldBe(["id"]);
        command.Error.FieldErrors!.Keys.ShouldBe(["reason"]);
        provider.GetRequiredService<CallCounter>().Calls.ShouldBe(0);
    }

    [Fact]
    public void Decorating_without_any_handler_registered_is_allowed()
    {
        var services = new ServiceCollection();

        Should.NotThrow(() => services.DecorateApplicationHandlers());
    }

    [Theory]
    [InlineData("Name", "name")]
    [InlineData("Owner.FirstName", "owner.firstName")]
    [InlineData("Items[0].Name", "items[0].name")]
    [InlineData("", "")]
    public void Field_paths_are_converted_to_camel_case(string propertyPath, string expected)
    {
        RequestValidation.ToCamelCasePath(propertyPath).ShouldBe(expected);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CallCounter>();
        services.AddApplicationHandlers(typeof(HandlerDecorationTests).Assembly);
        services.DecorateApplicationHandlers();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

public sealed class CallCounter
{
    public int Calls { get; set; }
}

public sealed record PetName(string Value);

public sealed record RenamePetCommand(PetName NewName) : ICommand<string>;

public sealed record ArchivePetCommand(string Reason) : ICommand;

public sealed record FindPetQuery(int Id) : IQuery<string>;

internal sealed class RenamePetCommandValidator : AbstractValidator<RenamePetCommand>
{
    public RenamePetCommandValidator() => RuleFor(command => command.NewName.Value).NotEmpty();
}

internal sealed class ArchivePetCommandValidator : AbstractValidator<ArchivePetCommand>
{
    public ArchivePetCommandValidator() => RuleFor(command => command.Reason).NotEmpty();
}

internal sealed class FindPetQueryValidator : AbstractValidator<FindPetQuery>
{
    public FindPetQueryValidator() => RuleFor(query => query.Id).GreaterThan(0);
}

internal sealed class RenamePetCommandHandler(CallCounter counter) : ICommandHandler<RenamePetCommand, string>
{
    public Task<Result<string>> Handle(RenamePetCommand command, CancellationToken cancellationToken)
    {
        counter.Calls++;
        return Task.FromResult<Result<string>>(command.NewName.Value.ToUpperInvariant());
    }
}

internal sealed class ArchivePetCommandHandler(CallCounter counter) : ICommandHandler<ArchivePetCommand>
{
    public Task<Result> Handle(ArchivePetCommand command, CancellationToken cancellationToken)
    {
        counter.Calls++;
        return Task.FromResult(Result.Success());
    }
}

internal sealed class FindPetQueryHandler(CallCounter counter) : IQueryHandler<FindPetQuery, string>
{
    public Task<Result<string>> Handle(FindPetQuery query, CancellationToken cancellationToken)
    {
        counter.Calls++;
        return Task.FromResult<Result<string>>("pet");
    }
}
