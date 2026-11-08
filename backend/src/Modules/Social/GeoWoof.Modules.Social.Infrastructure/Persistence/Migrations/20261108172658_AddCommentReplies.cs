using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Social.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "social",
                table: "comments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_id",
                schema: "social",
                table: "comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_comments_parent_id_created_at_id",
                schema: "social",
                table: "comments",
                columns: new[] { "parent_id", "created_at", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_comments_comments_parent_id",
                schema: "social",
                table: "comments",
                column: "parent_id",
                principalSchema: "social",
                principalTable: "comments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_comments_comments_parent_id",
                schema: "social",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_comments_parent_id_created_at_id",
                schema: "social",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "social",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "parent_id",
                schema: "social",
                table: "comments");
        }
    }
}
