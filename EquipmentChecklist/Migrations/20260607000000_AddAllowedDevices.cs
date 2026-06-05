using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <summary>
    /// Adds the <c>AllowedDevices</c> table for the MDM-lite device
    /// allowlisting feature.
    ///
    /// <para>Idempotent — uses <c>CREATE TABLE IF NOT EXISTS</c> so the
    /// boot-time auto-migrate is safe to re-run on a DB where the table
    /// happens to already exist (test fixtures, copy-restore from prod).</para>
    /// </summary>
    public partial class AddAllowedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "AllowedDevices" (
                    "Id"                 serial PRIMARY KEY,
                    "DeviceFingerprint"  character varying(128) NOT NULL,
                    "Label"              character varying(100) NULL,
                    "Manufacturer"       character varying(80)  NULL,
                    "Model"              character varying(80)  NULL,
                    "Platform"           character varying(40)  NULL,
                    "OsVersion"          character varying(40)  NULL,
                    "AssignedUserId"     character varying(450) NULL,
                    "IsActive"           boolean                NOT NULL DEFAULT TRUE,
                    "ApprovedByAdminId"  character varying(450) NULL,
                    "CreatedAt"          timestamp with time zone NOT NULL,
                    "ApprovedAt"         timestamp with time zone NULL,
                    "LastSeenAt"         timestamp with time zone NULL,
                    "DeactivatedAt"      timestamp with time zone NULL,
                    "Notes"              character varying(500) NULL,
                    CONSTRAINT "FK_AllowedDevices_AspNetUsers_AssignedUserId"
                        FOREIGN KEY ("AssignedUserId")
                        REFERENCES "AspNetUsers" ("Id")
                        ON DELETE SET NULL,
                    CONSTRAINT "FK_AllowedDevices_AspNetUsers_ApprovedByAdminId"
                        FOREIGN KEY ("ApprovedByAdminId")
                        REFERENCES "AspNetUsers" ("Id")
                        ON DELETE SET NULL
                );
                """);

            // Unique fingerprint — also indexed for the lookup-on-every-request
            // path. The covering index includes IsActive so the JWT validator
            // can answer "is this fingerprint allowed right now?" without
            // hitting the heap.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AllowedDevices_DeviceFingerprint\" " +
                "ON \"AllowedDevices\" (\"DeviceFingerprint\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AllowedDevices_AssignedUserId\" " +
                "ON \"AllowedDevices\" (\"AssignedUserId\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AllowedDevices_ApprovedByAdminId\" " +
                "ON \"AllowedDevices\" (\"ApprovedByAdminId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"AllowedDevices\";");
        }
    }
}
