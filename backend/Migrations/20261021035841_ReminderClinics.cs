using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReminderClinics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "reception"); // every existing hospital uses Recepción

            migrationBuilder.AlterColumn<Guid>(
                name: "ContactId",
                table: "AppointmentReminders",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "AppointmentReminders",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OptOutPendingAt",
                table: "AppointmentReminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientHash",
                table: "AppointmentReminders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_RecipientHash",
                table: "AppointmentReminders",
                column: "RecipientHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The general number's rows have no contact or channel and cannot survive the NOT NULL below; they are send state, not records.
            migrationBuilder.Sql("DELETE FROM \"AppointmentReminders\" WHERE \"ContactId\" IS NULL OR \"ChannelId\" IS NULL;");

            migrationBuilder.DropIndex(
                name: "IX_AppointmentReminders_RecipientHash",
                table: "AppointmentReminders");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OptOutPendingAt",
                table: "AppointmentReminders");

            migrationBuilder.DropColumn(
                name: "RecipientHash",
                table: "AppointmentReminders");

            migrationBuilder.AlterColumn<Guid>(
                name: "ContactId",
                table: "AppointmentReminders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "AppointmentReminders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
