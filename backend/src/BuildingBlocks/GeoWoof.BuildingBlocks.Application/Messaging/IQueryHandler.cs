using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.BuildingBlocks.Application.Messaging;

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}
