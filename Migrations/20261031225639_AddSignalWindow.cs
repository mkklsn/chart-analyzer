using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChartAnalyzer.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WindowMinutes",
                table: "Signals",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowMinutes",
                table: "Signals");
        }
    }
}
