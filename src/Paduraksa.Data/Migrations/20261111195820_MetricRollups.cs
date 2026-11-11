using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Subject = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Minute = table.Column<long>(type: "INTEGER", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    Status1xx = table.Column<long>(type: "INTEGER", nullable: false),
                    Status2xx = table.Column<long>(type: "INTEGER", nullable: false),
                    Status3xx = table.Column<long>(type: "INTEGER", nullable: false),
                    Status4xx = table.Column<long>(type: "INTEGER", nullable: false),
                    Status5xx = table.Column<long>(type: "INTEGER", nullable: false),
                    NoResponse = table.Column<long>(type: "INTEGER", nullable: false),
                    Refused = table.Column<long>(type: "INTEGER", nullable: false),
                    Errors = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesOut = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalMicrosSum = table.Column<long>(type: "INTEGER", nullable: false),
                    UpstreamMicrosSum = table.Column<long>(type: "INTEGER", nullable: false),
                    UpstreamTimed = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalP50 = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalP95 = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalP99 = table.Column<int>(type: "INTEGER", nullable: false),
                    UpstreamP50 = table.Column<int>(type: "INTEGER", nullable: false),
                    UpstreamP95 = table.Column<int>(type: "INTEGER", nullable: false),
                    UpstreamP99 = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollups", x => x.Id);
                    table.CheckConstraint("CK_MetricRollups_Subject", "\"Subject\" IN (0, 1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_Minute",
                table: "MetricRollups",
                column: "Minute");

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_Subject_SubjectKey_Minute",
                table: "MetricRollups",
                columns: new[] { "Subject", "SubjectKey", "Minute" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricRollups");
        }
    }
}
