# Optional browser Web Push

1. In **Notifications → Web Push**, an administrator enables the feature and
   supplies a `mailto:` administrator email or HTTPS contact URL for VAPID.
2. On HTTPS (or localhost), open the notification bell's settings and choose
   **Enable browser notifications**. Only this explicit action prompts for
   browser permission. Each browser/device must enroll separately.
3. **Disable on this browser** removes the authenticated server subscription,
   unsubscribes the browser and unregisters only this plugin's narrow worker.
   Administrator disabling removes all saved subscriptions and pending pushes;
   users must explicitly enroll again if enabled later.

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

Tests verify VAPID signatures/audience/lifetime, enrollment authorization and
step-up, endpoint rejection, protected/recipient-bound storage and migration,
revocation, bounded retries, worker privacy/BaseUrl routing and explicit opt-in
and unsubscribe flows. These fixtures do **not** certify physical-device delivery
through every browser vendor's public push infrastructure.
