using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManpowerManagement.Migrations
{
    /// <inheritdoc />
    public partial class AddIndirectHourBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Indirect1067Count",
                schema: "mp",
                table: "ShiftManpowers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Indirect733Count",
                schema: "mp",
                table: "ShiftManpowers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Indirect841Count",
                schema: "mp",
                table: "ShiftManpowers",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Indirect1067Count",
                schema: "mp",
                table: "ShiftManpowers");

            migrationBuilder.DropColumn(
                name: "Indirect733Count",
                schema: "mp",
                table: "ShiftManpowers");

            migrationBuilder.DropColumn(
                name: "Indirect841Count",
                schema: "mp",
                table: "ShiftManpowers");
        }
    }
}
