using System.Collections.Generic;

namespace AVAQueryBuilder;

public class DerivedFieldResult
{
    public List<DerivedField> Fields { get; set; } = new();
}

public class DerivedField
{
    public string SourceField { get; set; } = string.Empty;
    public string Derivation { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}
