using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    operation = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    error_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    part_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "message_failures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    error_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    replayed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replayed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_failures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_logs_tenant_id_at",
                table: "integration_logs",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_logs_tenant_id_correlation_id",
                table: "integration_logs",
                columns: new[] { "tenant_id", "correlation_id" },
                filter: "correlation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_logs_tenant_id_order_id",
                table: "integration_logs",
                columns: new[] { "tenant_id", "order_id" },
                filter: "order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_logs_tenant_id_part_id",
                table: "integration_logs",
                columns: new[] { "tenant_id", "part_id" },
                filter: "part_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_message_failures_tenant_id_status_failed_at",
                table: "message_failures",
                columns: new[] { "tenant_id", "status", "failed_at" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("integration_logs"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("message_failures"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("integration_logs"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("message_failures"));
            migrationBuilder.DropTable(
                name: "integration_logs");

            migrationBuilder.DropTable(
                name: "message_failures");
        }
    }
}
