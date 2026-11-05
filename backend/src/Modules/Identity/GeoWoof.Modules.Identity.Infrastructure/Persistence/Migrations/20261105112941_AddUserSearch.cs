using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            // unaccent is only STABLE, because its dictionary could change. It does not, and an index needs an IMMUTABLE function.
            migrationBuilder.Sql("""
                CREATE FUNCTION identity.normalize_search_text(value text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $$ SELECT lower(public.unaccent('public.unaccent'::regdictionary, value)) $$;
                """);
            migrationBuilder.Sql("""
                CREATE INDEX ix_users_search_display_name ON identity.users USING gin (identity.normalize_search_text(display_name) public.gin_trgm_ops);
                CREATE INDEX ix_users_search_username ON identity.users USING gin (identity.normalize_search_text(username) public.gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX identity.ix_users_search_username;
                DROP INDEX identity.ix_users_search_display_name;
                DROP FUNCTION identity.normalize_search_text(text);
                """);

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
