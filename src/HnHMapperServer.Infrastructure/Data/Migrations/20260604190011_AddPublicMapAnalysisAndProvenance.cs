using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicMapAnalysisAndProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceMapId",
                table: "PublicMapGridIndex",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTenantId",
                table: "PublicMapGridIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PublicMapAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicMapId = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AlignmentHash = table.Column<string>(type: "TEXT", nullable: false),
                    ClusterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StandaloneCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EstMinX = table.Column<int>(type: "INTEGER", nullable: true),
                    EstMaxX = table.Column<int>(type: "INTEGER", nullable: true),
                    EstMinY = table.Column<int>(type: "INTEGER", nullable: true),
                    EstMaxY = table.Column<int>(type: "INTEGER", nullable: true),
                    EstZoom0TileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EstTotalTileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMapAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicMapAnalyses_PublicMaps_PublicMapId",
                        column: x => x.PublicMapId,
                        principalTable: "PublicMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicMapSourceAlignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicMapId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceMapId = table.Column<int>(type: "INTEGER", nullable: false),
                    ComponentIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    UnifiedOffsetX = table.Column<int>(type: "INTEGER", nullable: false),
                    UnifiedOffsetY = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchCountToComponent = table.Column<int>(type: "INTEGER", nullable: false),
                    AlignmentConfidence = table.Column<double>(type: "REAL", nullable: false),
                    IsStandalone = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMapSourceAlignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicMapSourceAlignments_PublicMaps_PublicMapId",
                        column: x => x.PublicMapId,
                        principalTable: "PublicMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapAnalyses_PublicMapId",
                table: "PublicMapAnalyses",
                column: "PublicMapId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceTenantId_SourceMapId",
                table: "PublicMapSourceAlignments",
                columns: new[] { "PublicMapId", "SourceTenantId", "SourceMapId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicMapAnalyses");

            migrationBuilder.DropTable(
                name: "PublicMapSourceAlignments");

            migrationBuilder.DropColumn(
                name: "SourceMapId",
                table: "PublicMapGridIndex");

            migrationBuilder.DropColumn(
                name: "SourceTenantId",
                table: "PublicMapGridIndex");
        }
    }
}
