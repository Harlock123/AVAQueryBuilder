using System.Collections.Generic;

namespace AVAQueryBuilder;

public class ConnectedSourceResult
{
    public string SourceTableName { get; set; } = string.Empty;
    public List<string> JoinFieldsFromSource { get; set; } = new();
    public string LookupTableName { get; set; } = string.Empty;
    public string JoinFieldInLookup { get; set; } = string.Empty;
    public string JoinType { get; set; } = "LEFT JOIN";
    public List<string> ReturnFields { get; set; } = new();
    public Dictionary<string, string> ColumnAliases { get; set; } = new();
    public int OrdinalValue { get; set; }
}
