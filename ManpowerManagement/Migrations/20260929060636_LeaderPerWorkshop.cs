using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class LeaderPerWorkshop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Leaders are now assigned per workshop: keep one row per leader + workshop (an active one if any) and clear the shift.
            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT Id, ROW_NUMBER() OVER (PARTITION BY EmployeeCode, WorkshopId ORDER BY IsActive DESC, Id) AS rn
    FROM mp.AttendanceLeaders)
DELETE FROM mp.AttendanceLeaders WHERE Id IN (SELECT Id FROM ranked WHERE rn > 1);
UPDATE mp.AttendanceLeaders SET Shift = N'';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
