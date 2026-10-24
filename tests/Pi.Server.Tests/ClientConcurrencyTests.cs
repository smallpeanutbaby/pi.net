using System.Collections.Concurrent;
using Pi.Chord.Services;
using Pi.Client;
using Pi.Protocol;
using Xunit;
using DeltaOp = Pi.Chord.Delta.DeltaOp;
using DeltaPath = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;

namespace Pi.Server.Tests;

/// <summary>
/// client.ts 的每连接状态在 TS 里只有一个线程碰；C# 的调用方线程与传输读泵线程并发，
/// 请求 id 分配与订阅的就绪/排队切换都必须是原子的。
/// </summary>
public class ClientConcurrencyTests
{
    private const string ServerId = "11111111-2222-4333-8444-555555555555";

    private static readonly ServiceInstanceAddress Instance = new("inst-a", 1L);

    /// <summary>
    /// 假服务端传输：解码客户端帧，把应答从单一"读泵"尾链上按序投递（与 Unix 传输一致），
    /// 并记录每个请求 id 被发送的次数。
    /// </summary>
    private sealed class FakeServerTransport(IByteTransportHandlers handlers, ConcurrentDictionary<string, int> seen)
        : IByteTransport
    {
        private readonly ProtocolCodec.ClientMessageDecoder _decoder = new();
        private readonly object _gate = new();
        private Task _pumpTail = Task.CompletedTask;

        public Func<ClientMessage.Request, ServerMessage?> OnRequest { get; set; } =
            request => new ServerMessage.ResponseOk(request.Id, null);

        public Task SendAsync(byte[] chunk)
        {
            List<ClientMessage> messages;
            lock (_gate) messages = _decoder.Push(chunk).ToList();
            foreach (var message in messages)
            {
                ServerMessage? reply = message switch
                {
                    ClientMessage.Hello => new ServerMessage.Hello(ProtocolVersion.Current, ServerId),
                    ClientMessage.Request request => OnRequest(request),
                    _ => null,
                };
                if (message is ClientMessage.Request seenRequest) seen.AddOrUpdate(seenRequest.Id, 1, (_, count) => count + 1);
                if (reply is not null) Deliver(reply);
            }
            return Task.CompletedTask;
        }

        public void Deliver(ServerMessage message)
        {
            var frame = ProtocolCodec.EncodeServerMessage(message);
            lock (_gate) _pumpTail = _pumpTail.ContinueWith(_ => handlers.OnData(frame), TaskScheduler.Default);
        }

        public void Close()
        {
        }
    }

    private static async Task<(Pi.Client.Client Client, FakeServerTransport Transport)> ConnectAsync(
        ConcurrentDictionary<string, int> seen)
    {
        FakeServerTransport? transport = null;
        var client = await Pi.Client.Client.ConnectAsync(new ClientOptions
        {
            ServerId = ServerId,
            TransportFactory = handlers =>
            {
                transport = new FakeServerTransport(handlers, seen);
                return Task.FromResult<IByteTransport>(transport);
            },
        });
        return (client, transport!);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverReuseARequestId()
    {
        var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var (client, _) = await ConnectAsync(seen);
        var disconnected = new List<Exception?>();
        client.OnConnectionStateChange(change =>
        {
            if (change.State == Pi.Client.ConnectionState.Disconnected) lock (disconnected) disconnected.Add(change.Error);
        });

        // 重复的请求 id 会让服务端以 invalid_request 拒绝、客户端随之拆掉整条连接。
        const int count = 5000;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new RpcTarget.ServerTarget(ServerId);
        var requests = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await client.RequestAsync(target, new ServiceCall("svc", "m", []));
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(count, seen.Count);
        Assert.DoesNotContain(seen, entry => entry.Value > 1);
        lock (disconnected) Assert.Empty(disconnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task StartingASubscriptionWhileUpdatesArriveDeliversEachOnceInOrder()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var seen = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var (client, transport) = await ConnectAsync(seen);
            var encoder = new ServiceStateEncoder();
            string? subscriptionId = null;
            transport.OnRequest = request =>
            {
                var call = ServiceWire.ParseServiceCall(request.Call);
                if (ServiceWire.DecodeServiceControlCall(call) is not ServiceControlCall.Subscribe subscribe)
                {
                    return new ServerMessage.ResponseOk(request.Id, null);
                }
                subscriptionId = subscribe.SubscriptionId;
                var snapshot = new ServiceSubscriptionSnapshot("fs", ServiceMode.Singleton,
                [
                    new ServiceInstanceSnapshot(Instance,
                    [
                        new ServiceMemberSnapshot.State("doc", 1L,
                            [new DeltaOp.Replace(new Dictionary<string, object?> { ["n"] = 1L })]),
                    ]),
                ]);
                return new ServerMessage.ResponseOk(request.Id, ServiceWire.ToValue(encoder.EncodeSnapshot(snapshot)));
            };

            var delivered = new List<long>();
            const int updates = 200;
            var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = await client.SubscribeServiceAsync(
                new RpcTarget.ServerTarget(ServerId), "fs", ServiceMode.Singleton,
                update =>
                {
                    lock (delivered)
                    {
                        delivered.Add(((ServiceProviderUpdate.StateUpdate)update).Sequence);
                        if (delivered.Count == updates) all.TrySetResult();
                    }
                    return Task.CompletedTask;
                });
            Assert.NotNull(subscriptionId);

            // 读泵线程持续投递更新，调用方线程在中途 Start：排队的与直达的更新既不能丢也不能重复。
            var pusher = Task.Run(() =>
            {
                for (var sequence = 2L; sequence < updates + 2; sequence++)
                {
                    var update = new ServiceProviderUpdate.StateUpdate(Instance, "doc", sequence,
                        [new DeltaOp.Set(new DeltaPath([Seg.Key("n")]), sequence)]);
                    transport.Deliver(new ServerMessage.ServiceEvent(subscriptionId!,
                        ServiceWire.ToValue(encoder.EncodeUpdate(update))));
                }
            });
            await Task.Yield();
            subscription.Start();
            await pusher;

            await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (delivered) Assert.Equal(Enumerable.Range(2, updates).Select(i => (long)i), delivered);
            await client.DisposeAsync();
        }
    }
}
