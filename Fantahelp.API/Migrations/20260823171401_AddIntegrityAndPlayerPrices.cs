using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fantahelp.API.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrityAndPlayerPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Integrity",
                table: "Players",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerPrices",
                columns: table => new
                {
                    PlayerId = table.Column<int>(type: "integer", nullable: false),
                    Credits = table.Column<int>(type: "integer", nullable: false),
                    Starters = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    Std = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerPrices", x => new { x.PlayerId, x.Credits, x.Starters });
                    table.ForeignKey(
                        name: "FK_PlayerPrices_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerPrices");

            migrationBuilder.DropColumn(
                name: "Integrity",
                table: "Players");
        }
    }
}
