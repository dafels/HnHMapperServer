using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalRecipe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CookingStation",
                table: "Foods",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeText",
                table: "Foods",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CookingStation",
                table: "Foods");

            migrationBuilder.DropColumn(
                name: "RecipeText",
                table: "Foods");
        }
    }
}
