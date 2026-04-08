using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddMechanicToMachineAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MechanicId",
                table: "MachineAssignments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineAssignments_MechanicId",
                table: "MachineAssignments",
                column: "MechanicId");

            migrationBuilder.AddForeignKey(
                name: "FK_MachineAssignments_AspNetUsers_MechanicId",
                table: "MachineAssignments",
                column: "MechanicId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MachineAssignments_AspNetUsers_MechanicId",
                table: "MachineAssignments");

            migrationBuilder.DropIndex(
                name: "IX_MachineAssignments_MechanicId",
                table: "MachineAssignments");

            migrationBuilder.DropColumn(
                name: "MechanicId",
                table: "MachineAssignments");
        }
    }
}
