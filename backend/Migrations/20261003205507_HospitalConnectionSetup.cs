using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class HospitalConnectionSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HospitalConnection",
                table: "Tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HospitalConnection",
                table: "Tenants");
        }
    }
}
