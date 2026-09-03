using System.Globalization;

namespace BHS.Transport;

/// <summary>
/// How an instance came to be in the registry.
/// </summary>
public enum RevitInstanceOrigin
{
    /// <summary>It called <c>Register</c> - the supported way to be found.</summary>
    Registered,

    /// <summary>It was found by enumerating the pipe namespace after Win-side lost its registry.</summary>
    Recovered,
}

/// <summary>
/// One Revit process, as Win-side knows it.
/// </summary>
/// <remarks>
/// A snapshot rather than a live view: everything here was true when it was recorded. The one
/// thing that keeps changing - whether the process is still there - is the registry's business,
/// not this type's.
/// </remarks>
public sealed class RevitInstance
{
    internal RevitInstance(
        string instanceId,
        string? correlationToken,
        string pipeName,
        int release,
        int processId,
        string? account,
        bool verified,
        RevitInstanceOrigin origin)
    {
        InstanceId = instanceId;
        CorrelationToken = correlationToken;
        PipeName = pipeName;
        Release = release;
        ProcessId = processId;
        Account = account;
        Verified = verified;
        Origin = origin;
        RegisteredAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Identifies this Revit process for the life of the process.</summary>
    public string InstanceId { get; }

    /// <summary>
    /// The token Win-side handed the process when it started it, or null when a person started it.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case: most Revit sessions begin with somebody double-clicking. A null
    /// token is what says "this one is not ours to drive".
    /// <para>
    /// Always null on a recovered instance. The token lives in the registration message and in the
    /// started process's environment, and enumeration reaches neither.
    /// </para>
    /// </remarks>
    public string? CorrelationToken { get; }

    /// <summary>Where to call it back, without the <c>\.\pipe\</c> prefix.</summary>
    public string PipeName { get; }

    /// <summary>The Revit release year.</summary>
    public int Release { get; }

    /// <summary>
    /// The process hosting the add-in. Verified against the pipe when <see cref="Verified"/> is
    /// true, and merely claimed by the peer otherwise.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>The account the registration was made under, or null when it could not be read.</summary>
    public string? Account { get; }

    /// <summary>
    /// True when the operating system agreed that <see cref="ProcessId"/> is what serves
    /// <see cref="PipeName"/>.
    /// </summary>
    /// <remarks>
    /// The second layer of trust, applied rather than merely available. False is not by itself a
    /// verdict - a busy peer can miss a short deadline - but an instance that never verifies is
    /// one whose word is the only evidence it exists.
    /// </remarks>
    public bool Verified { get; }

    /// <summary>Whether it announced itself or was found afterwards.</summary>
    public RevitInstanceOrigin Origin { get; }

    /// <summary>When this record was made.</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>True when Win-side started this process itself and knows the token it carries.</summary>
    public bool StartedByUs => !string.IsNullOrEmpty(CorrelationToken);

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Revit {0} pid {1} ({2}{3})",
            Release,
            ProcessId,
            PipeName,
            Verified ? string.Empty : ", unverified");
}
