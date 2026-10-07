using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class AddNewHireMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LearningCurveTargets",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkshopId = table.Column<int>(type: "int", nullable: false),
                    Process = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TenureWeek = table.Column<int>(type: "int", nullable: false),
                    TargetKps = table.Column<decimal>(type: "decimal(9,3)", precision: 9, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearningCurveTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LearningCurveTargets_Workshops_WorkshopId",
                        column: x => x.WorkshopId,
                        principalSchema: "mp",
                        principalTable: "Workshops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NewHireOutputs",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Kps = table.Column<decimal>(type: "decimal(9,3)", precision: 9, scale: 3, nullable: false),
                    EnteredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewHireOutputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewHireOutputs_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "mp",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewHireRiskSettings",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ConsecutiveAbsentDays = table.Column<int>(type: "int", nullable: false),
                    NoShowDays = table.Column<int>(type: "int", nullable: false),
                    LeaveRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    LeaveRateWindowDays = table.Column<int>(type: "int", nullable: false),
                    OutputPercentOfTarget = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    LowOutputDays = table.Column<int>(type: "int", nullable: false),
                    DecisionWarningDay = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewHireRiskSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProbationReviews",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExtendedUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LeaderComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HrAcknowledgedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HrAcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProbationReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProbationReviews_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "mp",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LearningCurveTargets_WorkshopId_Process_TenureWeek",
                schema: "mp",
                table: "LearningCurveTargets",
                columns: new[] { "WorkshopId", "Process", "TenureWeek" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewHireOutputs_EmployeeId_BusinessDate",
                schema: "mp",
                table: "NewHireOutputs",
                columns: new[] { "EmployeeId", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProbationReviews_EmployeeId_DecidedAtUtc",
                schema: "mp",
                table: "ProbationReviews",
                columns: new[] { "EmployeeId", "DecidedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearningCurveTargets",
                schema: "mp");

            migrationBuilder.DropTable(
                name: "NewHireOutputs",
                schema: "mp");

            migrationBuilder.DropTable(
                name: "NewHireRiskSettings",
                schema: "mp");

            migrationBuilder.DropTable(
                name: "ProbationReviews",
                schema: "mp");
        }
    }
}
