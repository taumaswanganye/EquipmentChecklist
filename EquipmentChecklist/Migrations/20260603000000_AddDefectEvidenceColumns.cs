using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <summary>
    /// Adds the four defect-evidence columns to <c>SubmissionItems</c>:
    /// <list type="bullet">
    ///   <item><description><c>PhotoData</c> / <c>PhotoMimeType</c> — defect photo
    ///   captured at submit time (Round 1 of the mobile evidence work).</description></item>
    ///   <item><description><c>AudioData</c> / <c>AudioMimeType</c> — optional voice
    ///   memo recorded by the operator (Round 2).</description></item>
    /// </list>
    ///
    /// <para>These properties were added to <c>Models.SubmissionItem</c> during
    /// the mobile rounds but the matching migration never shipped, so a server
    /// hitting Postgres with the new model throws:</para>
    /// <code>
    ///   PostgresException: 42703: column s.AudioData does not exist
    /// </code>
    ///
    /// <para>The columns are all nullable so existing rows don't need a backfill.</para>
    /// </summary>
    public partial class AddDefectEvidenceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Why raw SQL instead of MigrationBuilder.AddColumn(): the user's
            // DB may already have one or more of these columns from a manual
            // hot-fix issued earlier when the Postgres 42703 first appeared.
            // EF's AddColumn doesn't emit IF NOT EXISTS, so the first call
            // would throw "column already exists" and roll back the whole
            // transaction — leaving the other three columns un-added and the
            // migration paradoxically marked as failed mid-flight.
            //
            // Postgres's ADD COLUMN IF NOT EXISTS makes this safe to run from
            // any starting state: clean DB, fully-hot-fixed DB, partial mix.
            migrationBuilder.Sql(
                """
                ALTER TABLE "SubmissionItems"
                    ADD COLUMN IF NOT EXISTS "PhotoData"     bytea,
                    ADD COLUMN IF NOT EXISTS "PhotoMimeType" character varying(50),
                    ADD COLUMN IF NOT EXISTS "AudioData"     bytea,
                    ADD COLUMN IF NOT EXISTS "AudioMimeType" character varying(50);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric: DROP COLUMN IF EXISTS so a partial-rollback scenario
            // (someone manually dropped one column before running Down) doesn't
            // bomb out either.
            migrationBuilder.Sql(
                """
                ALTER TABLE "SubmissionItems"
                    DROP COLUMN IF EXISTS "AudioMimeType",
                    DROP COLUMN IF EXISTS "AudioData",
                    DROP COLUMN IF EXISTS "PhotoMimeType",
                    DROP COLUMN IF EXISTS "PhotoData";
                """);
        }
    }
}
