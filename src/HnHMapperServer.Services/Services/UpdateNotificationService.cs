using HnHMapperServer.Core.Models;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using System.Threading.Channels;
using System.Collections.Concurrent;

namespace HnHMapperServer.Services.Services;

public class UpdateNotificationService : IUpdateNotificationService
{
    private readonly ConcurrentBag<Channel<TileData>> _tileChannels = new();
    private readonly ConcurrentBag<Channel<MergeDto>> _mergeChannels = new();
    private readonly ConcurrentBag<Channel<MapInfo>> _mapUpdateChannels = new();
    private readonly ConcurrentBag<Channel<int>> _mapDeleteChannels = new();
    private readonly ConcurrentBag<Channel<MapRevisionDto>> _mapRevisionChannels = new();
    private readonly ConcurrentBag<Channel<CustomMarkerEventDto>> _customMarkerCreatedChannels = new();
    private readonly ConcurrentBag<Channel<CustomMarkerEventDto>> _customMarkerUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<CustomMarkerDeleteEventDto>> _customMarkerDeletedChannels = new();
    private readonly ConcurrentBag<Channel<CharacterDeltaDto>> _characterDeltaChannels = new();
    private readonly ConcurrentBag<Channel<PingEventDto>> _pingCreatedChannels = new();
    private readonly ConcurrentBag<Channel<PingDeleteEventDto>> _pingDeletedChannels = new();
    private readonly ConcurrentBag<Channel<RoadEventDto>> _roadCreatedChannels = new();
    private readonly ConcurrentBag<Channel<RoadEventDto>> _roadUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<RoadDeleteEventDto>> _roadDeletedChannels = new();
    private readonly ConcurrentBag<Channel<OverlayEventDto>> _overlayUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<NotificationEventDto>> _notificationCreatedChannels = new();
    private readonly ConcurrentBag<Channel<NotificationEventDto>> _notificationUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<int>> _notificationReadChannels = new();
    private readonly ConcurrentBag<Channel<int>> _notificationDismissedChannels = new();
    private readonly ConcurrentBag<Channel<TimerEventDto>> _timerCreatedChannels = new();
    private readonly ConcurrentBag<Channel<TimerEventDto>> _timerUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<TimerEventDto>> _timerCompletedChannels = new();
    private readonly ConcurrentBag<Channel<int>> _timerDeletedChannels = new();
    private readonly ConcurrentBag<Channel<MarkerEventDto>> _markerCreatedChannels = new();
    private readonly ConcurrentBag<Channel<MarkerEventDto>> _markerUpdatedChannels = new();
    private readonly ConcurrentBag<Channel<MarkerDeleteEventDto>> _markerDeletedChannels = new();

    public ChannelReader<TileData> SubscribeToTileUpdates()
    {
        var channel = Channel.CreateUnbounded<TileData>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _tileChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<MergeDto> SubscribeToMergeUpdates()
    {
        var channel = Channel.CreateUnbounded<MergeDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _mergeChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<MapInfo> SubscribeToMapUpdates()
    {
        var channel = Channel.CreateUnbounded<MapInfo>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _mapUpdateChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<int> SubscribeToMapDeletes()
    {
        var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _mapDeleteChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<MapRevisionDto> SubscribeToMapRevisions()
    {
        var channel = Channel.CreateUnbounded<MapRevisionDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _mapRevisionChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyTileUpdate(TileData tileData)
    {
        var channelsToRemove = new ConcurrentBag<Channel<TileData>>();

        foreach (var channel in _tileChannels)
        {
            if (!channel.Writer.TryWrite(tileData))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMapMerge(int fromMapId, int toMapId, Coord shift, string tenantId)
    {
        var merge = new MergeDto
        {
            From = fromMapId,
            To = toMapId,
            Shift = shift,
            TenantId = tenantId
        };

        var channelsToRemove = new ConcurrentBag<Channel<MergeDto>>();

        foreach (var channel in _mergeChannels)
        {
            if (!channel.Writer.TryWrite(merge))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMapUpdated(MapInfo mapInfo)
    {
        var channelsToRemove = new ConcurrentBag<Channel<MapInfo>>();

        foreach (var channel in _mapUpdateChannels)
        {
            if (!channel.Writer.TryWrite(mapInfo))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMapDeleted(int mapId)
    {
        var channelsToRemove = new ConcurrentBag<Channel<int>>();

        foreach (var channel in _mapDeleteChannels)
        {
            if (!channel.Writer.TryWrite(mapId))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMapRevision(int mapId, int revision)
    {
        var dto = new MapRevisionDto
        {
            MapId = mapId,
            Revision = revision
        };

        var channelsToRemove = new ConcurrentBag<Channel<MapRevisionDto>>();

        foreach (var channel in _mapRevisionChannels)
        {
            if (!channel.Writer.TryWrite(dto))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public ChannelReader<CustomMarkerEventDto> SubscribeToCustomMarkerCreated()
    {
        var channel = Channel.CreateUnbounded<CustomMarkerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _customMarkerCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<CustomMarkerEventDto> SubscribeToCustomMarkerUpdated()
    {
        var channel = Channel.CreateUnbounded<CustomMarkerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _customMarkerUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<CustomMarkerDeleteEventDto> SubscribeToCustomMarkerDeleted()
    {
        var channel = Channel.CreateUnbounded<CustomMarkerDeleteEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _customMarkerDeletedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyCustomMarkerCreated(CustomMarkerEventDto marker)
    {
        var channelsToRemove = new ConcurrentBag<Channel<CustomMarkerEventDto>>();

        foreach (var channel in _customMarkerCreatedChannels)
        {
            if (!channel.Writer.TryWrite(marker))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyCustomMarkerUpdated(CustomMarkerEventDto marker)
    {
        var channelsToRemove = new ConcurrentBag<Channel<CustomMarkerEventDto>>();

        foreach (var channel in _customMarkerUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(marker))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyCustomMarkerDeleted(CustomMarkerDeleteEventDto deleteEvent)
    {
        var channelsToRemove = new ConcurrentBag<Channel<CustomMarkerDeleteEventDto>>();

        foreach (var channel in _customMarkerDeletedChannels)
        {
            if (!channel.Writer.TryWrite(deleteEvent))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public ChannelReader<CharacterDeltaDto> SubscribeToCharacterDelta()
    {
        // Use unbounded channel to prevent dropping deltas
        // Each SSE connection filters by tenant ID, so cross-tenant deltas
        // will be skipped but not dropped
        var channel = Channel.CreateUnbounded<CharacterDeltaDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _characterDeltaChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyCharacterDelta(CharacterDeltaDto delta)
    {
        var channelsToRemove = new ConcurrentBag<Channel<CharacterDeltaDto>>();

        foreach (var channel in _characterDeltaChannels)
        {
            if (!channel.Writer.TryWrite(delta))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public ChannelReader<PingEventDto> SubscribeToPingCreated()
    {
        var channel = Channel.CreateUnbounded<PingEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _pingCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<PingDeleteEventDto> SubscribeToPingDeleted()
    {
        var channel = Channel.CreateUnbounded<PingDeleteEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _pingDeletedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyPingCreated(PingEventDto ping)
    {
        var channelsToRemove = new ConcurrentBag<Channel<PingEventDto>>();

        foreach (var channel in _pingCreatedChannels)
        {
            if (!channel.Writer.TryWrite(ping))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyPingDeleted(PingDeleteEventDto deleteEvent)
    {
        var channelsToRemove = new ConcurrentBag<Channel<PingDeleteEventDto>>();

        foreach (var channel in _pingDeletedChannels)
        {
            if (!channel.Writer.TryWrite(deleteEvent))
            {
                // Channel is likely closed or full, mark for removal
                channelsToRemove.Add(channel);
            }
        }

        // Clean up dead channels
        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    // Road events
    public ChannelReader<RoadEventDto> SubscribeToRoadCreated()
    {
        var channel = Channel.CreateUnbounded<RoadEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _roadCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<RoadEventDto> SubscribeToRoadUpdated()
    {
        var channel = Channel.CreateUnbounded<RoadEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _roadUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<RoadDeleteEventDto> SubscribeToRoadDeleted()
    {
        var channel = Channel.CreateUnbounded<RoadDeleteEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _roadDeletedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyRoadCreated(RoadEventDto road)
    {
        var channelsToRemove = new ConcurrentBag<Channel<RoadEventDto>>();

        foreach (var channel in _roadCreatedChannels)
        {
            if (!channel.Writer.TryWrite(road))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyRoadUpdated(RoadEventDto road)
    {
        var channelsToRemove = new ConcurrentBag<Channel<RoadEventDto>>();

        foreach (var channel in _roadUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(road))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyRoadDeleted(RoadDeleteEventDto deleteEvent)
    {
        var channelsToRemove = new ConcurrentBag<Channel<RoadDeleteEventDto>>();

        foreach (var channel in _roadDeletedChannels)
        {
            if (!channel.Writer.TryWrite(deleteEvent))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    // Overlay events
    public ChannelReader<OverlayEventDto> SubscribeToOverlayUpdated()
    {
        var channel = Channel.CreateUnbounded<OverlayEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _overlayUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyOverlayUpdated(OverlayEventDto overlay)
    {
        var channelsToRemove = new ConcurrentBag<Channel<OverlayEventDto>>();

        foreach (var channel in _overlayUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(overlay))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    // Notification events.
    // Unlike the map channels, these are subscribed by the always-on /api/notifications/stream
    // on every authenticated page, and subscribers have no unsubscribe — bounded channels keep
    // an abandoned connection from buffering events without limit (REST list remains the
    // source of truth for anything dropped).
    private static Channel<T> CreateNotificationChannel<T>() =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<NotificationEventDto> SubscribeToNotificationCreated()
    {
        var channel = CreateNotificationChannel<NotificationEventDto>();
        _notificationCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<NotificationEventDto> SubscribeToNotificationUpdated()
    {
        var channel = CreateNotificationChannel<NotificationEventDto>();
        _notificationUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<int> SubscribeToNotificationRead()
    {
        var channel = CreateNotificationChannel<int>();
        _notificationReadChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<int> SubscribeToNotificationDismissed()
    {
        var channel = CreateNotificationChannel<int>();
        _notificationDismissedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyNotificationCreated(NotificationEventDto notification)
    {
        var channelsToRemove = new ConcurrentBag<Channel<NotificationEventDto>>();

        foreach (var channel in _notificationCreatedChannels)
        {
            if (!channel.Writer.TryWrite(notification))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyNotificationUpdated(NotificationEventDto notification)
    {
        var channelsToRemove = new ConcurrentBag<Channel<NotificationEventDto>>();

        foreach (var channel in _notificationUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(notification))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyNotificationRead(int notificationId)
    {
        var channelsToRemove = new ConcurrentBag<Channel<int>>();

        foreach (var channel in _notificationReadChannels)
        {
            if (!channel.Writer.TryWrite(notificationId))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyNotificationDismissed(int notificationId)
    {
        var channelsToRemove = new ConcurrentBag<Channel<int>>();

        foreach (var channel in _notificationDismissedChannels)
        {
            if (!channel.Writer.TryWrite(notificationId))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    // Timer events
    public ChannelReader<TimerEventDto> SubscribeToTimerCreated()
    {
        var channel = Channel.CreateUnbounded<TimerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _timerCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<TimerEventDto> SubscribeToTimerUpdated()
    {
        var channel = Channel.CreateUnbounded<TimerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _timerUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<TimerEventDto> SubscribeToTimerCompleted()
    {
        var channel = Channel.CreateUnbounded<TimerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _timerCompletedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<int> SubscribeToTimerDeleted()
    {
        var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _timerDeletedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyTimerCreated(TimerEventDto timer)
    {
        var channelsToRemove = new ConcurrentBag<Channel<TimerEventDto>>();

        foreach (var channel in _timerCreatedChannels)
        {
            if (!channel.Writer.TryWrite(timer))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyTimerUpdated(TimerEventDto timer)
    {
        var channelsToRemove = new ConcurrentBag<Channel<TimerEventDto>>();

        foreach (var channel in _timerUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(timer))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyTimerCompleted(TimerEventDto timer)
    {
        var channelsToRemove = new ConcurrentBag<Channel<TimerEventDto>>();

        foreach (var channel in _timerCompletedChannels)
        {
            if (!channel.Writer.TryWrite(timer))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyTimerDeleted(int timerId)
    {
        var channelsToRemove = new ConcurrentBag<Channel<int>>();

        foreach (var channel in _timerDeletedChannels)
        {
            if (!channel.Writer.TryWrite(timerId))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    // Game marker events
    public ChannelReader<MarkerEventDto> SubscribeToMarkerCreated()
    {
        var channel = Channel.CreateUnbounded<MarkerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _markerCreatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<MarkerEventDto> SubscribeToMarkerUpdated()
    {
        var channel = Channel.CreateUnbounded<MarkerEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _markerUpdatedChannels.Add(channel);
        return channel.Reader;
    }

    public ChannelReader<MarkerDeleteEventDto> SubscribeToMarkerDeleted()
    {
        var channel = Channel.CreateUnbounded<MarkerDeleteEventDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _markerDeletedChannels.Add(channel);
        return channel.Reader;
    }

    public void NotifyMarkerCreated(MarkerEventDto marker)
    {
        var channelsToRemove = new ConcurrentBag<Channel<MarkerEventDto>>();

        foreach (var channel in _markerCreatedChannels)
        {
            if (!channel.Writer.TryWrite(marker))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMarkerUpdated(MarkerEventDto marker)
    {
        var channelsToRemove = new ConcurrentBag<Channel<MarkerEventDto>>();

        foreach (var channel in _markerUpdatedChannels)
        {
            if (!channel.Writer.TryWrite(marker))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }

    public void NotifyMarkerDeleted(MarkerDeleteEventDto deleteEvent)
    {
        var channelsToRemove = new ConcurrentBag<Channel<MarkerDeleteEventDto>>();

        foreach (var channel in _markerDeletedChannels)
        {
            if (!channel.Writer.TryWrite(deleteEvent))
            {
                channelsToRemove.Add(channel);
            }
        }

        foreach (var deadChannel in channelsToRemove)
        {
            deadChannel.Writer.TryComplete();
        }
    }
}
