using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BladeControl.Ipc;

/// <summary>
/// Access-control policy for the Runtime IPC pipe.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The runtime was validated while console-hosted by the
/// interactive user, where <see cref="PipeOptions.CurrentUserOnly"/> was an adequate and
/// cheap restriction: server and client were the same account. As an SCM service the runtime
/// runs as LocalSystem, so that option becomes both wrong and unusable — it would restrict
/// the pipe to LocalSystem and, on the client side, assert that the server runs as the
/// connecting user, which is exactly what is no longer true.</para>
///
/// <para><b>What must not happen instead.</b> The naive replacement — dropping
/// CurrentUserOnly and letting the default DACL or an Everyone grant stand — would expose a
/// hardware-control channel. Anything able to write to this pipe can set fan speeds and
/// performance levels, so the pipe is a privilege boundary and is treated as one.</para>
///
/// <para><b>Threat model.</b> Four distinct threats, and how the policy answers each:</para>
/// <list type="number">
/// <item><description><i>Remote/network callers.</i> Windows exposes named pipes over SMB,
/// so a pipe is reachable from the network unless refused. Access is granted to
/// <c>INTERACTIVE</c> (S-1-5-4) rather than <c>Users</c> or <c>Everyone</c>: that SID is
/// present only in tokens produced by a local logon, so a network logon is excluded by
/// construction rather than by a blocklist. <c>NETWORK</c> (S-1-5-2) is additionally denied
/// outright, and the server independently verifies locality per connection with
/// <c>GetNamedPipeClientComputerName</c>.</description></item>
/// <item><description><i>Anonymous and service-account callers.</i> <c>ANONYMOUS LOGON</c>
/// (S-1-5-7) is denied explicitly. No grant is made to Everyone, Users, Guests,
/// NetworkService or LocalService, so an unprivileged sandboxed process with no interactive
/// token cannot reach the channel.</description></item>
/// <item><description><i>Pipe squatting / server spoofing.</i> Any process may create a pipe
/// by name, so a malicious one could publish <c>BladeControl.Runtime.v1</c> before the
/// service does and feed the UI fabricated telemetry — or harvest what the UI sends. Two
/// mitigations: the right to create additional instances of the pipe
/// (<see cref="PipeAccessRights.CreateNewInstance"/>) is granted only to LocalSystem and
/// Administrators, and the client verifies the connected pipe's owner with
/// <see cref="VerifyServerIsPrivileged"/> before trusting it.</description></item>
/// <item><description><i>Tampering with the channel's own ACL.</i> <c>WRITE_DAC</c> and
/// <c>WRITE_OWNER</c> are never granted to interactive users, so a logged-on standard user
/// cannot widen the pipe's permissions.</description></item>
/// </list>
///
/// <para><b>What an interactive user can do.</b> Read, write and synchronise — i.e. issue the
/// typed operations the UI and CLI need, including fan and performance changes. That is the
/// intended privilege level: these are the physical operators of the machine, and the runtime
/// still validates every state-changing request against its own safety gates. The pipe ACL
/// is not the thermal-safety boundary; it is the "who may ask at all" boundary.</para>
/// </remarks>
public static class RuntimePipeSecurity
{
    /// <summary>Rights an interactive client needs to run one request/response exchange.</summary>
    private const PipeAccessRights ClientRights =
        PipeAccessRights.Read |
        PipeAccessRights.Write |
        PipeAccessRights.Synchronize;

    /// <summary>
    /// Builds the descriptor the service applies when creating the pipe.
    /// </summary>
    public static PipeSecurity CreateServerSecurity()
    {
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);

        var security = new PipeSecurity();

        // Deny rules first: an explicit deny wins over any allow, and ordering here makes the
        // intent readable even though PipeSecurity canonicalises the ACL itself.
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            anonymous,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        // The service account owns the pipe and may create further instances.
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Locally logged-on users may talk to the runtime, but may not create a competing
        // instance of the pipe and may not rewrite its ACL.
        security.AddAccessRule(new PipeAccessRule(
            interactive,
            ClientRights,
            AccessControlType.Allow));

        // The owner is deliberately left unset. Windows takes an object's default owner from
        // the creating token's TOKEN_OWNER — which is not the same as TOKEN_USER: for an
        // elevated administrator token it is BUILTIN\Administrators, and for a service it is
        // LocalSystem. Both are exactly what VerifyServerIsPrivileged accepts. Forcing the
        // owner explicitly would need SeRestorePrivilege and would fail for a non-elevated
        // host, while adding nothing: an unprivileged squatter cannot produce a privileged
        // TOKEN_OWNER either way.
        return security;
    }

    /// <summary>
    /// Creates the server pipe with <see cref="CreateServerSecurity"/> applied at creation
    /// time. Applying the descriptor atomically matters: a pipe created with a default DACL
    /// and secured afterwards is briefly reachable by the wrong callers.
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(
        string pipeName = RuntimeIpcEndpoint.PipeName,
        int maximumServerInstances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: RuntimeIpcEndpoint.MaximumMessageBytes,
            outBufferSize: RuntimeIpcEndpoint.MaximumMessageBytes,
            pipeSecurity: CreateServerSecurity());
    }

    /// <summary>
    /// Describes whether the identities in the policy are permitted, for tests and docs.
    /// </summary>
    public static PipeAccessDecision Evaluate(WellKnownSidType identity)
    {
        return identity switch
        {
            WellKnownSidType.LocalSystemSid => PipeAccessDecision.FullControl,
            WellKnownSidType.BuiltinAdministratorsSid => PipeAccessDecision.FullControl,
            WellKnownSidType.InteractiveSid => PipeAccessDecision.ReadWrite,
            WellKnownSidType.NetworkSid => PipeAccessDecision.Denied,
            WellKnownSidType.AnonymousSid => PipeAccessDecision.Denied,
            _ => PipeAccessDecision.NotGranted
        };
    }

    /// <summary>
    /// Confirms a connected pipe was published by a privileged account, so a squatted pipe
    /// created by an unprivileged process is not mistaken for the runtime.
    /// </summary>
    /// <remarks>
    /// Reads the owner from the pipe's security descriptor, which needs only READ_CONTROL —
    /// granted to interactive users. Deliberately avoids opening the server process: a
    /// standard user cannot obtain a handle to a LocalSystem process, so a process-identity
    /// check would fail for legitimate clients.
    /// </remarks>
    public static bool VerifyServerIsPrivileged(PipeStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            PipeSecurity security = pipe.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            {
                return false;
            }

            return IsPrivilegedOwner(owner);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
            IOException or PlatformNotSupportedException or ObjectDisposedException)
        {
            // Cannot establish who published the pipe, so it is not trusted.
            return false;
        }
    }

    /// <summary>
    /// True when the SID is one a genuine BladeControl runtime host could own its pipe as.
    /// </summary>
    /// <remarks>
    /// <para>Exactly two identities, because exactly two hosting modes exist:</para>
    /// <list type="bullet">
    /// <item><description><b>LocalSystem</b> — the installed service. The MSI registers it
    /// with <c>Account="LocalSystem"</c>, and nothing less can open Razer HID or read CPU
    /// MSRs.</description></item>
    /// <item><description><b>BUILTIN\Administrators</b> — the documented elevated console
    /// host used for development. Windows takes an object's default owner from the token's
    /// <c>TOKEN_OWNER</c>, which for an elevated administrator token is the Administrators
    /// group rather than the user, so this is the owner such a host actually produces.</description></item>
    /// </list>
    /// <para>LocalService and NetworkService are deliberately <i>not</i> trusted. The runtime
    /// can never run as either — they lack the privileges to reach the hardware — so accepting
    /// them would only widen the trusted set to include the kind of low-privilege sandboxed
    /// service account a hostile process is most likely to be running under.</para>
    /// <para>An ordinary user SID is never trusted, even though a non-elevated developer host
    /// would produce one. Supporting that would mean trusting any pipe published by any
    /// logged-on user, which is precisely the squatting attack this check exists to stop.</para>
    /// </remarks>
    public static bool IsPrivilegedOwner(SecurityIdentifier owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
    }
}

/// <summary>Outcome of the pipe policy for one identity.</summary>
public enum PipeAccessDecision
{
    /// <summary>No rule grants access; the default is refusal.</summary>
    NotGranted,

    /// <summary>An explicit deny rule refuses this identity.</summary>
    Denied,

    /// <summary>May issue typed requests, but not reshape the channel.</summary>
    ReadWrite,

    /// <summary>May also create pipe instances and rewrite the descriptor.</summary>
    FullControl
}
