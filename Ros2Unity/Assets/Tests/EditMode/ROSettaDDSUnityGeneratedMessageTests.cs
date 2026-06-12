using System;
using System.Collections;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.BuiltinInterfaces;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class ROSettaDDSUnityGeneratedMessageTests
    {
        private static readonly MultiArrayLayout SampleLayout = new MultiArrayLayout(
            new[] { new MultiArrayDimension("axis", 3u, 3u) },
            1u);

        private static readonly IGeneratedMessageRoundTripCase[] GeneratedMessageCases =
        {
            Case("builtin_interfaces/Duration", DurationSerializer.Instance, new Duration(-2, 345u)),
            Case("builtin_interfaces/Time", TimeSerializer.Instance, new Time(123, 456u)),
            Case("std_msgs/Bool", BoolMessageSerializer.Instance, new BoolMessage(true)),
            Case("std_msgs/Byte", ByteMessageSerializer.Instance, new ByteMessage(0xa5)),
            Case("std_msgs/ByteMultiArray", ByteMultiArraySerializer.Instance, new ByteMultiArray(SampleLayout, new byte[] { 0, 127, 255 })),
            Case("std_msgs/Char", CharMessageSerializer.Instance, new CharMessage(-42)),
            Case("std_msgs/ColorRGBA", ColorRgbaSerializer.Instance, new ColorRgba(0.1f, 0.2f, 0.3f, 0.4f)),
            Case("std_msgs/Empty", EmptyMessageSerializer.Instance, new EmptyMessage()),
            Case("std_msgs/Float32", Float32MessageSerializer.Instance, new Float32Message(12.5f)),
            Case("std_msgs/Float32MultiArray", Float32MultiArraySerializer.Instance, new Float32MultiArray(SampleLayout, new[] { -1.5f, 0f, 2.5f })),
            Case("std_msgs/Float64", Float64MessageSerializer.Instance, new Float64Message(-123.75d)),
            Case("std_msgs/Float64MultiArray", Float64MultiArraySerializer.Instance, new Float64MultiArray(SampleLayout, new[] { -1.5d, 0d, 2.5d })),
            Case("std_msgs/Header", HeaderSerializer.Instance, new Header(new Time(321, 654u), "map")),
            Case("std_msgs/Int8", Int8MessageSerializer.Instance, new Int8Message(-8)),
            Case("std_msgs/Int8MultiArray", Int8MultiArraySerializer.Instance, new Int8MultiArray(SampleLayout, new sbyte[] { -8, 0, 8 })),
            Case("std_msgs/Int16", Int16MessageSerializer.Instance, new Int16Message(-1600)),
            Case("std_msgs/Int16MultiArray", Int16MultiArraySerializer.Instance, new Int16MultiArray(SampleLayout, new short[] { -16, 0, 16 })),
            Case("std_msgs/Int32", Int32MessageSerializer.Instance, new Int32Message(-320000)),
            Case("std_msgs/Int32MultiArray", Int32MultiArraySerializer.Instance, new Int32MultiArray(SampleLayout, new[] { -32, 0, 32 })),
            Case("std_msgs/Int64", Int64MessageSerializer.Instance, new Int64Message(-6400000000L)),
            Case("std_msgs/Int64MultiArray", Int64MultiArraySerializer.Instance, new Int64MultiArray(SampleLayout, new[] { -64L, 0L, 64L })),
            Case("std_msgs/MultiArrayDimension", MultiArrayDimensionSerializer.Instance, new MultiArrayDimension("width", 640u, 1920u)),
            Case("std_msgs/MultiArrayLayout", MultiArrayLayoutSerializer.Instance, SampleLayout),
            Case("std_msgs/String", StringMessageSerializer.Instance, new StringMessage("unity-roundtrip")),
            Case("std_msgs/UInt8", UInt8MessageSerializer.Instance, new UInt8Message(250)),
            Case("std_msgs/UInt8MultiArray", UInt8MultiArraySerializer.Instance, new UInt8MultiArray(SampleLayout, new byte[] { 1, 2, 250 })),
            Case("std_msgs/UInt16", UInt16MessageSerializer.Instance, new UInt16Message(65000)),
            Case("std_msgs/UInt16MultiArray", UInt16MultiArraySerializer.Instance, new UInt16MultiArray(SampleLayout, new ushort[] { 1, 2, 65000 })),
            Case("std_msgs/UInt32", UInt32MessageSerializer.Instance, new UInt32Message(4000000000u)),
            Case("std_msgs/UInt32MultiArray", UInt32MultiArraySerializer.Instance, new UInt32MultiArray(SampleLayout, new[] { 1u, 2u, 4000000000u })),
            Case("std_msgs/UInt64", UInt64MessageSerializer.Instance, new UInt64Message(18000000000000000000ul)),
            Case("std_msgs/UInt64MultiArray", UInt64MultiArraySerializer.Instance, new UInt64MultiArray(SampleLayout, new[] { 1ul, 2ul, 18000000000000000000ul })),
        };

        public static IEnumerable MessageRoundTripCases
        {
            get
            {
                foreach (var messageCase in GeneratedMessageCases)
                {
                    yield return new TestCaseData(messageCase).SetName(
                        "全生成msg型をpublish_receiveできる(" + messageCase.Name + ")");
                }
            }
        }

        [TestCaseSource(nameof(MessageRoundTripCases))]
        public void 全生成msg型をpublish_receiveできる(object messageCase)
        {
            ((IGeneratedMessageRoundTripCase)messageCase).AssertRoundTrip();
        }

        [Test]
        public void 明示msg型一覧が_Msgsアセンブリ内の全Serializerを網羅する()
        {
            var serializerInterface = typeof(ICdrSerializer<>);
            var actual = typeof(StringMessage).Assembly
                .GetTypes()
                .Where(type => type.Namespace != null
                            && type.Namespace.StartsWith("ROSettaDDS.Msgs.", StringComparison.Ordinal)
                            && type.GetInterfaces().Any(
                                implemented => implemented.IsGenericType
                                            && implemented.GetGenericTypeDefinition() == serializerInterface))
                .ToArray();
            var expected = GeneratedMessageCases
                .Select(messageCase => messageCase.SerializerType)
                .Distinct()
                .ToArray();

            CollectionAssert.AreEquivalent(
                actual,
                expected,
                "GeneratedMessageCases must explicitly list every generated Msgs serializer.");
        }

        [Test]
        public void 空配列を持つ_MultiArray_をroundtripできる()
        {
            var received = RoundTripAndAssert(
                "unity_empty_multi_array",
                ByteMultiArraySerializer.Instance,
                new ByteMultiArray(new MultiArrayLayout(Array.Empty<MultiArrayDimension>(), 0u), Array.Empty<byte>()));

            Assert.IsNotNull(received.Data);
            Assert.IsEmpty(received.Data);
            Assert.IsNotNull(received.Layout.Dim);
            Assert.IsEmpty(received.Layout.Dim);
        }

        [Test]
        public void _64KiB超の_MultiArray_を_DATA_FRAG経路でroundtripできる()
        {
            var data = new byte[70 * 1024];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            var received = RoundTripAndAssert(
                "unity_large_multi_array",
                ByteMultiArraySerializer.Instance,
                new ByteMultiArray(new MultiArrayLayout(Array.Empty<MultiArrayDimension>(), 0u), data));

            CollectionAssert.AreEqual(data, received.Data);
        }

        [Test]
        public void UTF8マルチバイト文字列をroundtripできる()
        {
            const string expected = "日本語と絵文字をUnityで往復確認";
            var received = RoundTripAndAssert(
                "unity_utf8_string",
                StringMessageSerializer.Instance,
                new StringMessage(expected));

            Assert.AreEqual(expected, received.Data);
        }

        internal static T RoundTripAndAssert<T>(
            string topicPrefix,
            ICdrSerializer<T> serializer,
            T value)
        {
            using var pair = UnityLoopbackTestSupport.CreatePair();
            string topic = UnityLoopbackTestSupport.UniqueTopic(topicPrefix);
            T received = default;
            int receivedCount = 0;

            using var sub = pair.Reader.CreateSubscription<T>(
                topic,
                serializer,
                (message, _) =>
                {
                    received = message;
                    Interlocked.Exchange(ref receivedCount, 1);
                });
            using var pub = pair.Writer.CreatePublisher<T>(topic, serializer);

            pair.Start();
            UnityLoopbackTestSupport.AssertDiscovered(pair, topic);

            var expectedPayload = pub.SerializeWithEncapsulation(value).ToArray();
            pub.PublishAsync(value).GetAwaiter().GetResult();

            Assert.IsTrue(
                UnityLoopbackTestSupport.WaitUntil(
                    () => Volatile.Read(ref receivedCount) == 1,
                    UnityLoopbackTestSupport.ReceiveTimeout),
                "Message roundtrip timed out for " + typeof(T).FullName + ".");

            var actualPayload = pub.SerializeWithEncapsulation(received).ToArray();
            CollectionAssert.AreEqual(expectedPayload, actualPayload);
            return received;
        }

        private static IGeneratedMessageRoundTripCase Case<T>(
            string name,
            ICdrSerializer<T> serializer,
            T value)
            => new GeneratedMessageRoundTripCase<T>(name, serializer, value);

        private interface IGeneratedMessageRoundTripCase
        {
            string Name { get; }
            Type SerializerType { get; }
            void AssertRoundTrip();
        }

        private sealed class GeneratedMessageRoundTripCase<T> : IGeneratedMessageRoundTripCase
        {
            private readonly ICdrSerializer<T> _serializer;
            private readonly T _value;

            internal GeneratedMessageRoundTripCase(
                string name,
                ICdrSerializer<T> serializer,
                T value)
            {
                Name = name;
                _serializer = serializer;
                _value = value;
            }

            public string Name { get; }
            public Type SerializerType => _serializer.GetType();

            public void AssertRoundTrip()
                => RoundTripAndAssert("unity_generated_" + typeof(T).Name, _serializer, _value);

            public override string ToString() => Name;
        }
    }
}
