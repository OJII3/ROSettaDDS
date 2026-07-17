using ROSettaDDS.Common.Logging;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class NodeTests
{
    [Fact]
    public void コンストラクタは_Context_参照と_Name_を保持する()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        using var node = new Node(ctx, "chatter_talker");

        Assert.Same(ctx, node.Context);
        Assert.Equal("chatter_talker", node.Name);
    }

    [Fact]
    public void null_context_を渡すと_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Node(null!, "name"));
    }

    [Fact]
    public void null_name_を渡すと_ArgumentException()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        Assert.Throws<ArgumentException>(() => new Node(ctx, null!));
    }

    [Fact]
    public void CreatePublisher_が_Publisher_を返し_Dispose_後に例外を投げる()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "talker");
        try
        {
            using var pub = node.CreatePublisher<StringMessage>(
                "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
            Assert.NotNull(pub);
        }
        finally { node.Dispose(); }

        Assert.Throws<ObjectDisposedException>(() =>
            node.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
    }

    [Fact]
    public void CreateSubscription_が_Subscription_を返す()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "listener");
        using var sub = node.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg) => { });
        Assert.NotNull(sub);
    }

    [Fact]
    public async Task 異なる_Context_上の_2_Node_で_Pub_Sub_できる()
    {
        var opts = new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
        };
        using var talkerCtx = new Context(opts);
        using var listenerCtx = new Context(opts);
        talkerCtx.Start();
        listenerCtx.Start();

        using var talker = new Node(talkerCtx, "talker");
        using var listener = new Node(listenerCtx, "listener");

        var topicName = $"chatter_{System.Guid.NewGuid():N}";
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var sub = listener.CreateSubscription<StringMessage>(
            topicName, StringMessageSerializer.Instance,
            (msg, _) => received.Enqueue(msg.Data),
            typeName: StringMessage.DdsTypeName);
        using var pub = talker.CreatePublisher<StringMessage>(
            topicName, StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // 両側でマッチしてから配信
        await sub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));

        await pub.PublishAsync(new StringMessage("hello"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.False(received.IsEmpty);
        Assert.Equal("hello", received.First());
    }

    [Fact]
    public void Dispose後のCreatePublisherはrollbackしてObjectDisposedExceptionを投げ元例外を温存する()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        var node = new Node(ctx, "talker");
        node.Dispose();

        var ex = Assert.Throws<ObjectDisposedException>(() =>
            node.CreatePublisher<StringMessage>(
                "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));

        Assert.Contains(typeof(Node).Name, ex.ObjectName, StringComparison.Ordinal);
    }

}
