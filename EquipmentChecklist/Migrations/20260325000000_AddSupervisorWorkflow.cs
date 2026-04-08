using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EquipmentChecklist.Migrations
{
    /// <inheritdoc />
    public partial class AddSupervisorWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add RejectionReason and RejectedMechanicId to ChecklistSubmissions
            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "ChecklistSubmissions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedMechanicId",
                table: "ChecklistSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistSubmissions_RejectedMechanicId",
                table: "ChecklistSubmissions",
                column: "RejectedMechanicId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChecklistSubmissions_AspNetUsers_RejectedMechanicId",
                table: "ChecklistSubmissions",
                column: "RejectedMechanicId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Create OperatorSupervisorAssignments table
            migrationBuilder.CreateTable(
                name: "OperatorSupervisorAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperatorId   = table.Column<string>(type: "text", nullable: false),
                    SupervisorId = table.Column<string>(type: "text", nullable: false),
                    AssignedAt   = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive     = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorSupervisorAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatorSupervisorAssignments_AspNetUsers_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperatorSupervisorAssignments_AspNetUsers_SupervisorId",
                        column: x => x.SupervisorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorSupervisorAssignments_OperatorId",
                table: "OperatorSupervisorAssignments",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorSupervisorAssignments_SupervisorId",
                table: "OperatorSupervisorAssignments",
                column: "SupervisorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "OperatorSupervisorAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_ChecklistSubmissions_AspNetUsers_RejectedMechanicId",
                table: "ChecklistSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_ChecklistSubmissions_RejectedMechanicId",
                table: "ChecklistSubmissions");

            migrationBuilder.DropColumn(name: "RejectionReason",    table: "ChecklistSubmissions");
            migrationBuilder.DropColumn(name: "RejectedMechanicId", table: "ChecklistSubmissions");
        }
    }
}
