using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddDigitalSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperatorSignature",
                table: "ChecklistSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupervisorSignature",
                table: "ChecklistSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MechanicSignature",
                table: "DefectOrders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatorSignature",
                table: "ChecklistSubmissions");

            migrationBuilder.DropColumn(
                name: "SupervisorSignature",
                table: "ChecklistSubmissions");

            migrationBuilder.DropColumn(
                name: "MechanicSignature",
                table: "DefectOrders");
        }
    }
}
