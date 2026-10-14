using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class CustomDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_check_message",
                table: "tenant_domains",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_checked_at",
                table: "tenant_domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "requested_at",
                table: "tenant_domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_token",
                table: "tenant_domains",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_verification_status",
                table: "tenant_domains",
                column: "verification_status",
                filter: "verification_status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_domains_verification_status",
                table: "tenant_domains");

            migrationBuilder.DropColumn(
                name: "last_check_message",
                table: "tenant_domains");

            migrationBuilder.DropColumn(
                name: "last_checked_at",
                table: "tenant_domains");

            migrationBuilder.DropColumn(
                name: "requested_at",
                table: "tenant_domains");

            migrationBuilder.DropColumn(
                name: "verification_token",
                table: "tenant_domains");
        }
    }
}
