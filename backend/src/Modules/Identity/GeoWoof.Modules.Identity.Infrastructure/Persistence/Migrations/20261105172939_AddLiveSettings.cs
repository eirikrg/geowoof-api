using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "hide_start_distance_m",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<string>(
                name: "live_visibility",
                schema: "identity",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hide_start_distance_m",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "live_visibility",
                schema: "identity",
                table: "users");
        }
    }
}
