using System.ClientModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ExpenseAssistant.Evals;

internal static class RetrievalFailure
{
    public static string Classify(Exception exception) => exception switch
    {
        ClientResultException provider => $"PROVIDER_HTTP_{provider.Status}",
        HttpRequestException => "PROVIDER_CONNECTION_FAILED",
        SqliteException => "DATABASE_FAILED",
        JsonException => "INVALID_VECTOR_JSON",
        ArgumentException => "INVALID_SEARCH_ARGUMENTS",
        InvalidOperationException => "INDEX_OR_CONFIGURATION_FAILED",
        _ => "RETRIEVAL_FAILED"
    };
}
