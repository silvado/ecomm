using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelShipments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_shipment_id",
                table: "customer_orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_external_shipment_id",
                table: "customer_orders",
                columns: new[] { "tenant_id", "external_shipment_id" },
                filter: "external_shipment_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customer_orders_tenant_id_external_shipment_id",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "external_shipment_id",
                table: "customer_orders");
        }
    }
}
