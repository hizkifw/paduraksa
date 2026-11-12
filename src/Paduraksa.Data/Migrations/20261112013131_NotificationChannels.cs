using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpiryNotices",
                columns: table => new
                {
                    CertificateId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpiryNotices", x => new { x.CertificateId, x.NotAfter, x.Days });
                    table.ForeignKey(
                        name: "FK_ExpiryNotices_Certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Events = table.Column<string>(type: "TEXT", nullable: false),
                    ServerUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Topic = table.Column<string>(type: "TEXT", nullable: true),
                    SmtpHost = table.Column<string>(type: "TEXT", nullable: true),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: true),
                    SmtpSecurity = table.Column<string>(type: "TEXT", nullable: true),
                    SmtpUsername = table.Column<string>(type: "TEXT", nullable: true),
                    MailFrom = table.Column<string>(type: "TEXT", nullable: true),
                    MailTo = table.Column<string>(type: "TEXT", nullable: false),
                    SecretId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SigningSecretId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastSentAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                    table.CheckConstraint("CK_NotificationChannels_Kind", "\"Kind\" IN ('ntfy', 'gotify', 'discord', 'webhook', 'email')");
                    table.CheckConstraint("CK_NotificationChannels_SmtpSecurity", "\"SmtpSecurity\" IS NULL OR \"SmtpSecurity\" IN ('starttls', 'implicit-tls', 'none')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpiryNotices");

            migrationBuilder.DropTable(
                name: "NotificationChannels");
        }
    }
}
