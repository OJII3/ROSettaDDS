using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

internal sealed class UserEndpointManager
{
    private readonly DiscoveryDb _discoveryDb;
    private readonly IEndpointReceiver _receiver;
    private readonly ILogger _logger;
    private readonly EndpointRegistry _registry = new();

    public UserEndpointManager(DiscoveryDb discoveryDb, IEndpointReceiver receiver, ILogger logger)
    {
        _discoveryDb = discoveryDb ?? throw new ArgumentNullException(nameof(discoveryDb));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterWriter(DiscoveredEndpointData endpointData, StatefulWriter writer)
    {
        ValidateEndpoint(endpointData, EndpointKind.Writer);
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        _registry.AddLocalWriter(endpointData, writer);
        _receiver.RegisterWriter(writer.WriterEntityId, writer);

        var localWriter = new LocalWriter(endpointData, writer);
        foreach (var localReader in _registry.GetLocalReadersForTopic(endpointData.TopicName))
        {
            var d = EndpointMatcher.EvaluateLocalLocal(localReader, localWriter);
            if (d.IsCompatible)
            {
                localReader.Reader.MatchWriter(localWriter.EndpointData.EndpointGuid, d.UnicastLocator);
                localWriter.Writer.MatchReader(localReader.EndpointData.EndpointGuid, d.SecondaryLocator, d.ReliabilityKind ?? ReliabilityKind.Reliable);
                _logger.Debug($"DomainParticipant: matched local reader with local writer on topic={localReader.EndpointData.TopicName} writer={localWriter.EndpointData.EndpointGuid}");
            }
            else
            {
                localReader.Reader.UnmatchWriter(localWriter.EndpointData.EndpointGuid);
                localWriter.Writer.UnmatchReader(localReader.EndpointData.EndpointGuid);
            }
        }

        foreach (var remoteReader in _discoveryDb.ReaderSnapshot())
        {
            if (remoteReader.TopicName == endpointData.TopicName)
            {
                var d = EndpointMatcher.EvaluateLocalRemote(localWriter, remoteReader);
                if (d.IsCompatible)
                {
                    var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remoteReader, _discoveryDb.Snapshot());
                    localWriter.Writer.MatchReader(remoteReader.Data.EndpointGuid, loc, d.ReliabilityKind ?? ReliabilityKind.Reliable);
                    _logger.Debug($"DomainParticipant: matched local writer with remote reader on topic={remoteReader.TopicName} reader={remoteReader.Data.EndpointGuid}");
                }
                else
                {
                    localWriter.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
                }
            }
        }
    }

    public void RegisterReader(DiscoveredEndpointData endpointData, IUserReader reader)
    {
        ValidateEndpoint(endpointData, EndpointKind.Reader);
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        _registry.AddLocalReader(endpointData, reader);
        _receiver.RegisterReader(reader.ReaderEntityId, reader.Handler);

        var localReader = new LocalReader(endpointData, reader);
        foreach (var localWriter in _registry.GetLocalWritersForTopic(endpointData.TopicName))
        {
            var d = EndpointMatcher.EvaluateLocalLocal(localReader, localWriter);
            if (d.IsCompatible)
            {
                localReader.Reader.MatchWriter(localWriter.EndpointData.EndpointGuid, d.UnicastLocator);
                localWriter.Writer.MatchReader(localReader.EndpointData.EndpointGuid, d.SecondaryLocator, d.ReliabilityKind ?? ReliabilityKind.Reliable);
                _logger.Debug($"DomainParticipant: matched local reader with local writer on topic={localReader.EndpointData.TopicName} writer={localWriter.EndpointData.EndpointGuid}");
            }
            else
            {
                localReader.Reader.UnmatchWriter(localWriter.EndpointData.EndpointGuid);
                localWriter.Writer.UnmatchReader(localReader.EndpointData.EndpointGuid);
            }
        }

        foreach (var remoteWriter in _discoveryDb.WriterSnapshot())
        {
            if (remoteWriter.TopicName == endpointData.TopicName)
            {
                var d = EndpointMatcher.EvaluateLocalRemote(localReader, remoteWriter);
                if (d.IsCompatible)
                {
                    var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remoteWriter, _discoveryDb.Snapshot());
                    localReader.Reader.MatchWriter(remoteWriter.Data.EndpointGuid, loc);
                    _logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remoteWriter.TopicName} writer={remoteWriter.Data.EndpointGuid}");
                }
                else
                {
                    localReader.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
                }
            }
        }
    }

    public UnregisterResult UnregisterWriter(Guid endpointGuid, StatefulWriter writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var removed = _registry.RemoveLocalWriter(endpointGuid, writer);
        if (removed.Endpoint is null)
        {
            return UnregisterResult.NotFound;
        }

        _receiver.UnregisterWriter(writer.WriterEntityId);

        foreach (var localReader in removed.LocalReaders)
        {
            localReader.Reader.UnmatchWriter(endpointGuid);
        }

        var shouldAdvertise = _registry.ShouldAdvertiseForTopic(removed.Endpoint.TopicName, endpointGuid);
        return new UnregisterResult(removed.Endpoint, shouldAdvertise);
    }

    public UnregisterResult UnregisterReader(Guid endpointGuid, IUserReader reader)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var removed = _registry.RemoveLocalReader(endpointGuid, reader);
        if (removed.Endpoint is null)
        {
            return UnregisterResult.NotFound;
        }

        _receiver.UnregisterReader(reader.ReaderEntityId);

        foreach (var localWriter in removed.LocalWriters)
        {
            localWriter.Writer.UnmatchReader(endpointGuid);
        }

        var shouldAdvertise = _registry.ShouldAdvertiseForTopic(removed.Endpoint.TopicName, endpointGuid);
        return new UnregisterResult(removed.Endpoint, shouldAdvertise);
    }

    public EndpointSnapshot Snapshot() => _registry.Snapshot();

    public void StartWriters() => _registry.StartWriters();

    public void StopWriters() => _registry.StopWriters();

    public void RemoteReaderChanged(RemoteEndpoint remoteReader)
    {
        foreach (var writer in _registry.GetLocalWritersForTopic(remoteReader.TopicName))
        {
            var d = EndpointMatcher.EvaluateLocalRemote(writer, remoteReader);
            if (d.IsCompatible)
            {
                var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remoteReader, _discoveryDb.Snapshot());
                writer.Writer.MatchReader(remoteReader.Data.EndpointGuid, loc, d.ReliabilityKind ?? ReliabilityKind.Reliable);
                _logger.Debug($"DomainParticipant: matched local writer with remote reader on topic={remoteReader.TopicName} reader={remoteReader.Data.EndpointGuid}");
            }
            else
            {
                writer.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
            }
        }
    }

    public void RemoteWriterChanged(RemoteEndpoint remoteWriter)
    {
        foreach (var reader in _registry.GetLocalReadersForTopic(remoteWriter.TopicName))
        {
            var d = EndpointMatcher.EvaluateLocalRemote(reader, remoteWriter);
            if (d.IsCompatible)
            {
                var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remoteWriter, _discoveryDb.Snapshot());
                reader.Reader.MatchWriter(remoteWriter.Data.EndpointGuid, loc);
                _logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remoteWriter.TopicName} writer={remoteWriter.Data.EndpointGuid}");
            }
            else
            {
                reader.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
            }
        }
    }

    public void RemoteReaderLost(RemoteEndpoint remoteReader)
    {
        foreach (var writer in _registry.GetLocalWritersForTopic(remoteReader.TopicName))
        {
            writer.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
        }
    }

    public void RemoteWriterLost(RemoteEndpoint remoteWriter)
    {
        foreach (var reader in _registry.GetLocalReadersForTopic(remoteWriter.TopicName))
        {
            reader.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
        }
    }

    public readonly record struct UnregisterResult(DiscoveredEndpointData? Endpoint, bool ShouldAdvertise)
    {
        public static UnregisterResult NotFound => new(null, false);
    }

    private static void ValidateEndpoint(DiscoveredEndpointData endpoint, EndpointKind expectedKind)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (endpoint.Kind != expectedKind)
        {
            throw new ArgumentException($"Expected {expectedKind} endpoint, got {endpoint.Kind}.", nameof(endpoint));
        }
        if (string.IsNullOrEmpty(endpoint.TopicName))
        {
            throw new ArgumentException("Endpoint topic name cannot be null or empty.", nameof(endpoint));
        }
    }
}
