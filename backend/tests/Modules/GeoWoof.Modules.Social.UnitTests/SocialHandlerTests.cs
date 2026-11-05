using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Application.Blocks;
using GeoWoof.Modules.Social.Application.Events;
using GeoWoof.Modules.Social.Application.Friends;
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
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeRequests _requests = new();
    private readonly FakeFriendships _friendships = new();
    private readonly FakeBlocks _blocks = new();
    private readonly FakeReports _reports = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeIdentity _identity = new();
    private readonly FakeGraphs _graphs;

    public SocialHandlerTests()
    {
        _graphs = new FakeGraphs(_friendships, _blocks);
        _identity.Add(Ana, "ana");
        _identity.Add(Bea, "bea");
        _identity.Add(Carlos, "carlos");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private UserSummaries Summaries => new(_identity, new FakeMedia());

    private FriendRequestAcceptance Acceptance => new(_friendships, _time);

    private SendFriendRequestCommandHandler SendHandler => new(_requests, _friendships, _graphs, _unitOfWork, Acceptance, Summaries, _time);

    private AcceptFriendRequestCommandHandler AcceptHandler => new(_requests, _graphs, _unitOfWork, Acceptance, Summaries);

    private GetRelationshipQueryHandler RelationshipHandler => new(_requests, _graphs, Summaries);

    private BlockUserCommandHandler BlockHandler => new(_blocks, _friendships, _requests, _identity, _graphs, _unitOfWork, _time);

    private Task<BuildingBlocks.Domain.Results.Result<SendFriendRequestResult>> SendAsync(Guid from, Guid to) =>
        SendHandler.Handle(new SendFriendRequestCommand(from, to), Token);

    private async Task BlockAsync(Guid from, Guid to) => (await BlockHandler.Handle(new BlockUserCommand(from, to), Token)).IsSuccess.ShouldBeTrue();

    private async Task<string> RelationshipAsync(Guid viewer, Guid user) =>
        (await RelationshipHandler.Handle(new GetRelationshipQuery(viewer, user), Token)).Value.Status;

    private void Befriend(Guid first, Guid second) => _friendships.Add(Friendship.Between(Guid.CreateVersion7(), first, second, Now));

    [Fact]
    public async Task A_new_request_is_created_and_can_be_sent_again_without_duplicating_it()
    {
        var first = await SendAsync(Ana, Bea);
        var second = await SendAsync(Ana, Bea);

        first.Value.Created.ShouldBeTrue();
        first.Value.Request.Direction.ShouldBe("Sent");
        first.Value.Request.Status.ShouldBe("Pending");
        first.Value.Request.User.Id.ShouldBe(Bea);
        second.Value.Created.ShouldBeFalse();
        second.Value.Request.Id.ShouldBe(first.Value.Request.Id);
        _requests.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Asking_someone_who_had_already_asked_makes_you_friends()
    {
        await SendAsync(Bea, Ana);

        var answer = await SendAsync(Ana, Bea);

        answer.Value.Created.ShouldBeFalse();
        answer.Value.Request.Status.ShouldBe("Accepted");
        answer.Value.Request.Direction.ShouldBe("Received");
        _friendships.Items.Count.ShouldBe(1);
        _graphs.Invalidated.ShouldBe([Ana, Bea], ignoreOrder: true);
    }

    [Fact]
    public async Task Two_people_asking_at_the_same_time_end_up_as_friends()
    {
        // What Bea sent while this request was saving: the database rejects a second pending request.
        _unitOfWork.FailNextSave = true;
        _unitOfWork.OnDiscard = () =>
        {
            _requests.Items.Clear();
            _requests.Add(FriendRequest.Send(Guid.CreateVersion7(), Bea, Ana, Now));
        };

        var answer = await SendAsync(Ana, Bea);

        answer.Value.Request.Status.ShouldBe("Accepted");
        _friendships.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task You_cannot_ask_yourself()
    {
        (await SendAsync(Ana, Ana)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
    }

    [Fact]
    public async Task You_cannot_ask_a_friend()
    {
        Befriend(Ana, Bea);

        (await SendAsync(Ana, Bea)).Error.ShouldBe(SocialErrors.AlreadyFriends);
    }

    [Fact]
    public async Task Someone_who_does_not_exist_and_someone_with_a_block_give_the_same_answer()
    {
        await BlockAsync(Bea, Ana);
        await BlockAsync(Ana, Carlos);

        (await SendAsync(Ana, Guid.CreateVersion7())).Error.ShouldBe(SocialErrors.UserNotFound);
        (await SendAsync(Ana, Bea)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await SendAsync(Ana, Carlos)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Accepting_creates_one_friendship_even_if_repeated()
    {
        var request = (await SendAsync(Bea, Ana)).Value.Request;

        var first = await AcceptHandler.Handle(new AcceptFriendRequestCommand(Ana, request.Id), Token);
        var second = await AcceptHandler.Handle(new AcceptFriendRequestCommand(Ana, request.Id), Token);

        first.Value.Status.ShouldBe("Accepted");
        second.Value.Status.ShouldBe("Accepted");
        _friendships.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Only_who_receives_a_request_can_accept_or_reject_it()
    {
        var request = (await SendAsync(Bea, Ana)).Value.Request;

        (await AcceptHandler.Handle(new AcceptFriendRequestCommand(Bea, request.Id), Token)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await AcceptHandler.Handle(new AcceptFriendRequestCommand(Carlos, request.Id), Token)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await new RejectFriendRequestCommandHandler(_requests, _unitOfWork, _time).Handle(new RejectFriendRequestCommand(Carlos, request.Id), Token))
            .Error.ShouldBe(SocialErrors.RequestNotFound);
        _friendships.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_who_sent_a_request_can_cancel_it()
    {
        var request = (await SendAsync(Bea, Ana)).Value.Request;
        var handler = new CancelFriendRequestCommandHandler(_requests, _unitOfWork, _time);

        (await handler.Handle(new CancelFriendRequestCommand(Ana, request.Id), Token)).Error.ShouldBe(SocialErrors.RequestNotFound);
        (await handler.Handle(new CancelFriendRequestCommand(Bea, request.Id), Token)).IsSuccess.ShouldBeTrue();
        (await AcceptHandler.Handle(new AcceptFriendRequestCommand(Ana, request.Id), Token)).Error.ShouldBe(SocialErrors.RequestNotPending);
    }

    [Fact]
    public async Task A_request_that_arrives_while_blocking_cannot_be_accepted_or_seen()
    {
        var request = FriendRequest.Send(Guid.CreateVersion7(), Bea, Ana, Now);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Ana, Bea, Now));
        _requests.Add(request);

        (await AcceptHandler.Handle(new AcceptFriendRequestCommand(Ana, request.Id), Token)).Error.ShouldBe(SocialErrors.RequestNotFound);
        var received = await new GetFriendRequestsQueryHandler(_requests, _graphs, Summaries).Handle(new GetFriendRequestsQuery(Ana, RequestDirection.Received, null, null), Token);
        received.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_of_requests_are_paged_from_the_most_recent()
    {
        await SendAsync(Bea, Ana);
        _time.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(Carlos, Ana);
        var handler = new GetFriendRequestsQueryHandler(_requests, _graphs, Summaries);

        var first = await handler.Handle(new GetFriendRequestsQuery(Ana, RequestDirection.Received, 1, null), Token);
        var second = await handler.Handle(new GetFriendRequestsQuery(Ana, RequestDirection.Received, 1, first.Value.NextCursor), Token);

        first.Value.Items.Single().User.Id.ShouldBe(Carlos);
        second.Value.Items.Single().User.Id.ShouldBe(Bea);
        second.Value.NextCursor.ShouldBeNull();
        (await handler.Handle(new GetFriendRequestsQuery(Ana, RequestDirection.Sent, null, null), Token)).Value.Items.ShouldBeEmpty();
        (await handler.Handle(new GetFriendRequestsQuery(Ana, RequestDirection.Received, null, "not a cursor"), Token)).Error.ShouldBe(SocialErrors.InvalidCursor);
    }

    [Fact]
    public async Task Removing_a_friend_ends_it_for_both_and_forgets_their_graphs()
    {
        Befriend(Ana, Bea);
        var handler = new RemoveFriendCommandHandler(_friendships, _graphs, _unitOfWork);

        (await handler.Handle(new RemoveFriendCommand(Bea, Ana), Token)).IsSuccess.ShouldBeTrue();

        _friendships.Items.ShouldBeEmpty();
        _graphs.Invalidated.ShouldBe([Ana, Bea], ignoreOrder: true);
        (await handler.Handle(new RemoveFriendCommand(Bea, Ana), Token)).Error.ShouldBe(SocialErrors.NotFriends);
    }

    [Fact]
    public async Task Blocking_ends_the_friendship_and_the_requests_in_both_directions()
    {
        Befriend(Ana, Bea);
        _requests.Add(FriendRequest.Send(Guid.CreateVersion7(), Carlos, Ana, Now));

        await BlockAsync(Ana, Bea);
        await BlockAsync(Ana, Carlos);

        _friendships.Items.ShouldBeEmpty();
        _requests.Items.ShouldBeEmpty();
        _blocks.Items.Count.ShouldBe(2);
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
        (await BlockHandler.Handle(new BlockUserCommand(Ana, Ana), Token)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
        (await BlockHandler.Handle(new BlockUserCommand(Ana, Guid.CreateVersion7()), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Unblocking_does_not_bring_back_the_friendship()
    {
        Befriend(Ana, Bea);
        await BlockAsync(Ana, Bea);

        var handler = new UnblockUserCommandHandler(_blocks, _graphs, _unitOfWork);
        (await handler.Handle(new UnblockUserCommand(Ana, Bea), Token)).IsSuccess.ShouldBeTrue();
        (await handler.Handle(new UnblockUserCommand(Ana, Bea), Token)).IsSuccess.ShouldBeTrue();

        _blocks.Items.ShouldBeEmpty();
        _friendships.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_relationship_says_what_each_person_can_do()
    {
        (await RelationshipAsync(Ana, Ana)).ShouldBe("Self");
        (await RelationshipAsync(Ana, Bea)).ShouldBe("None");

        var request = (await SendAsync(Ana, Bea)).Value.Request;
        var sent = await RelationshipHandler.Handle(new GetRelationshipQuery(Ana, Bea), Token);
        var received = await RelationshipHandler.Handle(new GetRelationshipQuery(Bea, Ana), Token);
        sent.Value.ShouldBe(new RelationshipDto("RequestSent", request.Id));
        received.Value.ShouldBe(new RelationshipDto("RequestReceived", request.Id));

        await AcceptHandler.Handle(new AcceptFriendRequestCommand(Bea, request.Id), Token);
        (await RelationshipAsync(Ana, Bea)).ShouldBe("Friends");
        (await RelationshipAsync(Bea, Ana)).ShouldBe("Friends");
    }

    [Fact]
    public async Task Who_is_blocked_cannot_tell_that_they_are()
    {
        await BlockAsync(Ana, Bea);

        (await RelationshipAsync(Ana, Bea)).ShouldBe("Blocked");
        (await RelationshipHandler.Handle(new GetRelationshipQuery(Bea, Ana), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await RelationshipHandler.Handle(new GetRelationshipQuery(Bea, Guid.CreateVersion7()), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
    }

    [Fact]
    public async Task Lists_of_friends_and_blocks_show_the_other_person()
    {
        Befriend(Ana, Bea);
        await BlockAsync(Ana, Carlos);

        var friends = await new GetFriendsQueryHandler(_friendships, _graphs, Summaries).Handle(new GetFriendsQuery(Ana, null, null), Token);
        var blocked = await new GetBlocksQueryHandler(_blocks, Summaries).Handle(new GetBlocksQuery(Ana, null, null), Token);

        friends.Value.Items.Single().User.Id.ShouldBe(Bea);
        blocked.Value.Items.Single().User.Id.ShouldBe(Carlos);
    }

    [Fact]
    public async Task An_account_being_deleted_is_left_out_of_the_lists()
    {
        Befriend(Ana, Bea);
        _identity.Users.Remove(Bea);

        var friends = await new GetFriendsQueryHandler(_friendships, _graphs, Summaries).Handle(new GetFriendsQuery(Ana, null, null), Token);

        friends.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_report_is_kept_once_while_it_is_open()
    {
        var handler = new CreateReportCommandHandler(_reports, _identity, _unitOfWork, _time);

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
        var handler = new CreateReportCommandHandler(_reports, _identity, _unitOfWork, _time);
        await BlockAsync(Ana, Bea);

        (await handler.Handle(new CreateReportCommand(Ana, "User", Ana, "Spam", null), Token)).Error.ShouldBe(SocialErrors.CannotTargetSelf);
        (await handler.Handle(new CreateReportCommand(Ana, "User", Guid.CreateVersion7(), "Spam", null), Token)).Error.ShouldBe(SocialErrors.UserNotFound);
        (await handler.Handle(new CreateReportCommand(Ana, "User", Bea, "Spam", null), Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_an_account_erases_all_that_links_it_to_other_people()
    {
        Befriend(Ana, Bea);
        Befriend(Bea, Carlos);
        _requests.Add(FriendRequest.Send(Guid.CreateVersion7(), Carlos, Ana, Now));
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Ana, Carlos, Now));
        _reports.Add(Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Carlos, ReportReason.Spam, null, Now));
        _reports.Add(Report.Create(Guid.CreateVersion7(), Carlos, ReportTargetType.User, Ana, ReportReason.Spam, null, Now));

        await new DeleteSocialDataOnUserDeleted(_requests, _friendships, _blocks, _reports).Handle(new UserDeleted(Ana, Now), Token);

        _friendships.Items.Single().OtherThan(Bea).ShouldBe(Carlos);
        _requests.Items.ShouldBeEmpty();
        _blocks.Items.ShouldBeEmpty();
        _reports.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("Friend")]
    [InlineData("None")]
    [InlineData("Blocked")]
    public async Task The_api_for_other_modules_answers_each_relationship(string expected)
    {
        var target = Bea;
        switch (expected)
        {
            case "Self":
                target = Ana;
                break;
            case "Friend":
                Befriend(Ana, Bea);
                break;
            case "Blocked":
                _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Bea, Ana, Now));
                break;
        }

        var answer = await new RelationshipsApi(_graphs).GetRelationshipAsync(Ana, target, Token);

        answer.ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task A_block_wins_over_a_friendship_and_hides_people_in_both_directions()
    {
        // A friendship accepted just while blocking.
        Befriend(Ana, Bea);
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Ana, Bea, Now));
        _blocks.Add(UserBlock.Create(Guid.CreateVersion7(), Carlos, Ana, Now));
        var api = new RelationshipsApi(_graphs);

        (await api.GetRelationshipAsync(Ana, Bea, Token)).ShouldBe(Relationship.Blocked);
        (await api.GetHiddenUserIdsAsync(Ana, Token)).ShouldBe([Bea, Carlos], ignoreOrder: true);
    }

    [Fact]
    public void The_graph_survives_the_copy_in_redis()
    {
        var graph = new SocialGraph([Ana], [Bea], [Carlos]);

        // HybridCache writes it with the default options of System.Text.Json.
        var copy = System.Text.Json.JsonSerializer.Deserialize<SocialGraph>(System.Text.Json.JsonSerializer.Serialize(graph))!;

        copy.Friends.ShouldBe([Ana]);
        copy.IsBlockedEitherWay(Bea).ShouldBeTrue();
        copy.IsBlockedEitherWay(Carlos).ShouldBeTrue();
        copy.IsBlockedEitherWay(Ana).ShouldBeFalse();
    }

    [Fact]
    public void The_validators_accept_only_what_the_contract_says()
    {
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Spam", null)).IsValid.ShouldBeTrue();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "Post", Bea, "Spam", null)).IsValid.ShouldBeFalse();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Rude", null)).IsValid.ShouldBeFalse();
        new CreateReportCommandValidator().Validate(new CreateReportCommand(Ana, "User", Bea, "Spam", new string('x', Report.DetailsMaxLength + 1))).IsValid.ShouldBeFalse();
        new SendFriendRequestCommandValidator().Validate(new SendFriendRequestCommand(Ana, Guid.Empty)).IsValid.ShouldBeFalse();
    }
}
