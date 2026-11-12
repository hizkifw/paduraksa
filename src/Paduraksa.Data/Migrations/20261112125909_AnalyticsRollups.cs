using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<int>(type: "INTEGER", nullable: false),
                    HostKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Start = table.Column<long>(type: "INTEGER", nullable: false),
                    CoveredSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesOut = table.Column<long>(type: "INTEGER", nullable: false),
                    ServerErrors = table.Column<long>(type: "INTEGER", nullable: false),
                    Visitors = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsRollups", x => x.Id);
                    table.CheckConstraint("CK_AnalyticsRollups_Subject", "\"Subject\" IN (0, 1, 2)");
                    table.CheckConstraint("CK_AnalyticsRollups_Tier", "\"Tier\" IN (0, 1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRollups_Tier_Start",
                table: "AnalyticsRollups",
                columns: new[] { "Tier", "Start" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRollups_Tier_Subject_HostKey_Start",
                table: "AnalyticsRollups",
                columns: new[] { "Tier", "Subject", "HostKey", "Start" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsRollups");
        }
    }
}
