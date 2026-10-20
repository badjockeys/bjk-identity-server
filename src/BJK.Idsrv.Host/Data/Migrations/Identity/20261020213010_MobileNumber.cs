using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Idsrv.Host.Data.Migrations.Identity
{
    /// <inheritdoc />
    public partial class MobileNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PhoneNumber",
                schema: "auth",
                table: "AspNetUsers",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MobileCodeSentAt",
                schema: "auth",
                table: "AspNetUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MobileCodesDay",
                schema: "auth",
                table: "AspNetUsers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MobileCodesSent",
                schema: "auth",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MobileDiscoverable",
                schema: "auth",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                schema: "auth",
                table: "AspNetUsers",
                column: "PhoneNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MobileCodeSentAt",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MobileCodesDay",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MobileCodesSent",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MobileDiscoverable",
                schema: "auth",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneNumber",
                schema: "auth",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);
        }
    }
}
