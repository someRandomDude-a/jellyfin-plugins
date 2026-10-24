using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccountHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscordLinkChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_challenges",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    DiscordId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedBy = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_challenges", x => x.TokenHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discord_challenges_ExpiresAtUnixMs",
                table: "discord_challenges",
                column: "ExpiresAtUnixMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_challenges");
        }
    }
}
