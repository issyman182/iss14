using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class EconomyStage2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_transaction",
                columns: table => new
                {
                    bank_transaction_id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    from_kind = table.Column<string>(type: "TEXT", nullable: false),
                    from_id = table.Column<string>(type: "TEXT", nullable: false),
                    to_kind = table.Column<string>(type: "TEXT", nullable: false),
                    to_id = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transaction", x => x.bank_transaction_id);
                });

            migrationBuilder.CreateTable(
                name: "player_title",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_title", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "player_unlock",
                columns: table => new
                {
                    player_unlock_id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    unlock_id = table.Column<string>(type: "TEXT", nullable: false),
                    time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    price = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_unlock", x => x.player_unlock_id);
                });

            migrationBuilder.CreateTable(
                name: "salary_override",
                columns: table => new
                {
                    job = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_override", x => x.job);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_from_id",
                table: "bank_transaction",
                column: "from_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_time",
                table: "bank_transaction",
                column: "time");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transaction_to_id",
                table: "bank_transaction",
                column: "to_id");

            migrationBuilder.CreateIndex(
                name: "IX_player_unlock_user_id_unlock_id",
                table: "player_unlock",
                columns: new[] { "user_id", "unlock_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_transaction");

            migrationBuilder.DropTable(
                name: "player_title");

            migrationBuilder.DropTable(
                name: "player_unlock");

            migrationBuilder.DropTable(
                name: "salary_override");
        }
    }
}
