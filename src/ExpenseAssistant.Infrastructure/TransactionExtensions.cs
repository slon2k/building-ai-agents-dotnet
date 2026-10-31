using ExpenseAssistant.Core;

namespace ExpenseAssistant.Infrastructure;

public static class TransactionExtensions
{
    extension (Transaction transaction)
    {
        public string BuildSearchableText => 
            $"Merchant: {transaction.Merchant.Trim()}; " +
            $"Description: {transaction.Description.Trim()}";
    }
}