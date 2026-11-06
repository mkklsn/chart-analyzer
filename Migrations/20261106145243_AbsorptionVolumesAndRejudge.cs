using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChartAnalyzer.Migrations
{
    /// <inheritdoc />
    public partial class AbsorptionVolumesAndRejudge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear the scoreboard: on startup it's rebuilt from the alerts with per-market volumes,
            // and re-judged with the new break buffer (old failures aren't alerted again).
            migrationBuilder.Sql("DELETE FROM ZoneAbsorptions");

            migrationBuilder.RenameColumn(
                name: "SpotVolume",
                table: "ZoneAbsorptions",
                newName: "BinanceVolume");

            migrationBuilder.AddColumn<decimal>(
                name: "CoinbaseVolume",
                table: "ZoneAbsorptions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PerpVolume",
                table: "ZoneAbsorptions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoinbaseVolume",
                table: "ZoneAbsorptions");

            migrationBuilder.DropColumn(
                name: "PerpVolume",
                table: "ZoneAbsorptions");

            migrationBuilder.RenameColumn(
                name: "BinanceVolume",
                table: "ZoneAbsorptions",
                newName: "SpotVolume");
        }
    }
}
