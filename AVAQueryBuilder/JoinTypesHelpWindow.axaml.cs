using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AVAQueryBuilder;

public partial class JoinTypesHelpWindow : Window
{
    public JoinTypesHelpWindow()
    {
        InitializeComponent();
    }

    private void CmdClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
