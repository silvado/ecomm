using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders",
                sql: "(delivery_method = 'Shipping' AND (delivery_postal_code IS NOT NULL OR anonymized_at IS NOT NULL) AND shipping_service_id IS NOT NULL) OR (delivery_method = 'Shipping' AND origin = 'MercadoLivre') OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders",
                sql: "(delivery_method = 'Shipping' AND (delivery_postal_code IS NOT NULL OR anonymized_at IS NOT NULL) AND shipping_service_id IS NOT NULL) OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");
        }
    }
}
