using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class AcmeIssuance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcmeAccounts",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DirectoryUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Contacts = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AccountUrl = table.Column<string>(type: "TEXT", nullable: false),
                    KeyAlgorithm = table.Column<string>(type: "TEXT", nullable: false),
                    EabKid = table.Column<string>(type: "TEXT", nullable: true),
                    EabSecretId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TermsOfServiceAgreed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcmeAccounts", x => x.Id);
                    table.CheckConstraint("CK_AcmeAccounts_KeyAlgorithm", "\"KeyAlgorithm\" IN ('ES256', 'ES384', 'RS256')");
                    table.CheckConstraint("CK_AcmeAccounts_Status", "\"Status\" IN ('active', 'deactivated')");
                });

            migrationBuilder.CreateTable(
                name: "DnsProviders",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialSecretId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsProviders", x => x.Id);
                    table.CheckConstraint("CK_DnsProviders_Kind", "\"Kind\" IN ('cloudflare', 'desec')");
                });

            migrationBuilder.CreateTable(
                name: "AcmeOrders",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CertificateId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AccountId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengeType = table.Column<string>(type: "TEXT", nullable: false),
                    DnsProviderId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    KeySpec = table.Column<string>(type: "TEXT", nullable: false),
                    Names = table.Column<string>(type: "TEXT", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredChain = table.Column<string>(type: "TEXT", nullable: true),
                    OrderUrl = table.Column<string>(type: "TEXT", nullable: true),
                    StagedKeyId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Csr = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AttachedInfo = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    RetryAt = table.Column<string>(type: "TEXT", nullable: true),
                    NextRenewalAt = table.Column<string>(type: "TEXT", nullable: true),
                    AriWindowStart = table.Column<string>(type: "TEXT", nullable: true),
                    AriWindowEnd = table.Column<string>(type: "TEXT", nullable: true),
                    AriExplanationUrl = table.Column<string>(type: "TEXT", nullable: true),
                    AriCheckAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcmeOrders", x => x.Id);
                    table.CheckConstraint("CK_AcmeOrders_ChallengeType", "\"ChallengeType\" IN ('http-01', 'dns-01')");
                    table.CheckConstraint("CK_AcmeOrders_DnsProvider", "\"ChallengeType\" <> 'dns-01' OR \"DnsProviderId\" IS NOT NULL");
                    table.CheckConstraint("CK_AcmeOrders_KeySpec", "\"KeySpec\" IN ('ecdsa-p256', 'ecdsa-p384', 'rsa-2048', 'rsa-3072', 'rsa-4096')");
                    table.CheckConstraint("CK_AcmeOrders_Status", "\"Status\" IN ('pending', 'ordered', 'finalizing', 'valid', 'failed')");
                    table.ForeignKey(
                        name: "FK_AcmeOrders_AcmeAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AcmeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcmeOrders_Certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AcmeOrders_DnsProviders_DnsProviderId",
                        column: x => x.DnsProviderId,
                        principalTable: "DnsProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcmeAccounts_DirectoryUrl",
                table: "AcmeAccounts",
                column: "DirectoryUrl");

            migrationBuilder.CreateIndex(
                name: "IX_AcmeOrders_AccountId",
                table: "AcmeOrders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AcmeOrders_CertificateId",
                table: "AcmeOrders",
                column: "CertificateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcmeOrders_DnsProviderId",
                table: "AcmeOrders",
                column: "DnsProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcmeOrders");

            migrationBuilder.DropTable(
                name: "AcmeAccounts");

            migrationBuilder.DropTable(
                name: "DnsProviders");
        }
    }
}
