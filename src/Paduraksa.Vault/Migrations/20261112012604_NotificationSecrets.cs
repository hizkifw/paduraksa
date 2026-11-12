using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paduraksa.Vault.Migrations
{
    /// <inheritdoc />
    public partial class NotificationSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Secrets_Kind",
                table: "Secrets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Secrets_Kind",
                table: "Secrets",
                sql: "\"Kind\" IN ('dns-api-token', 'eab-hmac-key', 'totp-secret', 'tsig-key', 'hmac-signing-key', 'notification-secret')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The narrower check refuses the new kind, so those rows go first (a notification channel then has no secret: its values cannot be rebuilt).
            migrationBuilder.Sql("DELETE FROM \"Secrets\" WHERE \"Kind\" = 'notification-secret';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Secrets_Kind",
                table: "Secrets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Secrets_Kind",
                table: "Secrets",
                sql: "\"Kind\" IN ('dns-api-token', 'eab-hmac-key', 'totp-secret', 'tsig-key', 'hmac-signing-key')");
        }
    }
}
