using GeoWoof.Modules.Social.Application.Abstractions;
using GeoWoof.Modules.Social.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GeoWoof.Modules.Social.Infrastructure.Persistence;

internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.ToTable("posts");
        builder.HasKey(post => post.Id);
        builder.Property(post => post.Id).ValueGeneratedNever();
        builder.Property(post => post.Text).HasMaxLength(Post.TextMaxLength);
        builder.Property(post => post.Visibility).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(post => new { post.AuthorId, post.CreatedAt, post.Id });

        // Explore reads the most recent of the whole app, always with these filters.
        builder.HasIndex(post => new { post.CreatedAt, post.Id })
            .HasDatabaseName("ix_posts_explore")
            .HasFilter("visibility = 'Everyone' AND NOT author_is_private");

        builder.HasMany(post => post.Media).WithOne().HasForeignKey(item => item.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(post => post.Pets).WithOne().HasForeignKey(pet => pet.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(post => post.Mentions).WithOne().HasForeignKey(mention => mention.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(post => post.Media).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(post => post.Pets).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(post => post.Mentions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PostMediaConfiguration : IEntityTypeConfiguration<PostMedia>
{
    public void Configure(EntityTypeBuilder<PostMedia> builder)
    {
        builder.ToTable("post_media");
        builder.HasKey(item => new { item.PostId, item.Position });
        builder.Property(item => item.Type).HasConversion<string>().HasMaxLength(16);
    }
}

internal sealed class PostPetConfiguration : IEntityTypeConfiguration<PostPet>
{
    public void Configure(EntityTypeBuilder<PostPet> builder)
    {
        builder.ToTable("post_pets");
        builder.HasKey(pet => new { pet.PostId, pet.PetId });
        builder.HasIndex(pet => pet.PetId);
    }
}

internal sealed class PostMentionConfiguration : IEntityTypeConfiguration<PostMention>
{
    public void Configure(EntityTypeBuilder<PostMention> builder)
    {
        builder.ToTable("post_mentions");
        builder.HasKey(mention => new { mention.PostId, mention.UserId });
        builder.Property(mention => mention.Username).HasMaxLength(30);
        builder.HasIndex(mention => mention.UserId);
    }
}

internal sealed class PostLikeConfiguration : IEntityTypeConfiguration<PostLike>
{
    public void Configure(EntityTypeBuilder<PostLike> builder)
    {
        builder.ToTable("post_likes");
        builder.HasKey(like => like.Id);
        builder.Property(like => like.Id).ValueGeneratedNever();
        builder.HasOne<Post>().WithMany().HasForeignKey(like => like.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(like => new { like.PostId, like.UserId }).IsUnique();
        builder.HasIndex(like => like.UserId);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("comments");
        builder.HasKey(comment => comment.Id);
        builder.Property(comment => comment.Id).ValueGeneratedNever();
        builder.Property(comment => comment.Text).HasMaxLength(Comment.TextMaxLength).IsRequired();
        builder.HasOne<Post>().WithMany().HasForeignKey(comment => comment.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(comment => new { comment.PostId, comment.CreatedAt, comment.Id });
        builder.HasIndex(comment => comment.AuthorId);
        builder.HasMany(comment => comment.Mentions).WithOne().HasForeignKey(mention => mention.CommentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(comment => comment.Mentions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CommentMentionConfiguration : IEntityTypeConfiguration<CommentMention>
{
    public void Configure(EntityTypeBuilder<CommentMention> builder)
    {
        builder.ToTable("comment_mentions");
        builder.HasKey(mention => new { mention.CommentId, mention.UserId });
        builder.Property(mention => mention.Username).HasMaxLength(30);
        builder.HasIndex(mention => mention.UserId);
    }
}

internal sealed class RepostConfiguration : IEntityTypeConfiguration<Repost>
{
    public void Configure(EntityTypeBuilder<Repost> builder)
    {
        builder.ToTable("reposts");
        builder.HasKey(repost => repost.Id);
        builder.Property(repost => repost.Id).ValueGeneratedNever();
        builder.HasOne<Post>().WithMany().HasForeignKey(repost => repost.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(repost => new { repost.PostId, repost.UserId }).IsUnique();
        builder.HasIndex(repost => new { repost.UserId, repost.CreatedAt, repost.Id });
    }
}

internal sealed class SavedPostConfiguration : IEntityTypeConfiguration<SavedPost>
{
    public void Configure(EntityTypeBuilder<SavedPost> builder)
    {
        builder.ToTable("saved_posts");
        builder.HasKey(saved => saved.Id);
        builder.Property(saved => saved.Id).ValueGeneratedNever();
        builder.HasOne<Post>().WithMany().HasForeignKey(saved => saved.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(saved => new { saved.UserId, saved.PostId }).IsUnique();
        builder.HasIndex(saved => new { saved.UserId, saved.CreatedAt, saved.Id });
        builder.HasIndex(saved => saved.PostId);
    }
}

internal sealed class PostRepository(SocialDbContext dbContext) : IPostRepository
{
    public Task<Post?> GetAsync(Guid postId, CancellationToken cancellationToken) =>
        dbContext.Posts
            .Include(post => post.Media)
            .Include(post => post.Pets)
            .Include(post => post.Mentions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(post => post.Id == postId, cancellationToken);

    public Task<Post?> GetVisibleAsync(Guid postId, PostAudience audience, CancellationToken cancellationToken) =>
        Visible(audience).FirstOrDefaultAsync(post => post.Id == postId, cancellationToken);

    public async Task<IReadOnlyList<Post>> GetWithDetailsAsync(IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken) =>
        await dbContext.Posts.AsNoTracking()
            .Where(post => postIds.Contains(post.Id))
            .Include(post => post.Media)
            .Include(post => post.Pets)
            .Include(post => post.Mentions)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, PostStats>> GetStatsAsync(IReadOnlyCollection<Guid> postIds, Guid viewerId, IReadOnlyCollection<Guid> hiddenIds, CancellationToken cancellationToken)
    {
        var likes = await dbContext.PostLikes.AsNoTracking()
            .Where(like => postIds.Contains(like.PostId) && !hiddenIds.Contains(like.UserId))
            .GroupBy(like => like.PostId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count, cancellationToken);
        var comments = await dbContext.Comments.AsNoTracking()
            .Where(comment => postIds.Contains(comment.PostId) && !hiddenIds.Contains(comment.AuthorId))
            .GroupBy(comment => comment.PostId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count, cancellationToken);
        var reposts = await dbContext.Reposts.AsNoTracking()
            .Where(repost => postIds.Contains(repost.PostId) && !hiddenIds.Contains(repost.UserId))
            .GroupBy(repost => repost.PostId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count, cancellationToken);
        var liked = await dbContext.PostLikes.AsNoTracking()
            .Where(like => like.UserId == viewerId && postIds.Contains(like.PostId))
            .Select(like => like.PostId)
            .ToListAsync(cancellationToken);
        var saved = await dbContext.SavedPosts.AsNoTracking()
            .Where(item => item.UserId == viewerId && postIds.Contains(item.PostId))
            .Select(item => item.PostId)
            .ToListAsync(cancellationToken);
        var reposted = await dbContext.Reposts.AsNoTracking()
            .Where(repost => repost.UserId == viewerId && postIds.Contains(repost.PostId))
            .Select(repost => repost.PostId)
            .ToListAsync(cancellationToken);

        return postIds.Distinct().ToDictionary(
            postId => postId,
            postId => new PostStats(
                likes.GetValueOrDefault(postId),
                comments.GetValueOrDefault(postId),
                reposts.GetValueOrDefault(postId),
                liked.Contains(postId),
                saved.Contains(postId),
                reposted.Contains(postId)));
    }

    public async Task<IReadOnlyList<TimelineRow>> GetFeedPageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var viewerId = audience.ViewerId;
        var following = dbContext.Follows.AsNoTracking().Where(follow => follow.FollowerId == viewerId).Select(follow => follow.FolloweeId);
        var hidden = audience.HiddenIds.ToList();
        var posts = Visible(audience)
            .Where(post => post.AuthorId == viewerId || following.Contains(post.AuthorId))
            .Select(post => new { post.Id, PostId = post.Id, ActorId = post.AuthorId, At = post.CreatedAt });
        var reposts =
            from repost in dbContext.Reposts.AsNoTracking()
            join post in Visible(audience) on repost.PostId equals post.Id
            where !hidden.Contains(repost.UserId) && (repost.UserId == viewerId || following.Contains(repost.UserId))
            select new { repost.Id, repost.PostId, ActorId = repost.UserId, At = repost.CreatedAt };
        var rows = posts.Concat(reposts);
        if (after is not null)
        {
            rows = rows.Where(row => row.At < after.At || (row.At == after.At && row.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(row => row.At).ThenByDescending(row => row.Id)
            .Take(take)
            .Select(row => new TimelineRow(row.Id, row.PostId, row.ActorId, row.At))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TimelineRow>> GetUserPageAsync(Guid userId, PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var posts = Visible(audience)
            .Where(post => post.AuthorId == userId)
            .Select(post => new { post.Id, PostId = post.Id, ActorId = post.AuthorId, At = post.CreatedAt });
        var reposts =
            from repost in dbContext.Reposts.AsNoTracking()
            join post in Visible(audience) on repost.PostId equals post.Id
            where repost.UserId == userId
            select new { repost.Id, repost.PostId, ActorId = repost.UserId, At = repost.CreatedAt };
        var rows = posts.Concat(reposts);
        if (after is not null)
        {
            rows = rows.Where(row => row.At < after.At || (row.At == after.At && row.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(row => row.At).ThenByDescending(row => row.Id)
            .Take(take)
            .Select(row => new TimelineRow(row.Id, row.PostId, row.ActorId, row.At))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PostPageRow>> GetExplorePageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var viewerId = audience.ViewerId;
        var toStranger = audience.ToStrangerOfPublic.ToList();
        var hidden = audience.HiddenIds.ToList();
        var rows = dbContext.Posts.AsNoTracking()
            .Where(post => !post.AuthorIsPrivate && toStranger.Contains(post.Visibility) && post.AuthorId != viewerId && !hidden.Contains(post.AuthorId));
        if (after is not null)
        {
            rows = rows.Where(post => post.CreatedAt < after.At || (post.CreatedAt == after.At && post.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(post => post.CreatedAt).ThenByDescending(post => post.Id)
            .Take(take)
            .Select(post => new PostPageRow(post.Id, post.CreatedAt, post.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PostPageRow>> GetSavedPageAsync(PostAudience audience, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var viewerId = audience.ViewerId;
        var rows =
            from saved in dbContext.SavedPosts.AsNoTracking()
            join post in Visible(audience) on saved.PostId equals post.Id
            where saved.UserId == viewerId
            select new { saved.PostId, At = saved.CreatedAt, saved.Id };
        if (after is not null)
        {
            rows = rows.Where(row => row.At < after.At || (row.At == after.At && row.Id.CompareTo(after.Id) < 0));
        }

        return await rows
            .OrderByDescending(row => row.At).ThenByDescending(row => row.Id)
            .Take(take)
            .Select(row => new PostPageRow(row.PostId, row.At, row.Id))
            .ToListAsync(cancellationToken);
    }

    public Task SetAuthorPrivacyAsync(Guid authorId, bool isPrivate, CancellationToken cancellationToken) =>
        dbContext.Posts
            .Where(post => post.AuthorId == authorId && post.AuthorIsPrivate != isPrivate)
            .ExecuteUpdateAsync(setters => setters.SetProperty(post => post.AuthorIsPrivate, isPrivate), cancellationToken);

    public Task RemovePetAsync(Guid petId, CancellationToken cancellationToken) =>
        dbContext.PostPets.Where(pet => pet.PetId == petId).ExecuteDeleteAsync(cancellationToken);

    public async Task RemoveContentOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await dbContext.PostMentions.Where(mention => mention.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.CommentMentions.Where(mention => mention.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.SavedPosts.Where(saved => saved.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Reposts.Where(repost => repost.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.PostLikes.Where(like => like.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Comments.Where(comment => comment.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);

        // The rest of what depends on them is removed by the foreign keys. The Media module erases the images of the account.
        await dbContext.Posts.Where(post => post.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public void Add(Post post) => dbContext.Posts.Add(post);

    public void Remove(Post post) => dbContext.Posts.Remove(post);

    /// <summary>
    /// <see cref="BuildingBlocks.Domain.Visibility.VisibilityRules"/> for a query over many authors, with
    /// what it says of each kind of viewer. The rules are applied again when each post is shown.
    /// </summary>
    private IQueryable<Post> Visible(PostAudience audience)
    {
        var viewerId = audience.ViewerId;
        var hidden = audience.HiddenIds.ToList();
        var toFriend = audience.ToFriend.ToList();
        var toFollower = audience.ToFollower.ToList();
        var toStrangerOfPublic = audience.ToStrangerOfPublic.ToList();
        var toStrangerOfPrivate = audience.ToStrangerOfPrivate.ToList();
        var follows = dbContext.Follows.AsNoTracking();
        return dbContext.Posts.AsNoTracking().Where(post =>
            !hidden.Contains(post.AuthorId)
            && (post.AuthorId == viewerId
                || (!post.AuthorIsPrivate && toStrangerOfPublic.Contains(post.Visibility))
                || (post.AuthorIsPrivate && toStrangerOfPrivate.Contains(post.Visibility))
                || (toFollower.Contains(post.Visibility) && follows.Any(follow => follow.FollowerId == viewerId && follow.FolloweeId == post.AuthorId))
                || (toFriend.Contains(post.Visibility)
                    && follows.Any(follow => follow.FollowerId == viewerId && follow.FolloweeId == post.AuthorId)
                    && follows.Any(follow => follow.FollowerId == post.AuthorId && follow.FolloweeId == viewerId))));
    }
}

internal sealed class PostActionRepository(SocialDbContext dbContext) : IPostActionRepository
{
    public Task<PostLike?> GetLikeAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.PostLikes.FirstOrDefaultAsync(like => like.PostId == postId && like.UserId == userId, cancellationToken);

    public Task<Repost?> GetRepostAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.Reposts.FirstOrDefaultAsync(repost => repost.PostId == postId && repost.UserId == userId, cancellationToken);

    public Task<SavedPost?> GetSavedAsync(Guid postId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.SavedPosts.FirstOrDefaultAsync(saved => saved.PostId == postId && saved.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<Repost>> GetRepostsBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var rows =
            from repost in dbContext.Reposts
            join post in dbContext.Posts on repost.PostId equals post.Id
            where (repost.UserId == first && post.AuthorId == second) || (repost.UserId == second && post.AuthorId == first)
            select repost;
        return await rows.ToListAsync(cancellationToken);
    }

    public void Add(PostLike like) => dbContext.PostLikes.Add(like);

    public void Remove(PostLike like) => dbContext.PostLikes.Remove(like);

    public void Add(Repost repost) => dbContext.Reposts.Add(repost);

    public void Remove(Repost repost) => dbContext.Reposts.Remove(repost);

    public void Add(SavedPost saved) => dbContext.SavedPosts.Add(saved);

    public void Remove(SavedPost saved) => dbContext.SavedPosts.Remove(saved);
}

internal sealed class CommentRepository(SocialDbContext dbContext) : ICommentRepository
{
    public Task<Comment?> GetAsync(Guid commentId, CancellationToken cancellationToken) =>
        dbContext.Comments.Include(comment => comment.Mentions).FirstOrDefaultAsync(comment => comment.Id == commentId, cancellationToken);

    public async Task<IReadOnlyList<Comment>> GetPageAsync(Guid postId, IReadOnlyCollection<Guid> hiddenIds, SocialPosition? after, int take, CancellationToken cancellationToken)
    {
        var rows = dbContext.Comments.AsNoTracking().Where(comment => comment.PostId == postId && !hiddenIds.Contains(comment.AuthorId));
        if (after is not null)
        {
            rows = rows.Where(comment => comment.CreatedAt > after.At || (comment.CreatedAt == after.At && comment.Id.CompareTo(after.Id) > 0));
        }

        return await rows
            .OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id)
            .Take(take)
            .Include(comment => comment.Mentions)
            .ToListAsync(cancellationToken);
    }

    public void Add(Comment comment) => dbContext.Comments.Add(comment);

    public void Remove(Comment comment) => dbContext.Comments.Remove(comment);
}
