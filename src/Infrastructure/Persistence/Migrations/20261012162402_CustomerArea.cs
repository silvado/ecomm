using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                table: "store_settings",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp",
                table: "store_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_access_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_access_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_tenant_id_buyer_email_placed_at",
                table: "customer_orders",
                columns: new[] { "tenant_id", "buyer_email", "placed_at" },
                filter: "buyer_email <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_customer_access_links_tenant_id_email_created_at",
                table: "customer_access_links",
                columns: new[] { "tenant_id", "email", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_access_links_tenant_id_token_hash",
                table: "customer_access_links",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_sessions_tenant_id_expires_at",
                table: "customer_sessions",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_sessions_tenant_id_token_hash",
                table: "customer_sessions",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("customer_access_links"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("customer_sessions"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("customer_access_links"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("customer_sessions"));

            migrationBuilder.DropTable(
                name: "customer_access_links");

            migrationBuilder.DropTable(
                name: "customer_sessions");

            migrationBuilder.DropIndex(
                name: "ix_customer_orders_tenant_id_buyer_email_placed_at",
                table: "customer_orders");

            migrationBuilder.DropColumn(
                name: "contact_email",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "smtp",
                table: "store_settings");
        }
    }
}
