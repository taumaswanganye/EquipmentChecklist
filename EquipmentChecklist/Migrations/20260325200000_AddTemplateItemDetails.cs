using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateItemDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StatusLabel",
                table: "ChecklistTemplateItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "ChecklistTemplateItems",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InOrderCondition",
                table: "ChecklistTemplateItems",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefectCondition",
                table: "ChecklistTemplateItems",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IconPath",
                table: "ChecklistTemplateItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StatusLabel",       table: "ChecklistTemplateItems");
            migrationBuilder.DropColumn(name: "Action",            table: "ChecklistTemplateItems");
            migrationBuilder.DropColumn(name: "InOrderCondition",  table: "ChecklistTemplateItems");
            migrationBuilder.DropColumn(name: "DefectCondition",   table: "ChecklistTemplateItems");
            migrationBuilder.DropColumn(name: "IconPath",          table: "ChecklistTemplateItems");
        }
    }
}
