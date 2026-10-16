using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "monthly_price",
                table: "plans",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "subscription_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    charge_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: true),
                    description = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    charge_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    payment_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_via = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    paid_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_invoices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_on = table.Column<DateOnly>(type: "date", nullable: false),
                    due_day = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_subscriptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "plans",
                keyColumn: "id",
                keyValue: new Guid("0199c4a0-0000-7000-8000-000000000001"),
                column: "monthly_price",
                value: 0m);

            migrationBuilder.UpdateData(
                table: "plans",
                keyColumn: "id",
                keyValue: new Guid("0199c4a0-0000-7000-8000-000000000002"),
                column: "monthly_price",
                value: 0m);

            migrationBuilder.UpdateData(
                table: "plans",
                keyColumn: "id",
                keyValue: new Guid("0199c4a0-0000-7000-8000-000000000003"),
                column: "monthly_price",
                value: 0m);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_events_gateway_idempotency_key",
                table: "subscription_events",
                columns: new[] { "gateway", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_events_received_at",
                table: "subscription_events",
                column: "received_at",
                filter: "status = 'Received'");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_charge_id",
                table: "subscription_invoices",
                column: "charge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_tenant_id_due_date",
                table: "subscription_invoices",
                columns: new[] { "tenant_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_invoices_tenant_id_period",
                table: "subscription_invoices",
                columns: new[] { "tenant_id", "period" },
                unique: true,
                filter: "kind = 'Monthly'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscription_events");

            migrationBuilder.DropTable(
                name: "subscription_invoices");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropColumn(
                name: "monthly_price",
                table: "plans");
        }
    }
}
