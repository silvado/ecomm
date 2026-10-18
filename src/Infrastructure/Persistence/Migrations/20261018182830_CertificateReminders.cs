using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CertificateReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    days_left = table.Column<int>(type: "integer", nullable: false),
                    recipients = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_reminders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_reminders_tenant_id_certificate_expires_at_days",
                table: "certificate_reminders",
                columns: new[] { "tenant_id", "certificate_expires_at", "days_left" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("certificate_reminders"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("certificate_reminders"));

            migrationBuilder.DropTable(
                name: "certificate_reminders");
        }
    }
}
