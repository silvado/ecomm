using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fiscal",
                table: "store_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "fiscal_origin",
                table: "parts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ncm",
                table: "parts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_code",
                table: "parts",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fiscal_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    recipient = table.Column<string>(type: "jsonb", nullable: true),
                    access_key = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: true),
                    number = table.Column<int>(type: "integer", nullable: true),
                    series = table.Column<int>(type: "integer", nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xml_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    danfe_storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_justification = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_fiscal_documents_customer_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "customer_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_documents_order_id",
                table: "fiscal_documents",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_documents_tenant_id_order_id_active",
                table: "fiscal_documents",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "status IN ('Processing', 'Authorized')");

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_documents_tenant_id_order_id_requested_at",
                table: "fiscal_documents",
                columns: new[] { "tenant_id", "order_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_documents_tenant_id_reference",
                table: "fiscal_documents",
                columns: new[] { "tenant_id", "reference" },
                unique: true,
                filter: "reference IS NOT NULL");

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("fiscal_documents"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("fiscal_documents"));

            migrationBuilder.DropTable(
                name: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "fiscal",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "fiscal_origin",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "ncm",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "tax_code",
                table: "parts");
        }
    }
}
