using System.Security.Cryptography;
using System.Text.Json;

namespace ExpenseAssistant.Evals;

public sealed record RetrievalCatalogSnapshot(RetrievalCatalog Catalog, string Hash)
{
    public static async Task<RetrievalCatalogSnapshot> LoadAsync(
        string path, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        RetrievalCatalog catalog = JsonSerializer.Deserialize<RetrievalCatalog>(bytes, options)
            ?? throw new InvalidDataException("Retrieval catalog is null.");
        return new RetrievalCatalogSnapshot(catalog, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
