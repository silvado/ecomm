using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class PlanFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plan_features",
                columns: table => new
                {
                    plan_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    feature = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_features", x => new { x.plan_code, x.feature });
                    table.ForeignKey(
                        name: "fk_plan_features_plans_plan_code",
                        column: x => x.plan_code,
                        principalTable: "plans",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "plan_features",
                columns: new[] { "feature", "plan_code" },
                values: new object[,]
                {
                    { "ai:buyer_answers", "completo" },
                    { "ai:daily_summary", "completo" },
                    { "ai:part_from_photos", "completo" },
                    { "ai:price_suggestion", "completo" },
                    { "channel:mercado-livre", "completo" },
                    { "channel:meta", "completo" },
                    { "channel:olx", "completo" },
                    { "custom_domain", "completo" },
                    { "ai:part_from_photos", "essencial" },
                    { "channel:mercado-livre", "essencial" },
                    { "ai:buyer_answers", "profissional" },
                    { "ai:daily_summary", "profissional" },
                    { "ai:part_from_photos", "profissional" },
                    { "ai:price_suggestion", "profissional" },
                    { "channel:mercado-livre", "profissional" },
                    { "channel:meta", "profissional" },
                    { "custom_domain", "profissional" }
                });

            migrationBuilder.InsertData(
                table: "plan_limits",
                columns: new[] { "key", "plan_code", "value" },
                values: new object[,]
                {
                    { "ai_listings_month", "completo", 1000 },
                    { "active_parts", "essencial", 500 },
                    { "ai_listings_month", "essencial", 50 },
                    { "active_parts", "profissional", 3000 },
                    { "ai_listings_month", "profissional", 300 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_features");

            migrationBuilder.DeleteData(
                table: "plan_limits",
                keyColumns: new[] { "key", "plan_code" },
                keyValues: new object[] { "ai_listings_month", "completo" });

            migrationBuilder.DeleteData(
                table: "plan_limits",
                keyColumns: new[] { "key", "plan_code" },
                keyValues: new object[] { "active_parts", "essencial" });

            migrationBuilder.DeleteData(
                table: "plan_limits",
                keyColumns: new[] { "key", "plan_code" },
                keyValues: new object[] { "ai_listings_month", "essencial" });

            migrationBuilder.DeleteData(
                table: "plan_limits",
                keyColumns: new[] { "key", "plan_code" },
                keyValues: new object[] { "active_parts", "profissional" });

            migrationBuilder.DeleteData(
                table: "plan_limits",
                keyColumns: new[] { "key", "plan_code" },
                keyValues: new object[] { "ai_listings_month", "profissional" });
        }
    }
}
