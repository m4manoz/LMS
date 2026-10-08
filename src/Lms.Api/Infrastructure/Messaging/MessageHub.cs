using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lms.Api.Infrastructure.Messaging;

/// <summary>
/// A signal that something changed in a conversation. It carries no message text: the browser asks for the messages itself, through the usual checks.
/// <see cref="Recipients"/> lists exactly who is told (direct conversations); null means everyone who can reach the course (course chats).
/// </summary>
public sealed record MessageEvent(string Type, Guid TenantId, Guid ConversationId, Guid? MessageId, Guid? CourseId, IReadOnlyList<Guid>? Recipients);

/// <summary>
/// Tells connected browsers about new, edited and deleted messages as they happen, so a conversation updates without waiting for the next poll.
/// It lives in this server's memory: with several servers behind one address a person only hears about messages handled by the server they are connected to,
/// and the page's slower polling covers the rest.
/// </summary>
public sealed class MessageHub
{
    public sealed class Subscription(MessageHub hub, Guid id, Guid tenantId, Guid userId) : IDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid UserId { get; } = userId;
        /// <summary>Bounded: a browser that stops reading loses its oldest signals instead of growing memory. A lost signal costs one missed refresh, and polling catches up.</summary>
        public Channel<MessageEvent> Events { get; } = Channel.CreateBounded<MessageEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public void Dispose() => hub.Remove(id);
    }

    private readonly ConcurrentDictionary<Guid, Subscription> subscriptions = new();

    public int ConnectionCount => subscriptions.Count;

    public Subscription Subscribe(Guid tenantId, Guid userId)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(this, id, tenantId, userId);
        subscriptions[id] = subscription;
        return subscription;
    }

    internal void Remove(Guid id)
    {
        if (subscriptions.TryRemove(id, out var removed)) removed.Events.Writer.TryComplete();
    }

    public void Publish(MessageEvent message)
    {
        foreach (var subscription in subscriptions.Values)
        {
            if (subscription.TenantId != message.TenantId) continue;   // never across organizations
            if (message.Recipients is not null && !message.Recipients.Contains(subscription.UserId)) continue;
            subscription.Events.Writer.TryWrite(message);
        }
    }
}
