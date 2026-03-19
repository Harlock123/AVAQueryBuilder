using System.Collections.Generic;
using System.ComponentModel;

namespace AVAQueryBuilder;

public class DerivedFieldViewModel : INotifyPropertyChanged
{
    private string _selectedField = string.Empty;
    private string _selectedDerivation = string.Empty;
    private string _parameter = string.Empty;
    private string _alias = string.Empty;
    private bool _showParameter;

    public static readonly List<string> Derivations = new()
    {
        // String
        "UPPER CASE",
        "lower case",
        "Trim",
        "Length",
        "Left N Chars",
        "Right N Chars",
        "Reverse",
        // Date
        "Year Part",
        "Month Part",
        "Day Part",
        "Hour Part",
        "Month Name",
        "Day Name",
        "Date Only",
        "DATEDIFF (Days)",
        "DATEDIFF (Months)",
        "DATEDIFF (Years)",
        "DATEADD (Days)",
        "DATEADD (Months)",
        "DATEADD (Years)",
        // Null handling
        "ISNULL (Default)",
        "COALESCE (Fallback)",
        // Numeric
        "Absolute Value",
        "Round to N Decimals",
        "Ceiling",
        "Floor"
    };

    public static readonly HashSet<string> ParameterRequired = new()
    {
        "Left N Chars",
        "Right N Chars",
        "Round to N Decimals",
        "DATEDIFF (Days)",
        "DATEDIFF (Months)",
        "DATEDIFF (Years)",
        "DATEADD (Days)",
        "DATEADD (Months)",
        "DATEADD (Years)",
        "ISNULL (Default)",
        "COALESCE (Fallback)"
    };

    public List<string> AvailableFields { get; set; } = new();

    public string SelectedField
    {
        get => _selectedField;
        set { _selectedField = value; OnPropertyChanged(nameof(SelectedField)); }
    }

    public string SelectedDerivation
    {
        get => _selectedDerivation;
        set
        {
            _selectedDerivation = value;
            ShowParameter = ParameterRequired.Contains(value);
            OnPropertyChanged(nameof(SelectedDerivation));
        }
    }

    public string Parameter
    {
        get => _parameter;
        set { _parameter = value; OnPropertyChanged(nameof(Parameter)); }
    }

    public string Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(nameof(Alias)); }
    }

    public bool ShowParameter
    {
        get => _showParameter;
        set { _showParameter = value; OnPropertyChanged(nameof(ShowParameter)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static string ToSqlExpression(string field, string derivation, string parameter)
    {
        return derivation switch
        {
            "UPPER CASE" => $"UPPER({field})",
            "lower case" => $"LOWER({field})",
            "Trim" => $"TRIM({field})",
            "Length" => $"LEN({field})",
            "Left N Chars" => $"LEFT({field}, {parameter})",
            "Right N Chars" => $"RIGHT({field}, {parameter})",
            "Reverse" => $"REVERSE({field})",
            "Year Part" => $"DATEPART(YEAR, {field})",
            "Month Part" => $"DATEPART(MONTH, {field})",
            "Day Part" => $"DATEPART(DAY, {field})",
            "Hour Part" => $"DATEPART(HOUR, {field})",
            "Month Name" => $"DATENAME(MONTH, {field})",
            "Day Name" => $"DATENAME(WEEKDAY, {field})",
            "Date Only" => $"CAST({field} AS DATE)",
            "DATEDIFF (Days)" => $"DATEDIFF(DAY, {field}, {parameter})",
            "DATEDIFF (Months)" => $"DATEDIFF(MONTH, {field}, {parameter})",
            "DATEDIFF (Years)" => $"DATEDIFF(YEAR, {field}, {parameter})",
            "DATEADD (Days)" => $"DATEADD(DAY, {parameter}, {field})",
            "DATEADD (Months)" => $"DATEADD(MONTH, {parameter}, {field})",
            "DATEADD (Years)" => $"DATEADD(YEAR, {parameter}, {field})",
            "ISNULL (Default)" => $"ISNULL({field}, '{parameter}')",
            "COALESCE (Fallback)" => $"COALESCE({field}, {parameter})",
            "Absolute Value" => $"ABS({field})",
            "Round to N Decimals" => $"ROUND({field}, {parameter})",
            "Ceiling" => $"CEILING({field})",
            "Floor" => $"FLOOR({field})",
            _ => field
        };
    }
}
