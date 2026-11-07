using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class R21CardToCardRace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rejected_card_to_card_PaymentLineId",
                table: "rejected_card_to_card");

            migrationBuilder.CreateIndex(
                name: "IX_rejected_card_to_card_PaymentLineId",
                table: "rejected_card_to_card",
                column: "PaymentLineId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rejected_card_to_card_PaymentLineId",
                table: "rejected_card_to_card");

            migrationBuilder.CreateIndex(
                name: "IX_rejected_card_to_card_PaymentLineId",
                table: "rejected_card_to_card",
                column: "PaymentLineId");
        }
    }
}
