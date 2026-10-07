using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftManpowerAndCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnteredBy",
                schema: "mp",
                table: "AttendanceEntries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShiftManpowers",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WorkshopId = table.Column<int>(type: "int", nullable: false),
                    Shift = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DirectCount = table.Column<int>(type: "int", nullable: false),
                    IndirectCount = table.Column<int>(type: "int", nullable: false),
                    EnteredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftManpowers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShiftManpowers_Workshops_WorkshopId",
                        column: x => x.WorkshopId,
                        principalSchema: "mp",
                        principalTable: "Workshops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftManpowers_BusinessDate_WorkshopId_Shift",
                schema: "mp",
                table: "ShiftManpowers",
                columns: new[] { "BusinessDate", "WorkshopId", "Shift" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShiftManpowers_WorkshopId",
                schema: "mp",
                table: "ShiftManpowers",
                column: "WorkshopId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShiftManpowers",
                schema: "mp");

            migrationBuilder.DropColumn(
                name: "EnteredBy",
                schema: "mp",
                table: "AttendanceEntries");
        }
    }
}
