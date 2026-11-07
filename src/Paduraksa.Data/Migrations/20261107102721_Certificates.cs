using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class Certificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Certificates",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityKeyIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    Sha256Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    KeyAlgorithm = table.Column<string>(type: "TEXT", nullable: false),
                    NotBefore = table.Column<string>(type: "TEXT", nullable: false),
                    NotAfter = table.Column<string>(type: "TEXT", nullable: false),
                    ChainIssuers = table.Column<string>(type: "TEXT", nullable: false),
                    PublicChainPem = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.Id);
                    table.CheckConstraint("CK_Certificates_Source", "\"Source\" IN ('acme', 'uploaded', 'self_signed', 'local_ca', 'local_ca_root')");
                    table.CheckConstraint("CK_Certificates_Status", "\"Status\" IN ('pending', 'active', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "CertificateNames",
                columns: table => new
                {
                    CertificateId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateNames", x => new { x.CertificateId, x.Name });
                    table.ForeignKey(
                        name: "FK_CertificateNames_Certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateNames_Name",
                table: "CertificateNames",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_NotAfter",
                table: "Certificates",
                column: "NotAfter");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_Sha256Fingerprint",
                table: "Certificates",
                column: "Sha256Fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateNames");

            migrationBuilder.DropTable(
                name: "Certificates");
        }
    }
}
