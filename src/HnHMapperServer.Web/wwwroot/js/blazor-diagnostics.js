// Blazor Circuit Diagnostics
// Captures and displays detailed error information in the blazor-error-ui banner

(function () {
    'use strict';

    console.log('[Blazor Diagnostics] Initializing error capture...');

    let lastError = null;
    let reconnectionAttempts = 0;
    let circuitState = 'initializing';

    // Update error UI with diagnostic information
    function showErrorDetails(errorType, message, details) {
        const errorUI = document.getElementById('blazor-error-ui');
        const errorMessage = document.getElementById('blazor-error-message');
        const errorDetails = document.getElementById('blazor-error-details');
        const errorTime = document.getElementById('blazor-error-time');

        if (!errorUI || !errorMessage) {
            console.error('[Blazor Diagnostics] Error UI elements not found');
            return;
        }

        const timestamp = new Date().toISOString();
        lastError = { errorType, message, details, timestamp };

        // Update message
        errorMessage.textContent = `${errorType}: ${message}`;

        // Update details (if provided)
        if (details && errorDetails) {
            errorDetails.textContent = details;
            errorDetails.style.display = 'block';
        }

        // Update timestamp
        if (errorTime) {
            errorTime.textContent = `Time: ${timestamp} | Circuit State: ${circuitState} | Reconnect Attempts: ${reconnectionAttempts}`;
        }

        // Show the error UI
        errorUI.style.display = 'block';

        // Log to console for debugging
        console.error('[Blazor Diagnostics] Error captured:', {
            errorType,
            message,
            details,
            timestamp,
            circuitState,
            reconnectionAttempts
        });
    }

    // Capture unhandled JavaScript errors
    window.addEventListener('error', function (event) {
        if (event.filename && event.filename.includes('_framework/blazor')) {
            showErrorDetails(
                'JavaScript Error',
                event.message,
                `File: ${event.filename}\nLine: ${event.lineno}\nColumn: ${event.colno}\n\n${event.error?.stack || 'No stack trace'}`
            );
        }
    }, true);

    // Capture unhandled promise rejections - logged and kept for getBlazorDiagnostics(),
    // but deliberately NOT shown in the banner. A rejected JS->.NET call does not break
    // the circuit; the page keeps working. The recurring example is MudBlazor's throttled
    // 'OnSizeChanged' resize callback firing against an object reference that died with a
    // paused circuit or a disposed component - with DetailedErrors off, every such server
    // refusal collapses into the same generic "There was an exception invoking '...'"
    // text. Painting "Blazor Circuit Error" for those made real failures indistinguishable.
    // Circuit-level failures still show the banner: the start/reconnect hooks below, plus
    // Blazor's own fatal-error display of #blazor-error-ui.
    window.addEventListener('unhandledrejection', function (event) {
        const reason = event.reason;
        lastError = {
            errorType: 'Unhandled Promise Rejection',
            message: reason?.message || String(reason),
            details: reason?.stack || JSON.stringify(reason, null, 2),
            timestamp: new Date().toISOString()
        };
        console.error('[Blazor Diagnostics] Unhandled promise rejection (banner suppressed):', {
            reason,
            circuitState,
            reconnectionAttempts
        });
    });

    // Hook into Blazor's reconnection events
    if (window.Blazor) {
        setupBlazorHooks(window.Blazor);
    } else {
        // Wait for Blazor to load
        const originalStart = window.Blazor?.start;
        Object.defineProperty(window, 'Blazor', {
            configurable: true,
            get: function () {
                return this._blazor;
            },
            set: function (value) {
                this._blazor = value;
                if (value) {
                    console.log('[Blazor Diagnostics] Blazor detected, setting up hooks...');
                    setupBlazorHooks(value);
                }
            }
        });
    }

    function setupBlazorHooks(blazor) {
        // Monitor Blazor start
        const originalStart = blazor.start;
        blazor.start = function () {
            console.log('[Blazor Diagnostics] Blazor starting...');
            circuitState = 'starting';

            const result = originalStart.apply(this, arguments);

            // Hook reconnection handlers after start
            if (result && result.then) {
                result.then(() => {
                    console.log('[Blazor Diagnostics] Blazor started successfully');
                    circuitState = 'connected';
                    setupReconnectionHandlers();
                }).catch(err => {
                    console.error('[Blazor Diagnostics] Blazor start failed:', err);
                    circuitState = 'failed';
                    showErrorDetails(
                        'Blazor Start Failed',
                        err.message || String(err),
                        err.stack || 'No stack trace'
                    );
                });
            }

            return result;
        };
    }

    function setupReconnectionHandlers() {
        // Hook into default reconnection handler
        if (window.Blazor && window.Blazor.defaultReconnectionHandler) {
            const handler = window.Blazor.defaultReconnectionHandler;

            // Override onConnectionDown
            const originalOnConnectionDown = handler.onConnectionDown;
            handler.onConnectionDown = function (options, error) {
                console.warn('[Blazor Diagnostics] Connection down:', error);
                circuitState = 'disconnected';
                reconnectionAttempts = 0;

                // Only show error UI if reconnection fails, not on initial disconnect
                // (Blazor has its own reconnection UI)

                if (originalOnConnectionDown) {
                    return originalOnConnectionDown.call(this, options, error);
                }
            };

            // Override onConnectionUp
            const originalOnConnectionUp = handler.onConnectionUp;
            handler.onConnectionUp = function () {
                console.log('[Blazor Diagnostics] Connection restored');
                circuitState = 'connected';
                reconnectionAttempts = 0;

                if (originalOnConnectionUp) {
                    return originalOnConnectionUp.call(this);
                }
            };

            // Override onRetry
            const originalOnRetry = handler.onRetry;
            handler.onRetry = function (retryCount) {
                console.log('[Blazor Diagnostics] Reconnection attempt:', retryCount);
                circuitState = 'reconnecting';
                reconnectionAttempts = retryCount;

                if (originalOnRetry) {
                    return originalOnRetry.call(this, retryCount);
                }
            };

            // Override onReconnectFailed
            const originalOnReconnectFailed = handler.onReconnectFailed;
            handler.onReconnectFailed = function (error) {
                console.error('[Blazor Diagnostics] Reconnection failed after all attempts:', error);
                circuitState = 'failed';

                showErrorDetails(
                    'Reconnection Failed',
                    'Circuit could not reconnect after multiple attempts',
                    `Error: ${error?.message || String(error)}\n\nThe SignalR connection was lost and could not be restored.\n\nPossible causes:\n- Network connectivity issues\n- Server restart or deployment\n- Circuit timeout (3 minutes)\n- Rate limiting or server overload\n\nPlease reload the page to restart the circuit.`
                );

                if (originalOnReconnectFailed) {
                    return originalOnReconnectFailed.call(this, error);
                }
            };

            console.log('[Blazor Diagnostics] Reconnection handlers installed');
        }
    }

    // Expose diagnostic info to console
    window.getBlazorDiagnostics = function () {
        return {
            lastError,
            circuitState,
            reconnectionAttempts,
            timestamp: new Date().toISOString()
        };
    };

    console.log('[Blazor Diagnostics] Ready. Type getBlazorDiagnostics() in console for current state.');
})();
