using System.Diagnostics;
using BHS.Transport;

namespace BHS.Revit.Launch;

/// <summary>
/// Starts a Revit and waits until it says it is there.
/// </summary>
/// <remarks>
/// The whole point is the waiting, not the starting. <c>Process.Start</c> returns in milliseconds
/// and means nothing: Revit takes around a minute to become usable, and the only honest signal
/// that our code is alive inside it is the registration arriving on the well-known pipe. Waiting
/// on a window instead would be both later and wrong - measured, the registration lands about
/// twice as early as a usable main window.
/// <para>
/// The wait watches the process handle as well as the clock, so a Revit that dies on startup fails
/// at the moment it dies rather than four minutes later.
/// </para>
/// </remarks>
public sealed class RevitLauncher
{
    private readonly RevitInstanceRegistry _registry;

    /// <param name="registry">
    /// Where the started Revit will register. It must already be filled by a server on
    /// <see cref="PipeNames.WinSide"/>, because that is the only route the registration takes.
    /// </param>
    public RevitLauncher(RevitInstanceRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Starts one Revit and returns when it has registered, died, or run out of time.
    /// </summary>
    /// <remarks>
    /// The expectation is declared before the process exists, so a Revit that registers unusually
    /// fast cannot arrive before anybody is listening for its token.
    /// </remarks>
    public async Task<RevitSession> LaunchAsync(
        RevitInstallation installation,
        RevitLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (installation is null)
            throw new ArgumentNullException(nameof(installation));

        options ??= new RevitLaunchOptions();

        var token = CorrelationToken.New();
        var expectation = _registry.Expect(token);

        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            WorkingDirectory = installation.InstallDirectory,
            UseShellExecute = false,
        };

        if (options.NoSplash)
            startInfo.ArgumentList.Add("/nosplash");

        if (!string.IsNullOrEmpty(options.Language))
        {
            startInfo.ArgumentList.Add("/language");
            startInfo.ArgumentList.Add(options.Language!);
        }

        foreach (var argument in options.Arguments)
            startInfo.ArgumentList.Add(argument);

        // Last, because Revit reads a bare path as the model to open.
        if (!string.IsNullOrEmpty(options.ModelPath))
            startInfo.ArgumentList.Add(options.ModelPath!);

        foreach (var pair in options.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

        // In the environment rather than on the command line: any user on this machine can read
        // another's command line through WMI, and a token others can read is not a token.
        startInfo.Environment[CorrelationToken.EnvironmentVariable] = token;

        var clock = Stopwatch.StartNew();
        Process? revit;

        try
        {
            revit = Process.Start(startInfo);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _registry.StopExpecting(token);
            return new RevitSession(RevitLaunchOutcome.NotStarted, installation, _registry, token, null, null, clock.Elapsed);
        }

        if (revit is null)
        {
            _registry.StopExpecting(token);
            return new RevitSession(RevitLaunchOutcome.NotStarted, installation, _registry, token, null, null, clock.Elapsed);
        }

        var outcome = await WaitAsync(expectation, revit, options.RegistrationTimeout, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        var instance = outcome == RevitLaunchOutcome.Registered ? await expectation.ConfigureAwait(false) : null;

        return new RevitSession(outcome, installation, _registry, token, revit, instance, clock.Elapsed);
    }

    private static async Task<RevitLaunchOutcome> WaitAsync(
        Task<RevitInstance> expectation,
        Process revit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Half a second is the resolution of "it died", and the registration itself does not
            // wait on this loop - the task completes the moment the call arrives.
            if (await Task.WhenAny(expectation, Task.Delay(500, cancellationToken)).ConfigureAwait(false) == expectation)
                return RevitLaunchOutcome.Registered;

            if (revit.HasExited)
                return RevitLaunchOutcome.ExitedBeforeRegistering;
        }

        return expectation.IsCompleted ? RevitLaunchOutcome.Registered : RevitLaunchOutcome.TimedOut;
    }
}
