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
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Upstreams",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Upstreams", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "GlobalSettings",
                columns: table => new
                {
                    Key = table.Column<int>(type: "INTEGER", nullable: false),
                    RevisionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UnknownHost = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultCertificateId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TrustedProxyMode = table.Column<string>(type: "TEXT", nullable: false),
                    Defaults = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSettings", x => x.Key);
                    table.CheckConstraint("CK_GlobalSettings_Singleton", "\"Key\" = 1");
                    table.ForeignKey(
                        name: "FK_GlobalSettings_ConfigRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "ConfigRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Hosts",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CertificateMode = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Hosts_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpstreamId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Destinations_Upstreams_UpstreamId",
                        column: x => x.UpstreamId,
                        principalTable: "Upstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostNames",
                columns: table => new
                {
                    HostId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    HostEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostNames", x => new { x.HostId, x.Name });
                    table.ForeignKey(
                        name: "FK_HostNames_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    HostId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpstreamId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Match = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routes_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Routes_Upstreams_UpstreamId",
                        column: x => x.UpstreamId,
                        principalTable: "Upstreams",
                        principalColumn: "Id");
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

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_UpstreamId",
                table: "Destinations",
                column: "UpstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalSettings_RevisionId",
                table: "GlobalSettings",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_HostNames_Name_Enabled",
                table: "HostNames",
                column: "Name",
                unique: true,
                filter: "\"HostEnabled\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_ProfileId",
                table: "Hosts",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_HostId_Position",
                table: "Routes",
                columns: new[] { "HostId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_UpstreamId",
                table: "Routes",
                column: "UpstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_Upstreams_Name",
                table: "Upstreams",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Destinations");

            migrationBuilder.DropTable(
                name: "GlobalSettings");

            migrationBuilder.DropTable(
                name: "HostNames");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "ConfigRevisions");

            migrationBuilder.DropTable(
                name: "Hosts");

            migrationBuilder.DropTable(
                name: "Upstreams");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
