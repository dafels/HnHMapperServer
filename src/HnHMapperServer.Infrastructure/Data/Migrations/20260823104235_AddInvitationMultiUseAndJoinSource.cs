using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationMultiUseAndJoinSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvitationId",
                table: "TenantUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JoinSource",
                table: "TenantUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<int>(
                name: "MaxUses",
                table: "TenantInvitations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Permissions",
                table: "TenantInvitations",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "UseCount",
                table: "TenantInvitations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Back-fill: every link created before multi-use support was single-use, and the ones already
            // redeemed count as one use. Memberships created before join tracking read "Legacy" via the column default.
            migrationBuilder.Sql("UPDATE TenantInvitations SET MaxUses = 1;");
            migrationBuilder.Sql("UPDATE TenantInvitations SET UseCount = 1 WHERE Status = 'Used';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvitationId",
                table: "TenantUsers");

            migrationBuilder.DropColumn(
                name: "JoinSource",
                table: "TenantUsers");

            migrationBuilder.DropColumn(
                name: "MaxUses",
                table: "TenantInvitations");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "TenantInvitations");

            migrationBuilder.DropColumn(
                name: "UseCount",
                table: "TenantInvitations");
        }
    }
}
