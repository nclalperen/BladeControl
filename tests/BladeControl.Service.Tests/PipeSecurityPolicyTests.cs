using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BladeControl.Ipc;

namespace BladeControl.Service.Tests;

/// <summary>
/// Asserts the pipe ACL actually says what docs/ipc-security.md claims. These inspect the
/// built security descriptor; no pipe is served and no hardware is touched.
/// </summary>
[TestClass]
public sealed class PipeSecurityPolicyTests
{
    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    private static PipeAccessRule[] RulesFor(PipeSecurity security, WellKnownSidType type)
    {
        SecurityIdentifier sid = Sid(type);
        return security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(sid))
            .ToArray();
    }

    [TestMethod]
    public void NetworkAndAnonymousCallersAreDeniedOutright()
    {
        PipeSecurity security = RuntimePipeSecurity.CreateServerSecurity();

        foreach (WellKnownSidType denied in new[]
        {
            WellKnownSidType.NetworkSid,
            WellKnownSidType.AnonymousSid
        })
        {
            PipeAccessRule[] rules = RulesFor(security, denied);
            Assert.AreNotEqual(0, rules.Length, $"{denied} must have an explicit rule.");
            Assert.IsTrue(
                rules.All(rule => rule.AccessControlType == AccessControlType.Deny),
                $"{denied} must only ever appear in deny rules.");
            Assert.IsTrue(
                rules.Any(rule => rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl)),
                $"{denied} must be denied every right, not a subset.");
        }
    }

    [TestMethod]
    public void LocallyLoggedOnUsersMayIssueRequests()
    {
        PipeSecurity security = RuntimePipeSecurity.CreateServerSecurity();
        PipeAccessRule[] rules = RulesFor(security, WellKnownSidType.InteractiveSid);

        Assert.AreEqual(1, rules.Length);
        PipeAccessRule rule = rules[0];
        Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
        Assert.IsTrue(rule.PipeAccessRights.HasFlag(PipeAccessRights.Read));
        Assert.IsTrue(rule.PipeAccessRights.HasFlag(PipeAccessRights.Write));
        Assert.IsTrue(rule.PipeAccessRights.HasFlag(PipeAccessRights.Synchronize));
    }

    /// <summary>
    /// The anti-squatting and anti-tampering half of the policy: an interactive user must not
    /// be able to add an instance of the pipe (and impersonate the runtime) or rewrite its
    /// permissions.
    /// </summary>
    [TestMethod]
    public void InteractiveUsersCannotCreatePipeInstancesOrRewriteTheDescriptor()
    {
        PipeSecurity security = RuntimePipeSecurity.CreateServerSecurity();
        PipeAccessRights granted = RulesFor(security, WellKnownSidType.InteractiveSid)
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Aggregate(default(PipeAccessRights), (all, rule) => all | rule.PipeAccessRights);

        Assert.IsFalse(
            granted.HasFlag(PipeAccessRights.CreateNewInstance),
            "Granting CreateNewInstance would let an unprivileged process squat the pipe.");
        Assert.IsFalse(granted.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.IsFalse(granted.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.IsFalse(granted.HasFlag(PipeAccessRights.FullControl));
    }

    [TestMethod]
    public void NoRuleGrantsAccessToEveryoneOrOrdinaryUsers()
    {
        PipeSecurity security = RuntimePipeSecurity.CreateServerSecurity();

        foreach (WellKnownSidType forbidden in new[]
        {
            WellKnownSidType.WorldSid,
            WellKnownSidType.BuiltinUsersSid,
            WellKnownSidType.BuiltinGuestsSid,
            WellKnownSidType.LocalServiceSid,
            WellKnownSidType.NetworkServiceSid,
            WellKnownSidType.AuthenticatedUserSid
        })
        {
            Assert.IsFalse(
                RulesFor(security, forbidden).Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow),
                $"{forbidden} must not be granted access to a hardware-control channel.");
        }
    }

    [DataTestMethod]
    [DataRow(WellKnownSidType.LocalSystemSid, PipeAccessDecision.FullControl)]
    [DataRow(WellKnownSidType.BuiltinAdministratorsSid, PipeAccessDecision.FullControl)]
    [DataRow(WellKnownSidType.InteractiveSid, PipeAccessDecision.ReadWrite)]
    [DataRow(WellKnownSidType.NetworkSid, PipeAccessDecision.Denied)]
    [DataRow(WellKnownSidType.AnonymousSid, PipeAccessDecision.Denied)]
    [DataRow(WellKnownSidType.WorldSid, PipeAccessDecision.NotGranted)]
    [DataRow(WellKnownSidType.BuiltinUsersSid, PipeAccessDecision.NotGranted)]
    public void DocumentedDecisionMatchesTheImplementedPolicy(
        WellKnownSidType identity,
        PipeAccessDecision expected) =>
        Assert.AreEqual(expected, RuntimePipeSecurity.Evaluate(identity));

    [TestMethod]
    public void EndpointIdentityAndProtocolBoundAreUnchanged()
    {
        Assert.AreEqual("BladeControl.Runtime.v1", RuntimeIpcEndpoint.PipeName);
        Assert.AreEqual(@"\\.\pipe\BladeControl.Runtime.v1", RuntimeIpcEndpoint.PipePath);

        // The 64 KiB ceiling is part of the validated protocol and must not drift.
        Assert.AreEqual(64 * 1024, RuntimeIpcEndpoint.MaximumMessageBytes);
        Assert.AreEqual(
            BladeControl.Runtime.RuntimeIpcDispatcher.MaximumMessageBytes,
            RuntimeIpcEndpoint.MaximumMessageBytes,
            "Both ends must enforce the same bound.");
        Assert.AreEqual(RuntimeIpcEndpoint.PipeName, RuntimeNamedPipeServer.PipeName);
    }

    /// <summary>
    /// The descriptor must be applied when the pipe is created. A pipe created with a default
    /// DACL and secured afterwards is briefly open to the wrong callers.
    /// </summary>
    [TestMethod]
    public void ServerStreamIsCreatedWithThePolicyAlreadyApplied()
    {
        string name = $"BladeControl.Test.{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = RuntimePipeSecurity.CreateServerStream(name);

        PipeSecurity applied = pipe.GetAccessControl();
        Assert.IsTrue(
            applied.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Any(rule => rule.IdentityReference.Equals(Sid(WellKnownSidType.InteractiveSid)) &&
                    rule.AccessControlType == AccessControlType.Allow),
            "The interactive grant must be present on the live pipe, not just the descriptor.");
        Assert.IsTrue(
            applied.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Any(rule => rule.IdentityReference.Equals(Sid(WellKnownSidType.NetworkSid)) &&
                    rule.AccessControlType == AccessControlType.Deny),
            "The network denial must survive onto the live pipe.");
    }

    /// <summary>
    /// Regression test for a wrong assumption this test file previously made: that a newly
    /// created object is owned by the creating token's <c>TOKEN_USER</c>.
    /// </summary>
    /// <remarks>
    /// Windows takes an object's default owner from <c>TOKEN_OWNER</c>, which is a separate
    /// field. For an elevated administrator token it is <c>BUILTIN\Administrators</c>
    /// (S-1-5-32-544), not the user account, so the old assertion passed only when the suite
    /// happened to run unelevated and silently failed on an elevated machine. Asserting
    /// against <see cref="WindowsIdentity.Owner"/> is correct in every context.
    /// </remarks>
    [TestMethod]
    public void PipeOwnerComesFromTokenOwnerNotTokenUser()
    {
        string name = $"BladeControl.Test.{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = RuntimePipeSecurity.CreateServerStream(name);

        var owner = (SecurityIdentifier?)pipe.GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier));
        Assert.IsNotNull(owner, "Verification reads the owner; it must be readable.");

        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        Assert.AreEqual(
            self.Owner,
            owner,
            "A created object's owner is the token's TOKEN_OWNER.");

        if (!Equals(self.Owner, self.User))
        {
            // Elevated: this is precisely the case the old TOKEN_USER assumption got wrong.
            Assert.AreNotEqual(
                self.User,
                owner,
                "TOKEN_OWNER and TOKEN_USER differ here, so the owner must not be TOKEN_USER.");
        }
    }

    /// <summary>
    /// The security contract, stated without depending on how the suite happens to be run:
    /// the client's verdict on a real pipe is exactly what the production trusted-owner
    /// policy says about that pipe's actual owner.
    /// </summary>
    [TestMethod]
    public void ServerVerificationAgreesWithTheProductionTrustedOwnerPolicy()
    {
        string name = $"BladeControl.Test.{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = RuntimePipeSecurity.CreateServerStream(name);

        var owner = (SecurityIdentifier)pipe.GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier))!;

        Assert.AreEqual(
            RuntimePipeSecurity.IsPrivilegedOwner(owner),
            RuntimePipeSecurity.VerifyServerIsPrivileged(pipe),
            "VerifyServerIsPrivileged must return exactly what the policy says about the " +
            "owner it read — no extra leniency, no extra strictness.");
    }

    /// <summary>
    /// The trusted-owner set is exactly the two accounts a genuine runtime host can own its
    /// pipe as: LocalSystem for the installed service, and BUILTIN\Administrators for the
    /// documented elevated console host (whose TOKEN_OWNER is the group, not the user).
    /// </summary>
    [DataTestMethod]
    [DataRow(WellKnownSidType.LocalSystemSid, true, "the installed service runs as LocalSystem")]
    [DataRow(WellKnownSidType.BuiltinAdministratorsSid, true, "an elevated console host owns as Administrators")]
    [DataRow(WellKnownSidType.LocalServiceSid, false, "the runtime cannot reach hardware as LocalService")]
    [DataRow(WellKnownSidType.NetworkServiceSid, false, "NetworkService is a sandbox, not a runtime host")]
    [DataRow(WellKnownSidType.WorldSid, false, "Everyone is never a runtime host")]
    [DataRow(WellKnownSidType.BuiltinUsersSid, false, "ordinary users are never trusted")]
    [DataRow(WellKnownSidType.AuthenticatedUserSid, false, "authentication alone earns no trust")]
    [DataRow(WellKnownSidType.InteractiveSid, false, "being logged on locally does not make you the runtime")]
    [DataRow(WellKnownSidType.AnonymousSid, false, "anonymous is never trusted")]
    public void TrustedOwnerPolicyAcceptsOnlyAccountsARuntimeHostCanOwnAs(
        WellKnownSidType identity,
        bool expected,
        string because) =>
        Assert.AreEqual(expected, RuntimePipeSecurity.IsPrivilegedOwner(Sid(identity)), because);

    /// <summary>
    /// An ordinary account SID is refused, which is the squatting case: a standard user
    /// publishing the runtime's pipe name must not be mistaken for the runtime. Uses a
    /// synthetic account SID so the result does not depend on who runs the suite.
    /// </summary>
    [TestMethod]
    public void OrdinaryAccountOwnedPipeIsNotTrustedEvenThoughADeveloperHostWouldProduceOne()
    {
        var ordinaryUser = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");

        Assert.IsFalse(
            RuntimePipeSecurity.IsPrivilegedOwner(ordinaryUser),
            "Trusting arbitrary user-owned pipes to support a non-elevated developer host " +
            "would re-open the squatting hole the owner check exists to close.");
    }
}
