using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

// DomainParticipant は Obsolete なのでコンパイル警告を抑止する。
#pragma warning disable CS0618

public class DomainParticipantObsoleteTests
{
    [Fact]
    public void コンストラクタで_Context_と_Node_が生成される()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotEqual(default, dp.Guid);
        Assert.NotEqual(default, dp.GuidPrefix);
    }

    [Fact]
    public void CreatePublisher_が_obsolete_でも動く()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Start();

        using var pub = dp.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
        Assert.NotNull(pub);
    }

    [Fact]
    public void CreateSubscription_が_obsolete_でも動く()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Start();

        using var sub = dp.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (msg, _) => { },
            StringMessage.DdsTypeName);
        Assert.NotNull(sub);
    }

    [Fact]
    public void Dispose_が_Context_と_Node_を_両方解放する()
    {
        var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Dispose();

        // 2 度 Dispose しても例外なし
        dp.Dispose();
    }
}

#pragma warning restore CS0618
