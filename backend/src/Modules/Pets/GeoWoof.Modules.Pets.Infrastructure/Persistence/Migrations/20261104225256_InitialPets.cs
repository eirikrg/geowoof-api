using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Pets.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pets");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "pets",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.message_id, x.consumer });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "pets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pets",
                schema: "pets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    species = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    species_other = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    breed = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    sex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    size = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    color = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    neutered = table.Column<bool>(type: "boolean", nullable: false),
                    microchip = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    avatar_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bio = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pet_weight_entries",
                schema: "pets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    measured_on = table.Column<DateOnly>(type: "date", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pet_weight_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_pet_weight_entries_pets_pet_id",
                        column: x => x.pet_id,
                        principalSchema: "pets",
                        principalTable: "pets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "pets",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL AND failed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pet_weight_entries_pet_id_measured_on",
                schema: "pets",
                table: "pet_weight_entries",
                columns: new[] { "pet_id", "measured_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pets_owner_id_created_at",
                schema: "pets",
                table: "pets",
                columns: new[] { "owner_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "pets");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "pets");

            migrationBuilder.DropTable(
                name: "pet_weight_entries",
                schema: "pets");

            migrationBuilder.DropTable(
                name: "pets",
                schema: "pets");
        }
    }
}
