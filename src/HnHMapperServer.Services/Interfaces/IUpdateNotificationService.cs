using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// In-memory broadcast hub for SSE. Every Subscribe method returns an
/// <see cref="IChannelSubscription{T}"/> that the caller MUST dispose when its
/// connection ends — disposal is the only unsubscribe, and an undisposed
/// subscription keeps buffering events (up to the channel bound) forever.
/// </summary>
public interface IUpdateNotificationService
{
    /// <summary>
    /// Subscribes to tile update notifications
    /// </summary>
    IChannelSubscription<TileData> SubscribeToTileUpdates();

    /// <summary>
    /// Subscribes to map merge notifications
    /// </summary>
    IChannelSubscription<MergeDto> SubscribeToMergeUpdates();

    /// <summary>
    /// Subscribes to map metadata update notifications (rename, hidden, priority changes)
    /// </summary>
    IChannelSubscription<MapInfo> SubscribeToMapUpdates();

    /// <summary>
    /// Subscribes to map deletion notifications
    /// </summary>
    IChannelSubscription<int> SubscribeToMapDeletes();

    /// <summary>
    /// Subscribes to map revision notifications (for cache busting)
    /// </summary>
    IChannelSubscription<MapRevisionDto> SubscribeToMapRevisions();

    /// <summary>
    /// Notifies all subscribers of a tile update
    /// </summary>
    void NotifyTileUpdate(TileData tileData);

    /// <summary>
    /// Notifies all subscribers of a map merge
    /// </summary>
    void NotifyMapMerge(int fromMapId, int toMapId, Coord shift, string tenantId);

    /// <summary>
    /// Notifies all subscribers of a map metadata update (rename, hidden, priority)
    /// </summary>
    void NotifyMapUpdated(MapInfo mapInfo);

    /// <summary>
    /// Notifies all subscribers of a map deletion
    /// </summary>
    void NotifyMapDeleted(int mapId);

    /// <summary>
    /// Notifies all subscribers of a map revision update (for cache busting)
    /// </summary>
    void NotifyMapRevision(int mapId, int revision);

    /// <summary>
    /// Subscribes to custom marker creation notifications
    /// </summary>
    IChannelSubscription<CustomMarkerEventDto> SubscribeToCustomMarkerCreated();

    /// <summary>
    /// Subscribes to custom marker update notifications
    /// </summary>
    IChannelSubscription<CustomMarkerEventDto> SubscribeToCustomMarkerUpdated();

    /// <summary>
    /// Subscribes to custom marker deletion notifications
    /// </summary>
    IChannelSubscription<CustomMarkerDeleteEventDto> SubscribeToCustomMarkerDeleted();

    /// <summary>
    /// Notifies all subscribers of a custom marker creation
    /// </summary>
    void NotifyCustomMarkerCreated(CustomMarkerEventDto marker);

    /// <summary>
    /// Notifies all subscribers of a custom marker update
    /// </summary>
    void NotifyCustomMarkerUpdated(CustomMarkerEventDto marker);

    /// <summary>
    /// Notifies all subscribers of a custom marker deletion
    /// </summary>
    void NotifyCustomMarkerDeleted(CustomMarkerDeleteEventDto deleteEvent);

    /// <summary>
    /// Subscribes to character delta notifications (incremental updates)
    /// </summary>
    IChannelSubscription<CharacterDeltaDto> SubscribeToCharacterDelta();

    /// <summary>
    /// Notifies all subscribers of character deltas (incremental changes)
    /// </summary>
    void NotifyCharacterDelta(CharacterDeltaDto delta);

    /// <summary>
    /// Subscribes to ping creation notifications
    /// </summary>
    IChannelSubscription<PingEventDto> SubscribeToPingCreated();

    /// <summary>
    /// Subscribes to ping deletion notifications
    /// </summary>
    IChannelSubscription<PingDeleteEventDto> SubscribeToPingDeleted();

    /// <summary>
    /// Notifies all subscribers of a ping creation
    /// </summary>
    void NotifyPingCreated(PingEventDto ping);

    /// <summary>
    /// Notifies all subscribers of a ping deletion
    /// </summary>
    void NotifyPingDeleted(PingDeleteEventDto deleteEvent);

    /// <summary>
    /// Subscribes to road creation notifications
    /// </summary>
    IChannelSubscription<RoadEventDto> SubscribeToRoadCreated();

    /// <summary>
    /// Subscribes to road update notifications
    /// </summary>
    IChannelSubscription<RoadEventDto> SubscribeToRoadUpdated();

    /// <summary>
    /// Subscribes to road deletion notifications
    /// </summary>
    IChannelSubscription<RoadDeleteEventDto> SubscribeToRoadDeleted();

    /// <summary>
    /// Notifies all subscribers of a road creation
    /// </summary>
    void NotifyRoadCreated(RoadEventDto road);

    /// <summary>
    /// Notifies all subscribers of a road update
    /// </summary>
    void NotifyRoadUpdated(RoadEventDto road);

    /// <summary>
    /// Notifies all subscribers of a road deletion
    /// </summary>
    void NotifyRoadDeleted(RoadDeleteEventDto deleteEvent);

    /// <summary>
    /// Subscribes to overlay update notifications
    /// </summary>
    IChannelSubscription<OverlayEventDto> SubscribeToOverlayUpdated();

    /// <summary>
    /// Notifies all subscribers of an overlay update
    /// </summary>
    void NotifyOverlayUpdated(OverlayEventDto overlay);

    /// <summary>
    /// Subscribes to notification creation events
    /// </summary>
    IChannelSubscription<NotificationEventDto> SubscribeToNotificationCreated();

    /// <summary>
    /// Subscribes to notification in-place update events (e.g. a coalesced digest changed)
    /// </summary>
    IChannelSubscription<NotificationEventDto> SubscribeToNotificationUpdated();

    /// <summary>
    /// Subscribes to notification read events
    /// </summary>
    IChannelSubscription<int> SubscribeToNotificationRead();

    /// <summary>
    /// Subscribes to notification dismiss events
    /// </summary>
    IChannelSubscription<int> SubscribeToNotificationDismissed();

    /// <summary>
    /// Notifies all subscribers of a notification creation
    /// </summary>
    void NotifyNotificationCreated(NotificationEventDto notification);

    /// <summary>
    /// Notifies all subscribers of a notification changing in place (e.g. a coalesced digest)
    /// </summary>
    void NotifyNotificationUpdated(NotificationEventDto notification);

    /// <summary>
    /// Notifies all subscribers of a notification being read
    /// </summary>
    void NotifyNotificationRead(int notificationId);

    /// <summary>
    /// Notifies all subscribers of a notification being dismissed
    /// </summary>
    void NotifyNotificationDismissed(int notificationId);

    /// <summary>
    /// Subscribes to timer creation events
    /// </summary>
    IChannelSubscription<TimerEventDto> SubscribeToTimerCreated();

    /// <summary>
    /// Subscribes to timer update events
    /// </summary>
    IChannelSubscription<TimerEventDto> SubscribeToTimerUpdated();

    /// <summary>
    /// Subscribes to timer completion events
    /// </summary>
    IChannelSubscription<TimerEventDto> SubscribeToTimerCompleted();

    /// <summary>
    /// Subscribes to timer deletion events
    /// </summary>
    IChannelSubscription<int> SubscribeToTimerDeleted();

    /// <summary>
    /// Notifies all subscribers of a timer creation
    /// </summary>
    void NotifyTimerCreated(TimerEventDto timer);

    /// <summary>
    /// Notifies all subscribers of a timer update
    /// </summary>
    void NotifyTimerUpdated(TimerEventDto timer);

    /// <summary>
    /// Notifies all subscribers of a timer completion
    /// </summary>
    void NotifyTimerCompleted(TimerEventDto timer);

    /// <summary>
    /// Notifies all subscribers of a timer deletion
    /// </summary>
    void NotifyTimerDeleted(int timerId);

    /// <summary>
    /// Subscribes to game marker creation notifications
    /// </summary>
    IChannelSubscription<MarkerEventDto> SubscribeToMarkerCreated();

    /// <summary>
    /// Subscribes to game marker update notifications
    /// </summary>
    IChannelSubscription<MarkerEventDto> SubscribeToMarkerUpdated();

    /// <summary>
    /// Subscribes to game marker deletion notifications
    /// </summary>
    IChannelSubscription<MarkerDeleteEventDto> SubscribeToMarkerDeleted();

    /// <summary>
    /// Notifies all subscribers of a game marker creation
    /// </summary>
    void NotifyMarkerCreated(MarkerEventDto marker);

    /// <summary>
    /// Notifies all subscribers of a game marker update
    /// </summary>
    void NotifyMarkerUpdated(MarkerEventDto marker);

    /// <summary>
    /// Notifies all subscribers of a game marker deletion
    /// </summary>
    void NotifyMarkerDeleted(MarkerDeleteEventDto deleteEvent);
}
