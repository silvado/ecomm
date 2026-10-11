using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "canceled_by",
                table: "customer_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                table: "customer_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "customer_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "shipped_at",
                table: "customer_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_code",
                table: "customer_orders",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_history_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_origin_external_id",
                table: "customer_orders",
                columns: new[] { "tenant_id", "origin", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_history_order_id",
                table: "order_history",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_history_tenant_id_order_id_at",
                table: "order_history",
                columns: new[] { "tenant_id", "order_id", "at" });

            // Dados de antes do RF17, de todas as lojas: o migrador não tem BYPASSRLS, então o dono da tabela suspende o
            // FORCE só dentro desta transação. Cancelados até aqui foram todos pelo prazo; cada pedido ganha o histórico
            // da criação e, se mudou de situação, da situação atual.
            migrationBuilder.Sql("ALTER TABLE customer_orders NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE customer_orders SET canceled_by = 'PaymentExpired' WHERE status = 'Canceled';");
            migrationBuilder.Sql("""
                INSERT INTO order_history (id, tenant_id, order_id, from_status, to_status, actor, user_id, note, at)
                SELECT gen_random_uuid(), tenant_id, id, NULL, 'PendingPayment', 'Buyer', NULL, NULL, placed_at FROM customer_orders;
                INSERT INTO order_history (id, tenant_id, order_id, from_status, to_status, actor, user_id, note, at)
                SELECT gen_random_uuid(), tenant_id, id, 'PendingPayment', status, 'System', NULL, 'Registrado antes do histórico de pedidos.',
                       COALESCE(paid_at, canceled_at, placed_at)
                FROM customer_orders WHERE status <> 'PendingPayment';
                """);
            migrationBuilder.Sql("ALTER TABLE customer_orders FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("order_history"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("order_history"));
            migrationBuilder.DropTable(
                name: "order_history");

            migrationBuilder.DropIndex(
                name: "ix_customer_orders_tenant_id_origin_external_id",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "canceled_by",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "shipped_at",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "tracking_code",
                table: "customer_orders");
        }
    }
}
