using GeoWoof.BuildingBlocks.Infrastructure.Persistence;

namespace GeoWoof.BuildingBlocks.UnitTests.Infrastructure;

public sealed class TextSearchTests
{
    [Theory]
    [InlineData("luna", "%luna%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("a\\b", "%a\\\\b%")]
    public void What_the_user_types_is_never_a_wildcard(string text, string expected)
    {
        TextSearch.ContainsPattern(text).ShouldBe(expected);
    }
}
