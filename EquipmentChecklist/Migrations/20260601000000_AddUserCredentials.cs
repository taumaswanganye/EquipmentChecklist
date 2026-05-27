using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId       = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey    = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount    = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    DeviceLabel  = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AaGuid       = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'"),
                    PinHash      = table.Column<string>(type: "text", nullable: true),
                    CreatedAt    = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt   = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive     = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCredentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserCredentials_UserId",
                table: "UserCredentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCredentials_CredentialId",
                table: "UserCredentials",
                column: "CredentialId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserCredentials");
        }
    }
}
