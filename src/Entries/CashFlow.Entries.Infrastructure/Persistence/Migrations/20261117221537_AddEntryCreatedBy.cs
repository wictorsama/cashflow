using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Entries.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntryCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                // Rows created before the author was recorded: honest about what is not known.
                defaultValue: "unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_by",
                table: "entries");
        }
    }
}
