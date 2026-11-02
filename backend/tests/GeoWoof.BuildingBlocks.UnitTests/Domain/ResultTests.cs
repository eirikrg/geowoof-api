using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.BuildingBlocks.UnitTests.Domain;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.NotFound("pets.not_found", "The pet does not exist.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_exposes_its_error()
    {
        Result result = SampleError;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Successful_value_result_exposes_its_value()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failed_value_result_has_no_value()
    {
        Result<int> result = SampleError;

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("pets.not_found");
        var exception = Should.Throw<InvalidOperationException>(() => result.Value);
        exception.Message.ShouldContain("pets.not_found");
    }

    [Fact]
    public void Static_factories_build_the_expected_results()
    {
        Result.Success("walk").Value.ShouldBe("walk");
        Result.Failure<string>(SampleError).Error.ShouldBe(SampleError);
        Result.Failure(SampleError).Error.ShouldBe(SampleError);
    }

    [Theory]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unprocessable)]
    public void Error_factories_set_the_error_type(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("x.invalid", "Invalid."),
            ErrorType.Unauthorized => Error.Unauthorized("x.unauthorized", "Unauthorized."),
            ErrorType.Forbidden => Error.Forbidden("x.forbidden", "Forbidden."),
            ErrorType.NotFound => Error.NotFound("x.not_found", "Not found."),
            ErrorType.Conflict => Error.Conflict("x.conflict", "Conflict."),
            ErrorType.Unprocessable => Error.Unprocessable("x.unprocessable", "Unprocessable."),
            _ => Error.Failure("x.failure", "Failure."),
        };

        error.Type.ShouldBe(type);
    }

    [Fact]
    public void Error_requires_code_and_message()
    {
        Should.Throw<ArgumentException>(() => Error.Failure(" ", "Message."));
        Should.Throw<ArgumentException>(() => Error.Failure("code", ""));
    }
}
