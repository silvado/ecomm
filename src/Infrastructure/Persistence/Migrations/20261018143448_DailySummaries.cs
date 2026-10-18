using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DailySummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "daily_summary_enabled",
                table: "store_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true); // lojas existentes: ligado às 7h, como as novas (RF34 CA1)

            migrationBuilder.AddColumn<int>(
                name: "daily_summary_hour",
                table: "store_settings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateTable(
                name: "daily_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    recipients = table.Column<int>(type: "integer", nullable: false),
                    ai_text = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_summaries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_daily_summaries_tenant_id_day",
                table: "daily_summaries",
                columns: new[] { "tenant_id", "day" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("daily_summaries"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("daily_summaries"));

            migrationBuilder.DropTable(
                name: "daily_summaries");

            migrationBuilder.DropColumn(
                name: "daily_summary_enabled",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "daily_summary_hour",
                table: "store_settings");
        }
    }
}
