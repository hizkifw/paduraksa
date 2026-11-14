using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollupRefusals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Dropped",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "NoRoute",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedAccessList",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedConnection",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedForwardAuth",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedOther",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedProofOfWork",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedRateLimit",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedRequestLimits",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RefusedRuleSet",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Unavailable",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UnknownHost",
                table: "MetricRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dropped",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "NoRoute",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedAccessList",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedConnection",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedForwardAuth",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedOther",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedProofOfWork",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedRateLimit",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedRequestLimits",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "RefusedRuleSet",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "Unavailable",
                table: "MetricRollups");

            migrationBuilder.DropColumn(
                name: "UnknownHost",
                table: "MetricRollups");
        }
    }
}
