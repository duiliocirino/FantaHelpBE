using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fantahelp.API.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTeamOwnerNameField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerName",
                table: "Teams");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerName",
                table: "Teams",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
