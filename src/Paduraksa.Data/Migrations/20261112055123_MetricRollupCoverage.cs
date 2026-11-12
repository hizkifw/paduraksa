using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollupCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows from before coverage was recorded are minutes that had traffic: the process was there for them, so they count as whole.
            migrationBuilder.AddColumn<int>(
                name: "CoveredSeconds",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoveredSeconds",
                table: "MetricRollups");
        }
    }
}
