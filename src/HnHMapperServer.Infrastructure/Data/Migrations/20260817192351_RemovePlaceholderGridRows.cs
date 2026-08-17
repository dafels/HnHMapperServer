using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePlaceholderGridRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data cleanup for the 2026-08 map-corruption incident: mapper clients mark
            // not-yet-loaded matrix cells with grid id "0", and the pre-hardening gridUpdate
            // path persisted those as real grids. A stored "0" then hijacked every later
            // partially-loaded matrix as a false anchor, stitching unrelated world regions
            // together. The code fix (placeholder guard in GridService/HmapImportService)
            // ships in the same release, so these rows cannot come back.
            // Cross-tenant on purpose — multiple tenants carry such rows.
            migrationBuilder.Sql("DELETE FROM Markers WHERE GridId = '0';"); // markers on a placeholder grid are dangling by definition
            migrationBuilder.Sql("DELETE FROM Grids WHERE Id = '0';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data cleanup; intentionally a no-op.
        }
    }
}
