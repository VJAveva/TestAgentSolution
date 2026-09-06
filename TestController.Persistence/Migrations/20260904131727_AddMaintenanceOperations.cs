using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestController.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ScriptPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerSource = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LinkedRunId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FailurePhase = table.Column<int>(type: "INTEGER", nullable: true),
                    LogPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceOperations_NodeId_StartedUtc",
                table: "MaintenanceOperations",
                columns: new[] { "NodeId", "StartedUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceOperations_StartedUtc",
                table: "MaintenanceOperations",
                column: "StartedUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceOperations");
        }
    }
}
