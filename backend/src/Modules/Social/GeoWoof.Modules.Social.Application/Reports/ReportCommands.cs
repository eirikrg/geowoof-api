using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.Application.Reports;

/// <summary>Whether this request created the report decides between 201 and 200.</summary>
public sealed record CreateReportResult(ReportDto Report, bool Created);

public sealed record CreateReportCommand(Guid ReporterId, string TargetType, Guid TargetId, string Reason, string? Details) : ICommand<CreateReportResult>;

internal sealed class CreateReportCommandValidator : AbstractValidator<CreateReportCommand>
{
    public CreateReportCommandValidator()
    {
        RuleFor(command => command.TargetType).NotNull().IsEnumName(typeof(ReportTargetType));
        RuleFor(command => command.TargetId).NotEmpty();
        RuleFor(command => command.Reason).NotNull().IsEnumName(typeof(ReportReason));
        RuleFor(command => command.Details).MaximumLength(Report.DetailsMaxLength);
    }
}

internal sealed class CreateReportCommandHandler(
    IReportRepository reports,
    IPostRepository posts,
    ICommentRepository comments,
    IIdentityApi identity,
    ISocialGraphCache graphs,
    ISocialUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<CreateReportCommand, CreateReportResult>
{
    public async Task<Result<CreateReportResult>> Handle(CreateReportCommand command, CancellationToken cancellationToken)
    {
        var targetType = Enum.Parse<ReportTargetType>(command.TargetType);
        if (await CheckTargetAsync(command, targetType, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await reports.GetOpenAsync(command.ReporterId, targetType, command.TargetId, cancellationToken) is { } open)
        {
            return new CreateReportResult(new ReportDto(open.Id), Created: false);
        }

        var now = timeProvider.GetUtcNow();
        var report = Report.Create(Guid.CreateVersion7(now), command.ReporterId, targetType, command.TargetId, Enum.Parse<ReportReason>(command.Reason), command.Details, now);
        reports.Add(report);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }

        return new CreateReportResult(new ReportDto(report.Id), Created: true);
    }

    private async Task<Error?> CheckTargetAsync(CreateReportCommand command, ReportTargetType targetType, CancellationToken cancellationToken)
    {
        switch (targetType)
        {
            case ReportTargetType.Post:
                var post = await FindVisiblePostAsync(command.ReporterId, command.TargetId, cancellationToken);
                if (post is null)
                {
                    return SocialErrors.PostNotFound;
                }

                return post.AuthorId == command.ReporterId ? SocialErrors.CannotTargetSelf : null;

            case ReportTargetType.Comment:
                var comment = await comments.GetAsync(command.TargetId, cancellationToken);
                // What is left of a deleted comment says nothing to report.
                if (comment is null
                    || comment.IsDeleted
                    || await FindVisiblePostAsync(command.ReporterId, comment.PostId, cancellationToken) is null
                    || (await graphs.GetAsync(command.ReporterId, cancellationToken)).IsBlockedEitherWay(comment.AuthorId))
                {
                    return SocialErrors.CommentNotFound;
                }

                return comment.AuthorId == command.ReporterId ? SocialErrors.CannotTargetSelf : null;

            default:
                if (command.ReporterId == command.TargetId)
                {
                    return SocialErrors.CannotTargetSelf;
                }

                // Whoever reports may have blocked that person already, so only the account has to exist.
                var users = await identity.GetPublicUsersAsync([command.TargetId], cancellationToken);
                return users.ContainsKey(command.TargetId) ? null : SocialErrors.UserNotFound;
        }
    }

    private async Task<Post?> FindVisiblePostAsync(Guid viewerId, Guid postId, CancellationToken cancellationToken) =>
        await posts.GetVisibleAsync(postId, PostAudience.For(viewerId, await graphs.GetAsync(viewerId, cancellationToken)), cancellationToken);
}
