using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncGuardian : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sync_alerts_enabled",
                table: "store_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "sync_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    problem_keys = table.Column<List<string>>(type: "text[]", nullable: false),
                    recipients = table.Column<int>(type: "integer", nullable: false),
                    ai_text = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_alerts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sync_alerts_tenant_id_sent_at",
                table: "sync_alerts",
                columns: new[] { "tenant_id", "sent_at" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("sync_alerts"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("sync_alerts"));

            migrationBuilder.DropTable(
                name: "sync_alerts");

            migrationBuilder.DropColumn(
                name: "sync_alerts_enabled",
                table: "store_settings");
        }
    }
}
