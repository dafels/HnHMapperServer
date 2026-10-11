using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDirtyWebpCells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirtyWebpCells",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    CellX = table.Column<int>(type: "INTEGER", nullable: false),
                    CellY = table.Column<int>(type: "INTEGER", nullable: false),
                    MarkedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirtyWebpCells", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirtyWebpCells_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirtyWebpCells_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirtyWebpCells_MapId",
                table: "DirtyWebpCells",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_DirtyWebpCells_TenantId_MapId_CellX_CellY",
                table: "DirtyWebpCells",
                columns: new[] { "TenantId", "MapId", "CellX", "CellY" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirtyWebpCells_TenantId_MarkedAt",
                table: "DirtyWebpCells",
                columns: new[] { "TenantId", "MarkedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirtyWebpCells");
        }
    }
}
