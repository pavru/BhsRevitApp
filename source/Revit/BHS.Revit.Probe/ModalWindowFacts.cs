using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.Revit.Probe;

/// <summary>
/// What a modal window can and cannot do while it owns the API thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>This measures a sentence that has been load-bearing and unmeasured.</b> The routing core's
/// own project file says an <c>ExternalEvent</c> raised from inside a modal window never fires,
/// because Revit never reaches <c>Idling</c> while the window is up - and the shape of the cabling
/// command rests on it: whether the search must run off the API thread, whether the pump is
/// reachable from a dialog, whether a progress bar can repaint. It was reasoned, not run.
/// </para>
/// <para>
/// <b>The window is real rather than a bare <c>DispatcherFrame</c>, and that is the point.</b>
/// <c>PushFrame</c> would exercise the same nested loop with nothing on screen, which is tempting
/// for an unattended sweep - but the claim is about a modal window, and measuring the mechanism I
/// believe underlies it would only confirm my own belief. Modality disables the owner window too,
/// and whether that changes what Revit does is exactly the sort of thing this repository has been
/// wrong about before.
/// </para>
/// <para>
/// <b>It never waits for a person.</b> The window closes itself, so this runs in the ordinary
/// unattended sweep rather than behind <c>BHS_PROBE_SHOW_TAB</c>: that flag exists because touching
/// Revit's own ribbon mid-load once produced a dialog only a human could answer. This asks Revit for
/// nothing. If the window ever fails to close, the pump stops draining, the probe stops answering,
/// and the sweep's watchdog ends the release in sixty seconds - a contained failure with a name.
/// </para>
/// </remarks>
internal static class ModalWindowFacts
{
    /// <summary>How long the window stays up, which is how long the pump is given to reach it.</summary>
    /// <remarks>
    /// Two seconds is a sample, not a budget. An idle Revit raises <c>Idling</c> many times a
    /// second, so work that has not run in two seconds of it has not been reached at all: the
    /// difference being measured is categorical, not a race.
    /// </remarks>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static Stopwatch? _clock;
    private static TimeSpan? _pumpRanAt;
    private static bool _windowClosed;

    /// <summary>
    /// Opens a modal window on the API thread and reports what worked from inside it.
    /// </summary>
    /// <remarks>
    /// Runs inside the pump, so the caller is already on the API thread in a valid API context -
    /// the same place a modal <c>IExternalCommand</c> stands when it calls <c>ShowDialog</c>. That
    /// equivalence is what makes the answer transfer to the command.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Measure(IRevitSession session, IRevitApiPump pump)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["modal:apiThread"] = Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture),
            ["modal:contextOutside"] = ContextName(),

            // Written before anything can change them, so that a step which never happens reads as
            // False rather than as a missing key. A fact that is absent when the news is bad is a
            // fact that reports success by default.
            ["modal:awaitResumed"] = "False",
            ["modal:apiCallWorked"] = "False",
            ["modal:pumpRanWhileModal"] = "False",
        };

        lock (Gate)
        {
            _clock = Stopwatch.StartNew();
            _pumpRanAt = null;
            _windowClosed = false;
        }

        var window = Build();
        window.Loaded += (_, _) => Inside(window, session, pump, facts);

        // The safety net, and deliberately not a DispatcherTimer: a timer runs on the very
        // dispatcher this measurement suspects, so a nested loop that failed to pump would take the
        // timer down with it and leave the window up for ever. A background thread asking the
        // dispatcher to close is the only closer that does not assume the answer.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Dwell + TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                // Discarded rather than awaited: a DispatcherOperation is awaitable, and awaiting
                // this one would mean waiting on the very dispatcher whose health is in question.
                _ = window.Dispatcher.BeginInvoke(new Action(window.Close), DispatcherPriority.Send);
            }
            catch (Exception error)
            {
                Log.For(nameof(ModalWindowFacts)).Warn(error, "the modal window's closer failed");
            }
        });

        window.ShowDialog();

        lock (Gate)
        {
            _windowClosed = true;
            facts["modal:shownFor"] = Seconds(_clock.Elapsed);
            facts["modal:pumpRanWhileModal"] = Yes(_pumpRanAt is not null);
        }

        return facts;
    }

    /// <summary>
    /// Whether the work posted from inside the window has run by now, asked after the fact.
    /// </summary>
    /// <remarks>
    /// <b>A separate question because it cannot be answered by the first one.</b> The measurement
    /// itself is a pump item, and the pump drains its whole queue in one pass - so work posted from
    /// inside the window is still queued behind the measurement when the window closes, and runs
    /// only once the measurement returns. Asking then would always read "not yet" and would look
    /// like a finding.
    ///
    /// The answer matters as much as the first: it says what a real command can count on happening
    /// the moment the user dismisses a dialog.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> After()
    {
        lock (Gate)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["modal:measured"] = Yes(_clock is not null),
                ["modal:windowClosed"] = Yes(_windowClosed),
                ["modal:pumpRanAfterClose"] = Yes(_pumpRanAt is not null),
                ["modal:pumpRanAt"] = _pumpRanAt is null ? "(never)" : Seconds(_pumpRanAt.Value),
            };
        }
    }

    /// <summary>Everything asked from inside the nested loop, in one place.</summary>
    private static async void Inside(Window window, IRevitSession session, IRevitApiPump pump, Dictionary<string, string> facts)
    {
        try
        {
            facts["modal:windowThread"] = Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
            facts["modal:contextInside"] = ContextName();

            // Question one: is the Revit API callable straight from the dialog? If it is, a modal
            // command needs no pump for its reads - it already stands in a valid API context, on
            // the API thread, and the pump exists for callers who do not.
            try
            {
                facts["modal:apiRead"] = session.Application.ActiveUIDocument?.Document?.Title ?? "(no document)";
                facts["modal:apiCallWorked"] = "True";
            }
            catch (Exception error)
            {
                facts["modal:apiRead"] = error.GetType().Name;
            }

            // Question two: does work posted to the pump run while the window is up?
            pump.Post("probe: posted from a modal window", _ => Ran());

            // Question three: does an await resume here at all? This is what decides whether a
            // dialog can show progress while a background search runs - and it is the one answer
            // that is not about Revit.
            var before = Environment.CurrentManagedThreadId;
            await Task.Run(() => System.Threading.Thread.Sleep(200)).ConfigureAwait(true);

            facts["modal:awaitResumed"] = "True";
            facts["modal:awaitResumedOnApiThread"] = Yes(Environment.CurrentManagedThreadId == before);
            facts["modal:awaitResumedAfter"] = Seconds(Elapsed());

            await Task.Delay(Dwell).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // An async void handler drops its exception on the dispatcher, which inside a nested
            // loop means a dialog nobody closes. Recorded and swallowed; the closer still runs.
            facts["modal:insideFailed"] = error.GetType().Name + ": " + error.Message;
        }
        finally
        {
            window.Close();
        }
    }

    private static void Ran()
    {
        lock (Gate)
            _pumpRanAt ??= _clock?.Elapsed ?? TimeSpan.Zero;
    }

    private static TimeSpan Elapsed()
    {
        lock (Gate)
            return _clock?.Elapsed ?? TimeSpan.Zero;
    }

    private static Window Build()
    {
        var window = new Window
        {
            Title = "BHS probe: measuring a modal window",
            Width = 380,
            Height = 140,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "The probe is measuring what a modal window can do. "
                       + "It closes itself; nothing here needs an answer.",
            },
        };

        // Owned by Revit's main window, so this is modal to Revit rather than to nothing: without an
        // owner a WPF dialog is modal only to its own application, and the measurement would be of a
        // window Revit does not know about.
        var main = Process.GetCurrentProcess().MainWindowHandle;

        if (main != IntPtr.Zero)
            new WindowInteropHelper(window).Owner = main;

        return window;
    }

    private static string ContextName() => SynchronizationContext.Current?.GetType().Name ?? "(none)";

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s";

    private static string Yes(bool value) => value ? "True" : "False";
}
