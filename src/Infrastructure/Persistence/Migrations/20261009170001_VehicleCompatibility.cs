using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VehicleCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.CreateTable(
                name: "vehicle_brands",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicle_brands", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicle_models",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicle_models", x => x.id);
                    table.ForeignKey(
                        name: "fk_vehicle_models_vehicle_brands_brand_id",
                        column: x => x.brand_id,
                        principalSchema: "ref",
                        principalTable: "vehicle_brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vehicle_versions",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    year_from = table.Column<int>(type: "integer", nullable: false),
                    year_to = table.Column<int>(type: "integer", nullable: true),
                    ml_reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    discontinued = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicle_versions", x => x.id);
                    table.CheckConstraint("ck_vehicle_versions_years", "year_to IS NULL OR year_to >= year_from");
                    table.ForeignKey(
                        name: "fk_vehicle_versions_vehicle_models_model_id",
                        column: x => x.model_id,
                        principalSchema: "ref",
                        principalTable: "vehicle_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "part_compatibilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year_from = table.Column<int>(type: "integer", nullable: true),
                    year_to = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_compatibilities", x => x.id);
                    table.CheckConstraint("ck_part_compatibilities_years", "(year_from IS NULL AND year_to IS NULL) OR (year_from IS NOT NULL AND year_to IS NOT NULL AND year_from <= year_to)");
                    table.ForeignKey(
                        name: "fk_part_compatibilities_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_part_compatibilities_vehicle_versions_vehicle_version_id",
                        column: x => x.vehicle_version_id,
                        principalSchema: "ref",
                        principalTable: "vehicle_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("part_compatibilities"));

            // Réplica da plataforma: a aplicação só lê; quem escreve é a importação (papel de migração).
            migrationBuilder.Sql($"""
                GRANT USAGE ON SCHEMA ref TO {TenantDatabaseBootstrap.AppRole};
                GRANT SELECT ON ALL TABLES IN SCHEMA ref TO {TenantDatabaseBootstrap.AppRole};
                """);

            migrationBuilder.CreateIndex(
                name: "ix_part_compatibilities_part_id_vehicle_version_id_year_from_y",
                table: "part_compatibilities",
                columns: new[] { "part_id", "vehicle_version_id", "year_from", "year_to" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_part_compatibilities_tenant_id_vehicle_version_id",
                table: "part_compatibilities",
                columns: new[] { "tenant_id", "vehicle_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_part_compatibilities_vehicle_version_id",
                table: "part_compatibilities",
                column: "vehicle_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_brands_name",
                schema: "ref",
                table: "vehicle_brands",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_models_brand_id_name",
                schema: "ref",
                table: "vehicle_models",
                columns: new[] { "brand_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_versions_model_id_engine_year_from",
                schema: "ref",
                table: "vehicle_versions",
                columns: new[] { "model_id", "engine", "year_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("part_compatibilities"));

            migrationBuilder.DropTable(
                name: "part_compatibilities");

            migrationBuilder.DropTable(
                name: "vehicle_versions",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "vehicle_models",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "vehicle_brands",
                schema: "ref");
        }
    }
}
