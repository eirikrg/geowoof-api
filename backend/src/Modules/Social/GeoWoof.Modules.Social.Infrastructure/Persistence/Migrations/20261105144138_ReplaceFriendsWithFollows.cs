using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Social.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceFriendsWithFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "follow_requests",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follow_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "follows",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    follower_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_follow_requests_requester_id_status_created_at_id",
                schema: "social",
                table: "follow_requests",
                columns: new[] { "requester_id", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_follow_requests_requester_id_target_id",
                schema: "social",
                table: "follow_requests",
                columns: new[] { "requester_id", "target_id" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_follow_requests_target_id_status_created_at_id",
                schema: "social",
                table: "follow_requests",
                columns: new[] { "target_id", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_follows_followee_id_created_at_id",
                schema: "social",
                table: "follows",
                columns: new[] { "followee_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_follows_follower_id_created_at_id",
                schema: "social",
                table: "follows",
                columns: new[] { "follower_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_follows_follower_id_followee_id",
                schema: "social",
                table: "follows",
                columns: new[] { "follower_id", "followee_id" },
                unique: true);

            // Friends follow each other, and all profiles are public so far: a pending request becomes a follow.
            migrationBuilder.Sql("""
                INSERT INTO social.follows (id, follower_id, followee_id, created_at)
                SELECT gen_random_uuid(), user_a_id, user_b_id, created_at FROM social.friendships
                UNION ALL
                SELECT gen_random_uuid(), user_b_id, user_a_id, created_at FROM social.friendships
                UNION ALL
                SELECT gen_random_uuid(), requester_id, addressee_id, created_at FROM social.friend_requests WHERE status = 'Pending'
                ON CONFLICT (follower_id, followee_id) DO NOTHING;
                """);

            migrationBuilder.DropTable(
                name: "friend_requests",
                schema: "social");

            migrationBuilder.DropTable(
                name: "friendships",
                schema: "social");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follow_requests",
                schema: "social");

            migrationBuilder.DropTable(
                name: "follows",
                schema: "social");

            migrationBuilder.CreateTable(
                name: "friend_requests",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    addressee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    user_high_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_low_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_a_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_b_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friendships", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_addressee_id_status_created_at_id",
                schema: "social",
                table: "friend_requests",
                columns: new[] { "addressee_id", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_requester_id_status_created_at_id",
                schema: "social",
                table: "friend_requests",
                columns: new[] { "requester_id", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_user_low_id_user_high_id",
                schema: "social",
                table: "friend_requests",
                columns: new[] { "user_low_id", "user_high_id" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_friendships_user_a_id_user_b_id",
                schema: "social",
                table: "friendships",
                columns: new[] { "user_a_id", "user_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_friendships_user_b_id",
                schema: "social",
                table: "friendships",
                column: "user_b_id");
        }
    }
}
