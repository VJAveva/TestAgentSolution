using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestController.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationCooldowns",
                columns: table => new
                {
                    CooldownId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Target = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastSentUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationCooldowns", x => x.CooldownId);
                });

            migrationBuilder.CreateTable(
                name: "NotificationMutes",
                columns: table => new
                {
                    MuteId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Target = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MutedByUserId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    MutedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationMutes", x => x.MuteId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationCooldowns_Target_TargetType",
                table: "NotificationCooldowns",
                columns: new[] { "Target", "TargetType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMutes_Target_TargetType",
                table: "NotificationMutes",
                columns: new[] { "Target", "TargetType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationCooldowns");

            migrationBuilder.DropTable(
                name: "NotificationMutes");
        }
    }
}
