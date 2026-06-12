using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestController.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedAdmin : Migration
    {
        // Initial admin password: "Admin@InitP4ss!" — MustChangePassword forces reset on first login.
        // Hash generated with BCrypt cost factor 12 (matches PasswordHasher).
        private const string AdminUserId = "00000000-0000-0000-0000-000000000001";
        private const string AdminPasswordHash =
            "$2a$12$LJ3m4sFKDJOiVuMnOx3vkOhCfGv9U0oVz0pXF1qpJd9.kVLJSd5Pu";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "UserId", "Username", "Email", "Role", "PasswordHash", "MustChangePassword", "IsActive", "CreatedUtc", "CreatedByUserId" },
                values: new object[] { AdminUserId, "admin", "admin@localhost", 0 /* Administrator */, AdminPasswordHash, true, true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null! });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "UserId",
                keyValue: AdminUserId);
        }
    }
}
