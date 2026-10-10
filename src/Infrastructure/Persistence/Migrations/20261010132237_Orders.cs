using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    access_token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    buyer_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    buyer_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    buyer_phone = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    buyer_cpf = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    delivery_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    delivery_postal_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    delivery_street = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    delivery_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    delivery_complement = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    delivery_district = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    delivery_city = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    delivery_state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    shipping_service_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    shipping_carrier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_service = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_days = table.Column<int>(type: "integer", nullable: true),
                    pickup_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    items_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    shipping_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payment_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_orders", x => x.id);
                    table.CheckConstraint("ck_customer_orders_delivery", "(delivery_method = 'Shipping' AND delivery_postal_code IS NOT NULL AND shipping_service_id IS NOT NULL) OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");
                    table.CheckConstraint("ck_customer_orders_totals", "items_total > 0 AND shipping_total >= 0 AND total = items_total + shipping_total");
                });

            migrationBuilder.CreateTable(
                name: "order_counters",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_counters", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    internal_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_positive", "quantity > 0 AND unit_price > 0");
                    table.ForeignKey(
                        name: "fk_order_items_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_order_items_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_items_stock_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "stock_reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_access_token_hash",
                table: "customer_orders",
                columns: new[] { "tenant_id", "access_token_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_number",
                table: "customer_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_placed_at",
                table: "customer_orders",
                columns: new[] { "tenant_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_items_order_id",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_part_id",
                table: "order_items",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_reservation_id",
                table: "order_items",
                column: "reservation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_items_tenant_id",
                table: "order_items",
                column: "tenant_id");

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("customer_orders"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("order_items"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("order_counters"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("order_counters"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("order_items"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("customer_orders"));

            migrationBuilder.DropTable(
                name: "order_counters");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "customer_orders");
        }
    }
}
