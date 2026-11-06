using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkLog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SolicitudesHorasCierreDispositivos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WasAdjusted",
                table: "Workdays",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DeviceCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceControlSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    BranchScope = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    GroupScope = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Warnings = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LogoutSharedOnClose = table.Column<bool>(type: "bit", nullable: false),
                    OfflinePolicy = table.Column<int>(type: "int", nullable: false),
                    CorporateAppList = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AlwaysAvailableAppList = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerificationPassed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceControlSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ClosedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReopenedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReopenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollPeriods_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    WorkdayId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActivityRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Target = table.Column<int>(type: "int", nullable: true),
                    OriginalValue = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProposedValue = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OvertimeFrom = table.Column<TimeOnly>(type: "time", nullable: true),
                    OvertimeTo = table.Column<TimeOnly>(type: "time", nullable: true),
                    AuthorizationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolverComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftChangeDays = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkRequests_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    OrdinaryDay = table.Column<int>(type: "int", nullable: false),
                    OrdinaryNight = table.Column<int>(type: "int", nullable: false),
                    OvertimeDay = table.Column<int>(type: "int", nullable: false),
                    OvertimeNight = table.Column<int>(type: "int", nullable: false),
                    SundayHoliday = table.Column<int>(type: "int", nullable: false),
                    Absences = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollLines_PayrollPeriods_PayrollPeriodId",
                        column: x => x.PayrollPeriodId,
                        principalTable: "PayrollPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_TenantId_DeviceId_RequestedAt",
                table: "DeviceCommands",
                columns: new[] { "TenantId", "DeviceId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceControlSettings_TenantId",
                table: "DeviceControlSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_PayrollPeriodId",
                table: "PayrollLines",
                column: "PayrollPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_BranchId",
                table: "PayrollPeriods",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_TenantId_BranchId_From",
                table: "PayrollPeriods",
                columns: new[] { "TenantId", "BranchId", "From" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkRequests_MemberId",
                table: "WorkRequests",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkRequests_TenantId_MemberId_Date",
                table: "WorkRequests",
                columns: new[] { "TenantId", "MemberId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkRequests_TenantId_Status_SubmittedAt",
                table: "WorkRequests",
                columns: new[] { "TenantId", "Status", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCommands");

            migrationBuilder.DropTable(
                name: "DeviceControlSettings");

            migrationBuilder.DropTable(
                name: "PayrollLines");

            migrationBuilder.DropTable(
                name: "WorkRequests");

            migrationBuilder.DropTable(
                name: "PayrollPeriods");

            migrationBuilder.DropColumn(
                name: "WasAdjusted",
                table: "Workdays");
        }
    }
}
