using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_ids = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    cursor = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    processed_items = table.Column<int>(type: "integer", nullable: false),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    linked_count = table.Column<int>(type: "integer", nullable: false),
                    already_linked_count = table.Column<int>(type: "integer", nullable: false),
                    rejected_count = table.Column<int>(type: "integer", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_imports", x => x.id);
                    table.CheckConstraint("ck_channel_imports_progress", "processed_items >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_imports_tenant_id_created_at",
                table: "channel_imports",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("channel_imports"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("channel_imports"));

            migrationBuilder.DropTable(
                name: "channel_imports");
        }
    }
}
