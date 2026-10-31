using ExpenseAssistant.Core;
using System.Security.Cryptography;
using System.Text;

namespace ExpenseAssistant.Infrastructure;

public static class TransactionExtensions
{
    public const string TextConstructionVersion = "merchant-description-v1";

    extension (Transaction transaction)
    {
        public string BuildSearchableText => 
            $"Merchant: {transaction.Merchant.Trim()}; " +
            $"Description: {transaction.Description.Trim()}";

        public string SearchableTextHash =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transaction.BuildSearchableText)));
    }
}