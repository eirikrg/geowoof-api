using System.Security.Claims;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Presentation.Results;
using GeoWoof.BuildingBlocks.Presentation.Security;
using GeoWoof.Modules.Pets.Application;
using GeoWoof.Modules.Pets.Application.Pets;
using GeoWoof.Modules.Pets.Application.Weights;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GeoWoof.Modules.Pets.Presentation;

public sealed record PetRequest(
    string Name,
    string Species,
    string? SpeciesOther,
    string? Breed,
    DateOnly? BirthDate,
    string Sex,
    string? Size,
    string? Color,
    bool Neutered,
    string? Microchip,
    Guid? AvatarAssetId,
    string? Bio)
{
    internal PetInput ToInput() => new(Name, Species, SpeciesOther, Breed, BirthDate, Sex, Size, Color, Neutered, Microchip, AvatarAssetId, Bio);
}

public sealed record WeightRequest(DateOnly MeasuredOn, decimal WeightKg);

/// <summary>Minimal API endpoints of the Pets module, mapped under <c>/api/v1</c>.</summary>
public static class PetsEndpoints
{
    public static IEndpointRouteBuilder MapPetsEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var pets = api.MapGroup("/pets").WithTags("Pets");
        api.MapGet("/users/{id:guid}/pets", GetUserPetsAsync).WithTags("Pets")
            .Produces<PagedResponse<PetDto>>().ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/search/pets", SearchPetsAsync).WithTags("Search")
            .Produces<PagedResponse<PetSearchResultDto>>().ProducesValidationProblem();

        pets.MapGet(string.Empty, GetPetsAsync).Produces<PagedResponse<PetDto>>();
        pets.MapPost(string.Empty, CreatePetAsync)
            .Produces<PetDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        pets.MapGet("/{id:guid}", GetPetAsync).Produces<PetDto>().ProducesProblem(StatusCodes.Status404NotFound);
        pets.MapPut("/{id:guid}", UpdatePetAsync)
            .Produces<PetDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        pets.MapDelete("/{id:guid}", DeletePetAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        var weights = pets.MapGroup("/{id:guid}/weights");
        weights.MapGet(string.Empty, GetWeightsAsync)
            .Produces<PagedResponse<WeightEntryDto>>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        weights.MapPost(string.Empty, AddWeightAsync)
            .Produces<WeightEntryDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        weights.MapPut("/{weightId:guid}", UpdateWeightAsync)
            .Produces<WeightEntryDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        weights.MapDelete("/{weightId:guid}", DeleteWeightAsync)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> GetUserPetsAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetUserPetsQuery, PagedResponse<PetDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetUserPetsQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> SearchPetsAsync(
        string? q,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<SearchPetsQuery, PagedResponse<PetSearchResultDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new SearchPetsQuery(user.GetUserId(), q ?? string.Empty, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> GetPetsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetPetsQuery, PagedResponse<PetDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPetsQuery(user.GetUserId()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> CreatePetAsync(
        PetRequest request,
        ClaimsPrincipal user,
        ICommandHandler<CreatePetCommand, PetDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new CreatePetCommand(user.GetUserId(), request.ToInput()), cancellationToken))
            .ToHttpResult(pet => TypedResults.Created($"/api/v1/pets/{pet.Id}", pet));

    private static async Task<IResult> GetPetAsync(
        Guid id,
        ClaimsPrincipal user,
        IQueryHandler<GetPetQuery, PetDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPetQuery(user.GetUserId(), id), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> UpdatePetAsync(
        Guid id,
        PetRequest request,
        ClaimsPrincipal user,
        ICommandHandler<UpdatePetCommand, PetDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UpdatePetCommand(user.GetUserId(), id, request.ToInput()), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> DeletePetAsync(
        Guid id,
        ClaimsPrincipal user,
        ICommandHandler<DeletePetCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeletePetCommand(user.GetUserId(), id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> GetWeightsAsync(
        Guid id,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetWeightsQuery, PagedResponse<WeightEntryDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetWeightsQuery(user.GetUserId(), id, limit, cursor), cancellationToken)).ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> AddWeightAsync(
        Guid id,
        WeightRequest request,
        ClaimsPrincipal user,
        ICommandHandler<AddWeightCommand, WeightEntryDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new AddWeightCommand(user.GetUserId(), id, request.MeasuredOn, request.WeightKg), cancellationToken))
            .ToHttpResult(entry => TypedResults.Created($"/api/v1/pets/{id}/weights/{entry.Id}", entry));

    private static async Task<IResult> UpdateWeightAsync(
        Guid id,
        Guid weightId,
        WeightRequest request,
        ClaimsPrincipal user,
        ICommandHandler<UpdateWeightCommand, WeightEntryDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new UpdateWeightCommand(user.GetUserId(), id, weightId, request.MeasuredOn, request.WeightKg), cancellationToken))
            .ToHttpResult(TypedResults.Ok);

    private static async Task<IResult> DeleteWeightAsync(
        Guid id,
        Guid weightId,
        ClaimsPrincipal user,
        ICommandHandler<DeleteWeightCommand> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteWeightCommand(user.GetUserId(), id, weightId), cancellationToken)).ToHttpResult();
}
