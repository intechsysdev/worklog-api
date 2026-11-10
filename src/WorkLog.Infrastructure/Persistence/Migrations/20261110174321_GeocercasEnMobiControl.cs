using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkLog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeocercasEnMobiControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MobiControlError",
                table: "WorkSites",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobiControlName",
                table: "WorkSites",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobiControlReferenceId",
                table: "WorkSites",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MobiControlSyncedAt",
                table: "WorkSites",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MobiControlError",
                table: "WorkSites");

            migrationBuilder.DropColumn(
                name: "MobiControlName",
                table: "WorkSites");

            migrationBuilder.DropColumn(
                name: "MobiControlReferenceId",
                table: "WorkSites");

            migrationBuilder.DropColumn(
                name: "MobiControlSyncedAt",
                table: "WorkSites");
        }
    }
}
