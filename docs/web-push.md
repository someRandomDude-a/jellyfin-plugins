# Optional browser Web Push

## Dependencies and setup

Install standalone Notifications using the [installation guide](../README.md#installation-and-upgrades)
and restart Jellyfin. Use an authenticated, Push-capable browser on real HTTPS
(or supported localhost). No Discord bot or Account Hub installation is needed
for standalone Push. Browser/OS permissions and delivery support remain external
requirements; the plugin cannot override Do Not Disturb or a denied site permission.

## Enable and enroll

1. In **Notifications → Browser notifications**, an administrator turns on
   **Allow browser notifications**. The toggle saves automatically. No contact,
   external URL, keys or other advanced settings are required. The adjacent
   **Enable on devices by default** toggle controls the default for devices that
   have not opted out; both switches save automatically. The device default is
   initially off for compatibility and can be enabled by an administrator.
2. On HTTPS (or localhost), open the notification bell's settings and choose
   **Notifications on this device**. With the global default enabled,
   opening the notification bell asks for permission if it has not been granted;
   already-granted permission can enroll automatically without prompting.
   Browser permission is never bypassed or prompted on page load.
3. Turning off **Notifications on this device** removes the authenticated server subscription,
   unsubscribes the browser and unregisters only this plugin's narrow worker.
   Administrator disabling removes all saved subscriptions and pending pushes;
    devices must enroll again if enabled later, according to the configured
    default, saved device choice and current browser permission.

## Device preferences and permissions

Device choices are local to the browser, server and signed-in account. An explicit
opt-out persists across reloads and is not overridden by the global default even
if browser permission is still granted. Declining or dismissing the prompt also
records an opt-out, preventing repeated requests. Users can change the switch
later. If local preference storage is unavailable, automatic enrollment defaults
off rather than forgetting an opt-out; explicit switching still works for the
current page. Clearing site storage clears these local preferences.

Blocked site permission is shown separately from the plugin switch, with
instructions to allow notifications in browser site settings and then turn the
switch on again. Permission status refreshes when returning to the browser or
reopening settings. The site cannot unblock itself or open privileged browser
settings automatically.

## URLs, delivery and privacy

The server uses Jellyfin's native smart/public server URL selection (including
configured published-address overrides), falling back to the host used by the
client. Only the origin is used as the VAPID server identifier: paths, queries and
credentials are never forwarded. For local HTTP setup, its HTTPS-form host is an
identifier only, not a callback, redirect, or a promise that HTTPS is configured.
Device enrollment still needs a real HTTPS connection (or browser-supported
localhost). Native reverse-proxy handling is reused; the plugin does not trust
arbitrary forwarding headers. The service worker and notification links stay on
the browser's own origin and preserve Jellyfin's BaseUrl.

If notifications are blocked, allow them in the browser's site settings. On
iPhone/iPad, add Jellyfin to the Home Screen and open that app first. Unsupported
or insecure browsers now show these requirements rather than silently hiding
notification setup. The device panel shows whether this device is enrolled.

The browser/OS may deliver while Jellyfin is closed, but timing and delivery are
not guaranteed. iOS/iPadOS may require an installed home-screen web app. Current
supported services are Mozilla Push, Google FCM and Apple Web Push. Custom or
private-network push endpoints are rejected, redirects/proxies are disabled and
DNS connections are pinned to public addresses.

Pushes use RFC 8030 payload-free signals with RFC 8292 P-256 VAPID signatures.
They display fixed generic text and open the native Jellyfin web entry under its
BaseUrl. No media titles, user identities, action capabilities or native session
tokens are sent to push services or stored in the service worker. Viewing content
or executing actions still requires native Jellyfin authorization and any 2FA.

Subscription endpoints/keys and the VAPID private key are protected with the
existing persistent Notifications key ring in additive plugin-owned tables.
Device enrollment is native-authenticated and recipient-bound; sending rechecks
enabled-user/admin status, media access, inbox archival and current preferences.
Per-user/global device, queue, work/time and retry limits apply; HTTP 404/410
removes dead subscriptions. Logout/account changes unsubscribe this browser;
expired subscriptions require fresh authenticated enrollment rather than storing
session tokens for background renewal.

The worker has its own `/Notifications/api/v1/notifications/push/` scope; it does
not replace/unregister Jellyfin's native service worker or control native pages.
Pushes never grant deletion consent or approval. Live sockets and inbox polling
continue independently if Push is disabled or unsupported.

## Verification and limitations

Tests verify VAPID signatures/audience/lifetime, enrollment authorization and
step-up, endpoint rejection, protected/recipient-bound storage and migration,
revocation, bounded retries, worker privacy/BaseUrl routing and explicit opt-in
and unsubscribe flows. These fixtures do **not** certify physical-device delivery
through every browser vendor's public push infrastructure.

## References

- [Notifications installation and migration](../plugins/notifications/README.md)
- [Categories, importance and muting](notification-channels.md)
- [Testing and current evidence](review-status.md)
- [RFC 8030: Web Push](https://www.rfc-editor.org/rfc/rfc8030) · [RFC 8292: VAPID](https://www.rfc-editor.org/rfc/rfc8292)
