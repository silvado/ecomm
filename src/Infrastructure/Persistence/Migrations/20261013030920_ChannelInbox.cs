using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inbox");

            migrationBuilder.CreateTable(
                name: "external_events",
                schema: "inbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_account = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    topic = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    payload = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    routed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_external_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_external_events_source_idempotency_key",
                schema: "inbox",
                table: "external_events",
                columns: new[] { "source", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_external_events_status_received_at",
                schema: "inbox",
                table: "external_events",
                columns: new[] { "status", "received_at" });

            // ADR-0001: schema sem RLS (o aviso ainda não tem loja). O app_user só lida com esta tabela; o padrão de
            // privilégios do app_user vale só para public, então a concessão aqui é explícita.
            migrationBuilder.Sql($"""
                REVOKE ALL ON SCHEMA inbox FROM PUBLIC;
                GRANT USAGE ON SCHEMA inbox TO {TenantDatabaseBootstrap.AppRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON inbox.external_events TO {TenantDatabaseBootstrap.AppRole};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_events",
                schema: "inbox");

            migrationBuilder.Sql("DROP SCHEMA IF EXISTS inbox");
        }
    }
}
