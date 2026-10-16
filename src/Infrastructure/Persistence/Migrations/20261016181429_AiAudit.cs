using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_interactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    input_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    output = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_interactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_counters",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_counters", x => new { x.tenant_id, x.feature, x.period });
                    table.CheckConstraint("ck_ai_usage_counters_used", "used >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_interactions_tenant_id_at",
                table: "ai_interactions",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_interactions_tenant_id_feature_at",
                table: "ai_interactions",
                columns: new[] { "tenant_id", "feature", "at" });

            // RF35/RF36: registro e contador de IA são da loja (ADR-0001).
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("ai_interactions"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("ai_usage_counters"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_interactions");

            migrationBuilder.DropTable(
                name: "ai_usage_counters");
        }
    }
}
