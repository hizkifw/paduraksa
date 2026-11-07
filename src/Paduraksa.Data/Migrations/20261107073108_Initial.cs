using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigRevisions",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ActorKind = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ActorTokenId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Document = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DocumentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigRevisions", x => x.Id);
                    table.CheckConstraint("CK_ConfigRevisions_Actor", "\"ActorKind\" IN ('system', 'cli', 'user', 'token') AND (\"ActorUserId\" IS NOT NULL) = (\"ActorKind\" = 'user') AND (\"ActorTokenId\" IS NOT NULL) = (\"ActorKind\" = 'token')");
                    table.CheckConstraint("CK_ConfigRevisions_Channel", "\"Channel\" IN ('system', 'cli', 'dashboard', 'api', 'mcp')");
                    table.CheckConstraint("CK_ConfigRevisions_DocumentHash", "length(\"DocumentHash\") = 32");
                    table.CheckConstraint("CK_ConfigRevisions_Root", "(\"Seq\" = 1) = (\"ParentId\" IS NULL)");
                    table.CheckConstraint("CK_ConfigRevisions_Seq", "\"Seq\" >= 1");
                    table.ForeignKey(
                        name: "FK_ConfigRevisions_ConfigRevisions_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ConfigRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    ActorKind = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ActorTokenId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    RevisionId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PrevHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Hash = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AuditEvents_Actor", "\"ActorKind\" IN ('system', 'cli', 'user', 'token') AND (\"ActorUserId\" IS NOT NULL) = (\"ActorKind\" = 'user') AND (\"ActorTokenId\" IS NOT NULL) = (\"ActorKind\" = 'token')");
                    table.CheckConstraint("CK_AuditEvents_Channel", "\"Channel\" IN ('system', 'cli', 'dashboard', 'api', 'mcp')");
                    table.CheckConstraint("CK_AuditEvents_Hashes", "length(\"PrevHash\") = 32 AND length(\"Hash\") = 32");
                    table.CheckConstraint("CK_AuditEvents_Outcome", "\"Outcome\" IN ('success', 'denied', 'invalid', 'conflict', 'failed')");
                    table.ForeignKey(
                        name: "FK_AuditEvents_ConfigRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "ConfigRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_PrevHash",
                table: "AuditEvents",
                column: "PrevHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RevisionId",
                table: "AuditEvents",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Target",
                table: "AuditEvents",
                column: "Target");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Timestamp",
                table: "AuditEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigRevisions_ParentId",
                table: "ConfigRevisions",
                column: "ParentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigRevisions_Seq",
                table: "ConfigRevisions",
                column: "Seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ConfigRevisions");
        }
    }
}
