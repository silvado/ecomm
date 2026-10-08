using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Vault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_data_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_data_keys", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_secrets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_secrets_tenant_id_kind_name",
                table: "tenant_secrets",
                columns: new[] { "tenant_id", "kind", "name" },
                unique: true);

            migrationBuilder.Sql(RowLevelSecurity.EnableFor("tenant_secrets"));
            migrationBuilder.Sql(RowLevelSecurity.EnableFor("tenant_data_keys"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("tenant_secrets"));
            migrationBuilder.Sql(RowLevelSecurity.DisableFor("tenant_data_keys"));

            migrationBuilder.DropTable(
                name: "tenant_data_keys");

            migrationBuilder.DropTable(
                name: "tenant_secrets");
        }
    }
}
