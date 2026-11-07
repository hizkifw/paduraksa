using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class Certificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificateKeys",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IsLocalCaRoot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ChainPem = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateKeys", x => x.Id);
                    table.CheckConstraint("CK_CertificateKeys_Chain", "length(\"ChainPem\") > 0");
                    table.CheckConstraint("CK_CertificateKeys_Sealed", "length(\"Sealed\") > 0");
                    table.CheckConstraint("CK_CertificateKeys_Version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "StagedKeys",
                columns: table => new
                {
                    CertificateId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    StagedKeyId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Names = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedKeys", x => x.CertificateId);
                    table.CheckConstraint("CK_StagedKeys_Sealed", "length(\"Sealed\") > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateKeys_IsLocalCaRoot",
                table: "CertificateKeys",
                column: "IsLocalCaRoot",
                unique: true,
                filter: "\"IsLocalCaRoot\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_StagedKeys_StagedKeyId",
                table: "StagedKeys",
                column: "StagedKeyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateKeys");

            migrationBuilder.DropTable(
                name: "StagedKeys");
        }
    }
}
