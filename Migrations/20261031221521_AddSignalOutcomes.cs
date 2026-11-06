using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChartAnalyzer.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Signals_CandleTime",
                table: "Signals");

            migrationBuilder.AddColumn<decimal>(
                name: "Close15m",
                table: "Signals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Close30m",
                table: "Signals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Close5m",
                table: "Signals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "High30m",
                table: "Signals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Low30m",
                table: "Signals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Signals_CandleTime_Symbol_Side",
                table: "Signals",
                columns: new[] { "CandleTime", "Symbol", "Side" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Signals_CandleTime_Symbol_Side",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Close15m",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Close30m",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Close5m",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "High30m",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Low30m",
                table: "Signals");

            migrationBuilder.CreateIndex(
                name: "IX_Signals_CandleTime",
                table: "Signals",
                column: "CandleTime");
        }
    }
}
