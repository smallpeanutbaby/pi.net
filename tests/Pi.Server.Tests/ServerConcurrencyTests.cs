using Pi.Chord.Services;
using Pi.Protocol;
using Xunit;
using Context = Pi.Chord.Context.Context;
using DeltaOp = Pi.Chord.Delta.DeltaOp;
using DeltaPath = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;

namespace Pi.Server.Tests;

/// <summary>
/// server.ts 的每连接状态在 TS 里只在事件循环上变动；C# 的读泵线程、请求续体与发布者并发，
/// 活动请求表、订阅编码器表与订阅排空的就绪切换都必须加锁。
/// </summary>
public class ServerConcurrencyTests
{
    private const string ServerId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static readonly ServiceInstanceAddress Instance = new("inst-a", 1L);

    private sealed record Metadata(string Id) : ISessionMetadata;

    /// <summary>进程内的字节连接：服务端写出的帧在这里解码并按序收集。</summary>
    private sealed class InProcessConnection : IByteConnection
    {
        private readonly ProtocolCodec.ServerMessageDecoder _decoder = new();
        private readonly object _gate = new();
        private readonly List<ServerMessage> _received = [];
        private readonly List<TaskCompletionSource<ServerMessage>> _waiters = [];

        public bool Closed { get; private set; }

        public Task SendAsync(byte[] chunk, CancellationToken cancellationToken = default)
        {
            List<ServerMessage> messages;
            lock (_gate)
            {
                messages = _decoder.Push(chunk).ToList();
                _received.AddRange(messages);
                foreach (var message in messages)
                {
                    if (_waiters.Count == 0) break;
                    var waiter = _waiters[0];
                    _waiters.RemoveAt(0);
                    waiter.TrySetResult(message);
                }
            }
            return Task.CompletedTask;
        }

        public Task CloseAsync(byte[]? finalChunk = null)
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public IReadOnlyList<ServerMessage> Received
        {
            get
            {
                lock (_gate) return _received.ToList();
            }
        }

        public async Task WaitForAsync(Func<IReadOnlyList<ServerMessage>, bool> condition, int milliseconds = 10_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!condition(Received))
            {
                Assert.True(DateTime.UtcNow < deadline, $"condition not met; received {Received.Count} messages");
                await Task.Delay(10);
            }
        }
    }

    private sealed class Attachment(Func<ServiceCall, ServiceUpdatePublisher, Task<object?>> invoke)
        : IRoutedServerServiceAttachment
    {
        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
            => invoke(call, publish);

        public Task ReleaseAsync(Context context) => Task.CompletedTask;
    }

    private sealed class Host(Func<ServiceCall, ServiceUpdatePublisher, Task<object?>> invoke) : IServerHost<Metadata>,
        IRoutedServerServiceHost
    {
        public IRoutedServerServiceHost ServerServices => this;

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
            => Task.FromResult(new Metadata(sessionId));

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => throw new NotSupportedException();

        public Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
            Context context)
            => Task.FromResult<IRoutedServerServiceAttachment>(new Attachment(invoke));
    }

    private static byte[] Encode(ClientMessage message) => ProtocolCodec.EncodeClientMessage(message);

    private static async Task<(Server<Metadata> Server, InProcessConnection Connection, IByteConnectionHandler Handler)>
        HandshakeAsync(Func<ServiceCall, ServiceUpdatePublisher, Task<object?>> invoke, List<Exception> errors)
    {
        var server = new Server<Metadata>(new Host(invoke), new ServerOptions
        {
            Listeners = [],
            ServerId = ServerId,
            OnError = error => { lock (errors) errors.Add(error); },
        });
        await server.StartAsync();
        var connection = new InProcessConnection();
        var handler = server.Accept(connection);
        handler.OnData(Encode(new ClientMessage.Hello(ProtocolVersion.Current)));
        await connection.WaitForAsync(received => received.Any(message => message is ServerMessage.Hello));
        return (server, connection, handler);
    }

    [Fact]
    public async Task ManyInFlightRequestsCompleteWhileContinuationsRemoveThem()
    {
        var errors = new List<Exception>();
        var (server, connection, handler) = await HandshakeAsync(async (call, _) =>
        {
            // 在别的线程上完成：请求表的移除与读泵线程的加入并发。
            await Task.Yield();
            return $"{call.Member}";
        }, errors);
        try
        {
            const int count = 2000;
            var target = new RpcTarget.ServerTarget(ServerId);
            for (var index = 0; index < count; index++)
            {
                handler.OnData(Encode(new ClientMessage.Request($"r{index}", target,
                    new Dictionary<string, object?>
                    {
                        ["serviceId"] = "svc",
                        ["member"] = $"m{index}",
                        ["args"] = new List<object?>(),
                    })));
            }

            await connection.WaitForAsync(received => received.Count(m => m is ServerMessage.ResponseOk) == count);
            var responses = connection.Received.OfType<ServerMessage.ResponseOk>().ToList();
            Assert.Equal(count, responses.Select(response => response.Id).Distinct().Count());
            Assert.All(responses, response => Assert.Equal($"m{response.Id[1..]}", response.Result));
            Assert.DoesNotContain(connection.Received, message => message is ServerMessage.ResponseError);
            lock (errors) Assert.Empty(errors);
            Assert.False(connection.Closed);
        }
        finally
        {
            await server.CloseAsync();
        }
    }

    [Fact]
    public async Task UpdatesPublishedWhileTheSubscribeResponseDrainsAreNeverStranded()
    {
        const int updates = 300;
        var errors = new List<Exception>();
        var (server, connection, handler) = await HandshakeAsync((call, publish) =>
        {
            if (ServiceWire.DecodeServiceControlCall(call) is not ServiceControlCall.Subscribe subscribe)
            {
                return Task.FromResult<object?>(null);
            }

            // 快照返回的同时就开始发布：前一部分在应答前排队，后一部分直达——切换点不能吞掉任何一条。
            _ = Task.Run(async () =>
            {
                for (var sequence = 2L; sequence < updates + 2; sequence++)
                {
                    await publish(subscribe.SubscriptionId,
                        new ServiceProviderUpdate.StateUpdate(Instance, "doc", sequence,
                            [new DeltaOp.Set(new DeltaPath([Seg.Key("n")]), sequence)]),
                        Context.Background);
                }
            });
            var snapshot = new ServiceSubscriptionSnapshot("fs", ServiceMode.Singleton,
            [
                new ServiceInstanceSnapshot(Instance,
                [
                    new ServiceMemberSnapshot.State("doc", 1L,
                        [new DeltaOp.Replace(new Dictionary<string, object?> { ["n"] = 1L })]),
                ]),
            ]);
            return Task.FromResult<object?>(ServiceWire.ToValue(new ServiceStateEncoder().EncodeSnapshot(snapshot)));
        }, errors);
        try
        {
            var subscribe = ServiceWire.CreateServiceSubscribeCall("service-1", "fs", ServiceMode.Singleton);
            handler.OnData(Encode(new ClientMessage.Request("r1", new RpcTarget.ServerTarget(ServerId),
                new Dictionary<string, object?>
                {
                    ["serviceId"] = subscribe.ServiceId,
                    ["member"] = subscribe.Member,
                    ["args"] = subscribe.Args,
                })));

            await connection.WaitForAsync(received => received.Count(m => m is ServerMessage.ServiceEvent) == updates);
            var decoder = new ServiceStateDecoder();
            var response = Assert.Single(connection.Received.OfType<ServerMessage.ResponseOk>());
            decoder.DecodeSnapshot(ServiceWire.ParseWireServiceSubscriptionSnapshot(response.Result));
            var sequences = connection.Received.OfType<ServerMessage.ServiceEvent>()
                .Select(@event => ((ServiceProviderUpdate.StateUpdate)decoder.DecodeUpdate(
                    ServiceWire.ParseWireServiceProviderUpdate(@event.Update))).Sequence)
                .ToList();
            Assert.Equal(Enumerable.Range(2, updates).Select(i => (long)i), sequences);
            lock (errors) Assert.Empty(errors);
        }
        finally
        {
            await server.CloseAsync();
        }
    }
}
