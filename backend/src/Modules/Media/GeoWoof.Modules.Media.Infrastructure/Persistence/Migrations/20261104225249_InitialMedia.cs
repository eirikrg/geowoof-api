using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Media.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "media",
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
                name: "media_assets",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    rejection_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_attached = table.Column<bool>(type: "boolean", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    original_removed = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "media",
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

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_owner_id_created_at",
                schema: "media",
                table: "media_assets",
                columns: new[] { "owner_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_status_created_at",
                schema: "media",
                table: "media_assets",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "media",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL AND failed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "media");

            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "media");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "media");
        }
    }
}
