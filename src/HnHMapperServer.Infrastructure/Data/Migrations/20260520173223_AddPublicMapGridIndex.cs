using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicMapGridIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicMapGridIndex",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicMapId = table.Column<string>(type: "TEXT", nullable: false),
                    UnifiedX = table.Column<int>(type: "INTEGER", nullable: false),
                    UnifiedY = table.Column<int>(type: "INTEGER", nullable: false),
                    GridId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotCache = table.Column<long>(type: "INTEGER", nullable: false),
                    IndexedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMapGridIndex", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicMapGridIndex_PublicMaps_PublicMapId",
                        column: x => x.PublicMapId,
                        principalTable: "PublicMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapGridIndex_PublicMapId_GridId",
                table: "PublicMapGridIndex",
                columns: new[] { "PublicMapId", "GridId" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapGridIndex_PublicMapId_UnifiedX_UnifiedY",
                table: "PublicMapGridIndex",
                columns: new[] { "PublicMapId", "UnifiedX", "UnifiedY" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicMapGridIndex");
        }
    }
}
