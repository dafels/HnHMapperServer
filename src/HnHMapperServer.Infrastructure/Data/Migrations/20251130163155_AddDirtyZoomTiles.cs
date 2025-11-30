using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDirtyZoomTiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirtyZoomTiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    CoordX = table.Column<int>(type: "INTEGER", nullable: false),
                    CoordY = table.Column<int>(type: "INTEGER", nullable: false),
                    Zoom = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirtyZoomTiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirtyZoomTiles_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirtyZoomTiles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirtyZoomTiles_MapId",
                table: "DirtyZoomTiles",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_DirtyZoomTiles_TenantId",
                table: "DirtyZoomTiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DirtyZoomTiles_TenantId_MapId_CoordX_CoordY_Zoom",
                table: "DirtyZoomTiles",
                columns: new[] { "TenantId", "MapId", "CoordX", "CoordY", "Zoom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirtyZoomTiles_TenantId_Zoom_MapId",
                table: "DirtyZoomTiles",
                columns: new[] { "TenantId", "Zoom", "MapId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirtyZoomTiles");
        }
    }
}
