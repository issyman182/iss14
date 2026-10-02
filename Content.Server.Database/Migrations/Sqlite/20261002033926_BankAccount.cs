using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class BankAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_account",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    balance = table.Column<long>(type: "INTEGER", nullable: false),
                    lifetime_earned = table.Column<long>(type: "INTEGER", nullable: false),
                    last_salary_payout = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_account", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_account_balance",
                table: "bank_account",
                column: "balance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_account");
        }
    }
}
