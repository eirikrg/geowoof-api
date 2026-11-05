using System.ComponentModel.DataAnnotations.Schema;
using GeoWoof.BuildingBlocks.Application.Paging;
using Microsoft.EntityFrameworkCore;

namespace GeoWoof.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Searches with pg_trgm and unaccent. Each module has its own <c>normalize_search_text</c> function
/// and its own GIN indexes, because modules do not share database objects (docs/architecture.md, 3.6).
/// </summary>
public static class TextSearch
{
    /// <summary>Low enough to forgive a typo ("luan" finds "Luna"), and high enough not to find unrelated names.</summary>
    private const string WordSimilarityThreshold = "0.35";

    /// <summary>A pattern for LIKE that finds the text anywhere: what the user typed must not act as a wildcard.</summary>
    public static string ContainsPattern(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }

    /// <summary>
    /// Runs a query that returns the columns <c>id</c> and <c>score</c>. The threshold of the
    /// <c>&lt;%</c> operator is a setting of the connection, so it applies inside a transaction of its own.
    /// </summary>
    public static async Task<IReadOnlyList<RelevancePosition>> RunAsync(DbContext dbContext, FormattableString sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.word_similarity_threshold', {WordSimilarityThreshold}, true)", cancellationToken);
        var rows = await dbContext.Database.SqlQuery<SearchRow>(sql).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(row => new RelevancePosition(row.Score, row.Id)).ToList();
    }

    private sealed class SearchRow
    {
        [Column("id")]
        public Guid Id { get; init; }

        [Column("score")]
        public decimal Score { get; init; }
    }
}
