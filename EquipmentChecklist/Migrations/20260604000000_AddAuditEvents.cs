using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <summary>
    /// Adds the append-only <c>AuditEvents</c> table that records every
    /// meaningful action on the system (submissions, sign-offs, rejects,
    /// repairs, sign-ins). One row per event, never updated, never deleted.
    ///
    /// <para>Uses idempotent raw SQL (CREATE TABLE IF NOT EXISTS,
    /// CREATE INDEX IF NOT EXISTS) so the boot-time auto-migrate is safe
    /// to re-run on a DB that already has the table.</para>
    /// </summary>
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Table.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "AuditEvents" (
                    "Id"               bigserial PRIMARY KEY,
                    "ActorUserId"      character varying(450) NULL,
                    "ActorName"        character varying(120) NULL,
                    "ActorEmail"       character varying(256) NULL,
                    "ActorRole"        character varying(40)  NULL,
                    "Action"           character varying(60)  NOT NULL,
                    "TargetType"       character varying(40)  NULL,
                    "TargetId"         bigint                 NULL,
                    "PayloadJson"      jsonb                  NULL,
                    "OccurredAtClient" timestamp with time zone NOT NULL,
                    "OccurredAtServer" timestamp with time zone NOT NULL,
                    "DeviceKind"       character varying(20)  NOT NULL DEFAULT 'web',
                    "IpAddress"        character varying(64)  NULL
                );
                """);

            // Indexes. Created separately with IF NOT EXISTS so the migration
            // tolerates a partial prior run.
            //
            //  · By actor + time: "what did Sipho do this morning?"
            //  · By target: "show me everything that's happened to submission #1247"
            //  · By time only: the admin list view's default sort
            //  · By action + time: stat dashboards / counts
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditEvents_ActorUserId_OccurredAtServer\" " +
                "ON \"AuditEvents\" (\"ActorUserId\", \"OccurredAtServer\" DESC);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditEvents_TargetType_TargetId\" " +
                "ON \"AuditEvents\" (\"TargetType\", \"TargetId\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditEvents_OccurredAtServer\" " +
                "ON \"AuditEvents\" (\"OccurredAtServer\" DESC);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditEvents_Action_OccurredAtServer\" " +
                "ON \"AuditEvents\" (\"Action\", \"OccurredAtServer\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP TABLE cascades the indexes.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"AuditEvents\";");
        }
    }
}
