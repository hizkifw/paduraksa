using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Subject = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Minute = table.Column<long>(type: "INTEGER", nullable: false),
                    CoveredSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengesServed = table.Column<long>(type: "INTEGER", nullable: false),
                    Solved = table.Column<long>(type: "INTEGER", nullable: false),
                    VerifyRejected = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockedNonBrowser = table.Column<long>(type: "INTEGER", nullable: false),
                    TokenMalformed = table.Column<long>(type: "INTEGER", nullable: false),
                    TokenBadTag = table.Column<long>(type: "INTEGER", nullable: false),
                    TokenExpired = table.Column<long>(type: "INTEGER", nullable: false),
                    TokenTooEasy = table.Column<long>(type: "INTEGER", nullable: false),
                    RateLimitAllowed = table.Column<long>(type: "INTEGER", nullable: false),
                    RateLimited = table.Column<long>(type: "INTEGER", nullable: false),
                    AddressRefused = table.Column<long>(type: "INTEGER", nullable: false),
                    Unauthorized = table.Column<long>(type: "INTEGER", nullable: false),
                    PasswordCacheHits = table.Column<long>(type: "INTEGER", nullable: false),
                    Verifications = table.Column<long>(type: "INTEGER", nullable: false),
                    Queued = table.Column<long>(type: "INTEGER", nullable: false),
                    QueueFull = table.Column<long>(type: "INTEGER", nullable: false),
                    QueueTimeouts = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectionsDenied = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectionsOverLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectionsUncounted = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInAllowed = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInDenied = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInRedirected = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInCacheHits = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInCalls = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInErrors = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInTimeouts = table.Column<long>(type: "INTEGER", nullable: false),
                    SignInBusy = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesDenied = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesDropped = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesAllowed = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesTimeouts = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityRollups", x => x.Id);
                    table.CheckConstraint("CK_SecurityRollups_Subject", "\"Subject\" IN (0, 1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRollups_Minute",
                table: "SecurityRollups",
                column: "Minute");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityRollups_Subject_SubjectKey_Minute",
                table: "SecurityRollups",
                columns: new[] { "Subject", "SubjectKey", "Minute" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityRollups");
        }
    }
}
