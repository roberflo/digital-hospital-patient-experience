using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class HospitalCommercial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HospitalPurchaseId",
                table: "Opportunities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HospitalPurchaseRequest",
                table: "Opportunities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HospitalQuote",
                table: "Opportunities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CommercialSyncedAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CustomerSince",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSource",
                table: "Contacts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HospitalCompanyId",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HospitalCompanyName",
                table: "Contacts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HospitalCustomerId",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_HospitalPurchaseId",
                table: "Opportunities",
                columns: new[] { "TenantId", "HospitalPurchaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_HospitalCustomerId",
                table: "Contacts",
                columns: new[] { "TenantId", "HospitalCustomerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_HospitalPurchaseId",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_HospitalCustomerId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "HospitalPurchaseId",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "HospitalPurchaseRequest",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "HospitalQuote",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "CommercialSyncedAt",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "CustomerSince",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "CustomerSource",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "HospitalCompanyId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "HospitalCompanyName",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "HospitalCustomerId",
                table: "Contacts");
        }
    }
}
