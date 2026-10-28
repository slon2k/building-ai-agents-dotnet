namespace ExpenseAssistant.Core;

public sealed class Transaction
{
    public required string Id { get; set; }
    public required DateOnly Date { get; set; }
    public required string Merchant { get; set; }
    public required string Description { get; set; }
    public required string CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public required decimal Amount { get; set; }
    public required string Currency { get; set; }
}