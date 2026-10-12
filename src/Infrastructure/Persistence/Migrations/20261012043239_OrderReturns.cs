using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_returns",
                table: "customer_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "returned_at",
                table: "customer_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_returns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    refund_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    refund_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_returns", x => x.id);
                    table.CheckConstraint("ck_order_returns_refund", "refund_amount IS NULL OR refund_amount >= 0");
                    table.ForeignKey(
                        name: "fk_order_returns_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_returns_order_id",
                table: "order_returns",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_returns_tenant_id_order_id_at",
                table: "order_returns",
                columns: new[] { "tenant_id", "order_id", "at" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("order_returns"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("order_returns"));
            migrationBuilder.DropTable(
                name: "order_returns");

            migrationBuilder.DropColumn(
                name: "has_returns",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "returned_at",
                table: "customer_orders");
        }
    }
}
