using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_exports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_tenant_id_requested_at",
                table: "data_exports",
                columns: new[] { "tenant_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_data_exports_one_pending",
                table: "data_exports",
                column: "tenant_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("data_exports"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_exports");
        }
    }
}
