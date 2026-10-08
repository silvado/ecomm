using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "plan_code",
                table: "tenants",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "essencial");

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.UniqueConstraint("ak_plans_code", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "user_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_limits",
                columns: table => new
                {
                    plan_code = table.Column<string>(type: "character varying(30)", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_limits", x => new { x.plan_code, x.key });
                    table.ForeignKey(
                        name: "fk_plan_limits_plans_plan_code",
                        column: x => x.plan_code,
                        principalTable: "plans",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_user_accounts_user_id",
                        column: x => x.user_id,
                        principalTable: "user_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_memberships", x => new { x.tenant_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_tenant_memberships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tenant_memberships_user_accounts_user_id",
                        column: x => x.user_id,
                        principalTable: "user_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "plans",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { new Guid("0199c4a0-0000-7000-8000-000000000001"), "essencial", "Essencial" },
                    { new Guid("0199c4a0-0000-7000-8000-000000000002"), "profissional", "Profissional" },
                    { new Guid("0199c4a0-0000-7000-8000-000000000003"), "completo", "Completo" }
                });

            migrationBuilder.InsertData(
                table: "plan_limits",
                columns: new[] { "key", "plan_code", "value" },
                values: new object[,]
                {
                    { "users", "completo", 10 },
                    { "users", "essencial", 2 },
                    { "users", "profissional", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_plan_code",
                table: "tenants",
                column: "plan_code");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id_tenant_id",
                table: "refresh_tokens",
                columns: new[] { "user_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_memberships_user_id",
                table: "tenant_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_accounts_email",
                table: "user_accounts",
                column: "email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tenants_plans_plan_code",
                table: "tenants",
                column: "plan_code",
                principalTable: "plans",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tenants_plans_plan_code",
                table: "tenants");

            migrationBuilder.DropTable(
                name: "plan_limits");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "tenant_memberships");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "user_accounts");

            migrationBuilder.DropIndex(
                name: "ix_tenants_plan_code",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "plan_code",
                table: "tenants");
        }
    }
}
