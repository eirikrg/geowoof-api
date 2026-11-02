namespace GeoWoof.BuildingBlocks.Domain.Results;

/// <summary>Outcome of an operation that either succeeds or fails with an <see cref="Results.Error"/>.</summary>
public class Result
{
    private readonly Error? _error;

    protected Result(Error? error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => !IsSuccess;

    /// <summary>The error of a failed result. Accessing it on a successful result is a programming error.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.FromValue(value);

    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.FromError(error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Outcome of an operation that produces a <typeparamref name="TValue"/> when it succeeds.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, Error? error)
        : base(error)
    {
        _value = value;
    }

    /// <summary>The value of a successful result. Accessing it on a failed result is a programming error.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"A failed result has no value (error '{Error.Code}').");

    internal static Result<TValue> FromValue(TValue value) => new(value, null);

    internal static Result<TValue> FromError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(default, error);
    }

    public static implicit operator Result<TValue>(TValue value) => FromValue(value);

    public static implicit operator Result<TValue>(Error error) => FromError(error);
}
