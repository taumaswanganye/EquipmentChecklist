using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <summary>
    /// Adds the admin-clearance gate to <c>Machines</c>:
    /// <list type="bullet">
    ///   <item><description><c>AwaitingAdminClearance</c> — true once a
    ///   mechanic completes the last open defect; false again after the
    ///   admin signs off.</description></item>
    ///   <item><description><c>ClearedByAdminId</c> / <c>ClearedAt</c> /
    ///   <c>AdminClearanceNotes</c> — who released the machine and when, with
    ///   optional notes recorded on the admin's clearance action.</description></item>
    /// </list>
    ///
    /// <para>Idempotent SQL so the boot-time auto-migrate is safe to re-run.</para>
    /// </summary>
    public partial class AddAdminClearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Machines"
                    ADD COLUMN IF NOT EXISTS "AwaitingAdminClearance" boolean       NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS "ClearedByAdminId"       character varying(450) NULL,
                    ADD COLUMN IF NOT EXISTS "ClearedAt"              timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "AdminClearanceNotes"    character varying(500) NULL;
                """);

            // FK to the admin who cleared (nullable, set-null on user delete
            // so we keep the historical clearance record but stop pointing at
            // a deleted user). Created via separate DO block so re-running is
            // safe — IF NOT EXISTS on constraints landed in PG 9.6.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'FK_Machines_AspNetUsers_ClearedByAdminId'
                    ) THEN
                        ALTER TABLE "Machines"
                            ADD CONSTRAINT "FK_Machines_AspNetUsers_ClearedByAdminId"
                            FOREIGN KEY ("ClearedByAdminId")
                            REFERENCES "AspNetUsers" ("Id")
                            ON DELETE SET NULL;
                    END IF;
                END $$;
                """);

            // Index lets the admin "Pending Clearances" list page filter
            // efficiently — should always be a tiny partial set anyway.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Machines_AwaitingAdminClearance\" " +
                "ON \"Machines\" (\"AwaitingAdminClearance\") " +
                "WHERE \"AwaitingAdminClearance\" = true;");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Machines_ClearedByAdminId\" " +
                "ON \"Machines\" (\"ClearedByAdminId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Machines"
                    DROP CONSTRAINT IF EXISTS "FK_Machines_AspNetUsers_ClearedByAdminId",
                    DROP COLUMN IF EXISTS "AdminClearanceNotes",
                    DROP COLUMN IF EXISTS "ClearedAt",
                    DROP COLUMN IF EXISTS "ClearedByAdminId",
                    DROP COLUMN IF EXISTS "AwaitingAdminClearance";
                """);
        }
    }
}
