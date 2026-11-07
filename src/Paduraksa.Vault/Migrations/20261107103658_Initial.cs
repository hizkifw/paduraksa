using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountKeys",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", nullable: false),
                    Kty = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Crv = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    X = table.Column<string>(type: "TEXT", nullable: true),
                    Y = table.Column<string>(type: "TEXT", nullable: true),
                    N = table.Column<string>(type: "TEXT", nullable: true),
                    E = table.Column<string>(type: "TEXT", nullable: true),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountKeys", x => x.Id);
                    table.CheckConstraint("CK_AccountKeys_Algorithm", "\"Algorithm\" IN ('ES256', 'ES384', 'RS256')");
                    table.CheckConstraint("CK_AccountKeys_Kty", "\"Kty\" IN ('EC', 'RSA')");
                    table.CheckConstraint("CK_AccountKeys_Sealed", "length(\"Sealed\") > 0");
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MacAlgorithm = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedHosts = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PreviousSealed = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PreviousVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    PreviousRetiredAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Id);
                    table.CheckConstraint("CK_Secrets_Kind", "\"Kind\" IN ('dns-api-token', 'eab-hmac-key', 'totp-secret', 'tsig-key', 'hmac-signing-key')");
                    table.CheckConstraint("CK_Secrets_MacAlgorithm", "\"MacAlgorithm\" IS NULL OR \"MacAlgorithm\" IN ('hmac-sha256', 'hmac-sha384', 'hmac-sha512')");
                    table.CheckConstraint("CK_Secrets_MacKinds", "(\"MacAlgorithm\" IS NOT NULL) = (\"Kind\" IN ('eab-hmac-key', 'tsig-key', 'hmac-signing-key'))");
                    table.CheckConstraint("CK_Secrets_Previous", "(\"PreviousSealed\" IS NULL) = (\"PreviousVersion\" IS NULL) AND (\"PreviousSealed\" IS NULL) = (\"PreviousRetiredAt\" IS NULL)");
                    table.CheckConstraint("CK_Secrets_Sealed", "length(\"Sealed\") > 0");
                    table.CheckConstraint("CK_Secrets_Version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "VaultMeta",
                columns: table => new
                {
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    KekCheck = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultMeta", x => x.Slot);
                    table.CheckConstraint("CK_VaultMeta_KekCheck", "length(\"KekCheck\") = 32");
                    table.CheckConstraint("CK_VaultMeta_Singleton", "\"Slot\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountKeys");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "VaultMeta");
        }
    }
}
