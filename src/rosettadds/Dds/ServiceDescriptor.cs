using ROSettaDDS.Cdr;

namespace ROSettaDDS.Dds;

/// <summary>
/// 1 つの ROS 2 サービス型の DDS 型名と Request/Response シリアライザを束ねる記述子。
/// <c>.srv</c> から生成される <c>&lt;Service&gt;Service.Descriptor</c> として提供される。
/// </summary>
public sealed class ServiceDescriptor<TRequest, TResponse>
{
    public string RequestDdsTypeName { get; }
    public string ResponseDdsTypeName { get; }
    public ICdrSerializer<TRequest> RequestSerializer { get; }
    public ICdrSerializer<TResponse> ResponseSerializer { get; }

    public ServiceDescriptor(
        string requestDdsTypeName,
        string responseDdsTypeName,
        ICdrSerializer<TRequest> requestSerializer,
        ICdrSerializer<TResponse> responseSerializer)
    {
        RequestDdsTypeName = requestDdsTypeName;
        ResponseDdsTypeName = responseDdsTypeName;
        RequestSerializer = requestSerializer;
        ResponseSerializer = responseSerializer;
    }
}
