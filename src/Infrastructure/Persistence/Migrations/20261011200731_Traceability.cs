using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Traceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dismantler_mode",
                table: "store_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "traceability_fields",
                table: "store_settings",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "parts",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "donor_vehicle_id",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "traceability_tag",
                table: "parts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "skipped",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "rows",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "errors",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.CreateTable(
                name: "donor_vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plate = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    chassis = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    renavam = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    brand = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    model_year = table.Column<int>(type: "integer", nullable: true),
                    color = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    deregistration_certificate = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    entry_invoice_key = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: true),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_donor_vehicles", x => x.id);
                    table.CheckConstraint("ck_donor_vehicles_identified", "plate IS NOT NULL OR chassis IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ix_parts_donor_vehicle_id",
                table: "parts",
                column: "donor_vehicle_id");

            migrationBuilder.CreateIndex(
                name: "ix_parts_tenant_id_traceability_tag",
                table: "parts",
                columns: new[] { "tenant_id", "traceability_tag" },
                unique: true,
                filter: "traceability_tag IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_donor_vehicles_tenant_id_chassis",
                table: "donor_vehicles",
                columns: new[] { "tenant_id", "chassis" },
                unique: true,
                filter: "chassis IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_donor_vehicles_tenant_id_created_at",
                table: "donor_vehicles",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_donor_vehicles_tenant_id_plate",
                table: "donor_vehicles",
                columns: new[] { "tenant_id", "plate" },
                unique: true,
                filter: "plate IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_donor_vehicles_tenant_id_renavam",
                table: "donor_vehicles",
                columns: new[] { "tenant_id", "renavam" },
                unique: true,
                filter: "renavam IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_parts_donor_vehicles_donor_vehicle_id",
                table: "parts",
                column: "donor_vehicle_id",
                principalTable: "donor_vehicles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("donor_vehicles"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("donor_vehicles"));
            migrationBuilder.DropForeignKey(
                name: "fk_parts_donor_vehicles_donor_vehicle_id",
                table: "parts");

            migrationBuilder.DropTable(
                name: "donor_vehicles");

            migrationBuilder.DropIndex(
                name: "ix_parts_donor_vehicle_id",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "ix_parts_tenant_id_traceability_tag",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "dismantler_mode",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "traceability_fields",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "donor_vehicle_id",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "traceability_tag",
                table: "parts");

            migrationBuilder.AlterColumn<string>(
                name: "skipped",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'::jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "rows",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'::jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "errors",
                table: "part_imports",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'::jsonb");
        }
    }
}
