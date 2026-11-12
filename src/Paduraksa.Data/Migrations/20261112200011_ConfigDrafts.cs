using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigDraftNotices",
                columns: table => new
                {
                    DraftId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OwnerUserId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DroppedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Entries = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigDraftNotices", x => x.DraftId);
                    table.ForeignKey(
                        name: "FK_ConfigDraftNotices_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigDrafts",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OwnerUserId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedRevisionId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    NextNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Entries = table.Column<string>(type: "TEXT", nullable: false),
                    PendingCredentials = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigDrafts", x => x.Id);
                    table.CheckConstraint("CK_ConfigDrafts_NextNumber", "\"NextNumber\" >= 1");
                    table.CheckConstraint("CK_ConfigDrafts_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_ConfigDrafts_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigDraftNotices_OwnerUserId",
                table: "ConfigDraftNotices",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigDrafts_OwnerUserId",
                table: "ConfigDrafts",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigDrafts_UpdatedAt",
                table: "ConfigDrafts",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigDraftNotices");

            migrationBuilder.DropTable(
                name: "ConfigDrafts");
        }
    }
}
