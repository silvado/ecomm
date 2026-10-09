using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "stock_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "parts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "condition",
                table: "parts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Used");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "parts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "parts",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "height_cm",
                table: "parts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "length_cm",
                table: "parts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "parts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "parts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "weight_g",
                table: "parts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width_cm",
                table: "parts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "part_oem_codes",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_oem_codes", x => new { x.part_id, x.code });
                    table.ForeignKey(
                        name: "fk_part_oem_codes_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("part_oem_codes"));

            migrationBuilder.CreateIndex(
                name: "ix_parts_tenant_id_status_updated_at",
                table: "parts",
                columns: new[] { "tenant_id", "status", "updated_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_parts_dimensions_positive",
                table: "parts",
                sql: "coalesce(length_cm, 1) > 0 AND coalesce(width_cm, 1) > 0 AND coalesce(height_cm, 1) > 0 AND coalesce(weight_g, 1) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_parts_price_positive",
                table: "parts",
                sql: "price > 0");

            migrationBuilder.CreateIndex(
                name: "ix_part_oem_codes_tenant_id_code",
                table: "part_oem_codes",
                columns: new[] { "tenant_id", "code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("part_oem_codes"));

            migrationBuilder.DropTable(
                name: "part_oem_codes");

            migrationBuilder.DropIndex(
                name: "ix_parts_tenant_id_status_updated_at",
                table: "parts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_parts_dimensions_positive",
                table: "parts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_parts_price_positive",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "condition",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "description",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "height_cm",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "length_cm",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "status",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "weight_g",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "width_cm",
                table: "parts");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "parts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);
        }
    }
}
