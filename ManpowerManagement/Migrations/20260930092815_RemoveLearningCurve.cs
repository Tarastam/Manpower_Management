using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLearningCurve : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearningCurveTargets",
                schema: "mp");

            migrationBuilder.DropColumn(
                name: "LowOutputDays",
                schema: "mp",
                table: "NewHireRiskSettings");

            migrationBuilder.DropColumn(
                name: "OutputPercentOfTarget",
                schema: "mp",
                table: "NewHireRiskSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LowOutputDays",
                schema: "mp",
                table: "NewHireRiskSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputPercentOfTarget",
                schema: "mp",
                table: "NewHireRiskSettings",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "LearningCurveTargets",
                schema: "mp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkshopId = table.Column<int>(type: "int", nullable: false),
                    Process = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetKps = table.Column<decimal>(type: "decimal(9,3)", precision: 9, scale: 3, nullable: false),
                    TenureWeek = table.Column<int>(type: "int", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_LearningCurveTargets_WorkshopId_Process_TenureWeek",
                schema: "mp",
                table: "LearningCurveTargets",
                columns: new[] { "WorkshopId", "Process", "TenureWeek" },
                unique: true);
        }
    }
}
