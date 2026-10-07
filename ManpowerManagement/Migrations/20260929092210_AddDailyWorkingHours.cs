using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyWorkingHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CalculatedAtUtc",
                schema: "mp",
                table: "ShiftManpowers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "IndirectHours",
                schema: "mp",
                table: "ShiftManpowers",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IndirectHoursIsManual",
                schema: "mp",
                table: "ShiftManpowers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalHours",
                schema: "mp",
                table: "ShiftManpowers",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalculatedAtUtc",
                schema: "mp",
                table: "ShiftManpowers");

            migrationBuilder.DropColumn(
                name: "IndirectHours",
                schema: "mp",
                table: "ShiftManpowers");

            migrationBuilder.DropColumn(
                name: "IndirectHoursIsManual",
                schema: "mp",
                table: "ShiftManpowers");

            migrationBuilder.DropColumn(
                name: "TotalHours",
                schema: "mp",
                table: "ShiftManpowers");
        }
    }
}
