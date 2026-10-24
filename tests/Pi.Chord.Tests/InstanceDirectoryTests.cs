using Pi.Chord.Services;
using Xunit;
using Context = Pi.Chord.Context.Context;

namespace Pi.Chord.Tests;

/// <summary>
/// services/instances.ts 的 <c>InstanceDirectory</c>：观察者处理器在通知期间同步退订或改动目录
/// 是合法的（TS 里 Set/Map 遍历中删除是安全的），C# 遍历活表会抛 "Collection was modified"。
/// </summary>
public class InstanceDirectoryTests
{
    private sealed class Entry(string key, long generation) : IInstanceDirectoryEntry
    {
        public string Key => key;

        public long Generation => generation;

        public object Service => this;

        public int Deactivated { get; private set; }

        public void Deactivate() => Deactivated++;
    }

    [Fact]
    public void ObserverMayUnsubscribeWhileBeingNotified()
    {
        var errors = new List<Exception>();
        var directory = new InstanceDirectory<Entry>(ready: false, errors.Add);
        directory.Insert(new Entry("a", 1));
        directory.Insert(new Entry("b", 1));

        var seen = new List<string>();
        IDisposable? subscription = null;
        subscription = directory.Observe((service, _) =>
        {
            seen.Add(((Entry)service).Key);
            // 第一次通知就退订：后续实例不再送达，目录也不能因此崩掉。
            subscription!.Dispose();
            return null;
        });
        directory.Observe((service, _) =>
        {
            seen.Add("second:" + ((Entry)service).Key);
            return null;
        });

        directory.Ready();

        Assert.Empty(errors);
        Assert.Equal(["a", "second:a", "second:b"], seen);
        Assert.Equal(1, directory.ObserverCount);
    }

    [Fact]
    public void ObserverMayInsertAndRemoveEntriesWhileBeingNotified()
    {
        var errors = new List<Exception>();
        var directory = new InstanceDirectory<Entry>(ready: true, errors.Add);
        var seen = new List<string>();
        var first = new Entry("a", 1);
        directory.Observe((service, _) =>
        {
            var entry = (Entry)service;
            seen.Add(entry.Key);
            // 处理器在通知期间改动目录：移除自己、插入一个新键。
            if (entry.Key == "a")
            {
                directory.Remove(entry);
                directory.Insert(new Entry("b", 1));
            }
            return null;
        });

        directory.Insert(first);

        Assert.Empty(errors);
        Assert.Equal(["a", "b"], seen);
        Assert.Equal(1, first.Deactivated);
        Assert.Null(directory.Get("a"));
        Assert.NotNull(directory.Get("b"));

        // 观察者在 Dispose 通知期间改动：同样不得抛。
        directory.Dispose();
        Assert.Empty(errors);
    }
}
