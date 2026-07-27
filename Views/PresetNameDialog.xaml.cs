using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WallpaperMatrix.Views;

public partial class PresetNameDialog : Window
{
    public string PresetName => PresetNameInput.Text.Trim();

    public PresetNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PresetNameInput.Focus();
            PresetNameInput.SelectAll();
        };
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetName.Length == 0)
        {
            PresetNameInput.Focus();
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PresetName.Length > 0)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
