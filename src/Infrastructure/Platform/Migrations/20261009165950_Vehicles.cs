using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Vehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vehicle_brands",
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
                        principalTable: "vehicle_brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vehicle_versions",
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
                        principalTable: "vehicle_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_brands_name",
                table: "vehicle_brands",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_models_brand_id_name",
                table: "vehicle_models",
                columns: new[] { "brand_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_versions_model_id_engine_year_from",
                table: "vehicle_versions",
                columns: new[] { "model_id", "engine", "year_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vehicle_versions");

            migrationBuilder.DropTable(
                name: "vehicle_models");

            migrationBuilder.DropTable(
                name: "vehicle_brands");
        }
    }
}
