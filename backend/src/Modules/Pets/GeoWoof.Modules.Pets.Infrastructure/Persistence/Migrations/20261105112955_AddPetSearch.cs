using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Pets.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPetSearch : Migration
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
                CREATE FUNCTION pets.normalize_search_text(value text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $$ SELECT lower(public.unaccent('public.unaccent'::regdictionary, value)) $$;
                """);
            migrationBuilder.Sql("""
                CREATE INDEX ix_pets_search_name ON pets.pets USING gin (pets.normalize_search_text(name) public.gin_trgm_ops);
                CREATE INDEX ix_pets_search_breed ON pets.pets USING gin (pets.normalize_search_text(breed) public.gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX pets.ix_pets_search_breed;
                DROP INDEX pets.ix_pets_search_name;
                DROP FUNCTION pets.normalize_search_text(text);
                """);

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
