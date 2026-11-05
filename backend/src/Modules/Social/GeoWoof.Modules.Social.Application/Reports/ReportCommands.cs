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

internal sealed class CreateReportCommandHandler(IReportRepository reports, IIdentityApi identity, ISocialUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<CreateReportCommand, CreateReportResult>
{
    public async Task<Result<CreateReportResult>> Handle(CreateReportCommand command, CancellationToken cancellationToken)
    {
        if (command.ReporterId == command.TargetId)
        {
            return SocialErrors.CannotTargetSelf;
        }

        // Whoever reports may have blocked that person already, so only the account has to exist.
        var users = await identity.GetPublicUsersAsync([command.TargetId], cancellationToken);
        if (!users.ContainsKey(command.TargetId))
        {
            return SocialErrors.UserNotFound;
        }

        var targetType = Enum.Parse<ReportTargetType>(command.TargetType);
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
}
