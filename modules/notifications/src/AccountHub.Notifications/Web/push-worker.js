/* Deliberately narrow scope: never replaces Jellyfin's own web service worker. */
'use strict';
self.addEventListener('push', event => {
    // RFC 8030 payload-free signal. Never render producer-controlled remote data.
    event.waitUntil(self.registration.showNotification('Jellyfin', {
        body: 'You have a new notification. Open Jellyfin to review it.',
        tag: 'jellyfin-notifications', renotify: false
    }));
});
self.addEventListener('notificationclick', event => {
    event.notification.close();
    const base = self.location.pathname.replace(/\/(?:Notifications|AccountHub)\/api\/v1\/notifications\/push\/sw\.js$/, '');
    const url = new URL(base + '/web/index.html', self.location.origin).href;
    event.waitUntil(self.clients.openWindow(url));
});
// Expired subscriptions require an authenticated user to enroll again. No tokens
// are persisted in the worker, and it cannot bypass native authentication/2FA.
