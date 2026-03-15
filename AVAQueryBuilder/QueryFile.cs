using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AVAQueryBuilder;

public class QueryFile
{
    public string ConnectionString { get; set; } = string.Empty;
    public int LookupOrdinal { get; set; }
    public List<EntityState> Entities { get; set; } = new();
    public List<ConnectorState> Connectors { get; set; } = new();
}

public class EntityState
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string EntityType { get; set; } = string.Empty; // "Table", "Lookup", "Limiter"

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TableSourceResult? TableSource { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectedSourceResult? ConnectedSource { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LimiterResult? Limiter { get; set; }
}

public class ConnectorState
{
    public string SourceEntityId { get; set; } = string.Empty;
    public string TargetEntityId { get; set; } = string.Empty;
    public string SourceEndpoint { get; set; } = string.Empty;
    public string TargetEndpoint { get; set; } = string.Empty;
}
