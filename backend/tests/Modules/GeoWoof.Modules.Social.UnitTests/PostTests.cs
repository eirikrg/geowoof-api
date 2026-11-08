using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Social.Domain;

namespace GeoWoof.Modules.Social.UnitTests;

public sealed class PostTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static Post APost(string? text = "Hola", IReadOnlyCollection<Mention>? mentions = null, IReadOnlyList<Guid>? assets = null) =>
        Post.Create(Guid.CreateVersion7(), Ana, text, ContentVisibility.Everyone, authorIsPrivate: false, assets ?? [Guid.CreateVersion7()], [], mentions ?? [], Now);

    [Theory]
    [InlineData("Hola @ana", new[] { "ana" })]
    [InlineData("@ana al principio", new[] { "ana" })]
    [InlineData("Con @Ana.Paws y @BEA_1", new[] { "ana.paws", "bea_1" })]
    [InlineData("Con @ana. Y luego @bea, y (@carlos)", new[] { "ana", "bea", "carlos" })]
    [InlineData("@ana y otra vez @ANA", new[] { "ana" })]
    [InlineData("Mi correo es ana@geowoof.dev", new string[0])]
    [InlineData("@@ana", new string[0])]
    [InlineData("@ab es corto y @1ana empieza mal", new string[0])]
    [InlineData("Sin menciones", new string[0])]
    [InlineData("", new string[0])]
    public void The_mentions_of_a_text_are_found(string text, string[] expected)
    {
        MentionParser.Parse(text).ShouldBe(expected);
    }

    [Fact]
    public void A_username_that_is_too_long_is_not_a_mention()
    {
        MentionParser.Parse("@" + new string('a', 31)).ShouldBeEmpty();
        MentionParser.Parse("@" + new string('a', 30)).ShouldHaveSingleItem();
    }

    [Fact]
    public void There_is_a_limit_of_mentions_in_a_text()
    {
        var text = string.Join(' ', Enumerable.Range(0, 15).Select(i => $"@user_{i}"));

        MentionParser.Parse(text).Count.ShouldBe(MentionParser.MaxMentions);
    }

    [Fact]
    public void Nothing_is_mentioned_without_a_text()
    {
        MentionParser.Parse(null).ShouldBeEmpty();
    }

    [Fact]
    public void A_post_keeps_the_order_of_its_images_and_says_that_it_was_created()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var post = APost(assets: [second, first]);

        post.Media.Select(item => item.AssetId).ShouldBe([second, first]);
        post.Media.Select(item => item.Position).ShouldBe([0, 1]);
        post.Media.ShouldAllBe(item => item.Type == PostMediaType.Image);
        post.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PostCreatedDomainEvent>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Hola  ", "Hola")]
    public void An_empty_text_counts_as_no_text(string? text, string? expected)
    {
        APost(text).Text.ShouldBe(expected);
    }

    [Fact]
    public void Each_mention_is_announced_except_your_own()
    {
        var post = APost(mentions: [new Mention(Bea, "bea"), new Mention(Ana, "ana")]);

        post.Mentions.Select(mention => mention.Username).ShouldBe(["bea", "ana"]);
        var announced = post.DomainEvents.OfType<UserMentionedDomainEvent>().ShouldHaveSingleItem();
        announced.MentionedUserId.ShouldBe(Bea);
        announced.CommentId.ShouldBeNull();
    }

    [Fact]
    public void A_comment_announces_itself_and_its_mentions()
    {
        var post = APost();

        var comment = Comment.Add(Guid.CreateVersion7(), post, Bea, "  Hola @ana  ", [new Mention(Ana, "ana")], Now);

        comment.Text.ShouldBe("Hola @ana");
        comment.DomainEvents.OfType<CommentAddedDomainEvent>().ShouldHaveSingleItem().PostAuthorId.ShouldBe(Ana);
        comment.DomainEvents.OfType<UserMentionedDomainEvent>().ShouldHaveSingleItem().CommentId.ShouldBe(comment.Id);
    }

    [Fact]
    public void A_comment_can_be_deleted_by_its_author_and_by_the_author_of_the_post()
    {
        var post = APost();
        var comment = Comment.Add(Guid.CreateVersion7(), post, Bea, "Hola", [], Now);

        comment.CanBeDeletedBy(Bea, post).ShouldBeTrue();
        comment.CanBeDeletedBy(Ana, post).ShouldBeTrue();
        comment.CanBeDeletedBy(Guid.CreateVersion7(), post).ShouldBeFalse();
    }

    [Fact]
    public void A_reply_goes_to_the_thread_of_its_comment_also_when_it_answers_a_reply()
    {
        var post = APost();
        var comment = Comment.Add(Guid.CreateVersion7(), post, Bea, "Hola", [], Now);

        var reply = Comment.Reply(Guid.CreateVersion7(), post, comment, Ana, "@bea hola", [new Mention(Bea, "bea")], Now);
        var replyToReply = Comment.Reply(Guid.CreateVersion7(), post, reply, Bea, "@ana qué tal", [], Now);

        comment.ParentId.ShouldBeNull();
        reply.ParentId.ShouldBe(comment.Id);
        replyToReply.ParentId.ShouldBe(comment.Id);
        reply.DomainEvents.OfType<CommentAddedDomainEvent>().ShouldHaveSingleItem().PostAuthorId.ShouldBe(Ana);
        reply.DomainEvents.OfType<UserMentionedDomainEvent>().ShouldHaveSingleItem().MentionedUserId.ShouldBe(Bea);
    }

    [Fact]
    public void A_reply_cannot_answer_a_comment_of_another_post()
    {
        var comment = Comment.Add(Guid.CreateVersion7(), APost(), Bea, "Hola", [], Now);

        Should.Throw<ArgumentException>(() => Comment.Reply(Guid.CreateVersion7(), APost(), comment, Ana, "Hola", [], Now));
    }

    [Fact]
    public void A_deleted_comment_keeps_nothing_of_what_it_said_and_nobody_can_delete_it_again()
    {
        var post = APost();
        var comment = Comment.Add(Guid.CreateVersion7(), post, Bea, "Hola @ana", [new Mention(Ana, "ana")], Now);

        comment.MarkDeleted(Now.AddMinutes(1));

        comment.IsDeleted.ShouldBeTrue();
        comment.DeletedAt.ShouldBe(Now.AddMinutes(1));
        comment.Text.ShouldBeEmpty();
        comment.Mentions.ShouldBeEmpty();
        comment.CanBeDeletedBy(Bea, post).ShouldBeFalse();
        comment.CanBeDeletedBy(Ana, post).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_comment_of_the_post_stays_as_deleted()
    {
        var post = APost();
        var comment = Comment.Add(Guid.CreateVersion7(), post, Bea, "Hola", [], Now);
        var reply = Comment.Reply(Guid.CreateVersion7(), post, comment, Ana, "Hola", [], Now);

        Should.Throw<InvalidOperationException>(() => reply.MarkDeleted(Now));
    }

    [Fact]
    public void A_like_and_a_repost_say_whose_post_it_is()
    {
        var post = APost();

        PostLike.Create(Guid.CreateVersion7(), post, Bea, Now).DomainEvents
            .OfType<PostLikedDomainEvent>().ShouldHaveSingleItem().PostAuthorId.ShouldBe(Ana);
        Repost.Create(Guid.CreateVersion7(), post, Bea, Now).DomainEvents
            .OfType<PostRepostedDomainEvent>().ShouldHaveSingleItem().PostAuthorId.ShouldBe(Ana);
    }
}
