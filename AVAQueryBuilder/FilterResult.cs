using System.Collections.Generic;

namespace AVAQueryBuilder;

public class FilterResult
{
    public string Combiner { get; set; } = "AND";
    public List<FilterCondition> Conditions { get; set; } = new();
}

public class FilterCondition
{
    public string Field { get; set; } = string.Empty;
    public string CastAs { get; set; } = "(none)";
    public string Operator { get; set; } = "=";
    public string Value { get; set; } = string.Empty;
    public string? Value2 { get; set; }
}
