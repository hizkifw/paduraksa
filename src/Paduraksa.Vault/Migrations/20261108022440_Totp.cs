using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class Totp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TotpStates",
                columns: table => new
                {
                    SecretId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastStep = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TotpStates", x => x.SecretId);
                    table.CheckConstraint("CK_TotpStates_LastStep", "\"LastStep\" IS NULL OR \"LastStep\" >= 0");
                    table.ForeignKey(
                        name: "FK_TotpStates_Secrets_SecretId",
                        column: x => x.SecretId,
                        principalTable: "Secrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TotpStates");
        }
    }
}
