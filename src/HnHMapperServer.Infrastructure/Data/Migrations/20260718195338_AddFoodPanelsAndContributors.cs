using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodPanelsAndContributors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContributedBy",
                table: "Foods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FoodPanels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsShared = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsFavorites = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodPanels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoodPanels_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FoodPanels_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FoodPanelItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PanelId = table.Column<int>(type: "INTEGER", nullable: false),
                    FoodName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IngredientSignature = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false, defaultValue: ""),
                    Label = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodPanelItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoodPanelItems_FoodPanels_PanelId",
                        column: x => x.PanelId,
                        principalTable: "FoodPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanelItems_PanelId",
                table: "FoodPanelItems",
                column: "PanelId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanelItems_PanelId_FoodName_IngredientSignature",
                table: "FoodPanelItems",
                columns: new[] { "PanelId", "FoodName", "IngredientSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanels_TenantId",
                table: "FoodPanels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanels_TenantId_UserId",
                table: "FoodPanels",
                columns: new[] { "TenantId", "UserId" },
                unique: true,
                filter: "\"IsFavorites\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanels_TenantId_UserId_Name",
                table: "FoodPanels",
                columns: new[] { "TenantId", "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FoodPanels_UserId",
                table: "FoodPanels",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FoodPanelItems");

            migrationBuilder.DropTable(
                name: "FoodPanels");

            migrationBuilder.DropColumn(
                name: "ContributedBy",
                table: "Foods");
        }
    }
}
