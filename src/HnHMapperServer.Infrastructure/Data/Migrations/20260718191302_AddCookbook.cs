using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCookbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Foods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ResourceName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Energy = table.Column<int>(type: "INTEGER", nullable: false),
                    Hunger = table.Column<decimal>(type: "TEXT", nullable: false),
                    WikiUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Categories = table.Column<string>(type: "TEXT", nullable: false),
                    SatiationGroups = table.Column<string>(type: "TEXT", nullable: false),
                    Feps = table.Column<string>(type: "TEXT", nullable: true),
                    Ingredients = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Foods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Foods_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FoodVariants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    FoodId = table.Column<int>(type: "INTEGER", nullable: false),
                    IngredientSignature = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Energy = table.Column<int>(type: "INTEGER", nullable: false),
                    Hunger = table.Column<decimal>(type: "TEXT", nullable: false),
                    TimesSeen = table.Column<int>(type: "INTEGER", nullable: false),
                    Feps = table.Column<string>(type: "TEXT", nullable: true),
                    Ingredients = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoodVariants_Foods_FoodId",
                        column: x => x.FoodId,
                        principalTable: "Foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FoodVariants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Foods_TenantId",
                table: "Foods",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Foods_TenantId_Name",
                table: "Foods",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FoodVariants_FoodId",
                table: "FoodVariants",
                column: "FoodId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodVariants_FoodId_IngredientSignature",
                table: "FoodVariants",
                columns: new[] { "FoodId", "IngredientSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FoodVariants_TenantId",
                table: "FoodVariants",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FoodVariants");

            migrationBuilder.DropTable(
                name: "Foods");
        }
    }
}
