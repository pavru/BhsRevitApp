using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using BHS.Revit.Launch;
using BHS.Transport;
using BHS.Transport.Configuration;
using BHS.Transport.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace BHS.Revit.Probe.Runner;

/// <summary>Why a wait ended.</summary>
internal enum WaitOutcome
{
    /// <summary>What was waited for happened.</summary>
    Arrived,

    /// <summary>Revit stopped saying anything, for longer than it is entitled to.</summary>
    WentQuiet,

    /// <summary>Revit is waiting for a person, which is not a hang.</summary>
    Blocked,

    /// <summary>Neither happened before the last-resort ceiling.</summary>
    Ceiling,

    /// <summary>Revit stopped answering at all - gone, or taking nobody's calls.</summary>
    Unreachable,
}

/// <summary>
/// Waiting on evidence that Revit is working, rather than on a clock.
/// </summary>
/// <remarks>
/// Split out of the runner because it is the one piece here with a rule of its own: every raise of
/// the budgets it replaced was paid for by a failure that was not one, and the rule learned each
/// time - ask whether the work began, not how long it has taken - belongs somewhere it can be read
/// without the four hundred lines of questions that use it.
/// </remarks>
internal static class WorkWatch
{
    /// <summary>
    /// Waits while Revit is demonstrably still working, rather than for a fixed number of seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budgets this replaces were raised three times, each time by a failure that was not one:
    /// two minutes while Revit 2026 opened a model perfectly well and finished at 3:51; five minutes
    /// on a 2024 whose log dates the document at 11:36 after registration. The rule learned each
    /// time - before raising a timeout, ask whether the work ever began - is exactly what a stream
    /// of phases answers directly. Measured across four releases with a model opening: Revit is
    /// never quiet for more than about ten seconds while it is working.
    /// </para>
    /// <para>
    /// <b>Three endings, not two.</b> A wait that only knows "arrived" and "timed out" has to treat
    /// a modal dialog as a hang, which is how this repository twice diagnosed one wrongly. Blocked
    /// is reported as itself, with the dialog's identifier, because the answer to it is a person and
    /// not a longer budget.
    /// </para>
    /// <para>
    /// <b>The ceiling stays.</b> A watchdog that waits forever while events keep arriving is a
    /// watchdog that can be held open by a Revit busily doing nothing, and an unattended sweep must
    /// end. It is now a last resort rather than the mechanism.
    /// </para>
    /// <para>
    /// <b>And it falls back honestly.</b> With no diagnostics - an edition that never enabled them,
    /// or a stream that failed - there is nothing to be silent, so silence must not be inferred:
    /// the wait reverts to the ceiling alone and says so.
    /// </para>
    /// <para>
    /// <b>Not every dialog is visible, and the watchdog must not promise otherwise.</b> Measured by
    /// breaking a model on purpose: Revit raised "contains an incorrect schema" on its own thread 3,
    /// during file loading, and <c>DialogBoxShowing</c> never fired - so the wait reported quiet
    /// during Starting rather than Blocked. It was still right that Revit had stopped, and still
    /// ended in a minute rather than fifteen. Blocked is the better answer when it is available; the
    /// quiet answer is the one that is always available.
    /// </para>
    /// <para>
    /// <b>Not used for the shutdown wait, on purpose.</b> Revit going quiet is what leaving looks
    /// like: the stream ends because the process is ending, and a watchdog would read its own
    /// success as a hang. Closing keeps its budget until there is a signal that distinguishes "the
    /// pipe closed because Revit left" from "the pipe went quiet because Revit stopped" - and there
    /// is no such signal today.
    /// </para>
    /// </remarks>
    internal static async Task<WaitOutcome> WaitWhileWorkingAsync(
        Func<bool> done,
        DiagnosticsWatcher watcher,
        TimeSpan quiet,
        TimeSpan ceiling)
    {
        var started = DateTime.UtcNow;
        var deadline = started + ceiling;
        var unanswered = 0;
        var announced = false;

        while (DateTime.UtcNow < deadline)
        {
            // Asking costs a call over the pipe, and a Revit that has died - or is holding a modal
            // dialog on the thread that serves it - answers with an exception rather than a value.
            // The first version let that escape and took the whole sweep down with it, which is the
            // one outcome a watchdog may never have: it exists for the case where Revit stops
            // behaving, so it cannot be the thing that breaks when Revit does.
            //
            // Found by breaking a model on purpose to see the Blocked path, which is the only reason
            // it was found at all.
            bool ready;

            try
            {
                ready = done();
                unanswered = 0;
            }
            catch (Exception)
            {
                ready = false;
                unanswered++;
            }

            if (ready)
                return WaitOutcome.Arrived;

            // Two seconds of refused calls, not one: a single failure is a pipe being busy, and
            // saying "gone" about a Revit that is merely occupied would be the same false diagnosis
            // in the other direction.
            if (unanswered >= 8)
                return WaitOutcome.Unreachable;

            // Nothing has ever been heard: either diagnostics are off or the stream never started.
            // Silence from something that has never spoken says nothing about whether it is working.
            //
            // And a stream that spoke and then broke is the same case, not the opposite one. The
            // reader records its failure and stops, after which nothing updates the last-heard mark
            // ever again - so a pipe that faults thirty seconds into a three-minute model open
            // leaves this reading "quiet for a minute" about a Revit that is working perfectly, and
            // the caller kills it and reports that the document never arrived. That is precisely the
            // false diagnosis this whole wait exists to end, arriving through the instrument.
            var listening = watcher.Received > 0 && watcher.Failure.Length == 0;

            // Measured from whichever is later: the last event, or the moment this wait began. The
            // events before it were about the previous question - registration, settings, the log -
            // and a wait must not open already out of patience for work it has not yet watched.
            var watching = DateTime.UtcNow - started;
            var quietFor = watcher.SinceLastHeard < watching ? watcher.SinceLastHeard : watching;

            // The phase alone, and the remembered dialog id deliberately left out of it. The id is
            // sticky by design - it survives until Revit visibly moves on, so that a dialog still on
            // screen is not forgotten the instant anything else happens. When it only worded a
            // sentence that bias was free. Stopping the clock on it is not: a dialog raised during
            // Starting and answered leaves the id set, Revit emits nothing but Starting, and a
            // genuine hang would then hold the wait for the whole ceiling - the watchdog decaying
            // back into the fixed budget it was written to replace.
            //
            // So the phase decides the behaviour and the id only decides the wording.
            var blocked = watcher.CurrentPhase == RevitPhase.Blocked;

            // While Revit is holding a dialog the quiet clock does not run, and this is a
            // correction rather than a tolerance. Blocked was already told apart from silence and
            // then treated exactly like it: the wait announced "Revit is waiting for somebody to
            // answer" and gave up sixty seconds later - while somebody was answering. Measured on
            // Revit 2024, where the dialog was answered and the process had already been killed.
            //
            // Silence during Blocked is not evidence of anything: Revit is not working, it is
            // asking, and the answer is a person rather than a longer budget. So the wait keeps its
            // ceiling, which still ends an unattended run, and stops pretending the clock means
            // something in this phase. An unattended run reaches the same failure, later and
            // correctly named; an attended one now finishes.
            if (blocked)
            {
                if (!announced)
                {
                    announced = true;
                    Console.WriteLine("       waiting: Revit is asking "
                                      + (watcher.BlockedBy.Length > 0 ? watcher.BlockedBy : "something")
                                      + " - answer it, or this ends at the ceiling");
                }
            }
            else
            {
                // Asked again the next time it blocks: a second dialog is a second moment when the
                // person at the screen is the thing being waited on, and the first announcement has
                // long scrolled away.
                announced = false;

                if (listening && quietFor > quiet)
                    return WaitOutcome.WentQuiet;
            }

            await Task.Delay(250);
        }

        try
        {
            if (done())
                return WaitOutcome.Arrived;
        }
        catch (Exception)
        {
            return WaitOutcome.Unreachable;
        }

        // Named for what it was doing when the time ran out. A run that spent its ceiling holding a
        // dialog nobody answered failed for that reason, and "fifteen minutes passed" would send
        // the reader looking for a hang that never happened.
        return watcher.CurrentPhase == RevitPhase.Blocked || watcher.BlockedBy.Length > 0
            ? WaitOutcome.Blocked
            : WaitOutcome.Ceiling;
    }

    /// <summary>Polls until a condition holds, or until the time runs out.</summary>
    /// <remarks>
    /// For the things that happen a moment after something else - configuration arriving, an
    /// instance leaving the registry. Everything worth waiting minutes for has a handle to watch
    /// instead, and that waiting lives in <see cref="RevitLauncher"/>.
    /// </remarks>
    internal static async Task<bool> WaitForAsync(Func<bool> condition, int millisecondsTimeout = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(50);
        }

        return condition();
    }
}
