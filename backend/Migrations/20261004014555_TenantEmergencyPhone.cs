using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class TenantEmergencyPhone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmergencyPhone",
                table: "Tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmergencyPhone",
                table: "Tenants");
        }
    }
}
