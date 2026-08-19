using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// In-memory broadcast hub for SSE (singleton). Subscribers get a bounded,
/// DropOldest channel; the SSE drain loops empty it every 500ms, so a full
/// channel means the consumer is gone or hopelessly stalled and the REST
/// endpoints remain the source of truth for anything dropped.
///
/// Disposing the returned subscription is the ONLY unsubscribe: TryWrite never
/// returns false on a DropOldest channel, so writers cannot detect dead
/// readers — an undisposed subscription stays registered forever, buffering up
/// to its capacity and adding cost to every broadcast.
/// </summary>
public class UpdateNotificationService : IUpdateNotificationService
{
    // Map-event capacity is generous (bursts of tile/character events between
    // 500ms drains); notification events are rarer and were already capped at 256.
    private const int MapEventCapacity = 1024;
    private const int NotificationEventCapacity = 256;

    private readonly SubscriptionSet<TileData> _tileUpdates = new(MapEventCapacity);
    private readonly SubscriptionSet<MergeDto> _mergeUpdates = new(MapEventCapacity);
    private readonly SubscriptionSet<MapInfo> _mapUpdates = new(MapEventCapacity);
    private readonly SubscriptionSet<int> _mapDeletes = new(MapEventCapacity);
    private readonly SubscriptionSet<MapRevisionDto> _mapRevisions = new(MapEventCapacity);
    private readonly SubscriptionSet<CustomMarkerEventDto> _customMarkerCreated = new(MapEventCapacity);
    private readonly SubscriptionSet<CustomMarkerEventDto> _customMarkerUpdated = new(MapEventCapacity);
    private readonly SubscriptionSet<CustomMarkerDeleteEventDto> _customMarkerDeleted = new(MapEventCapacity);
    private readonly SubscriptionSet<CharacterDeltaDto> _characterDeltas = new(MapEventCapacity);
    private readonly SubscriptionSet<PingEventDto> _pingCreated = new(MapEventCapacity);
    private readonly SubscriptionSet<PingDeleteEventDto> _pingDeleted = new(MapEventCapacity);
    private readonly SubscriptionSet<RoadEventDto> _roadCreated = new(MapEventCapacity);
    private readonly SubscriptionSet<RoadEventDto> _roadUpdated = new(MapEventCapacity);
    private readonly SubscriptionSet<RoadDeleteEventDto> _roadDeleted = new(MapEventCapacity);
    private readonly SubscriptionSet<OverlayEventDto> _overlayUpdated = new(MapEventCapacity);
    private readonly SubscriptionSet<NotificationEventDto> _notificationCreated = new(NotificationEventCapacity);
    private readonly SubscriptionSet<NotificationEventDto> _notificationUpdated = new(NotificationEventCapacity);
    private readonly SubscriptionSet<int> _notificationRead = new(NotificationEventCapacity);
    private readonly SubscriptionSet<int> _notificationDismissed = new(NotificationEventCapacity);
    private readonly SubscriptionSet<TimerEventDto> _timerCreated = new(MapEventCapacity);
    private readonly SubscriptionSet<TimerEventDto> _timerUpdated = new(MapEventCapacity);
    private readonly SubscriptionSet<TimerEventDto> _timerCompleted = new(MapEventCapacity);
    private readonly SubscriptionSet<int> _timerDeleted = new(MapEventCapacity);
    private readonly SubscriptionSet<MarkerEventDto> _markerCreated = new(MapEventCapacity);
    private readonly SubscriptionSet<MarkerEventDto> _markerUpdated = new(MapEventCapacity);
    private readonly SubscriptionSet<MarkerDeleteEventDto> _markerDeleted = new(MapEventCapacity);

    /// <summary>
    /// Live registrations across all event types. Not on the interface — used by
    /// tests and available for ops logging/metrics.
    /// </summary>
    public int ActiveSubscriptionCount =>
        _tileUpdates.Count + _mergeUpdates.Count + _mapUpdates.Count + _mapDeletes.Count +
        _mapRevisions.Count + _customMarkerCreated.Count + _customMarkerUpdated.Count +
        _customMarkerDeleted.Count + _characterDeltas.Count + _pingCreated.Count +
        _pingDeleted.Count + _roadCreated.Count + _roadUpdated.Count + _roadDeleted.Count +
        _overlayUpdated.Count + _notificationCreated.Count + _notificationUpdated.Count +
        _notificationRead.Count + _notificationDismissed.Count + _timerCreated.Count +
        _timerUpdated.Count + _timerCompleted.Count + _timerDeleted.Count +
        _markerCreated.Count + _markerUpdated.Count + _markerDeleted.Count;

    public IChannelSubscription<TileData> SubscribeToTileUpdates() => _tileUpdates.Subscribe();
    public IChannelSubscription<MergeDto> SubscribeToMergeUpdates() => _mergeUpdates.Subscribe();
    public IChannelSubscription<MapInfo> SubscribeToMapUpdates() => _mapUpdates.Subscribe();
    public IChannelSubscription<int> SubscribeToMapDeletes() => _mapDeletes.Subscribe();
    public IChannelSubscription<MapRevisionDto> SubscribeToMapRevisions() => _mapRevisions.Subscribe();
    public IChannelSubscription<CustomMarkerEventDto> SubscribeToCustomMarkerCreated() => _customMarkerCreated.Subscribe();
    public IChannelSubscription<CustomMarkerEventDto> SubscribeToCustomMarkerUpdated() => _customMarkerUpdated.Subscribe();
    public IChannelSubscription<CustomMarkerDeleteEventDto> SubscribeToCustomMarkerDeleted() => _customMarkerDeleted.Subscribe();
    public IChannelSubscription<CharacterDeltaDto> SubscribeToCharacterDelta() => _characterDeltas.Subscribe();
    public IChannelSubscription<PingEventDto> SubscribeToPingCreated() => _pingCreated.Subscribe();
    public IChannelSubscription<PingDeleteEventDto> SubscribeToPingDeleted() => _pingDeleted.Subscribe();
    public IChannelSubscription<RoadEventDto> SubscribeToRoadCreated() => _roadCreated.Subscribe();
    public IChannelSubscription<RoadEventDto> SubscribeToRoadUpdated() => _roadUpdated.Subscribe();
    public IChannelSubscription<RoadDeleteEventDto> SubscribeToRoadDeleted() => _roadDeleted.Subscribe();
    public IChannelSubscription<OverlayEventDto> SubscribeToOverlayUpdated() => _overlayUpdated.Subscribe();
    public IChannelSubscription<NotificationEventDto> SubscribeToNotificationCreated() => _notificationCreated.Subscribe();
    public IChannelSubscription<NotificationEventDto> SubscribeToNotificationUpdated() => _notificationUpdated.Subscribe();
    public IChannelSubscription<int> SubscribeToNotificationRead() => _notificationRead.Subscribe();
    public IChannelSubscription<int> SubscribeToNotificationDismissed() => _notificationDismissed.Subscribe();
    public IChannelSubscription<TimerEventDto> SubscribeToTimerCreated() => _timerCreated.Subscribe();
    public IChannelSubscription<TimerEventDto> SubscribeToTimerUpdated() => _timerUpdated.Subscribe();
    public IChannelSubscription<TimerEventDto> SubscribeToTimerCompleted() => _timerCompleted.Subscribe();
    public IChannelSubscription<int> SubscribeToTimerDeleted() => _timerDeleted.Subscribe();
    public IChannelSubscription<MarkerEventDto> SubscribeToMarkerCreated() => _markerCreated.Subscribe();
    public IChannelSubscription<MarkerEventDto> SubscribeToMarkerUpdated() => _markerUpdated.Subscribe();
    public IChannelSubscription<MarkerDeleteEventDto> SubscribeToMarkerDeleted() => _markerDeleted.Subscribe();

    public void NotifyTileUpdate(TileData tileData) => _tileUpdates.Notify(tileData);

    public void NotifyMapMerge(int fromMapId, int toMapId, Coord shift, string tenantId) =>
        _mergeUpdates.Notify(new MergeDto
        {
            From = fromMapId,
            To = toMapId,
            Shift = shift,
            TenantId = tenantId
        });

    public void NotifyMapUpdated(MapInfo mapInfo) => _mapUpdates.Notify(mapInfo);
    public void NotifyMapDeleted(int mapId) => _mapDeletes.Notify(mapId);

    public void NotifyMapRevision(int mapId, int revision) =>
        _mapRevisions.Notify(new MapRevisionDto
        {
            MapId = mapId,
            Revision = revision
        });

    public void NotifyCustomMarkerCreated(CustomMarkerEventDto marker) => _customMarkerCreated.Notify(marker);
    public void NotifyCustomMarkerUpdated(CustomMarkerEventDto marker) => _customMarkerUpdated.Notify(marker);
    public void NotifyCustomMarkerDeleted(CustomMarkerDeleteEventDto deleteEvent) => _customMarkerDeleted.Notify(deleteEvent);
    public void NotifyCharacterDelta(CharacterDeltaDto delta) => _characterDeltas.Notify(delta);
    public void NotifyPingCreated(PingEventDto ping) => _pingCreated.Notify(ping);
    public void NotifyPingDeleted(PingDeleteEventDto deleteEvent) => _pingDeleted.Notify(deleteEvent);
    public void NotifyRoadCreated(RoadEventDto road) => _roadCreated.Notify(road);
    public void NotifyRoadUpdated(RoadEventDto road) => _roadUpdated.Notify(road);
    public void NotifyRoadDeleted(RoadDeleteEventDto deleteEvent) => _roadDeleted.Notify(deleteEvent);
    public void NotifyOverlayUpdated(OverlayEventDto overlay) => _overlayUpdated.Notify(overlay);
    public void NotifyNotificationCreated(NotificationEventDto notification) => _notificationCreated.Notify(notification);
    public void NotifyNotificationUpdated(NotificationEventDto notification) => _notificationUpdated.Notify(notification);
    public void NotifyNotificationRead(int notificationId) => _notificationRead.Notify(notificationId);
    public void NotifyNotificationDismissed(int notificationId) => _notificationDismissed.Notify(notificationId);
    public void NotifyTimerCreated(TimerEventDto timer) => _timerCreated.Notify(timer);
    public void NotifyTimerUpdated(TimerEventDto timer) => _timerUpdated.Notify(timer);
    public void NotifyTimerCompleted(TimerEventDto timer) => _timerCompleted.Notify(timer);
    public void NotifyTimerDeleted(int timerId) => _timerDeleted.Notify(timerId);
    public void NotifyMarkerCreated(MarkerEventDto marker) => _markerCreated.Notify(marker);
    public void NotifyMarkerUpdated(MarkerEventDto marker) => _markerUpdated.Notify(marker);
    public void NotifyMarkerDeleted(MarkerDeleteEventDto deleteEvent) => _markerDeleted.Notify(deleteEvent);

    /// <summary>
    /// All subscribers of one event type. Channels are keyed by id so disposal can
    /// remove exactly one registration; a disposed channel is completed, which lets
    /// its buffered events be collected and stops it costing anything on Notify.
    /// </summary>
    private sealed class SubscriptionSet<T>
    {
        private readonly ConcurrentDictionary<Guid, Channel<T>> _channels = new();
        private readonly int _capacity;

        public SubscriptionSet(int capacity)
        {
            _capacity = capacity;
        }

        public int Count => _channels.Count;

        public IChannelSubscription<T> Subscribe()
        {
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            var id = Guid.NewGuid();
            _channels[id] = channel;
            return new Subscription(this, id, channel.Reader);
        }

        public void Notify(T item)
        {
            // Enumerating the ConcurrentDictionary directly avoids the snapshot
            // allocation of .Values. TryWrite on a disposed (completed) channel
            // returns false and is simply skipped.
            foreach (var entry in _channels)
            {
                entry.Value.Writer.TryWrite(item);
            }
        }

        private void Unsubscribe(Guid id)
        {
            if (_channels.TryRemove(id, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }

        private sealed class Subscription : IChannelSubscription<T>
        {
            private readonly SubscriptionSet<T> _owner;
            private readonly Guid _id;

            public Subscription(SubscriptionSet<T> owner, Guid id, ChannelReader<T> reader)
            {
                _owner = owner;
                _id = id;
                Reader = reader;
            }

            public ChannelReader<T> Reader { get; }

            public void Dispose() => _owner.Unsubscribe(_id);
        }
    }
}
