using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicMaps",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    AutoRegenerate = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    RegenerateIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    GenerationStatus = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "pending"),
                    LastGeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastGenerationDurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    TileCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    GenerationProgress = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    GenerationError = table.Column<string>(type: "TEXT", nullable: true),
                    MinX = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxX = table.Column<int>(type: "INTEGER", nullable: true),
                    MinY = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxY = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicMapSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicMapId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicMapSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicMapSources_PublicMaps_PublicMapId",
                        column: x => x.PublicMapId,
                        principalTable: "PublicMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicMapSources_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicMaps_GenerationStatus",
                table: "PublicMaps",
                column: "GenerationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMaps_IsActive",
                table: "PublicMaps",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSources_PublicMapId",
                table: "PublicMapSources",
                column: "PublicMapId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSources_PublicMapId_TenantId_MapId",
                table: "PublicMapSources",
                columns: new[] { "PublicMapId", "TenantId", "MapId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSources_TenantId",
                table: "PublicMapSources",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicMapSources");

            migrationBuilder.DropTable(
                name: "PublicMaps");
        }
    }
}
