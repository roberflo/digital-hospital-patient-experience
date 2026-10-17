using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class JobsStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Receipts_TenantId_CreatedAt",
                table: "Receipts",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_Status_CreatedAt",
                table: "Jobs",
                columns: new[] { "TenantId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipts_TenantId_CreatedAt",
                table: "Receipts");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TenantId_Status_CreatedAt",
                table: "Jobs");
        }
    }
}
