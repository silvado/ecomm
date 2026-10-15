using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalDataPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                table: "customer_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "personal_data_access_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_data_access_log", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders",
                sql: "(delivery_method = 'Shipping' AND (delivery_postal_code IS NOT NULL OR anonymized_at IS NOT NULL) AND shipping_service_id IS NOT NULL) OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_personal_data_access_log_tenant_id_at",
                table: "personal_data_access_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_personal_data_access_log_tenant_id_order_id",
                table: "personal_data_access_log",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("personal_data_access_log"));
            // RNF04: registro somente inclusão — a aplicação lê e acrescenta, nunca altera nem apaga.
            migrationBuilder.Sql($"REVOKE UPDATE, DELETE, TRUNCATE ON personal_data_access_log FROM {TenantDatabaseBootstrap.AppRole};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_data_access_log");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                table: "customer_orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_orders_delivery",
                table: "customer_orders",
                sql: "(delivery_method = 'Shipping' AND delivery_postal_code IS NOT NULL AND shipping_service_id IS NOT NULL) OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");
        }
    }
}
