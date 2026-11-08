using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class DataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Sealed = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                    table.CheckConstraint("CK_DataProtectionKeys_Name", "length(\"FriendlyName\") BETWEEN 1 AND 256");
                    table.CheckConstraint("CK_DataProtectionKeys_Sealed", "length(\"Sealed\") > 0");
                    table.CheckConstraint("CK_DataProtectionKeys_Version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataProtectionKeys_FriendlyName",
                table: "DataProtectionKeys",
                column: "FriendlyName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");
        }
    }
}
