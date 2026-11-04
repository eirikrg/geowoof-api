using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "avatar_asset_id",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bio",
                schema: "identity",
                table: "users",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                schema: "identity",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_asset_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "bio",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "city",
                schema: "identity",
                table: "users");
        }
    }
}
