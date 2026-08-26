using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodValueProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ValueSource",
                table: "Foods",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Wiki");

            migrationBuilder.AddColumn<string>(
                name: "ValueWorld",
                table: "Foods",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ValueSource",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "ValueWorld",
                table: "Foods");
        }
    }
}
