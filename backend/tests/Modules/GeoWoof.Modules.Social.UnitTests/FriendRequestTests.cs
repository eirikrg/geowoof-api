using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.UnitTests;

public sealed class FriendRequestTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static FriendRequest Pending() => FriendRequest.Send(Guid.CreateVersion7(), Ana, Bea, Now);

    private static void AnswerWith(FriendRequest request, FriendRequestStatus status)
    {
        var result = status switch
        {
            FriendRequestStatus.Accepted => request.Accept(Now),
            FriendRequestStatus.Rejected => request.Reject(Now),
            FriendRequestStatus.Cancelled => request.Cancel(Now),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_same_pair_gives_the_same_key_whoever_asks()
    {
        var mine = FriendRequest.Send(Guid.CreateVersion7(), Ana, Bea, Now);
        var theirs = FriendRequest.Send(Guid.CreateVersion7(), Bea, Ana, Now);

        theirs.UserLowId.ShouldBe(mine.UserLowId);
        theirs.UserHighId.ShouldBe(mine.UserHighId);
    }

    [Theory]
    [InlineData(FriendRequestStatus.Accepted)]
    [InlineData(FriendRequestStatus.Rejected)]
    [InlineData(FriendRequestStatus.Cancelled)]
    public void A_pending_request_can_get_any_answer(FriendRequestStatus status)
    {
        var request = Pending();

        AnswerWith(request, status);

        request.Status.ShouldBe(status);
        request.RespondedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(FriendRequestStatus.Accepted)]
    [InlineData(FriendRequestStatus.Rejected)]
    [InlineData(FriendRequestStatus.Cancelled)]
    public void The_same_answer_can_be_repeated_in_case_the_first_response_was_lost(FriendRequestStatus status)
    {
        var request = Pending();
        AnswerWith(request, status);

        AnswerWith(request, status);

        request.Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(FriendRequestStatus.Accepted, FriendRequestStatus.Rejected)]
    [InlineData(FriendRequestStatus.Accepted, FriendRequestStatus.Cancelled)]
    [InlineData(FriendRequestStatus.Rejected, FriendRequestStatus.Accepted)]
    [InlineData(FriendRequestStatus.Rejected, FriendRequestStatus.Cancelled)]
    [InlineData(FriendRequestStatus.Cancelled, FriendRequestStatus.Accepted)]
    [InlineData(FriendRequestStatus.Cancelled, FriendRequestStatus.Rejected)]
    public void An_answered_request_cannot_change(FriendRequestStatus first, FriendRequestStatus second)
    {
        var request = Pending();
        AnswerWith(request, first);

        var result = second switch
        {
            FriendRequestStatus.Accepted => request.Accept(Now),
            FriendRequestStatus.Rejected => request.Reject(Now),
            _ => request.Cancel(Now),
        };

        result.Error.ShouldBe(SocialErrors.RequestNotPending);
        request.Status.ShouldBe(first);
    }

    [Fact]
    public void Accepting_twice_announces_it_only_once()
    {
        var request = Pending();
        request.ClearDomainEvents();

        request.Accept(Now);
        request.Accept(Now);

        request.DomainEvents.OfType<FriendRequestAcceptedDomainEvent>().Count().ShouldBe(1);
    }

    [Fact]
    public void A_friendship_knows_the_other_person_from_both_sides()
    {
        var friendship = Friendship.Between(Guid.CreateVersion7(), Bea, Ana, Now);

        friendship.OtherThan(Ana).ShouldBe(Bea);
        friendship.OtherThan(Bea).ShouldBe(Ana);
    }

    [Fact]
    public void A_report_keeps_empty_details_as_nothing()
    {
        Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Bea, ReportReason.Spam, "   ", Now).Details.ShouldBeNull();
        Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Bea, ReportReason.Spam, "  spam ", Now).Details.ShouldBe("spam");
    }
}
