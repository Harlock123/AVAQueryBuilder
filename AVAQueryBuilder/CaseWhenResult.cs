using System.Collections.Generic;

namespace AVAQueryBuilder;

public class CaseWhenResult
{
    public List<CaseExpression> Expressions { get; set; } = new();
}

public class CaseExpression
{
    public string Field { get; set; } = string.Empty;
    public List<WhenThen> Conditions { get; set; } = new();
    public string ElseValue { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}

public class WhenThen
{
    public string Operator { get; set; } = "=";
    public string WhenValue { get; set; } = string.Empty;
    public string ThenValue { get; set; } = string.Empty;
}
