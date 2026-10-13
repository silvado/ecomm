using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "channel_listings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_pushed_quantity = table.Column<int>(type: "integer", nullable: true),
                    last_pushed_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_listings", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_listings_channel_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "channel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_channel_listings_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_connections_tenant_id_channel_external_account_id",
                table: "channel_connections",
                columns: new[] { "tenant_id", "channel", "external_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channel_listings_connection_id",
                table: "channel_listings",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_listings_part_id",
                table: "channel_listings",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_listings_tenant_id_connection_id_external_id",
                table: "channel_listings",
                columns: new[] { "tenant_id", "connection_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_channel_listings_tenant_id_part_id_connection_id",
                table: "channel_listings",
                columns: new[] { "tenant_id", "part_id", "connection_id" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("channel_connections"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("channel_listings"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("channel_listings"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("channel_connections"));

            migrationBuilder.DropTable(
                name: "channel_listings");

            migrationBuilder.DropTable(
                name: "channel_connections");
        }
    }
}
