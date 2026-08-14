using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;
using System.Threading.Channels;

namespace HnHMapperServer.Services.Interfaces;

public interface IUpdateNotificationService
{
    /// <summary>
    /// Subscribes to tile update notifications
    /// </summary>
    ChannelReader<TileData> SubscribeToTileUpdates();

    /// <summary>
    /// Subscribes to map merge notifications
    /// </summary>
    ChannelReader<MergeDto> SubscribeToMergeUpdates();

    /// <summary>
    /// Subscribes to map metadata update notifications (rename, hidden, priority changes)
    /// </summary>
    ChannelReader<MapInfo> SubscribeToMapUpdates();

    /// <summary>
    /// Subscribes to map deletion notifications
    /// </summary>
    ChannelReader<int> SubscribeToMapDeletes();

    /// <summary>
    /// Subscribes to map revision notifications (for cache busting)
    /// </summary>
    ChannelReader<MapRevisionDto> SubscribeToMapRevisions();

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
    ChannelReader<CustomMarkerEventDto> SubscribeToCustomMarkerCreated();

    /// <summary>
    /// Subscribes to custom marker update notifications
    /// </summary>
    ChannelReader<CustomMarkerEventDto> SubscribeToCustomMarkerUpdated();

    /// <summary>
    /// Subscribes to custom marker deletion notifications
    /// </summary>
    ChannelReader<CustomMarkerDeleteEventDto> SubscribeToCustomMarkerDeleted();

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
    ChannelReader<CharacterDeltaDto> SubscribeToCharacterDelta();

    /// <summary>
    /// Notifies all subscribers of character deltas (incremental changes)
    /// </summary>
    void NotifyCharacterDelta(CharacterDeltaDto delta);

    /// <summary>
    /// Subscribes to ping creation notifications
    /// </summary>
    ChannelReader<PingEventDto> SubscribeToPingCreated();

    /// <summary>
    /// Subscribes to ping deletion notifications
    /// </summary>
    ChannelReader<PingDeleteEventDto> SubscribeToPingDeleted();

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
    ChannelReader<RoadEventDto> SubscribeToRoadCreated();

    /// <summary>
    /// Subscribes to road update notifications
    /// </summary>
    ChannelReader<RoadEventDto> SubscribeToRoadUpdated();

    /// <summary>
    /// Subscribes to road deletion notifications
    /// </summary>
    ChannelReader<RoadDeleteEventDto> SubscribeToRoadDeleted();

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
    ChannelReader<OverlayEventDto> SubscribeToOverlayUpdated();

    /// <summary>
    /// Notifies all subscribers of an overlay update
    /// </summary>
    void NotifyOverlayUpdated(OverlayEventDto overlay);

    /// <summary>
    /// Subscribes to notification creation events
    /// </summary>
    ChannelReader<NotificationEventDto> SubscribeToNotificationCreated();

    /// <summary>
    /// Subscribes to notification in-place update events (e.g. a coalesced digest changed)
    /// </summary>
    ChannelReader<NotificationEventDto> SubscribeToNotificationUpdated();

    /// <summary>
    /// Subscribes to notification read events
    /// </summary>
    ChannelReader<int> SubscribeToNotificationRead();

    /// <summary>
    /// Subscribes to notification dismiss events
    /// </summary>
    ChannelReader<int> SubscribeToNotificationDismissed();

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
    ChannelReader<TimerEventDto> SubscribeToTimerCreated();

    /// <summary>
    /// Subscribes to timer update events
    /// </summary>
    ChannelReader<TimerEventDto> SubscribeToTimerUpdated();

    /// <summary>
    /// Subscribes to timer completion events
    /// </summary>
    ChannelReader<TimerEventDto> SubscribeToTimerCompleted();

    /// <summary>
    /// Subscribes to timer deletion events
    /// </summary>
    ChannelReader<int> SubscribeToTimerDeleted();

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
    ChannelReader<MarkerEventDto> SubscribeToMarkerCreated();

    /// <summary>
    /// Subscribes to game marker update notifications
    /// </summary>
    ChannelReader<MarkerEventDto> SubscribeToMarkerUpdated();

    /// <summary>
    /// Subscribes to game marker deletion notifications
    /// </summary>
    ChannelReader<MarkerDeleteEventDto> SubscribeToMarkerDeleted();

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
