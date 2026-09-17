using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace BHS.MEP.Cabling.Ui;

/// <summary>
/// The window the check for stale lengths happens in.
/// </summary>
/// <remarks>
/// The same three rules as <see cref="RoutingWindow"/>, and for the same measured reasons: the check
/// starts from <c>Loaded</c> so the first frame is painted before anything long begins; the window is
/// owned by Revit's main window, or it is modal to nobody; and a close asked for while the check is
/// still running cancels it and closes when it has actually stopped, rather than leaving a search
/// running against a document the command has already returned from.
/// </remarks>
public partial class ReviewWindow : Window
{
    private readonly ReviewViewModel _model;
    private bool _started;
    private bool _closeWhenIdle;

    public ReviewWindow(ReviewViewModel model)
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

    /// <summary>Runs the check once the window is on screen.</summary>
    public void StartWhenShown() => Loaded += OnLoaded;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_started)
            return;

        _started = true;
        await _model.ComputeAsync().ConfigureAwait(true);

        if (_closeWhenIdle)
            Close();
    }

    /// <summary>
    /// Asks the model to select what the check found, without closing the window.
    /// </summary>
    /// <remarks>
    /// <b>Not measured: what Revit does with a selection set from inside a modal dialog.</b> Reads
    /// answer from here on all four releases, and a transaction after an await commits - both measured -
    /// but a selection is neither. The delegate behind this is the one the sweep exercises from the
    /// pump, where it is measured; if a live Revit refuses it here, the window is where it will show.
    /// </remarks>
    private void OnSelect(object sender, RoutedEventArgs e) => _model.Select();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Refuses to close while the check is still running, and stops it instead.
    /// </summary>
    /// <remarks>
    /// The Close button is not disabled and the title bar's X never asks, so the first ask cancels and
    /// the check closes the window once it has actually stopped - otherwise the search would go on
    /// against a document the command has already returned from.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model.IsBusy)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            _model.Cancel();
        }

        base.OnClosing(e);
    }
}
