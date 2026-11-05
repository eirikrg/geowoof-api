using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.UnitTests;

public sealed class FollowTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static FollowRequest Pending() => FollowRequest.Send(Guid.CreateVersion7(), Ana, Bea, Now);

    private static void AnswerWith(FollowRequest request, FollowRequestStatus status)
    {
        var result = status switch
        {
            FollowRequestStatus.Accepted => request.Accept(Now),
            FollowRequestStatus.Rejected => request.Reject(Now),
            FollowRequestStatus.Cancelled => request.Cancel(Now),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Following_someone_who_does_not_follow_back_is_not_a_friendship()
    {
        var follow = Follow.Start(Guid.CreateVersion7(), Ana, Bea, theyFollowBack: false, Now);

        follow.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserFollowedDomainEvent>();
    }

    [Fact]
    public void Following_someone_who_follows_back_starts_a_friendship()
    {
        var follow = Follow.Start(Guid.CreateVersion7(), Ana, Bea, theyFollowBack: true, Now);

        follow.DomainEvents.OfType<FriendshipStartedDomainEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Stopping_only_ends_a_friendship_if_there_was_one()
    {
        var oneWay = Follow.Start(Guid.CreateVersion7(), Ana, Bea, theyFollowBack: false, Now);
        var mutual = Follow.Start(Guid.CreateVersion7(), Ana, Bea, theyFollowBack: true, Now);
        oneWay.ClearDomainEvents();
        mutual.ClearDomainEvents();

        oneWay.End(theyFollowBack: false);
        mutual.End(theyFollowBack: true);

        oneWay.DomainEvents.ShouldBeEmpty();
        mutual.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<FriendshipRemovedDomainEvent>();
    }

    [Theory]
    [InlineData(FollowRequestStatus.Accepted)]
    [InlineData(FollowRequestStatus.Rejected)]
    [InlineData(FollowRequestStatus.Cancelled)]
    public void A_pending_request_can_get_any_answer(FollowRequestStatus status)
    {
        var request = Pending();

        AnswerWith(request, status);

        request.Status.ShouldBe(status);
        request.RespondedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(FollowRequestStatus.Accepted)]
    [InlineData(FollowRequestStatus.Rejected)]
    [InlineData(FollowRequestStatus.Cancelled)]
    public void The_same_answer_can_be_repeated_in_case_the_first_response_was_lost(FollowRequestStatus status)
    {
        var request = Pending();
        AnswerWith(request, status);

        AnswerWith(request, status);

        request.Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(FollowRequestStatus.Accepted, FollowRequestStatus.Rejected)]
    [InlineData(FollowRequestStatus.Accepted, FollowRequestStatus.Cancelled)]
    [InlineData(FollowRequestStatus.Rejected, FollowRequestStatus.Accepted)]
    [InlineData(FollowRequestStatus.Rejected, FollowRequestStatus.Cancelled)]
    [InlineData(FollowRequestStatus.Cancelled, FollowRequestStatus.Accepted)]
    [InlineData(FollowRequestStatus.Cancelled, FollowRequestStatus.Rejected)]
    public void An_answered_request_cannot_change(FollowRequestStatus first, FollowRequestStatus second)
    {
        var request = Pending();
        AnswerWith(request, first);

        var result = second switch
        {
            FollowRequestStatus.Accepted => request.Accept(Now),
            FollowRequestStatus.Rejected => request.Reject(Now),
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

        request.DomainEvents.OfType<FollowRequestAcceptedDomainEvent>().Count().ShouldBe(1);
    }

    [Fact]
    public void A_request_knows_the_other_person_from_both_sides()
    {
        var request = Pending();

        request.OtherThan(Ana).ShouldBe(Bea);
        request.OtherThan(Bea).ShouldBe(Ana);
    }

    [Fact]
    public void A_report_keeps_empty_details_as_nothing()
    {
        Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Bea, ReportReason.Spam, "   ", Now).Details.ShouldBeNull();
        Report.Create(Guid.CreateVersion7(), Ana, ReportTargetType.User, Bea, ReportReason.Spam, "  spam ", Now).Details.ShouldBe("spam");
    }
}
