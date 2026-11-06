using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChartAnalyzer.Migrations
{
    /// <inheritdoc />
    public partial class AddZoneAbsorptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ZoneAbsorptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneLow = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    ZoneHigh = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Side = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Minute = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Level = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SpotVolume = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Exchanges = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PerpsOnly = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    ResolvedMinute = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedClose = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneAbsorptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ZoneAbsorptions_Minute",
                table: "ZoneAbsorptions",
                column: "Minute");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ZoneAbsorptions");
        }
    }
}
