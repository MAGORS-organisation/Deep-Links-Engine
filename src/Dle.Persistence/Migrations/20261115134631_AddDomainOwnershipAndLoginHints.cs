using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dle.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261115134631_AddDomainOwnershipAndLoginHints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ownership_verified_at",
            table: "domains",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "click_login_hints",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                click_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                link_id = table.Column<long>(type: "bigint", nullable: true),
                deeplink_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_click_login_hints", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_click_login_hints_expires",
            table: "click_login_hints",
            column: "expires_at");

        migrationBuilder.CreateIndex(
            name: "ix_click_login_hints_lookup",
            table: "click_login_hints",
            columns: new[] { "tenant_id", "login_key_hash", "occurred_at" },
            descending: new[] { false, false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "click_login_hints");

        migrationBuilder.DropColumn(
            name: "ownership_verified_at",
            table: "domains");
    }
}
