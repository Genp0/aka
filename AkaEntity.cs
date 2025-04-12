using Azure.Data.Tables;

namespace Aka;

public class AkaEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "aka"; // Fixed partition key
    public string RowKey { get; set; } = default!; // Alias
    public string? Url { get; set; } // Target URL
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }
}