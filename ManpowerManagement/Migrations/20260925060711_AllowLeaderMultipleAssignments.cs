using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class AllowLeaderMultipleAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceLeaders_EmployeeCode",
                schema: "mp",
                table: "AttendanceLeaders");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLeaders_EmployeeCode_WorkshopId_Shift",
                schema: "mp",
                table: "AttendanceLeaders",
                columns: new[] { "EmployeeCode", "WorkshopId", "Shift" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceLeaders_EmployeeCode_WorkshopId_Shift",
                schema: "mp",
                table: "AttendanceLeaders");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLeaders_EmployeeCode",
                schema: "mp",
                table: "AttendanceLeaders",
                column: "EmployeeCode",
                unique: true);
        }
    }
}
