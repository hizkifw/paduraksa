using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkPath = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvents", x => x.Id);
                    table.CheckConstraint("CK_NotificationEvents_Kind", "\"Kind\" IN ('upstream.down', 'upstream.up', 'destination.down', 'destination.up', 'certificate.expiring', 'acme.renewal_failed', 'acme.renewal_recovered', 'signin.lockout', 'signin.new_address', 'test')");
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    EventId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    SentAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    DigestId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.CheckConstraint("CK_NotificationDeliveries_State", "\"State\" IN ('pending', 'sent', 'failed', 'expired', 'dropped')");
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "NotificationEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ChannelId_Id",
                table: "NotificationDeliveries",
                columns: new[] { "ChannelId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ChannelId_State_NextAttemptAt",
                table: "NotificationDeliveries",
                columns: new[] { "ChannelId", "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_EventId_ChannelId",
                table: "NotificationDeliveries",
                columns: new[] { "EventId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvents_CreatedAt",
                table: "NotificationEvents",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationEvents");
        }
    }
}
