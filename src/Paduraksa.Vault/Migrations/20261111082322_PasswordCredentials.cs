using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class PasswordCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasswordCredentials",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordCredentials", x => x.Id);
                    table.CheckConstraint("CK_PasswordCredentials_Sealed", "length(\"Sealed\") > 0");
                    table.CheckConstraint("CK_PasswordCredentials_Version", "\"Version\" >= 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordCredentials");
        }
    }
}
