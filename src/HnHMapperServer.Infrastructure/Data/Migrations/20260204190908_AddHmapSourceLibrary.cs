using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHmapSourceLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HmapSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", nullable: true),
                    TotalGrids = table.Column<int>(type: "INTEGER", nullable: true),
                    SegmentCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MinX = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxX = table.Column<int>(type: "INTEGER", nullable: true),
                    MinY = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxY = table.Column<int>(type: "INTEGER", nullable: true),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmapSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicMapHmapSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicMapId = table.Column<string>(type: "TEXT", nullable: false),
                    HmapSourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NewGrids = table.Column<int>(type: "INTEGER", nullable: true),
                    OverlappingGrids = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMapHmapSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicMapHmapSources_HmapSources_HmapSourceId",
                        column: x => x.HmapSourceId,
                        principalTable: "HmapSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicMapHmapSources_PublicMaps_PublicMapId",
                        column: x => x.PublicMapId,
                        principalTable: "PublicMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmapSources_Name",
                table: "HmapSources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_HmapSources_UploadedAt",
                table: "HmapSources",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapHmapSources_HmapSourceId",
                table: "PublicMapHmapSources",
                column: "HmapSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapHmapSources_PublicMapId",
                table: "PublicMapHmapSources",
                column: "PublicMapId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapHmapSources_PublicMapId_HmapSourceId",
                table: "PublicMapHmapSources",
                columns: new[] { "PublicMapId", "HmapSourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicMapHmapSources");

            migrationBuilder.DropTable(
                name: "HmapSources");
        }
    }
}
