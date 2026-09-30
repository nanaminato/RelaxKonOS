internal static class FrpcAppliedStateChecks
{
    internal static void Run()
    {
        var profile = new TunnelServerProfile { Id = Guid.NewGuid(), Revision = 1 };
        var first = new TunnelDefinition { Id = Guid.NewGuid(), ServerProfileId = profile.Id, Revision = 1, Enabled = true };
        var second = new TunnelDefinition { Id = Guid.NewGuid(), ServerProfileId = profile.Id, Revision = 1, Enabled = false };
        var applied = FrpcAppliedState.Fingerprint(profile, [first, second], "protected-token-a");
        var desired = FrpcAppliedState.Fingerprint(profile, [second, first], "protected-token-a");
        TestAssert.Assert(FrpcAppliedState.Project(TunnelConnectionState.Connected, applied, desired, true) == TunnelConnectionState.Connected, "Order changes must not invalidate identical applied definitions.");
        TestAssert.Assert(FrpcAppliedState.Project(TunnelConnectionState.Connected, applied, desired, false) == TunnelConnectionState.Disconnected, "Disabled definitions must not inherit a connected label.");
        first.Revision++;
        desired = FrpcAppliedState.Fingerprint(profile, [first, second], "protected-token-a");
        TestAssert.Assert(FrpcAppliedState.Project(TunnelConnectionState.Connected, applied, desired, true) == TunnelConnectionState.SavedNotApplied, "An old connected process falsely claimed a changed definition was applied.");
        first.Revision--; profile.Revision++;
        TestAssert.Assert(FrpcAppliedState.Fingerprint(profile, [first, second], "protected-token-a") != applied, "Profile changes were not tracked.");
        profile.Revision--;
        TestAssert.Assert(FrpcAppliedState.Fingerprint(profile, [first], "protected-token-a") != applied, "Removed or moved definitions were not tracked.");
        TestAssert.Assert(FrpcAppliedState.Fingerprint(profile, [first, second], "protected-token-b") != applied, "Token rotation without profile revision change was not tracked.");
        TestAssert.Assert(FrpcAppliedState.Project(TunnelConnectionState.Starting, null, desired, true) == TunnelConnectionState.Unknown, "A recovered process with unknown applied identity was labeled applied.");
        TestAssert.Assert(FrpcAppliedState.Project(TunnelConnectionState.Disconnected, applied, desired, true) == TunnelConnectionState.Disconnected, "A stopped process was labeled connected.");
    }
}
