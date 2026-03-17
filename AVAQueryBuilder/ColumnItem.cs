using System.ComponentModel;

namespace AVAQueryBuilder;

public class ColumnItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _alias = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Alias
    {
        get => _alias;
        set
        {
            if (_alias == value) return;
            _alias = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Alias)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
