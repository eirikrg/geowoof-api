using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Application.Events;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain.Sessions;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Identity.Domain.Verification;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Identity.UnitTests.Application;

/// <summary>
/// The real Identity application layer (decorated handlers, validators, services) wired to in-memory
/// fakes of its ports, so use cases can be tested without a database.
/// </summary>
internal sealed class IdentityTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public IdentityTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddOptions<AuthSettings>().Configure(settings =>
        {
            settings.MaxFailedSignInAttempts = 3;
            settings.LockoutDuration = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IUserRepository>(Users);
        services.AddSingleton<IRefreshTokenRepository>(RefreshTokens);
        services.AddSingleton<IVerificationCodeRepository>(Codes);
        services.AddSingleton<IIdentityUnitOfWork>(UnitOfWork);
        services.AddSingleton<IPasswordHasher>(PasswordHasher);
        services.AddSingleton<IAccessTokenIssuer, FakeAccessTokenIssuer>();
        services.AddSingleton<ISecretGenerator>(Secrets);
        services.AddSingleton<IGoogleIdTokenValidator>(Google);
        services.AddSingleton<IEmailSender>(Emails);
        services.AddSingleton<IMediaApi>(Media);
        services.AddSingleton<IRelationshipsApi>(Relationships);
        services.AddSingleton<IIntegrationEventPublisher>(Published);
        services.AddIdentityApplication();
        services.DecorateApplicationHandlers();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));

    public FakeMediaApi Media { get; } = new();

    public FakeRelationships Relationships { get; } = new();

    public InMemoryUsers Users { get; } = new();

    public InMemoryRefreshTokens RefreshTokens { get; } = new();

    public InMemoryCodes Codes { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakePasswordHasher PasswordHasher { get; } = new();

    public FakeSecrets Secrets { get; } = new();

    public FakeGoogle Google { get; } = new();

    public CapturingEmailSender Emails { get; } = new();

    public CapturingPublisher Published { get; } = new();

    public async Task<Result<TResponse>> SendAsync<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
        return await handler.Handle(command, TestContext.Current.CancellationToken);
    }

    public async Task<Result> SendAsync<TCommand>(TCommand command)
        where TCommand : ICommand
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>();
        return await handler.Handle(command, TestContext.Current.CancellationToken);
    }

    public async Task<Result<TResponse>> QueryAsync<TQuery, TResponse>(TQuery query)
        where TQuery : IQuery<TResponse>
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
        return await handler.Handle(query, TestContext.Current.CancellationToken);
    }

    /// <summary>A user stored directly in the fake repository.</summary>
    public User AddUser(string email, string? password = "Password1", bool verified = false, string? username = null)
    {
        var user = password is null
            ? User.RegisterWithGoogle(Guid.CreateVersion7(), Email.Create(email), "google-" + email, "Ana", Language.Es, Time.GetUtcNow())
            : User.RegisterWithPassword(Guid.CreateVersion7(), Email.Create(email), PasswordHasher.Hash(password), "Ana", Language.Es, Time.GetUtcNow());
        if (verified)
        {
            user.MarkEmailVerified();
        }

        if (username is not null)
        {
            user.ChangeUsername(Username.Create(username).Value);
        }

        user.ClearDomainEvents();
        Users.Add(user);
        return user;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

internal sealed class FakeMediaApi : IMediaApi
{
    public List<Guid> Attached { get; } = [];

    public List<Guid> Released { get; } = [];

    public MediaAttachFailure? AttachFailure { get; set; }

    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken)
    {
        if (AttachFailure is null)
        {
            Attached.Add(assetId);
        }

        return Task.FromResult(AttachFailure);
    }

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        Released.Add(assetId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, MediaImage>>(assetIds.ToDictionary(id => id, id => new MediaImage(id, "thumb", "medium")));

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class InMemoryUsers : IUserRepository
{
    public List<User> Items { get; } = [];

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(user => user.Id == userId));

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(user => user.Email == email));

    public Task<User?> GetByExternalLoginAsync(string provider, string subject, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(user => user.ExternalLogins.Contains(new ExternalLogin(provider, subject))));

    public Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Exists(user => user.Email == email));

    public Task<bool> UsernameExistsAsync(Username username, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Exists(user => user.Username == username));

    public Task<IReadOnlyList<User>> GetPublicByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>(Items.Where(user => userIds.Contains(user.Id) && user.HasCompletedOnboarding).ToList());

    /// <summary>The real search is SQL and has its own integration tests: this finds people by exact name, in order.</summary>
    public Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedIds, RelevancePosition after, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RelevancePosition>>(Items
            .Where(user => user.DisplayName == text && user.HasCompletedOnboarding && !excludedIds.Contains(user.Id))
            .Select(user => new RelevancePosition(1, user.Id))
            .OrderByDescending(hit => hit.Id)
            .Where(hit => hit.Id.CompareTo(after.Id) < 0 || after.Id == Guid.Empty)
            .Take(take)
            .ToList());

    public void Add(User user) => Items.Add(user);

    public void Remove(User user) => Items.Remove(user);
}

internal sealed class InMemoryRefreshTokens : IRefreshTokenRepository
{
    public List<RefreshToken> Items { get; } = [];

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(token => token.TokenHash == tokenHash));

    public Task<IReadOnlyList<RefreshToken>> GetActiveByFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(Items.Where(token => token.FamilyId == familyId && token.IsActive(now)).ToList());

    public Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(Items.Where(token => token.UserId == userId && token.IsActive(now)).ToList());

    public void Add(RefreshToken token) => Items.Add(token);

    public Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        Items.RemoveAll(token => token.UserId == userId);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryCodes : IVerificationCodeRepository
{
    public List<VerificationCode> Items { get; } = [];

    public Task<VerificationCode?> GetLatestAsync(Guid userId, VerificationPurpose purpose, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Where(code => code.UserId == userId && code.Purpose == purpose).MaxBy(code => code.CreatedAt));

    public Task<IReadOnlyList<VerificationCode>> GetUsableAsync(Guid userId, VerificationPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VerificationCode>>(
            Items.Where(code => code.UserId == userId && code.Purpose == purpose && code.IsUsable(now)).ToList());

    public void Add(VerificationCode code) => Items.Add(code);

    public Task RemoveAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        Items.RemoveAll(code => code.UserId == userId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IIdentityUnitOfWork
{
    public int Saves { get; private set; }

    /// <summary>When set, the next save throws it (simulates database constraint or concurrency errors).</summary>
    public Exception? FailNextSaveWith { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSaveWith is { } exception)
        {
            FailNextSaveWith = null;
            throw exception;
        }

        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int Simulations { get; private set; }

    public string Hash(string password) => "hashed:" + password;

    public PasswordCheck Verify(string passwordHash, string password) =>
        passwordHash == "hashed:" + password ? PasswordCheck.Succeeded : PasswordCheck.Failed;

    public void SimulateVerification(string password) => Simulations++;
}

internal sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
{
    public AccessToken Issue(User user, DateTimeOffset now) =>
        new($"access:{user.Id}:{(user.EmailVerified ? "verified" : "unverified")}", now.AddMinutes(15));
}

internal sealed class FakeSecrets : ISecretGenerator
{
    private int _tokens;

    public string NextCode { get; set; } = "123456";

    public string NewRefreshToken() => $"refresh-{Interlocked.Increment(ref _tokens)}";

    public string HashRefreshToken(string refreshToken) => "h:" + refreshToken;

    public string NewVerificationCode() => NextCode;

    public string HashVerificationCode(Guid userId, VerificationPurpose purpose, string code) => $"{purpose}:{userId}:{code}";
}

internal sealed class FakeGoogle : IGoogleIdTokenValidator
{
    public bool IsEnabled { get; set; } = true;

    public Dictionary<string, GoogleIdentity> Tokens { get; } = new(StringComparer.Ordinal);

    public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken) =>
        Task.FromResult(Tokens.GetValueOrDefault(idToken));
}

internal sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public bool Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("SMTP is down.");
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingPublisher : IIntegrationEventPublisher
{
    public List<IIntegrationEvent> Events { get; } = [];

    public void Publish(IIntegrationEvent integrationEvent) => Events.Add(integrationEvent);
}

/// <summary>Who is related to whom, in the tests of a module that only knows the contract of Social.</summary>
internal sealed class FakeRelationships : IRelationshipsApi
{
    private readonly HashSet<(Guid, Guid)> _friends = [];
    private readonly HashSet<(Guid, Guid)> _blocks = [];

    public void Befriend(Guid first, Guid second)
    {
        _friends.Add((first, second));
        _friends.Add((second, first));
    }

    public void Block(Guid blocker, Guid blocked) => _blocks.Add((blocker, blocked));

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewerId == targetId)
        {
            return Task.FromResult(Relationship.Self);
        }

        if (_blocks.Contains((viewerId, targetId)) || _blocks.Contains((targetId, viewerId)))
        {
            return Task.FromResult(Relationship.Blocked);
        }

        return Task.FromResult(_friends.Contains((viewerId, targetId)) ? Relationship.Friend : Relationship.None);
    }

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> hidden = _blocks
            .Where(block => block.Item1 == userId || block.Item2 == userId)
            .Select(block => block.Item1 == userId ? block.Item2 : block.Item1)
            .Distinct()
            .ToList();
        return Task.FromResult(hidden);
    }
}
