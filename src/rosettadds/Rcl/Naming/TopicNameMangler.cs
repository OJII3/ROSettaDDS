namespace ROSettaDDS.Rcl.Naming;

/// <summary>
/// ROS 2 のトピック名 ⇔ DDS トピック名 (RTPS 上の名前) を変換する。
/// 既定 prefix:
/// - トピック (msg)         : "rt/"
/// - サービス request       : "rq/"
/// - サービス reply         : "rr/"
/// - パラメータ             : "rp/"
/// 例: "/chatter" → "rt/chatter"
/// </summary>
public static class TopicNameMangler
{
    public const string TopicPrefix = "rt/";
    public const string ServiceRequestPrefix = "rq/";
    public const string ServiceReplyPrefix = "rr/";
    public const string ParameterPrefix = "rp/";

    /// <summary>ROS 2 ユーザートピック名から RTPS 上の名前へ。先頭の "/" は除去する。</summary>
    public static string MangleTopic(string userName)
    {
        if (string.IsNullOrEmpty(userName)) throw new ArgumentException("Value cannot be null or empty.", nameof(userName));
        return TopicPrefix + userName.TrimStart('/');
    }

    /// <summary>RTPS 上の名前からユーザートピック名へ。prefix が無ければそのまま返す。</summary>
    public static string DemangleTopic(string ddsName)
    {
        if (ddsName is null) throw new ArgumentNullException(nameof(ddsName));
        return ddsName.StartsWith(TopicPrefix, StringComparison.Ordinal)
            ? ddsName[TopicPrefix.Length..]
            : ddsName;
    }
}
