using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// local user endpoint の登録状態、マッチング、packet dispatch を管理する。
/// </summary>
internal sealed class UserEndpointManager
{
    private readonly object _lock = new();
    private readonly DiscoveryDb _discoveryDb;
    private readonly ILogger _logger;
    private readonly List<DiscoveredEndpointData> _writers = new();
    private readonly List<DiscoveredEndpointData> _readers = new();
    private readonly Dictionary<string, List<LocalWriter>> _writersByTopic = new();
    private readonly Dictionary<string, List<LocalReader>> _readersByTopic = new();
    private StatefulWriter[] _writerSnapshot = Array.Empty<StatefulWriter>();
    private StatelessReader[] _readerSnapshot = Array.Empty<StatelessReader>();

    public UserEndpointManager(DiscoveryDb discoveryDb, ILogger logger)
    {
        _discoveryDb = discoveryDb ?? throw new ArgumentNullException(nameof(discoveryDb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterWriter(DiscoveredEndpointData endpointData, StatefulWriter writer)
    {
        ValidateEndpoint(endpointData, EndpointKind.Writer);
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var localWriter = new LocalWriter(endpointData, writer);
        LocalReader[] localReaders;
        lock (_lock)
        {
            _writers.Add(endpointData);
            AddByTopic(_writersByTopic, endpointData.TopicName, localWriter);
            RefreshSnapshotsLocked();
            localReaders = SnapshotForTopic(_readersByTopic, endpointData.TopicName);
        }

        foreach (var localReader in localReaders)
        {
            Match(localReader, localWriter);
        }
        foreach (var remoteReader in _discoveryDb.ReaderSnapshot())
        {
            if (remoteReader.TopicName == endpointData.TopicName)
            {
                Match(localWriter, remoteReader);
            }
        }
    }

    public void RegisterReader(DiscoveredEndpointData endpointData, StatelessReader reader)
    {
        ValidateEndpoint(endpointData, EndpointKind.Reader);
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var localReader = new LocalReader(endpointData, reader);
        LocalWriter[] localWriters;
        lock (_lock)
        {
            _readers.Add(endpointData);
            AddByTopic(_readersByTopic, endpointData.TopicName, localReader);
            RefreshSnapshotsLocked();
            localWriters = SnapshotForTopic(_writersByTopic, endpointData.TopicName);
        }

        foreach (var localWriter in localWriters)
        {
            Match(localReader, localWriter);
        }
        foreach (var remoteWriter in _discoveryDb.WriterSnapshot())
        {
            if (remoteWriter.TopicName == endpointData.TopicName)
            {
                Match(localReader, remoteWriter);
            }
        }
    }

    public UnregisterResult UnregisterWriter(Guid endpointGuid, StatefulWriter writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        DiscoveredEndpointData? endpoint;
        bool shouldAdvertise;
        LocalReader[] localReaders;
        lock (_lock)
        {
            endpoint = RemoveEndpoint(_writers, endpointGuid);
            if (endpoint is null)
            {
                return UnregisterResult.NotFound;
            }
            shouldAdvertise = RemoveByReference(_writersByTopic, endpoint.TopicName, writer, static item => item.Writer)
                && !ContainsGuid(_writersByTopic, endpoint.TopicName, endpointGuid, static item => item.EndpointData);
            RefreshSnapshotsLocked();
            localReaders = SnapshotForTopic(_readersByTopic, endpoint.TopicName);
        }

        foreach (var localReader in localReaders)
        {
            localReader.Reader.UnmatchWriter(endpointGuid);
        }
        return new UnregisterResult(endpoint, shouldAdvertise);
    }

    public UnregisterResult UnregisterReader(Guid endpointGuid, StatelessReader reader)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        DiscoveredEndpointData? endpoint;
        bool shouldAdvertise;
        LocalWriter[] localWriters;
        lock (_lock)
        {
            endpoint = RemoveEndpoint(_readers, endpointGuid);
            if (endpoint is null)
            {
                return UnregisterResult.NotFound;
            }
            shouldAdvertise = RemoveByReference(_readersByTopic, endpoint.TopicName, reader, static item => item.Reader)
                && !ContainsGuid(_readersByTopic, endpoint.TopicName, endpointGuid, static item => item.EndpointData);
            RefreshSnapshotsLocked();
            localWriters = SnapshotForTopic(_writersByTopic, endpoint.TopicName);
        }

        foreach (var localWriter in localWriters)
        {
            localWriter.Writer.UnmatchReader(endpointGuid);
        }
        return new UnregisterResult(endpoint, shouldAdvertise);
    }

    public EndpointSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new EndpointSnapshot(
                _writersByTopic.Values.SelectMany(static items => items).Select(static item => item.Writer).ToArray(),
                _readersByTopic.Values.SelectMany(static items => items).Select(static item => item.Reader).ToArray());
        }
    }

    public void StartWriters()
    {
        foreach (var writer in Volatile.Read(ref _writerSnapshot))
        {
            writer.Start();
        }
    }

    public void StopWriters()
    {
        foreach (var writer in Volatile.Read(ref _writerSnapshot))
        {
            writer.Stop();
        }
    }

    public void DispatchPacket(ReadOnlyMemory<byte> packet, Locator source)
    {
        foreach (var writer in Volatile.Read(ref _writerSnapshot))
        {
            writer.OnPacketReceived(packet, source);
        }
        foreach (var reader in Volatile.Read(ref _readerSnapshot))
        {
            reader.OnPacketReceived(packet, source);
        }
    }

    public void RemoteReaderChanged(RemoteEndpoint remoteReader)
    {
        foreach (var writer in GetWriters(remoteReader.TopicName))
        {
            Match(writer, remoteReader);
        }
    }

    public void RemoteWriterChanged(RemoteEndpoint remoteWriter)
    {
        foreach (var reader in GetReaders(remoteWriter.TopicName))
        {
            Match(reader, remoteWriter);
        }
    }

    public void RemoteReaderLost(RemoteEndpoint remoteReader)
    {
        foreach (var writer in GetWriters(remoteReader.TopicName))
        {
            writer.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
        }
    }

    public void RemoteWriterLost(RemoteEndpoint remoteWriter)
    {
        foreach (var reader in GetReaders(remoteWriter.TopicName))
        {
            reader.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
        }
    }

    private void Match(LocalWriter local, RemoteEndpoint remoteReader)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remoteReader.TypeName))
        {
            local.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
            return;
        }

        local.Writer.MatchReader(remoteReader.Data.EndpointGuid, ResolveRemoteReaderUnicastLocator(remoteReader));
        _logger.Debug($"DomainParticipant: matched local writer with remote reader on topic={remoteReader.TopicName} reader={remoteReader.Data.EndpointGuid}");
    }

    private void Match(LocalReader local, RemoteEndpoint remoteWriter)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remoteWriter.TypeName))
        {
            local.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
            return;
        }

        local.Reader.MatchWriter(remoteWriter.Data.EndpointGuid);
        _logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remoteWriter.TopicName} writer={remoteWriter.Data.EndpointGuid}");
    }

    private void Match(LocalReader reader, LocalWriter writer)
    {
        if (!TypeMatches(reader.EndpointData.TypeName, writer.EndpointData.TypeName))
        {
            reader.Reader.UnmatchWriter(writer.EndpointData.EndpointGuid);
            writer.Writer.UnmatchReader(reader.EndpointData.EndpointGuid);
            return;
        }

        reader.Reader.MatchWriter(writer.EndpointData.EndpointGuid);
        writer.Writer.MatchReader(reader.EndpointData.EndpointGuid, FirstUdpLocator(reader.EndpointData.UnicastLocators));
        _logger.Debug($"DomainParticipant: matched local reader with local writer on topic={reader.EndpointData.TopicName} writer={writer.EndpointData.EndpointGuid}");
    }

    private Locator? ResolveRemoteReaderUnicastLocator(RemoteEndpoint remoteReader)
    {
        var locator = FirstUdpLocator(remoteReader.Data.UnicastLocators);
        if (locator is not null)
        {
            return locator;
        }

        foreach (var participant in _discoveryDb.Snapshot())
        {
            if (participant.GuidPrefix.Equals(remoteReader.Data.EndpointGuid.Prefix))
            {
                return FirstUdpLocator(participant.Data.DefaultUnicastLocators);
            }
        }
        return null;
    }

    private LocalWriter[] GetWriters(string topicName)
    {
        lock (_lock)
        {
            return SnapshotForTopic(_writersByTopic, topicName);
        }
    }

    private LocalReader[] GetReaders(string topicName)
    {
        lock (_lock)
        {
            return SnapshotForTopic(_readersByTopic, topicName);
        }
    }

    private void RefreshSnapshotsLocked()
    {
        _writerSnapshot = _writersByTopic.Values
            .SelectMany(static items => items)
            .Select(static item => item.Writer)
            .ToArray();
        _readerSnapshot = _readersByTopic.Values
            .SelectMany(static items => items)
            .Select(static item => item.Reader)
            .ToArray();
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

    private static void AddByTopic<T>(Dictionary<string, List<T>> itemsByTopic, string topicName, T item)
    {
        if (!itemsByTopic.TryGetValue(topicName, out var items))
        {
            items = new List<T>();
            itemsByTopic[topicName] = items;
        }
        items.Add(item);
    }

    private static T[] SnapshotForTopic<T>(Dictionary<string, List<T>> itemsByTopic, string topicName)
        => itemsByTopic.TryGetValue(topicName, out var items) ? items.ToArray() : Array.Empty<T>();

    private static DiscoveredEndpointData? RemoveEndpoint(List<DiscoveredEndpointData> endpoints, Guid endpointGuid)
    {
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].EndpointGuid.Equals(endpointGuid))
            {
                var endpoint = endpoints[i];
                endpoints.RemoveAt(i);
                return endpoint;
            }
        }
        return null;
    }

    private static bool RemoveByReference<TItem, TValue>(
        Dictionary<string, List<TItem>> itemsByTopic,
        string topicName,
        TValue value,
        Func<TItem, TValue> selector)
        where TValue : class
    {
        if (!itemsByTopic.TryGetValue(topicName, out var items))
        {
            return false;
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(selector(items[i]), value))
            {
                items.RemoveAt(i);
                if (items.Count == 0)
                {
                    itemsByTopic.Remove(topicName);
                }
                return true;
            }
        }
        return false;
    }

    private static bool ContainsGuid<T>(
        Dictionary<string, List<T>> itemsByTopic,
        string topicName,
        Guid endpointGuid,
        Func<T, DiscoveredEndpointData> selector)
        => itemsByTopic.TryGetValue(topicName, out var items)
        && items.Any(item => selector(item).EndpointGuid.Equals(endpointGuid));

    private static bool TypeMatches(string localTypeName, string remoteTypeName)
        => !string.IsNullOrEmpty(localTypeName)
        && !string.IsNullOrEmpty(remoteTypeName)
        && string.Equals(localTypeName, remoteTypeName, StringComparison.Ordinal);

    private static Locator? FirstUdpLocator(IEnumerable<Locator> locators)
    {
        foreach (var locator in locators)
        {
            if (locator.Kind is LocatorKind.UdpV4 or LocatorKind.UdpV6)
            {
                return locator;
            }
        }
        return null;
    }

    private sealed record LocalReader(DiscoveredEndpointData EndpointData, StatelessReader Reader);
    private sealed record LocalWriter(DiscoveredEndpointData EndpointData, StatefulWriter Writer);

    public readonly record struct EndpointSnapshot(StatefulWriter[] Writers, StatelessReader[] Readers);

    public readonly record struct UnregisterResult(DiscoveredEndpointData? Endpoint, bool ShouldAdvertise)
    {
        public static UnregisterResult NotFound => new(null, false);
    }
}
