using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fantahelp.API.Migrations
{
    /// <inheritdoc />
    public partial class LeagueGoalBonusPerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GoalBonusPerRole",
                table: "Leagues",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoalBonusPerRole",
                table: "Leagues");
        }
    }
}
