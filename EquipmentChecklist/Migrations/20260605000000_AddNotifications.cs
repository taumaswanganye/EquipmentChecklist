using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <summary>
    /// Adds the <c>Notifications</c> table for the cross-role inbox.
    ///
    /// <para>The Notification entity was added to the model during the
    /// cross-role notification round but the migration never shipped — so a
    /// fresh DB has the table from the snapshot but an upgraded DB does not,
    /// and any call to <c>NotificationService.PushAsync</c> throws
    /// <c>42P01: relation "Notifications" does not exist</c>.</para>
    ///
    /// <para>That manifested as "An error occurred while saving the entity
    /// changes" on the web's Checklist submit path — the submission save
    /// succeeded, then <c>ProcessSubmissionAsync</c> tried to notify the
    /// operator's supervisor on GO-BUT/NO-GO and the notification write
    /// killed the request.</para>
    ///
    /// <para>Idempotent SQL so the boot-time auto-migrate is safe to re-run
    /// on a DB where the table happens to already exist.</para>
    /// </summary>
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "Notifications" (
                    "Id"                   serial PRIMARY KEY,
                    "UserId"               character varying(450) NOT NULL,
                    "Kind"                 character varying(60)  NOT NULL,
                    "Title"                character varying(160) NOT NULL,
                    "Body"                 character varying(500) NULL,
                    "PayloadJson"          text                   NULL,
                    "RelatedSubmissionId"  integer                NULL,
                    "RelatedMachineId"     integer                NULL,
                    "CreatedAt"            timestamp with time zone NOT NULL,
                    "ReadAt"               timestamp with time zone NULL,
                    CONSTRAINT "FK_Notifications_AspNetUsers_UserId"
                        FOREIGN KEY ("UserId")
                        REFERENCES "AspNetUsers" ("Id")
                        ON DELETE CASCADE
                );
                """);

            // Indexes — the bell-badge query is by-user-by-time, ordered
            // descending, so this is the workhorse. The other two help the
            // "deep-link from a notification" path and any admin reporting.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Notifications_UserId_CreatedAt\" " +
                "ON \"Notifications\" (\"UserId\", \"CreatedAt\" DESC);");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Notifications_UserId\" " +
                "ON \"Notifications\" (\"UserId\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Notifications_CreatedAt\" " +
                "ON \"Notifications\" (\"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"Notifications\";");
        }
    }
}
