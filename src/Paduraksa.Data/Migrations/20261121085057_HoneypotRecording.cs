using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoneypotRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HoneypotRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Time = table.Column<long>(type: "INTEGER", nullable: false),
                    Entry = table.Column<int>(type: "INTEGER", nullable: false),
                    PageKind = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkKind = table.Column<int>(type: "INTEGER", nullable: false),
                    PageKey = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentKey = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Pow = table.Column<int>(type: "INTEGER", nullable: false),
                    SolveMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TlsVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Accept = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcceptLanguage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SecFetchSite = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecFetchMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecFetchDest = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecFetchUser = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Referer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PathTruncated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoneypotRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HoneypotSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    HostKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Canary = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadRobotsFirst = table.Column<bool>(type: "INTEGER", nullable: false),
                    Requests = table.Column<int>(type: "INTEGER", nullable: false),
                    Pages = table.Column<int>(type: "INTEGER", nullable: false),
                    GuessedUrls = table.Column<int>(type: "INTEGER", nullable: false),
                    Images = table.Column<int>(type: "INTEGER", nullable: false),
                    JsRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengesShown = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengesSolved = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengesPassed = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkDirect = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkNormal = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkNoFollow = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkUgc = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkSponsored = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkHidden = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkComment = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkScript = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkBeacon = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkSitemap = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkFeed = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkNoIndexPage = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkImage = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoneypotSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoneypotRequests_SessionId_Id",
                table: "HoneypotRequests",
                columns: new[] { "SessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_HoneypotRequests_Time",
                table: "HoneypotRequests",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_HoneypotSessions_LastSeen",
                table: "HoneypotSessions",
                column: "LastSeen");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoneypotRequests");

            migrationBuilder.DropTable(
                name: "HoneypotSessions");
        }
    }
}
