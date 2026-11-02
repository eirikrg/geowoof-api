using FluentValidation;
using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.BuildingBlocks.Application.Behaviors;

/// <summary>Runs FluentValidation validators and converts failures into a single validation <see cref="Error"/>.</summary>
internal static class RequestValidation
{
    public const string ErrorCode = "validation.failed";

    public const string ErrorMessage = "One or more validation errors occurred.";

    public static async Task<Error?> ValidateAsync<TRequest>(
        IEnumerable<IValidator<TRequest>> validators,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var fieldErrors = failures
            .GroupBy(failure => ToCamelCasePath(failure.PropertyName), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return Error.Validation(ErrorCode, ErrorMessage, fieldErrors);
    }

    /// <summary>Converts <c>Owner.FirstName</c> or <c>Items[0].Name</c> into <c>owner.firstName</c> or <c>items[0].name</c>.</summary>
    internal static string ToCamelCasePath(string propertyPath)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return string.Empty;
        }

        var segments = propertyPath.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = string.Concat(char.ToLowerInvariant(segment[0]).ToString(), segment.AsSpan(1));
            }
        }

        return string.Join('.', segments);
    }
}
