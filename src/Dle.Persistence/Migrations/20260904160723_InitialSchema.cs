using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

using Dle.Persistence.Sql;

#nullable disable

namespace Dle.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand written, and preserved whenever this file is regenerated. What follows the
            // scaffolded block is schema PostgreSQL has and the EF Core model cannot express: a
            // version dependent identifier default, a declaratively partitioned table, a BRIN
            // index and a per table autovacuum setting (§B.5.2, §B.5.3).
            //
            // The identifier function comes first, because every CreateTable below names it as a
            // column default.
            migrationBuilder.Sql(DlePostgresScripts.CreateUuidV7Function);

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    bundle_id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: true),
                    cert_fingerprints = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    play_signing_fingerprints = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    store_id = table.Column<string>(type: "text", nullable: true),
                    custom_scheme = table.Column<string>(type: "text", nullable: true),
                    min_app_version = table.Column<string>(type: "text", nullable: true),
                    appclip_bundle_id = table.Column<string>(type: "text", nullable: true),
                    store_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_apps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "claim_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    click_id = table.Column<string>(type: "text", nullable: true),
                    link_id = table.Column<long>(type: "bigint", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "citext", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tls_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    aasa_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    assetlinks_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verification_log = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    consent_mode_override = table.Column<string>(type: "text", nullable: true),
                    branding = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    default_og = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domains", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.tenant_id, x.endpoint, x.key });
                });

            migrationBuilder.CreateTable(
                name: "signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kid = table.Column<string>(type: "text", nullable: false),
                    algorithm = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    private_key_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "slug_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    name = table.Column<string>(type: "text", nullable: false, defaultValue: "default"),
                    block_start = table.Column<long>(type: "bigint", nullable: false),
                    block_end = table.Column<long>(type: "bigint", nullable: false),
                    claimed_by = table.Column<string>(type: "text", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slug_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    slug = table.Column<string>(type: "citext", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    consent_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "aggregate_only"),
                    settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "installs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    install_id = table.Column<string>(type: "text", nullable: false),
                    first_open_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    raw_referrer = table.Column<string>(type: "text", nullable: true),
                    platform = table.Column<string>(type: "text", nullable: false),
                    app_version = table.Column<string>(type: "text", nullable: true),
                    login_key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installs", x => x.id);
                    table.ForeignKey(
                        name: "fk_installs_app",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sdk_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sdk_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_sdk_keys_app",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_domains",
                columns: table => new
                {
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_domains", x => new { x.app_id, x.domain_id });
                    table.ForeignKey(
                        name: "fk_app_domains_app",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_app_domains_domain",
                        column: x => x.domain_id,
                        principalTable: "domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "domain_verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    redirect_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    issues = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domain_verifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_domain_verifications_domain",
                        column: x => x.domain_id,
                        principalTable: "domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_keys_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    utm = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaigns_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "citext", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    target_url = table.Column<string>(type: "text", nullable: false),
                    deeplink_path = table.Column<string>(type: "text", nullable: true),
                    routing_rules = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    og_meta = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    utm = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expired_url = table.Column<string>(type: "text", nullable: true),
                    quarantined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_links_domain",
                        column: x => x.domain_id,
                        principalTable: "domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_links_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    event_types = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_subscriptions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "attributions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    click_id = table.Column<string>(type: "text", nullable: true),
                    link_id = table.Column<long>(type: "bigint", nullable: true),
                    match_type = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(3,2)", nullable: false),
                    matched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    window_seconds = table.Column<int>(type: "integer", nullable: true),
                    evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attributions", x => x.id);
                    table.ForeignKey(
                        name: "fk_attributions_install",
                        column: x => x.install_id,
                        principalTable: "installs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "abuse_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    link_id = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    reporter_email_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "new"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_abuse_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_abuse_reports_link",
                        column: x => x.link_id,
                        principalTable: "links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "link_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    link_id = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    change_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_versions_link",
                        column: x => x.link_id,
                        principalTable: "links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "dle_uuidv7()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    response_code = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_subscription",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_abuse_reports_link",
                table: "abuse_reports",
                column: "link_id");

            migrationBuilder.CreateIndex(
                name: "ix_abuse_reports_queue",
                table: "abuse_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_tenant",
                table: "api_keys",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_api_keys_prefix",
                table: "api_keys",
                column: "prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_domains_domain",
                table: "app_domains",
                column: "domain_id");

            migrationBuilder.CreateIndex(
                name: "uq_apps_tenant_platform_bundle",
                table: "apps",
                columns: new[] { "tenant_id", "platform", "bundle_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attributions_tenant_matched",
                table: "attributions",
                columns: new[] { "tenant_id", "matched_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "uq_attributions_click",
                table: "attributions",
                column: "click_id",
                unique: true,
                filter: "click_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_attributions_install",
                table: "attributions",
                column: "install_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject",
                table: "audit_log",
                columns: new[] { "tenant_id", "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_occurred",
                table: "audit_log",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_tenant_name",
                table: "campaigns",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_codes_expires",
                table: "claim_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "uq_claim_codes_tenant_hash",
                table: "claim_codes",
                columns: new[] { "tenant_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_domain_verifications_domain_kind_checked",
                table: "domain_verifications",
                columns: new[] { "domain_id", "kind", "checked_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_domains_tenant",
                table: "domains",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_domains_host",
                table: "domains",
                column: "host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_installs_login_key",
                table: "installs",
                columns: new[] { "tenant_id", "login_key_hash" },
                filter: "login_key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_installs_app_install",
                table: "installs",
                columns: new[] { "app_id", "install_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_link_versions_link_version",
                table: "link_versions",
                columns: new[] { "link_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_links_resolve",
                table: "links",
                columns: new[] { "domain_id", "slug" })
                .Annotation("Npgsql:IndexInclude", new[] { "target_url", "deeplink_path", "routing_rules", "og_meta", "is_active", "starts_at", "expires_at", "quarantined_at", "tenant_id", "id", "utm", "title", "campaign_id", "expired_url" });

            migrationBuilder.CreateIndex(
                name: "ix_links_tags",
                table: "links",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_campaign",
                table: "links",
                columns: new[] { "tenant_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_created",
                table: "links",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "uq_links_domain_slug",
                table: "links",
                columns: new[] { "domain_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sdk_keys_app",
                table: "sdk_keys",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "uq_sdk_keys_prefix",
                table: "sdk_keys",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_signing_keys_purpose_validity",
                table: "signing_keys",
                columns: new[] { "purpose", "not_after" });

            migrationBuilder.CreateIndex(
                name: "uq_signing_keys_current",
                table: "signing_keys",
                columns: new[] { "tenant_id", "purpose" },
                unique: true,
                filter: "is_current")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_signing_keys_kid",
                table: "signing_keys",
                column: "kid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_slug_sequences_name_start",
                table: "slug_sequences",
                columns: new[] { "name", "block_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_subscription_id",
                table: "webhook_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_due",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_created",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscriptions_event_types",
                table: "webhook_subscriptions",
                column: "event_types")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscriptions_tenant",
                table: "webhook_subscriptions",
                column: "tenant_id");

            // The click stream of §B.5.3 and the SDK event stream beside it: partitioned tables
            // with no EF Core equivalent, their BRIN and btree indexes, and the partition
            // maintenance. The fallback maintenance functions are created before the pg_partman
            // handover, because the handover calls them when pg_partman turns out not to be
            // installed.
            migrationBuilder.Sql(DlePostgresScripts.CreateFallbackMaintenanceFunction);
            migrationBuilder.Sql(DlePostgresScripts.CreateClickEvents);
            migrationBuilder.Sql(DlePostgresScripts.CreateSdkEvents);
            migrationBuilder.Sql(DlePostgresScripts.ConfigurePartitioning);

            // Keeps the visibility map fresh enough for ix_links_resolve to be answered from the
            // index alone (planner note in §B.5.2).
            migrationBuilder.Sql(DlePostgresScripts.TuneLinksAutovacuum);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hand written, and preserved whenever this file is regenerated. Unwound in the
            // reverse order of Up: the partition configuration before the table it points at, and
            // the table before the functions it depends on.
            migrationBuilder.Sql(DlePostgresScripts.RemovePartitioning);
            migrationBuilder.Sql(DlePostgresScripts.DropSdkEvents);
            migrationBuilder.Sql(DlePostgresScripts.DropClickEvents);
            migrationBuilder.Sql(DlePostgresScripts.DropFallbackMaintenanceFunction);
            migrationBuilder.Sql(DlePostgresScripts.ResetLinksAutovacuum);

            migrationBuilder.DropTable(
                name: "abuse_reports");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "app_domains");

            migrationBuilder.DropTable(
                name: "attributions");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropTable(
                name: "claim_codes");

            migrationBuilder.DropTable(
                name: "domain_verifications");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "link_versions");

            migrationBuilder.DropTable(
                name: "sdk_keys");

            migrationBuilder.DropTable(
                name: "signing_keys");

            migrationBuilder.DropTable(
                name: "slug_sequences");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "installs");

            migrationBuilder.DropTable(
                name: "links");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "domains");

            migrationBuilder.DropTable(
                name: "tenants");

            // Last, because every table that defaulted to it has gone by now.
            migrationBuilder.Sql(DlePostgresScripts.DropUuidV7Function);
        }
    }
}
