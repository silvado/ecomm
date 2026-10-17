using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MercadoLivreConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "channel_connections",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_reason",
                table: "channel_connections",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "token_expires_at",
                table: "channel_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "channel_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_verifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_authorizations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_authorizations_tenant_id_state_hash",
                table: "channel_authorizations",
                columns: new[] { "tenant_id", "state_hash" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("channel_authorizations"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_authorizations");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "channel_connections");

            migrationBuilder.DropColumn(
                name: "status_reason",
                table: "channel_connections");

            migrationBuilder.DropColumn(
                name: "token_expires_at",
                table: "channel_connections");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "channel_connections");
        }
    }
}
