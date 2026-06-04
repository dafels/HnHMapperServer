using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicMapSourceTypeProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceTenantId_SourceMapId",
                table: "PublicMapSourceAlignments");

            migrationBuilder.AlterColumn<string>(
                name: "SourceTenantId",
                table: "PublicMapSourceAlignments",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "SourceMapId",
                table: "PublicMapSourceAlignments",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "SourceHmapId",
                table: "PublicMapSourceAlignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "PublicMapSourceAlignments",
                type: "TEXT",
                nullable: false,
                defaultValue: "Tenant");

            migrationBuilder.AddColumn<int>(
                name: "SourceHmapId",
                table: "PublicMapGridIndex",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "PublicMapGridIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceHmapId",
                table: "PublicMapSourceAlignments",
                columns: new[] { "PublicMapId", "SourceHmapId" },
                unique: true,
                filter: "\"SourceType\" = 'Hmap'");

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceTenantId_SourceMapId",
                table: "PublicMapSourceAlignments",
                columns: new[] { "PublicMapId", "SourceTenantId", "SourceMapId" },
                unique: true,
                filter: "\"SourceType\" = 'Tenant'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceHmapId",
                table: "PublicMapSourceAlignments");

            migrationBuilder.DropIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceTenantId_SourceMapId",
                table: "PublicMapSourceAlignments");

            migrationBuilder.DropColumn(
                name: "SourceHmapId",
                table: "PublicMapSourceAlignments");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "PublicMapSourceAlignments");

            migrationBuilder.DropColumn(
                name: "SourceHmapId",
                table: "PublicMapGridIndex");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "PublicMapGridIndex");

            migrationBuilder.AlterColumn<string>(
                name: "SourceTenantId",
                table: "PublicMapSourceAlignments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SourceMapId",
                table: "PublicMapSourceAlignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicMapSourceAlignments_PublicMapId_SourceTenantId_SourceMapId",
                table: "PublicMapSourceAlignments",
                columns: new[] { "PublicMapId", "SourceTenantId", "SourceMapId" },
                unique: true);
        }
    }
}
