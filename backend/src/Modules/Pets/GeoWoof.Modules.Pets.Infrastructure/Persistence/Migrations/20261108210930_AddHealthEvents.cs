using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Pets.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_events",
                schema: "pets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    done_on = table.Column<DateOnly>(type: "date", nullable: true),
                    repeat_every = table.Column<int>(type: "integer", nullable: true),
                    repeat_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    repeat_until = table.Column<DateOnly>(type: "date", nullable: true),
                    reminder = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_events_pets_pet_id",
                        column: x => x.pet_id,
                        principalSchema: "pets",
                        principalTable: "pets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_events_pet_id_due_on",
                schema: "pets",
                table: "health_events",
                columns: new[] { "pet_id", "due_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_events",
                schema: "pets");
        }
    }
}
