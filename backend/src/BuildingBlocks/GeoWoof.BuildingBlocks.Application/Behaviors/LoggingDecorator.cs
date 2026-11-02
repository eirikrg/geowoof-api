using System.Diagnostics;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using Microsoft.Extensions.Logging;

namespace GeoWoof.BuildingBlocks.Application.Behaviors;

/// <summary>Outermost decorator: traces every request and logs its outcome and duration.</summary>
internal static partial class LoggingDecorator
{
    internal sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<CommandHandler<TCommand>> logger) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken) =>
            Observe(typeof(TCommand).Name, logger, () => inner.Handle(command, cancellationToken));
    }

    internal sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<CommandHandler<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken) =>
            Observe(typeof(TCommand).Name, logger, () => inner.Handle(command, cancellationToken));
    }

    internal sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<QueryHandler<TQuery, TResponse>> logger) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken) =>
            Observe(typeof(TQuery).Name, logger, () => inner.Handle(query, cancellationToken));
    }

    private static async Task<TResult> Observe<TResult>(string requestName, ILogger logger, Func<Task<TResult>> next)
        where TResult : Result
    {
        using var activity = ApplicationDiagnostics.ActivitySource.StartActivity(requestName);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await next();
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            if (result.IsSuccess)
            {
                LogSucceeded(logger, requestName, elapsedMs);
            }
            else
            {
                activity?.SetTag("geowoof.error.code", result.Error.Code);
                LogFailed(logger, requestName, result.Error.Code, elapsedMs);
            }

            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            LogThrew(logger, exception, requestName, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{RequestName} succeeded in {ElapsedMs:0.0} ms")]
    private static partial void LogSucceeded(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RequestName} failed with {ErrorCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "{RequestName} threw after {ElapsedMs:0.0} ms")]
    private static partial void LogThrew(ILogger logger, Exception exception, string requestName, double elapsedMs);
}
