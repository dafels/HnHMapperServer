// NotificationCenter JavaScript Interop
// Owns its own EventSource to the dedicated notification stream, so live
// notifications work on every authenticated page (not just /map), survive SPA
// navigations, and recover from dropped connections with exponential backoff.
// SSE payloads are camelCase (server serializes with JsonNamingPolicy.CamelCase).

window.notificationCenter = {
    dotNetRef: null,
    eventSource: null,
    reconnectAttempts: 0,
    reconnectTimer: null,
    connectCount: 0,

    // Single source of truth for the stream path (dev: Web-side proxy; prod: Caddy @notifsse)
    STREAM_URL: '/api/notifications/stream',

    /**
     * Initialize the notification center
     * @param {DotNetObjectReference} dotNetReference - Reference to Blazor component
     */
    init: function (dotNetReference) {
        // A component remount (e.g. AuthorizeView revalidation) just swaps the ref;
        // the EventSource persists for the life of the browser page.
        this.dotNetRef = dotNetReference;
        console.log('[NotificationCenter] Initialized');
        this.connect();
    },

    /**
     * Create the EventSource and attach listeners. Idempotent: an open or
     * still-connecting stream is left alone. Listeners are attached to each new
     * EventSource instance, so reconnects can never end up listener-less.
     */
    connect: function () {
        if (this.eventSource && this.eventSource.readyState !== EventSource.CLOSED) {
            return;
        }

        const es = new EventSource(this.STREAM_URL, { withCredentials: true });
        this.eventSource = es;

        es.onopen = () => {
            this.reconnectAttempts = 0;
            this.connectCount++;
            console.log('[NotificationCenter] Stream connected');
            if (this.connectCount > 1) {
                // Refetch list + count in Blazor to heal anything missed while down
                this.safeInvoke('OnStreamReconnected');
            }
        };

        es.onerror = () => {
            // CONNECTING means the browser is auto-retrying — let it.
            // CLOSED is permanent (e.g. non-200 after cookie expiry) and needs manual retry.
            if (es.readyState === EventSource.CLOSED) {
                this.scheduleReconnect();
            }
        };

        es.addEventListener('notificationCreated', (e) => {
            try {
                const notification = JSON.parse(e.data);
                console.log('[NotificationCenter] Notification created:', notification.id);

                this.safeInvoke('OnNotificationReceived', notification);
                this.safeInvoke('ShowSnackbarNotification', notification);
                this.showBrowserNotification(notification);
                this.playNotificationSound(notification.type);
            } catch (error) {
                console.error('[NotificationCenter] Error parsing notification:', error);
            }
        });

        // In-place update of a coalesced digest: silent by design — no toast, no
        // sound, no OS notification. The bell entry just refreshes its content.
        es.addEventListener('notificationUpdated', (e) => {
            try {
                const notification = JSON.parse(e.data);
                console.log('[NotificationCenter] Notification updated:', notification.id);

                this.safeInvoke('OnNotificationUpdated', notification);
            } catch (error) {
                console.error('[NotificationCenter] Error parsing notification update:', error);
            }
        });

        es.addEventListener('notificationRead', (e) => {
            try {
                const data = JSON.parse(e.data);
                this.safeInvoke('OnNotificationRead', data.id);
            } catch (error) {
                console.error('[NotificationCenter] Error parsing notification read event:', error);
            }
        });

        es.addEventListener('notificationDismissed', (e) => {
            try {
                const data = JSON.parse(e.data);
                this.safeInvoke('OnNotificationDismissed', data.id);
            } catch (error) {
                console.error('[NotificationCenter] Error parsing notification dismissed event:', error);
            }
        });
    },

    /**
     * Exponential backoff reconnect for permanently-failed streams: 1s, 2s, 4s …
     * capped at 30s, with jitter so tabs don't stampede.
     */
    scheduleReconnect: function () {
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
        }

        const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts), 30000)
            + Math.floor(Math.random() * 400) - 200;
        this.reconnectAttempts++;
        console.warn(`[NotificationCenter] Stream closed, reconnecting in ${Math.max(delay, 250)}ms`);

        this.reconnectTimer = setTimeout(() => {
            this.reconnectTimer = null;
            if (this.eventSource) {
                this.eventSource.close();
                this.eventSource = null;
            }
            this.connect();
        }, Math.max(delay, 250));
    },

    /**
     * Invoke a Blazor method, tolerating circuit transitions (no ref yet, or the
     * circuit died between event arrival and dispatch).
     */
    safeInvoke: function (method, ...args) {
        if (!this.dotNetRef) {
            return;
        }
        try {
            this.dotNetRef.invokeMethodAsync(method, ...args).catch(() => {
                // Circuit is reconnecting or gone; state heals via OnStreamReconnected
                // or the menu-open refetch.
            });
        } catch (error) {
            console.warn('[NotificationCenter] Interop call failed:', error);
        }
    },

    /**
     * Show browser notification (if permission granted)
     * @param {object} notification - Notification data (camelCase)
     */
    showBrowserNotification: function (notification) {
        if (!('Notification' in window)) {
            return;
        }

        if (Notification.permission === 'granted') {
            const options = {
                body: notification.message,
                icon: '/favicon.ico',
                badge: '/favicon.ico',
                tag: `notification-${notification.id}`,
                requireInteraction: notification.priority === 'High',
                silent: false
            };

            const browserNotification = new Notification(notification.title, options);

            // Auto-close after 5 seconds (unless high priority)
            if (notification.priority !== 'High') {
                setTimeout(() => {
                    browserNotification.close();
                }, 5000);
            }

            browserNotification.onclick = () => {
                window.focus();
                browserNotification.close();
            };
        } else if (Notification.permission === 'default') {
            // Request permission
            Notification.requestPermission();
        }
    },

    /**
     * Play notification sound
     * @param {string} notificationType - Type of notification (camelCase payload field)
     */
    playNotificationSound: function (notificationType) {
        try {
            // ping.wav is the only sound asset that ships; the timer mp3 branches
            // below are a pre-existing gap kept for when those assets appear.
            let soundFile = '/sounds/ping.wav';

            if (notificationType === 'TimerPreExpiryWarning') {
                soundFile = '/sounds/timer-warning.mp3';
            } else if (notificationType === 'MarkerTimerExpired' || notificationType === 'StandaloneTimerExpired') {
                soundFile = '/sounds/timer-expired.mp3';
            }

            const audio = new Audio(soundFile);
            audio.volume = 0.5; // 50% volume
            // Cleanup after playback ends to prevent memory leak
            audio.onended = () => { audio.src = ''; };
            audio.play().catch((error) => {
                console.warn('[NotificationCenter] Could not play sound:', error);
                audio.src = ''; // Cleanup on error too
            });
        } catch (error) {
            console.warn('[NotificationCenter] Error playing sound:', error);
        }
    },

    /**
     * Request browser notification permission
     */
    requestNotificationPermission: async function () {
        if (!('Notification' in window)) {
            console.warn('[NotificationCenter] Browser notifications not supported');
            return false;
        }

        if (Notification.permission === 'granted') {
            return true;
        }

        if (Notification.permission !== 'denied') {
            const permission = await Notification.requestPermission();
            return permission === 'granted';
        }

        return false;
    },

    /**
     * Dispose the .NET reference only. The EventSource intentionally stays open:
     * the component can remount within the same page (AuthorizeView revalidation)
     * and re-init just swaps the ref; page unload closes the stream natively.
     */
    dispose: function () {
        console.log('[NotificationCenter] Disposed');
        this.dotNetRef = null;
    }
};
