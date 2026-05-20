using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddIconLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IconLibraryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name             = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FilePath         = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType      = table.Column<string>(type: "character varying(80)",  maxLength: 80,  nullable: true),
                    FileSize         = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt       = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedById     = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_IconLibraryItems", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_IconLibraryItems_Name",
                table: "IconLibraryItems",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IconLibraryItems");
        }
    }
}
