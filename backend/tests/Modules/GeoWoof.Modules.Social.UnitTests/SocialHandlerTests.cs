using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Blocks;
using GeoWoof.Modules.Social.Application.Events;
using GeoWoof.Modules.Social.Application.Follows;
using GeoWoof.Modules.Social.Application.Reports;
using GeoWoof.Modules.Social.Contracts;
using GeoWoof.Modules.Social.Domain;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Social.UnitTests;

public sealed class SocialHandlerTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();
    private static readonly Guid Carlos = Guid.CreateVersion7();
    private static readonly Guid Dani = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeFollows _follows = new();
    private readonly FakeRequests _requests = new();
    private readonly FakeBlocks _blocks = new();
    private readonly FakeReports _reports = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeIdentity _identity = new();
    private readonly FakeGraphs _graphs;

    public SocialHandlerTests()
    {
        _graphs = new FakeGraphs(_follows, _blocks);
        _identity.Add(Ana, "ana");
        _identity.Add(Bea, "bea");
        _identity.Add(Carlos, "carlos");
        _identity.Add(Dani, "dani", isPrivate: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private UserSummaries Summaries => new(_identity, _graphs, new FakeMedia());

    private RelationshipReader Reader => new(_graphs, _requests);

    private FollowService Service => new(_follows, _time);

    private Task<Result<RelationshipDto>> FollowAsync(Guid from, Guid to) =>
        new FollowUserCommandHandler(_follows, _requests, _identity, _graphs, _unitOfWork, Service, Reader, _time).Handle(new FollowUserCommand(from, to), Token);

    private Task<Result<RelationshipDto>> UnfollowAsync(Guid from, Guid to) =>
        new UnfollowUserCommandHandler(_follows, _requests, _identity, _graphs, _unitOfWork, Service, Reader, _time).Handle(new UnfollowUserCommand(from, to), Token);

    private Task<Result<RelationshipDto>> RemoveFollowerAsync(Guid user, Guid follower) =>
        new RemoveFollowerCommandHandler(_follows, _identity, _graphs, _unitOfWork, Service, Reader).Handle(new RemoveFollowerCommand(user, follower), Token);

    private Task<Result<RelationshipDto>> AcceptAsync(Guid user, Guid request) =>
        new AcceptFollowRequestCommandHandler(_requests, _graphs, _unitOfWork, Service, Reader, _time).Handle(new AcceptFollowRequestCommand(user, request), Token);

    private Task<Result> RejectAsync(Guid user, Guid request) =>
        new RejectFollowRequestCommandHandler(_requests, _unitOfWork, _time).Handle(new RejectFollowRequestCommand(user, request), Token);

    private Task<Result> CancelAsync(Guid user, Guid request) =>
        new CancelFollowRequestCommandHandler(_requests, _unitOfWork, _time).Handle(new CancelFollowRequestCommand(user, request), Token);

    private Task<Result<RelationshipDto>> RelationshipAsync(Guid viewer, Guid user) =>
        new GetRelationshipQueryHandler(_identity, Reader).Handle(new GetRelationshipQuery(viewer, user), Token);

    private Task<Result<BuildingBlocks.Application.Paging.PagedResponse<PersonDto>>> PeopleAsync(Guid viewer, Guid user, PeopleList list) =>
        new GetPeopleQueryHandler(_follows, _identity, _graphs, Summaries).Handle(new GetPeopleQuery(viewer, user, list, null, null), Token);

    private readonly FakePosts _posts = new();
    private readonly FakePostActions _postActions = new();

    private Task<Result> BlockAsync(Guid from, Guid to) =>
        new BlockUserCommandHandler(_blocks, _follows, _requests, _postActions, _identity, _graphs, _unitOfWork, _time).Handle(new BlockUserCommand(from, to), Token);

    private void AddFollow(Guid follower, Guid followee) =>
        _follows.Add(Follow.Start(Guid.CreateVersion7(), follower, followee, theyFollowBack: false, Now));

    private void Befriend(Guid first, Guid second)
    {
        AddFollow(first, second);
        AddFollow(second, first);
    }

    private FollowRequest AddRequest(Guid requester, Guid target)
    {
        var request = FollowRequest.Send(Guid.CreateVersion7(), requester, target, Now);
        _requests.Add(request);
        return request;
    }

    [Fact]
    public async Task Following_a_public_profile_is_immediate_and_repeating_it_changes_nothing()
    {
        var first = await FollowAsync(Ana, Bea);
        var second = await FollowAsync(Ana, Bea);

        first.Value.ShouldBe(new RelationshipDto("Following", false, null, null));
        second.Value.Status.ShouldBe("Following");
        _follows.Items.Count.ShouldBe(1);
        _requests.Items.ShouldBeEmpty();
        _graphs.Invalidated.ShouldBe([Ana, Bea], ignoreOrder: true);
    }

    [Fact]
    public async Task Following_a_private_profile_asks_first_and_repeating_it_does_not_ask_twice()
    {
        var first = await FollowAsync(Ana, Dani);
        var second = await FollowAsync(Ana, Dani);

        first.Value.Status.ShouldBe("Requested");
        first.Value.RequestId.ShouldBe(_requests.Items.Single().Id);
        second.Value.Status.ShouldBe("Requested");
        _follows.Items.ShouldBeEmpty();
        _requests.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Someone_who_already_follows_a_profile_that_became_private_stays_a_follower()
    {
        AddFollow(Ana, Dani);

        var answer = await FollowAsync(Ana, Dani);

        answer.Value.Status.ShouldBe("Following");
        _requests.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Following_someone_who_already_follows_you_makes_you_friends()
    {
        AddFollow(Bea, Ana);

        var answer = await FollowAsync(Ana, Bea);

        answer.Value.Status.ShouldBe("Friends");
        answer.Value.FollowsYou.ShouldBeTrue();
        _follows.Items.Single(follow => follow.FollowerId == Ana).DomainEvents.OfType<FriendshipStartedDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_people_following_at_the_same_time_end_up_as_friends()
    {
        // The database rejects this save because the row is already there.
        AddFollow(Bea, Ana);
        _unitOfWork.FailNextSave = true;
        _unitOfWork.OnDiscard = () =>
        {
            _follows.Items.RemoveAll(follow => follow.FollowerId == Ana);
            AddFollow(Ana, Bea);
        };

        var answer = await FollowAsync(Ana, Bea);

        answer.Value.Status.ShouldBe("Friends");
        _follows.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Nobody_can_follow_themselves()
    {
        (await FollowAsync(Ana, Ana)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
    }

    [Fact]
    public async Task Someone_who_does_not_exist_and_someone_with_a_block_give_the_same_answer()
    {
        await BlockAsync(Bea, Ana);
        await BlockAsync(Ana, Carlos);

        (await FollowAsync(Ana, Guid.CreateVersion7())).Error.ShouldBe(SocialErrors.UserNotFound);
        (await FollowAsync(Ana, Bea)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await FollowAsync(Ana, Carlos)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Stopping_ends_the_friendship_for_both_and_forgets_their_graphs()
    {
        Befriend(Ana, Bea);

        var answer = await UnfollowAsync(Ana, Bea);

        answer.Value.Status.ShouldBe("None");
        answer.Value.FollowsYou.ShouldBeTrue();
        _follows.Items.Single().FollowerId.ShouldBe(Bea);
        _graphs.Invalidated.ShouldBe([Ana, Bea], ignoreOrder: true);
        (await RelationshipAsync(Bea, Ana)).Value.Status.ShouldBe("Following");
    }

    [Fact]
    public async Task Stopping_cancels_the_request_that_was_not_answered()
    {
        await FollowAsync(Ana, Dani);

        var answer = await UnfollowAsync(Ana, Dani);

        answer.Value.Status.ShouldBe("None");
        _requests.Items.Single().Status.ShouldBe(FollowRequestStatus.Cancelled);
    }

    [Fact]
    public async Task Stopping_when_nothing_is_there_changes_nothing()
    {
        (await UnfollowAsync(Ana, Bea)).Value.Status.ShouldBe("None");
    }

    [Fact]
    public async Task Removing_a_follower_ends_the_friendship_but_you_keep_following_them()
    {
        Befriend(Ana, Bea);

        var answer = await RemoveFollowerAsync(Ana, Bea);

        answer.Value.Status.ShouldBe("Following");
        answer.Value.FollowsYou.ShouldBeFalse();
        _follows.Items.Single().FollowerId.ShouldBe(Ana);
    }

    [Fact]
    public async Task Accepting_lets_that_person_follow_you_and_does_not_make_you_follow_them()
    {
        var request = AddRequest(Ana, Dani);

        var answer = await AcceptAsync(Dani, request.Id);

        answer.Value.Status.ShouldBe("None");
        answer.Value.FollowsYou.ShouldBeTrue();
        _follows.Items.Single().FollowerId.ShouldBe(Ana);
        (await RelationshipAsync(Ana, Dani)).Value.Status.ShouldBe("Following");
    }

    [Fact]
    public async Task Accepting_someone_you_follow_makes_you_friends()
    {
        AddFollow(Dani, Ana);
        var request = AddRequest(Ana, Dani);

        (await AcceptAsync(Dani, request.Id)).Value.Status.ShouldBe("Friends");
    }

    [Fact]
    public async Task Accepting_twice_creates_one_follow()
    {
        var request = AddRequest(Ana, Dani);

        await AcceptAsync(Dani, request.Id);
        (await AcceptAsync(Dani, request.Id)).IsSuccess.ShouldBeTrue();

        _follows.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Only_who_receives_a_request_can_answer_it_and_only_who_sent_it_can_cancel_it()
    {
        var request = AddRequest(Ana, Dani);

        (await AcceptAsync(Ana, request.Id)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await AcceptAsync(Carlos, request.Id)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await RejectAsync(Carlos, request.Id)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await CancelAsync(Dani, request.Id)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await CancelAsync(Ana, request.Id)).IsSuccess.ShouldBeTrue();
        (await AcceptAsync(Dani, request.Id)).Error.ShouldBe(SocialErrors.RequestNotPending);
        _follows.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejecting_leaves_no_follow_and_can_be_asked_again()
    {
        var request = AddRequest(Ana, Dani);

        (await RejectAsync(Dani, request.Id)).IsSuccess.ShouldBeTrue();
        (await RejectAsync(Dani, request.Id)).IsSuccess.ShouldBeTrue();
        (await RelationshipAsync(Ana, Dani)).Value.Status.ShouldBe("None");

        (await FollowAsync(Ana, Dani)).Value.Status.ShouldBe("Requested");
    }

    [Fact]
    public async Task A_request_that_arrives_while_blocking_cannot_be_accepted_or_seen()
    {
        var request = FollowRequest.Send(Guid.CreateVersion7(), Bea, Dani, Now);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Dani, Bea, Now));
        _requests.Add(request);

        (await AcceptAsync(Dani, request.Id)).Error.ShouldBe(SocialErrors.RequestNotFound);
        var received = await new GetFollowRequestsQueryHandler(_requests, _graphs, Summaries).Handle(new GetFollowRequestsQuery(Dani, RequestDirection.Received, null, null), Token);
        received.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_of_requests_are_paged_from_the_most_recent()
    {
        var handler = new GetFollowRequestsQueryHandler(_requests, _graphs, Summaries);
        await FollowAsync(Bea, Dani);
        _time.Advance(TimeSpan.FromMinutes(1));
        await FollowAsync(Carlos, Dani);

        var first = await handler.Handle(new GetFollowRequestsQuery(Dani, RequestDirection.Received, 1, null), Token);
        var second = await handler.Handle(new GetFollowRequestsQuery(Dani, RequestDirection.Received, 1, first.Value.NextCursor), Token);

        first.Value.Items.Single().User.Id.ShouldBe(Carlos);
        first.Value.Items.Single().Direction.ShouldBe("Received");
        second.Value.Items.Single().User.Id.ShouldBe(Bea);
        second.Value.NextCursor.ShouldBeNull();
        (await handler.Handle(new GetFollowRequestsQuery(Dani, RequestDirection.Sent, null, null), Token)).Value.Items.ShouldBeEmpty();
        (await handler.Handle(new GetFollowRequestsQuery(Dani, RequestDirection.Received, null, "not a cursor"), Token)).Error.ShouldBe(SocialErrors.InvalidCursor);
    }

    [Fact]
    public async Task The_relationship_says_what_each_person_can_do()
    {
        (await RelationshipAsync(Ana, Ana)).Value.Status.ShouldBe("Self");
        (await RelationshipAsync(Ana, Bea)).Value.ShouldBe(new RelationshipDto("None", false, null, null));

        await FollowAsync(Bea, Ana);
        (await RelationshipAsync(Ana, Bea)).Value.ShouldBe(new RelationshipDto("None", true, null, null));
        (await RelationshipAsync(Bea, Ana)).Value.ShouldBe(new RelationshipDto("Following", false, null, null));

        await FollowAsync(Ana, Bea);
        (await RelationshipAsync(Ana, Bea)).Value.ShouldBe(new RelationshipDto("Friends", true, null, null));
    }

    [Fact]
    public async Task The_relationship_shows_the_requests_of_each_side()
    {
        var request = AddRequest(Ana, Dani);

        (await RelationshipAsync(Ana, Dani)).Value.ShouldBe(new RelationshipDto("Requested", false, request.Id, null));
        (await RelationshipAsync(Dani, Ana)).Value.ShouldBe(new RelationshipDto("None", false, null, request.Id));
    }

    [Fact]
    public async Task Who_is_blocked_cannot_tell_that_they_are()
    {
        await BlockAsync(Ana, Bea);

        (await RelationshipAsync(Ana, Bea)).Value.Status.ShouldBe("Blocked");
        (await RelationshipAsync(Bea, Ana)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await RelationshipAsync(Bea, Guid.CreateVersion7())).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Theory]
    [InlineData(PeopleList.Friends)]
    [InlineData(PeopleList.Followers)]
    [InlineData(PeopleList.Following)]
    public async Task Only_that_person_and_their_friends_can_see_their_lists(PeopleList list)
    {
        Befriend(Ana, Bea);
        AddFollow(Carlos, Ana);
        AddFollow(Ana, Dani);

        (await PeopleAsync(Ana, Ana, list)).IsSuccess.ShouldBeTrue();
        (await PeopleAsync(Bea, Ana, list)).IsSuccess.ShouldBeTrue();
        (await PeopleAsync(Carlos, Ana, list)).Error.ShouldBe(SocialErrors.ListsPrivate);
        (await PeopleAsync(Dani, Ana, list)).Error.ShouldBe(SocialErrors.ListsPrivate);
    }

    [Theory]
    [InlineData(PeopleList.Friends)]
    [InlineData(PeopleList.Followers)]
    [InlineData(PeopleList.Following)]
    public async Task With_a_block_or_without_an_account_the_lists_do_not_exist(PeopleList list)
    {
        await BlockAsync(Bea, Ana);

        (await PeopleAsync(Ana, Bea, list)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await PeopleAsync(Bea, Ana, list)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await PeopleAsync(Ana, Guid.CreateVersion7(), list)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Each_list_has_the_right_people()
    {
        Befriend(Ana, Bea);
        AddFollow(Carlos, Ana);
        AddFollow(Ana, Dani);

        (await PeopleAsync(Ana, Ana, PeopleList.Friends)).Value.Items.Select(person => person.User.Id).ShouldBe([Bea]);
        (await PeopleAsync(Ana, Ana, PeopleList.Followers)).Value.Items.Select(person => person.User.Id).ShouldBe([Bea, Carlos], ignoreOrder: true);
        (await PeopleAsync(Ana, Ana, PeopleList.Following)).Value.Items.Select(person => person.User.Id).ShouldBe([Bea, Dani], ignoreOrder: true);
    }

    [Fact]
    public async Task A_private_profile_that_you_do_not_follow_shows_only_its_username_in_a_list()
    {
        AddFollow(Dani, Ana);
        AddFollow(Dani, Bea);
        AddFollow(Bea, Dani);

        var strangerSees = (await PeopleAsync(Ana, Ana, PeopleList.Followers)).Value.Items.Single().User;
        var followerSees = (await PeopleAsync(Bea, Bea, PeopleList.Followers)).Value.Items.Single().User;

        strangerSees.DisplayName.ShouldBe("dani");
        strangerSees.City.ShouldBeNull();
        followerSees.DisplayName.ShouldBe("Name of dani");
        followerSees.City.ShouldBe("Madrid");
    }

    [Fact]
    public async Task An_account_being_deleted_is_left_out_of_the_lists()
    {
        Befriend(Ana, Bea);
        _identity.Users.Remove(Bea);

        (await PeopleAsync(Ana, Ana, PeopleList.Friends)).Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Anyone_can_see_the_numbers_even_of_a_private_profile_but_not_with_a_block()
    {
        Befriend(Dani, Bea);
        AddFollow(Carlos, Dani);
        var handler = new GetCountsQueryHandler(_follows, _identity, _graphs);

        (await handler.Handle(new GetCountsQuery(Ana, Dani), Token)).Value.ShouldBe(new CountsDto(2, 1, 1));
        (await handler.Handle(new GetCountsQuery(Dani, Dani), Token)).Value.ShouldBe(new CountsDto(2, 1, 1));

        await BlockAsync(Dani, Ana);
        (await handler.Handle(new GetCountsQuery(Ana, Dani), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await handler.Handle(new GetCountsQuery(Ana, Guid.CreateVersion7()), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Blocking_removes_the_follows_and_the_requests_in_both_directions_and_ends_one_friendship()
    {
        Befriend(Ana, Bea);
        foreach (var follow in _follows.Items)
        {
            follow.ClearDomainEvents();
        }

        var mine = _follows.Items.Single(follow => follow.FollowerId == Ana);
        var theirs = _follows.Items.Single(follow => follow.FollowerId == Bea);
        AddRequest(Carlos, Ana);
        AddRequest(Ana, Carlos);

        await BlockAsync(Ana, Bea);
        await BlockAsync(Ana, Carlos);

        _follows.Items.ShouldBeEmpty();
        _requests.Items.ShouldBeEmpty();
        _blocks.Items.Count.ShouldBe(2);
        (mine.DomainEvents.Count + theirs.DomainEvents.Count).ShouldBe(1);
        _graphs.Invalidated.ShouldContain(Bea);
        _graphs.Invalidated.ShouldContain(Carlos);
    }

    [Fact]
    public async Task Blocking_twice_is_the_same_as_blocking_once()
    {
        await BlockAsync(Ana, Bea);
        await BlockAsync(Ana, Bea);

        _blocks.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task You_cannot_block_yourself_or_someone_who_does_not_exist()
    {
        (await BlockAsync(Ana, Ana)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
        (await BlockAsync(Ana, Guid.CreateVersion7())).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Unblocking_does_not_bring_back_the_follows()
    {
        Befriend(Ana, Bea);
        await BlockAsync(Ana, Bea);

        var handler = new UnblockUserCommandHandler(_blocks, _graphs, _unitOfWork);
        (await handler.Handle(new UnblockUserCommand(Ana, Bea), Token)).IsSuccess.ShouldBeTrue();
        (await handler.Handle(new UnblockUserCommand(Ana, Bea), Token)).IsSuccess.ShouldBeTrue();

        _blocks.Items.ShouldBeEmpty();
        _follows.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_list_of_blocks_shows_the_other_person()
    {
        await BlockAsync(Ana, Carlos);

        var blocked = await new GetBlocksQueryHandler(_blocks, Summaries).Handle(new GetBlocksQuery(Ana, null, null), Token);

        blocked.Value.Items.Single().User.Id.ShouldBe(Carlos);
    }

    [Fact]
    public async Task A_report_is_kept_once_while_it_is_open()
    {
        var handler = new CreateReportCommandHandler(_reports, _posts, new FakeComments(), _identity, _graphs, _unitOfWork, _time);

        var first = await handler.Handle(new CreateReportCommand(Ana, "User", Bea, "Harassment", "  insists  "), Token);
        var second = await handler.Handle(new CreateReportCommand(Ana, "User", Bea, "Spam", null), Token);

        first.Value.Created.ShouldBeTrue();
        second.Value.Created.ShouldBeFalse();
        second.Value.Report.Id.ShouldBe(first.Value.Report.Id);
        _reports.Items.Single().Details.ShouldBe("insists");
    }

    [Fact]
    public async Task You_cannot_report_yourself_or_someone_who_does_not_exist_but_you_can_report_someone_you_blocked()
    {
        var handler = new CreateReportCommandHandler(_reports, _posts, new FakeComments(), _identity, _graphs, _unitOfWork, _time);
        await BlockAsync(Ana, Bea);

        (await handler.Handle(new CreateReportCommand(Ana, "User", Ana, "Spam", null), Token)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
        (await handler.Handle(new CreateReportCommand(Ana, "User", Guid.CreateVersion7(), "Spam", null), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await handler.Handle(new CreateReportCommand(Ana, "User", Bea, "Spam", null), Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_profile_that_becomes_public_accepts_who_was_waiting()
    {
        var pending = AddRequest(Ana, Dani);
        var answered = AddRequest(Bea, Dani);
        answered.Reject(Now);
        var other = AddRequest(Carlos, Ana);
        var consumer = new AcceptRequestsOnProfilePublic(_requests, Service, _graphs, _unitOfWork, _time);

        await consumer.Handle(new UserPrivacyChanged(Dani, IsPrivate: false, Now), Token);

        pending.Status.ShouldBe(FollowRequestStatus.Accepted);
        answered.Status.ShouldBe(FollowRequestStatus.Rejected);
        other.Status.ShouldBe(FollowRequestStatus.Pending);
        _follows.Items.Single().FollowerId.ShouldBe(Ana);
        _graphs.Invalidated.ShouldContain(Ana);
    }

    [Fact]
    public async Task A_profile_that_becomes_private_keeps_its_requests_and_followers()
    {
        var pending = AddRequest(Ana, Dani);
        AddFollow(Bea, Dani);
        var consumer = new AcceptRequestsOnProfilePublic(_requests, Service, _graphs, _unitOfWork, _time);

        await consumer.Handle(new UserPrivacyChanged(Dani, IsPrivate: true, Now), Token);

        pending.Status.ShouldBe(FollowRequestStatus.Pending);
        _follows.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_an_account_erases_all_that_links_it_to_other_people()
    {
        Befriend(Ana, Bea);
        Befriend(Bea, Carlos);
        AddRequest(Carlos, Ana);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Ana, Carlos, Now));
        _reports.Add(Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Carlos, ReportReason.Spam, null, Now));
        _reports.Add(Report.Create(Guid.CreateVersion7(), Carlos, ReportTargetType.User, Ana, ReportReason.Spam, null, Now));

        await new DeleteSocialDataOnUserDeleted(_requests, _follows, _blocks, _reports, _posts).Handle(new UserDeleted(Ana, Now), Token);

        _follows.Items.Count.ShouldBe(2);
        _follows.Items.ShouldAllBe(follow => follow.FollowerId != Ana && follow.FolloweeId != Ana);
        _requests.Items.ShouldBeEmpty();
        _blocks.Items.ShouldBeEmpty();
        _reports.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_api_for_other_modules_answers_each_relationship()
    {
        Befriend(Ana, Bea);
        AddFollow(Ana, Carlos);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Dani, Ana, Now));
        var api = new RelationshipsApi(_graphs);

        (await api.GetRelationshipAsync(Ana, Ana, Token)).ShouldBe(Relationship.Self);
        (await api.GetRelationshipAsync(Ana, Bea, Token)).ShouldBe(Relationship.Friend);
        (await api.GetRelationshipAsync(Ana, Carlos, Token)).ShouldBe(Relationship.Follower);
        (await api.GetRelationshipAsync(Carlos, Ana, Token)).ShouldBe(Relationship.None);
        (await api.GetRelationshipAsync(Ana, Dani, Token)).ShouldBe(Relationship.Blocked);
    }

    [Fact]
    public async Task A_block_wins_over_a_follow_and_hides_people_in_both_directions()
    {
        // A follow accepted just while blocking.
        Befriend(Ana, Bea);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Ana, Bea, Now));
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Carlos, Ana, Now));
        var api = new RelationshipsApi(_graphs);

        (await api.GetRelationshipAsync(Ana, Bea, Token)).ShouldBe(Relationship.Blocked);
        (await api.GetHiddenUserIdsAsync(Ana, Token)).ShouldBe([Bea, Carlos], ignoreOrder: true);
        (await api.GetFollowingIdsAsync(Ana, Token)).ShouldBe([Bea]);
    }

    [Fact]
    public void The_graph_survives_the_copy_in_redis()
    {
        var graph = new SocialGraph([Ana], [Bea], [Carlos]);

        // HybridCache writes it with the default options of System.Text.Json.
        var copy = System.Text.Json.JsonSerializer.Deserialize<SocialGraph>(System.Text.Json.JsonSerializer.Serialize(graph))!;

        copy.Following.ShouldBe([Ana]);
        copy.IsBlockedEitherWay(Bea).ShouldBeTrue();
        copy.IsBlockedEitherWay(Carlos).ShouldBeTrue();
        copy.IsBlockedEitherWay(Ana).ShouldBeFalse();
    }

    [Fact]
    public void The_validators_accept_only_what_the_contract_says()
    {
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Spam", null)).IsValid.ShouldBeTrue();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "Walk", Bea, "Spam", null)).IsValid.ShouldBeFalse();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Rude", null)).IsValid.ShouldBeFalse();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Spam", new string('x', Report.DetailsMaxLength + 1))).IsValid.ShouldBeFalse();
    }
}
