using ExpenseAssistant.Core;

namespace ExpenseAssistant.Infrastructure;

public static class TransactionExtensions
{
    public const string TextConstructionVersion = "merchant-description-v1";

    extension (Transaction transaction)
    {
        public string BuildSearchableText => 
            $"Merchant: {transaction.Merchant.Trim()}; " +
            $"Description: {transaction.Description.Trim()}";
    }
}