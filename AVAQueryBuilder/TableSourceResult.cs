using System.Collections.Generic;

namespace AVAQueryBuilder;

public class TableSourceResult
{
    public string TableName { get; set; } = string.Empty;
    public bool IsView { get; set; }
    public List<string> SelectedColumns { get; set; } = new();
}
