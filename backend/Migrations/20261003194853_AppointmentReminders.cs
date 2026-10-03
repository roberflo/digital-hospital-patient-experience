using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReminderChannelId",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderDayTemplate",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "recepcion_cita_dia_anterior_v1");

            migrationBuilder.AddColumn<string>(
                name: "ReminderHourTemplate",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "recepcion_cita_una_hora_v1");

            migrationBuilder.AddColumn<string>(
                name: "ReminderLanguage",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "es");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderLastSyncAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderSyncError",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderConsentAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppointmentReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Window = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentReminders", x => x.Id);
                    table.UniqueConstraint("AK_AppointmentReminders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AppointmentReminders_Channels_TenantId_ChannelId",
                        columns: x => new { x.TenantId, x.ChannelId },
                        principalTable: "Channels",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentReminders_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentReminders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_TenantId_AppointmentId_StartsAt_Window",
                table: "AppointmentReminders",
                columns: new[] { "TenantId", "AppointmentId", "StartsAt", "Window" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_TenantId_ChannelId",
                table: "AppointmentReminders",
                columns: new[] { "TenantId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_TenantId_ContactId",
                table: "AppointmentReminders",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReminders_TenantId_Status_DueAt",
                table: "AppointmentReminders",
                columns: new[] { "TenantId", "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentReminders");

            migrationBuilder.DropColumn(
                name: "ReminderChannelId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderDayTemplate",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderHourTemplate",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderLanguage",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderLastSyncAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderSyncError",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RemindersEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReminderConsentAt",
                table: "Contacts");
        }
    }
}
