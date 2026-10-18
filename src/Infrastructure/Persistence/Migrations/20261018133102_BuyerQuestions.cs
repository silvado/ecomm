using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BuyerQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_answer_questions",
                table: "store_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "buyer_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    external_item_id = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    draft_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    draft_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    drafted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answer_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    answered_via = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    answered_by = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_buyer_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_buyer_questions_channel_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "channel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_buyer_questions_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_buyer_questions_connection_id",
                table: "buyer_questions",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_buyer_questions_part_id",
                table: "buyer_questions",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_buyer_questions_tenant_id_connection_id_external_id",
                table: "buyer_questions",
                columns: new[] { "tenant_id", "connection_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_buyer_questions_tenant_id_status_asked_at",
                table: "buyer_questions",
                columns: new[] { "tenant_id", "status", "asked_at" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("buyer_questions"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("buyer_questions"));

            migrationBuilder.DropTable(
                name: "buyer_questions");

            migrationBuilder.DropColumn(
                name: "auto_answer_questions",
                table: "store_settings");
        }
    }
}
