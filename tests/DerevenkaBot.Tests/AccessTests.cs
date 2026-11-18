using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Tests;

public sealed class AccessTests : IDisposable
{
    private readonly ImageOptions options = new() { OwnerUserId = 777,
        StoragePath = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N")) };
    private readonly ImageDatabase db;
    private readonly ImageAccessService access;
    private static readonly Actor Owner = new(777, 777, true);
    public AccessTests() { db = new(options); db.Migrate(); access = new(db, options); }
    [Fact]
    public void DefaultDenyWithOwnerBootstrap()
    {
        Assert.True(access.IsAllowed(Owner));
        Assert.False(access.IsAllowed(new(-100, 1, false)));
        Assert.False(access.IsAllowed(new(1, 1, true)));
    }
    [Fact]
    public void GroupPermissionIsPersistentAndDoesNotAuthorizePrivateChats()
    {
        access.Manage(new(-100, 777, false), "/allow", "here", "one");
        var restored = new ImageDatabase(options); restored.Migrate();
        var restoredAccess = new ImageAccessService(restored, options);
        Assert.True(restoredAccess.IsAllowed(new(-100, 1, false)));
        Assert.True(restoredAccess.IsAllowed(new(-100, 2, false)));
        Assert.False(restoredAccess.IsAllowed(new(-200, 1, false)));
        Assert.False(restoredAccess.IsAllowed(new(1, 1, true)));
    }
    [Fact]
    public void PersonalPermissionAuthorizesOnlyThatPerson()
    {
        access.Manage(Owner, "/allow", "user 1", "one");
        Assert.True(access.IsAllowed(new(1, 1, true)));
        Assert.True(access.IsAllowed(new(-100, 1, false)));
        Assert.False(access.IsAllowed(new(-100, 2, false)));
    }
    [Fact]
    public void OrdinaryMemberAndGroupAdminCannotGrantAccess()
    {
        Assert.Equal(Texts.OwnerRequired, access.Manage(new(-100, 1, false), "/allow", "here", "unauthorized"));
        Assert.Empty(db.Run(s => s.AccessGrants));
    }
    [Fact]
    public void ReplayedRevokeCannotRevokeANewerGrant()
    {
        access.Manage(Owner, "/allow", "user 1", "allow");
        access.Manage(Owner, "/revoke", "user 1", "revoke");
        access.Manage(Owner, "/allow", "user 1", "new-allow");
        access.Manage(Owner, "/revoke", "user 1", "revoke");
        Assert.True(access.IsAllowed(new(1, 1, true)));
    }
    [Fact]
    public async Task RevocationCancelsUnstartedJobsAndReleasesReservation()
    {
        access.Manage(Owner, "/allow", "user 1", "allow");
        var jobs = new ImageJobService(db, options);
        Assert.True((await jobs.EnqueueAsync(new("job", new(1, 1, true), 10, "кот"), default)).Accepted);
        access.Manage(Owner, "/revoke", "user 1", "revoke");
        Assert.Equal(JobStatus.Cancelled, db.Run(s => s.Jobs.Single().Status));
        Assert.True(db.Run(s => s.Usage.Single().Released));
    }
    [Fact]
    public void IndependentGrantsUseOrSemantics()
    {
        access.Manage(Owner, "/allow", "user 1", "user");
        access.Manage(Owner, "/allow", "chat -100", "chat");
        access.Manage(Owner, "/revoke", "user 1", "revoke-user");
        Assert.False(access.IsAllowed(new(1, 1, true)));
        Assert.True(access.IsAllowed(new(-100, 1, false)));
        access.Manage(Owner, "/revoke", "chat -100", "revoke-chat");
        Assert.False(access.IsAllowed(new(-100, 1, false)));
    }
    [Fact]
    public void RevocationOverridesInitialConfigurationGrants()
    {
        options.AllowedUsers = [1];
        Assert.True(access.IsAllowed(new(1, 1, true)));
        access.Manage(Owner, "/revoke", "user 1", "revoke");
        Assert.False(access.IsAllowed(new(1, 1, true)));
    }
    [Fact]
    public void PermissionListIsPrivateAndInvalidTargetsAreRejected()
    {
        access.Manage(Owner, "/allow", "user -100", "invalid-user");
        access.Manage(Owner, "/allow", "chat 123", "invalid-chat");
        access.Manage(new(-100, 777, false), "/allow", "chat -200", "public-grant");
        Assert.Empty(db.Run(s => s.AccessGrants));
        Assert.Contains("в личке", access.Manage(new(-100, 777, false), "/access", "", "list"));
    }
    [Theory]
    [InlineData("1", false)]
    [InlineData("user 1", false)]
    [InlineData("1", true)]
    public void OwnerCanGrantFriendByIdFromGroupOrPrivateChat(string arguments, bool isPrivate)
    {
        access.Manage(isPrivate ? Owner : new(-100, 777, false), "/allow", arguments, "grant");
        Assert.True(access.IsAllowed(new(1, 1, true)));
        Assert.False(access.IsAllowed(new(-100, 2, false)));
    }
    [Fact]
    public void ReplyGrantAndRevokeTargetOnlySpecifiedFriend()
    {
        var groupOwner = new Actor(-100, 777, false);
        access.Manage(groupOwner, "/allow", "", "allow-reply", replyUserId: 1);
        Assert.True(access.IsAllowed(new(1, 1, true)));
        access.Manage(groupOwner, "/revoke", "", "revoke-reply", replyUserId: 1);
        Assert.False(access.IsAllowed(new(1, 1, true)));
    }
    [Fact]
    public void ReplyDoesNotBypassOwnerCheckOrGrantOnInvalidArguments()
    {
        Assert.Equal(Texts.OwnerRequired, access.Manage(new(-100, 1, false), "/allow", "", "forbidden", replyUserId: 2));
        access.Manage(Owner, "/allow", "@friend", "username", replyUserId: 1);
        access.Manage(Owner, "/allow", "", "empty");
        Assert.Empty(db.Run(s => s.AccessGrants));
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(options.StoragePath)) Directory.Delete(options.StoragePath, true);
    }
}
