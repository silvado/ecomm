using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_admins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    totp_secret_cipher = table.Column<byte[]>(type: "bytea", nullable: true),
                    totp_key_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    totp_last_step = table.Column<long>(type: "bigint", nullable: true),
                    mfa_enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_admins", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admin_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_admin_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_admin_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_admin_recovery_codes_platform_admins_admin_id",
                        column: x => x.admin_id,
                        principalTable: "platform_admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_admin_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_admin_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_admin_sessions_platform_admins_admin_id",
                        column: x => x.admin_id,
                        principalTable: "platform_admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_recovery_codes_admin_id_code_hash",
                table: "platform_admin_recovery_codes",
                columns: new[] { "admin_id", "code_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_sessions_admin_id",
                table: "platform_admin_sessions",
                column: "admin_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_sessions_token_hash",
                table: "platform_admin_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_admins_email",
                table: "platform_admins",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_log_at",
                table: "platform_audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_log_tenant_id_at",
                table: "platform_audit_log",
                columns: new[] { "tenant_id", "at" });

            // ADR-0007: auditoria somente inclusão — a aplicação lê e acrescenta, nunca altera nem apaga.
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Persistence.TenantDatabaseBootstrap.AppRole}') THEN
                        REVOKE UPDATE, DELETE, TRUNCATE ON platform_audit_log FROM {Persistence.TenantDatabaseBootstrap.AppRole};
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_admin_recovery_codes");

            migrationBuilder.DropTable(
                name: "platform_admin_sessions");

            migrationBuilder.DropTable(
                name: "platform_audit_log");

            migrationBuilder.DropTable(
                name: "platform_admins");
        }
    }
}
