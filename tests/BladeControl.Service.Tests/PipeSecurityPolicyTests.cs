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
    public void OnlyPrivilegedAccountsCountAsAGenuineRuntimeServer()
    {
        Assert.IsTrue(RuntimePipeSecurity.IsPrivilegedOwner(Sid(WellKnownSidType.LocalSystemSid)));
        Assert.IsTrue(RuntimePipeSecurity.IsPrivilegedOwner(
            Sid(WellKnownSidType.BuiltinAdministratorsSid)));

        // A pipe published by an ordinary user is a squatted pipe, not the runtime.
        Assert.IsFalse(RuntimePipeSecurity.IsPrivilegedOwner(Sid(WellKnownSidType.WorldSid)));
        Assert.IsFalse(RuntimePipeSecurity.IsPrivilegedOwner(Sid(WellKnownSidType.BuiltinUsersSid)));
        Assert.IsFalse(RuntimePipeSecurity.IsPrivilegedOwner(Sid(WellKnownSidType.AnonymousSid)));
    }

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
    /// The owner mechanism the client depends on: Windows makes the creating account the
    /// owner, with no explicit SetOwner and therefore no privilege requirement. In the
    /// installed service that account is LocalSystem; here it is whoever runs the tests,
    /// which is exactly why an unprivileged squatter fails verification.
    /// </summary>
    [TestMethod]
    public void PipeOwnerIsTheCreatingAccountWhichIsWhatServerVerificationReadsAndTrusts()
    {
        string name = $"BladeControl.Test.{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = RuntimePipeSecurity.CreateServerStream(name);

        var owner = (SecurityIdentifier?)pipe.GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier));
        Assert.IsNotNull(owner, "Verification reads the owner; it must be readable.");

        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        Assert.AreEqual(self.User, owner);

        // A test host is normally an ordinary user, so verification must refuse it. If the
        // suite is running elevated as SYSTEM the same call correctly accepts it.
        Assert.AreEqual(
            RuntimePipeSecurity.IsPrivilegedOwner(owner),
            RuntimePipeSecurity.VerifyServerIsPrivileged(pipe),
            "VerifyServerIsPrivileged must agree with the owner it read.");
    }
}
