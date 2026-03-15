using System.Collections.Generic;

namespace AVAQueryBuilder;

public class SortingResult
{
    public List<SortingField> Fields { get; set; } = new();
}

public class SortingField
{
    public string Field { get; set; } = string.Empty;
    public string Direction { get; set; } = "ASC";
}
