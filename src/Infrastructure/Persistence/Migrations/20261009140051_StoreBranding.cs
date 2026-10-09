using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_brandings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    primary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    background_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    text_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    about = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    return_policy = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    footer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_store_brandings", x => x.tenant_id);
                });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("store_brandings"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("store_brandings"));

            migrationBuilder.DropTable(
                name: "store_brandings");
        }
    }
}
