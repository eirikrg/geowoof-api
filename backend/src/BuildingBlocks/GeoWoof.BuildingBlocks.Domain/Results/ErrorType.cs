namespace GeoWoof.BuildingBlocks.Domain.Results;

/// <summary>Category of a business error. Presentation maps it to an HTTP status code.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    Unauthorized = 2,
    Forbidden = 3,
    NotFound = 4,
    Conflict = 5,
    Unprocessable = 6,
}
