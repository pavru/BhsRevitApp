using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace BHS.MEP.Cabling.Ui;

/// <summary>
/// The window a routing run happens in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The run starts from <c>Loaded</c>, not before <c>ShowDialog</c>.</b> The window has to be up
/// for the continuation to have somewhere to come back to, and the first frame has to be painted
/// before anything long begins - otherwise the dialog appears already blank and already busy, which
/// looks like a hang at the exact moment somebody is deciding whether to trust it.
/// </para>
/// <para>
/// <b>It is modal to Revit only if it is owned by Revit.</b> A WPF dialog with no owner is modal to
/// its own application, and inside Revit that is nothing at all: the window would sit behind the
/// main window and take input from neither. Owning it also puts it where <c>CenterOwner</c> can
/// place it.
/// </para>
/// </remarks>
public partial class RoutingWindow : Window
{
    private readonly RoutingViewModel _model;
    private bool _started;
    private bool _closeWhenIdle;

    public RoutingWindow(RoutingViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>Makes Revit's main window the owner, so the dialog is modal to Revit.</summary>
    /// <remarks>
    /// A handle rather than a <c>Window</c>, because Revit's own is not a WPF one and this assembly
    /// could not name it if it were.
    /// </remarks>
    public void OwnedBy(IntPtr owner)
    {
        if (owner != IntPtr.Zero)
            new WindowInteropHelper(this) { Owner = owner };
    }

    /// <summary>Runs the search once the window is on screen.</summary>
    public void StartWhenShown() => Loaded += OnLoaded;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_started)
            return;

        _started = true;
        await _model.ComputeAsync().ConfigureAwait(true);

        // Only if somebody asked while it was still running. Closing on its own would take the
        // result away at the instant it appeared.
        if (_closeWhenIdle)
            Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _model.Cancel();

    /// <summary>
    /// Writes the run into the model, without closing the window first.
    /// </summary>
    /// <remarks>
    /// <b>In the same continuation as the run, and that is measured rather than chosen.</b> The pump
    /// does not execute while a modal dialog is open, so handing the write to it would mean the
    /// window describing the work disappears before the work is done. Measured on all four releases:
    /// a transaction started after an await inside this window starts, commits a real modification,
    /// and a group around it returns the document as it was.
    /// </remarks>
    private async void OnApply(object sender, RoutedEventArgs e) =>
        await _model.ApplyAsync().ConfigureAwait(true);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Refuses to close while the search is still running, and stops it instead.
    /// </summary>
    /// <remarks>
    /// The Close button is disabled while busy, but the title bar's X and Alt+F4 are not, and neither
    /// asks. Letting the window go then would leave the search running against a document the command
    /// has already returned from - which is the shape of a crash that arrives minutes later and names
    /// somewhere else. So the first ask cancels, and the run closes the window when it has actually
    /// stopped.
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
