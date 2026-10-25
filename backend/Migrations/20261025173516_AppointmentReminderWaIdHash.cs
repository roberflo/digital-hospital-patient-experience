using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentReminderWaIdHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WaIdHash",
                table: "AppointmentReminders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_WaIdHash",
                table: "AppointmentReminders",
                column: "WaIdHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppointmentReminders_WaIdHash",
                table: "AppointmentReminders");

            migrationBuilder.DropColumn(
                name: "WaIdHash",
                table: "AppointmentReminders");
        }
    }
}
