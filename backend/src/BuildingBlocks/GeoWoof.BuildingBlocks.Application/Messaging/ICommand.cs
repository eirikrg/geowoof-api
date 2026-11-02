namespace GeoWoof.BuildingBlocks.Application.Messaging;

/// <summary>A request that changes state and returns no value.</summary>
public interface ICommand;

/// <summary>A request that changes state and returns a <typeparamref name="TResponse"/>.</summary>
public interface ICommand<TResponse>;
