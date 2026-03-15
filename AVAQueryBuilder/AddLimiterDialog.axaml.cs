using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AVAQueryBuilder;

public partial class AddLimiterDialog : Window
{
    public LimiterResult? Result { get; private set; }
    public int? ExistingTopCount { get; set; }

    public AddLimiterDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ExistingTopCount.HasValue)
                nudTopCount.Value = ExistingTopCount.Value;
        };
    }

    private void CmdOk_Click(object? sender, RoutedEventArgs e)
    {
        var value = (int)(nudTopCount.Value ?? 100);
        Result = new LimiterResult { TopCount = value };
        Close(true);
    }

    private void CmdCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}
