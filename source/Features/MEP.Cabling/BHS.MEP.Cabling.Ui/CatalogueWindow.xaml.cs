using System.Windows;
using System.Windows.Interop;

namespace BHS.MEP.Cabling.Ui;

/// <summary>
/// The window a project's carrier rules are written in.
/// </summary>
/// <remarks>
/// <para>
/// The same two rules as the routing and lengths windows, for the same measured reasons: the window
/// is owned by Revit's main window or it is modal to nobody, and everything it does is a call into
/// the view model rather than logic of its own.
/// </para>
/// <para>
/// <b>Nothing here is long-running, so there is no cancelling and no busy state.</b> Counting a
/// category is one collector and a type lookup per element, and the reads happen on the API thread
/// this dialog was shown from - which answers, measured, on all four releases.
/// </para>
/// </remarks>
public partial class CatalogueWindow : Window
{
    private readonly CatalogueViewModel _model;

    public CatalogueWindow(CatalogueViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>Makes Revit's main window the owner, so the dialog is modal to Revit.</summary>
    public void OwnedBy(IntPtr owner)
    {
        if (owner != IntPtr.Zero)
            new WindowInteropHelper(this) { Owner = owner };
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _model.AddChosen();

    private void OnRemove(object sender, RoutedEventArgs e) =>
        _model.Remove((sender as FrameworkElement)?.DataContext as CatalogueRule);

    private void OnRecount(object sender, RoutedEventArgs e) => _model.Recount();

    private void OnRestore(object sender, RoutedEventArgs e) => _model.RestoreShipped();

    private void OnSave(object sender, RoutedEventArgs e) => _model.Save();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
