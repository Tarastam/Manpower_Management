using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class LeadersPerLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Leaders are now assigned to a line: move each row to the line above its workshop, then keep one row per leader + line.
            migrationBuilder.Sql(@"
WITH tree AS (
    SELECT Id, Id AS Root, ParentId, Name FROM mp.Workshops
    UNION ALL
    SELECT t.Id, p.Id, p.ParentId, p.Name FROM tree t JOIN mp.Workshops p ON p.Id = t.ParentId)
SELECT l.Id, t.Root INTO #leaderLine
FROM mp.AttendanceLeaders l JOIN tree t ON t.Id = l.WorkshopId
WHERE t.Name IN (N'Total Chemical Line', N'Total Assembly Line');
WITH ranked AS (
    SELECT l.Id, ROW_NUMBER() OVER (PARTITION BY l.EmployeeCode, ISNULL(x.Root, l.WorkshopId) ORDER BY l.IsActive DESC, l.Id) AS rn
    FROM mp.AttendanceLeaders l LEFT JOIN #leaderLine x ON x.Id = l.Id)
DELETE FROM mp.AttendanceLeaders WHERE Id IN (SELECT Id FROM ranked WHERE rn > 1);
UPDATE l SET WorkshopId = x.Root FROM mp.AttendanceLeaders l JOIN #leaderLine x ON x.Id = l.Id;
DROP TABLE #leaderLine;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
