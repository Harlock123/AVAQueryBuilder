using System.Collections.Generic;

namespace AVAQueryBuilder;

public class GroupByResult
{
    public List<string> GroupFields { get; set; } = new();
    public List<AggregateField> Aggregates { get; set; } = new();
    public List<HavingCondition> HavingConditions { get; set; } = new();
    public string HavingCombiner { get; set; } = "AND";
}

public class AggregateField
{
    public string Function { get; set; } = "COUNT";
    public string Field { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}

public class HavingCondition
{
    public string Expression { get; set; } = string.Empty;
    public string Operator { get; set; } = ">";
    public string Value { get; set; } = string.Empty;
}
