using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoWoof.Modules.Social.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posts",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    author_is_private = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_comments_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_likes",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_likes", x => x.id);
                    table.ForeignKey(
                        name: "fk_post_likes_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_media",
                schema: "social",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_media", x => new { x.post_id, x.position });
                    table.ForeignKey(
                        name: "fk_post_media_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_mentions",
                schema: "social",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_mentions", x => new { x.post_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_post_mentions_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_pets",
                schema: "social",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pet_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_pets", x => new { x.post_id, x.pet_id });
                    table.ForeignKey(
                        name: "fk_post_pets_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reposts",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reposts", x => x.id);
                    table.ForeignKey(
                        name: "fk_reposts_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_posts",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_posts", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_posts_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment_mentions",
                schema: "social",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_mentions", x => new { x.comment_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_comment_mentions_comments_comment_id",
                        column: x => x.comment_id,
                        principalSchema: "social",
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comment_mentions_user_id",
                schema: "social",
                table: "comment_mentions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_author_id",
                schema: "social",
                table: "comments",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_post_id_created_at_id",
                schema: "social",
                table: "comments",
                columns: new[] { "post_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_post_likes_post_id_user_id",
                schema: "social",
                table: "post_likes",
                columns: new[] { "post_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_likes_user_id",
                schema: "social",
                table: "post_likes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_mentions_user_id",
                schema: "social",
                table: "post_mentions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_pets_pet_id",
                schema: "social",
                table: "post_pets",
                column: "pet_id");

            migrationBuilder.CreateIndex(
                name: "ix_posts_author_id_created_at_id",
                schema: "social",
                table: "posts",
                columns: new[] { "author_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_explore",
                schema: "social",
                table: "posts",
                columns: new[] { "created_at", "id" },
                filter: "visibility = 'Everyone' AND NOT author_is_private");

            migrationBuilder.CreateIndex(
                name: "ix_reposts_post_id_user_id",
                schema: "social",
                table: "reposts",
                columns: new[] { "post_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reposts_user_id_created_at_id",
                schema: "social",
                table: "reposts",
                columns: new[] { "user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_saved_posts_post_id",
                schema: "social",
                table: "saved_posts",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_posts_user_id_created_at_id",
                schema: "social",
                table: "saved_posts",
                columns: new[] { "user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_saved_posts_user_id_post_id",
                schema: "social",
                table: "saved_posts",
                columns: new[] { "user_id", "post_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comment_mentions",
                schema: "social");

            migrationBuilder.DropTable(
                name: "post_likes",
                schema: "social");

            migrationBuilder.DropTable(
                name: "post_media",
                schema: "social");

            migrationBuilder.DropTable(
                name: "post_mentions",
                schema: "social");

            migrationBuilder.DropTable(
                name: "post_pets",
                schema: "social");

            migrationBuilder.DropTable(
                name: "reposts",
                schema: "social");

            migrationBuilder.DropTable(
                name: "saved_posts",
                schema: "social");

            migrationBuilder.DropTable(
                name: "comments",
                schema: "social");

            migrationBuilder.DropTable(
                name: "posts",
                schema: "social");
        }
    }
}
