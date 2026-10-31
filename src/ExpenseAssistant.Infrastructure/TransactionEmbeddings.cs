namespace ExpenseAssistant.Infrastructure;

public sealed class TransactionEmbeddings
{
    public required string TransactionId { get; set; }
    public required string IndexId { get; set; }
    public required string SearchableTextHash { get; set; }
    public required string VectorJson { get; set; }
}

public sealed class EmbeddingIndexMetadata
{
    public required string IndexId { get; set; }
    public required string ModelConfigurationId { get; set; }
    public required int Dimensions { get; set; }
    public required string TextConstructionVersion { get; set; }
    public required string DatasetVersion { get; set; }
}