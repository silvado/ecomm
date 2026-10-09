using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "part_photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    original_content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_photos", x => x.id);
                    table.CheckConstraint("ck_part_photos_position", "position >= 0 AND position < 20");
                    table.ForeignKey(
                        name: "fk_part_photos_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("part_photos"));

            migrationBuilder.CreateIndex(
                name: "ix_part_photos_part_id",
                table: "part_photos",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_part_photos_tenant_id_part_id_position",
                table: "part_photos",
                columns: new[] { "tenant_id", "part_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("part_photos"));

            migrationBuilder.DropTable(
                name: "part_photos");
        }
    }
}
