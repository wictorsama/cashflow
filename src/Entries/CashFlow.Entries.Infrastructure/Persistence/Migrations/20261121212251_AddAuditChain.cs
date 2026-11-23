using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CashFlow.Entries.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    entry_type = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    original_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prev_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_entry_id",
                table: "audit_log",
                column: "entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_merchant_id_seq",
                table: "audit_log",
                columns: new[] { "merchant_id", "seq" });

            // The audit log only grows. A privileged person can still drop the trigger, but the hash chain then shows
            // that the log was changed (see AuditTrailVerifier).
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_log_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_log is append-only (% refused)', TG_OP USING ERRCODE = 'restrict_violation';
                END;
                $$;
                CREATE TRIGGER audit_log_no_change BEFORE UPDATE OR DELETE ON audit_log
                    FOR EACH ROW EXECUTE FUNCTION audit_log_append_only();
                CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_append_only();
                """);

            // Entries registered before this moment are not expected to be in the chain.
            migrationBuilder.Sql("""
                CREATE TABLE audit_meta (key text PRIMARY KEY, value text NOT NULL);
                INSERT INTO audit_meta (key, value) VALUES ('started_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE audit_meta; DROP TRIGGER audit_log_no_truncate ON audit_log; DROP TRIGGER audit_log_no_change ON audit_log; DROP FUNCTION audit_log_append_only();");
            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
