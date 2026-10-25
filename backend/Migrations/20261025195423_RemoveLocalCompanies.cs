using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recepcion.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLocalCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_Companies_TenantId_CompanyId",
                table: "Contacts");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_CompanyId",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Contacts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Industry = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.UniqueConstraint("AK_Companies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Companies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_CompanyId",
                table: "Contacts",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Contacts_Companies_TenantId_CompanyId",
                table: "Contacts",
                columns: new[] { "TenantId", "CompanyId" },
                principalTable: "Companies",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
