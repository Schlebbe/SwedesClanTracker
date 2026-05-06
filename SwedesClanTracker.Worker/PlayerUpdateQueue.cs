using System.Collections.Generic;

namespace SwedesClanTracker.Worker;

public interface IPlayerUpdateQueue
{
    void EnqueueFront(int playerId);
    void EnqueueMissingPriority(int playerId);
    void EnqueueBack(int playerId);
    bool TryDequeue(out int playerId);
    Task WaitForItemAsync(TimeSpan timeout, CancellationToken ct);
}

public class PlayerUpdateQueue : IPlayerUpdateQueue
{
    private readonly LinkedList<int> _manualQueue = new();
    private readonly LinkedList<int> _missingQueue = new();
    private readonly LinkedList<int> _normalQueue = new();
    private readonly Dictionary<int, QueueTier> _set = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);

    private enum QueueTier
    {
        Manual,
        Missing,
        Normal
    }

    public void EnqueueFront(int playerId)
    {
        Enqueue(playerId, QueueTier.Manual);
    }

    public void EnqueueMissingPriority(int playerId)
    {
        Enqueue(playerId, QueueTier.Missing);
    }

    public void EnqueueBack(int playerId)
    {
        Enqueue(playerId, QueueTier.Normal);
    }

    private void Enqueue(int playerId, QueueTier tier)
    {
        lock (_lock)
        {
            if (_set.TryGetValue(playerId, out var existingTier))
            {
                if (existingTier == tier) return;
                if (existingTier < tier) return;

                var node = GetQueue(existingTier).Find(playerId);
                if (node is not null)
                {
                    GetQueue(existingTier).Remove(node);
                }
            }

            var targetQueue = GetQueue(tier);
            if (tier == QueueTier.Manual) targetQueue.AddFirst(playerId);
            else targetQueue.AddLast(playerId);
            _set[playerId] = tier;
            _signal.Release();
        }
    }

    public bool TryDequeue(out int playerId)
    {
        lock (_lock)
        {
            if (TryDequeueFrom(_manualQueue, out playerId)) return true;
            if (TryDequeueFrom(_missingQueue, out playerId)) return true;
            if (TryDequeueFrom(_normalQueue, out playerId)) return true;

            playerId = 0;
            return false;
        }
    }

    private bool TryDequeueFrom(LinkedList<int> queue, out int playerId)
    {
        if (queue.First is null)
        {
            playerId = 0;
            return false;
        }

        playerId = queue.First.Value;
        queue.RemoveFirst();
        _set.Remove(playerId);
        return true;
    }

    private LinkedList<int> GetQueue(QueueTier tier)
    {
        return tier switch
        {
            QueueTier.Manual => _manualQueue,
            QueueTier.Missing => _missingQueue,
            _ => _normalQueue
        };
    }

    public async Task WaitForItemAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return;
        try
        {
            await _signal.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignore timeout
        }
    }
}
