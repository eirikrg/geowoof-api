using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace GeoWoof.Modules.Walks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWalks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "walks");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "walks",
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
                schema: "walks",
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
                name: "walks",
                schema: "walks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    distance_m = table.Column<int>(type: "integer", nullable: false),
                    duration_s = table.Column<int>(type: "integer", nullable: false),
                    moving_time_s = table.Column<int>(type: "integer", nullable: false),
                    average_speed_mps = table.Column<double>(type: "double precision", nullable: false),
                    pace_seconds_per_km = table.Column<int>(type: "integer", nullable: true),
                    stats_version = table.Column<int>(type: "integer", nullable: false),
                    track = table.Column<string>(type: "jsonb", nullable: false),
                    preview_polylines = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    route = table.Column<MultiLineString>(type: "geography (MultiLineString, 4326)", nullable: false),
                    start_point = table.Column<Point>(type: "geography (Point, 4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_walks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "walk_pets",
                schema: "walks",
                columns: table => new
                {
                    pet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    walk_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_walk_pets", x => new { x.walk_id, x.pet_id });
                    table.ForeignKey(
                        name: "fk_walk_pets_walks_walk_id",
                        column: x => x.walk_id,
                        principalSchema: "walks",
                        principalTable: "walks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "walk_photos",
                schema: "walks",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    walk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_walk_photos", x => new { x.walk_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_walk_photos_walks_walk_id",
                        column: x => x.walk_id,
                        principalSchema: "walks",
                        principalTable: "walks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "walks",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL AND failed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_walk_pets_pet_id",
                schema: "walks",
                table: "walk_pets",
                column: "pet_id");

            migrationBuilder.CreateIndex(
                name: "ix_walks_owner_id_started_at_id",
                schema: "walks",
                table: "walks",
                columns: new[] { "owner_id", "started_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "walks");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "walks");

            migrationBuilder.DropTable(
                name: "walk_pets",
                schema: "walks");

            migrationBuilder.DropTable(
                name: "walk_photos",
                schema: "walks");

            migrationBuilder.DropTable(
                name: "walks",
                schema: "walks");
        }
    }
}
