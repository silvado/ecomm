using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CounterSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_cnpj",
                table: "customer_orders",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "customer_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_orders_counter_payment",
                table: "customer_orders",
                sql: "(origin = 'Counter') = (payment_method IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_orders_counter_payment",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "buyer_cnpj",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "customer_orders");
        }
    }
}
