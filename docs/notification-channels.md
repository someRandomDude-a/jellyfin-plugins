# Notification categories, importance and blocked history

## Dependencies and setup

Install standalone Notifications from the [platform catalog](../README.md#installation-and-upgrades)
and restart Jellyfin. Sign in and open the bell's **Settings** for personal
preferences; configure server delivery in **Dashboard → Plugins → Notifications**.
Library capture belongs to Notifications alone; Discord routes require Bridge. Consent actions
require a live, authorized Deletion Queue projection; preferences never grant it.

## Swipe and drag actions

Swipe or drag an inbox notification **right** to reveal **Mark read**,
or **left** to reveal **Hide notification**, then select to confirm (or Cancel).
Touch, mouse and pen input are supported on mobile and desktop.
Swipe surfaces inherit Jellyfin/skin colors and typography, use native palette
fallbacks, and keep low-specificity CSS defaults so skin overrides can win even
when plugin injection loads after the skin stylesheet.
Vertical scrolling and pinch zoom remain available. Hiding affects only the
notification, not its media or history. The options menu remains available for
keyboard users. History and Blocked rows have no swipe actions.

**Deletion consent requests:** a deliberate right-swipe/drag of at least 80px
submits the current user's enabled **Allow** consent and dismisses the notification
only after the native consent endpoint succeeds. The card explains this gesture;
the ordinary Allow/Keep/Undo buttons remain available. Left still reveals Hide,
which does not consent. Failed/stale/unauthorized consent never dismisses the notice.
If consent saves but hiding fails, use Hide to retry without submitting consent
again. Native revision/snapshot, current access, approvals and delays still apply;
the gesture does not approve an administrator review or directly execute removal.

## Personal categories and importance

In the notification bell's **Settings**, every signed-in user gets Android-style
personal category controls: an overall switch, per-plugin switches under **App
notification categories**, and expandable category rows. Open a row to choose
**Alerting**, **Important**, **Silent**, **Low**, or **Use server default**. Changes
save automatically; failed saves restore the confirmed setting. Radio choices
support native keyboard navigation and the surrounding Jellyfin theme.

These settings belong only to the current account. They do not change anyone
else's preferences, notification permissions, server routes, layouts or browser
enrollment policy. Ordinary users cannot discover administrator-only category
definitions or read server-wide message templates. Disabled parent switches still
override child opt-ins. Browser/Android sound, vibration, permission and Do Not
Disturb settings remain controlled by the device; this is not an Android settings
replacement.

Importance inherits from administrator-edited registered plugin defaults unless a more specific personal event or
media-category choice is set. Silent/Low retain visible inbox
rows without popups or Web Push. Important groups notices as Important and supplies a
high-urgency Push delivery hint. Browser/OS channels and sound remain OS-controlled.
Producer severity and authorization are not rewritten; critical alerts remain
enabled and Important in the inbox. Push still requires device enrollment and
permission. Noncritical administrator notices can be muted like other types.
Exact event-type choices win over media-category choices; overlapping enabled
media categories use the highest selected importance, then type-family and
plugin/global defaults apply. Muted media categories cannot elevate other notices.
Turning a category off also silences recent/queued alerts for existing visible
rows; it does not rewrite or delete already delivered history.

## Administrator defaults and Notification Studio

Open **Registered notification types → plugin → notification type**. The catalog
groups by the registering plugin source, then the plugin-reported notification
name and exact type identifier. Plugin summaries show type counts; unknown plugins
retain their reported source ID rather than being assigned to another plugin.
Both levels use keyboard-accessible expandable sections, sorted by name. Closing
a section preserves its unsaved studio draft; navigation does not save or reset
layouts. Each type still has its own explicit Save defaults / Save layout controls.

In **Dashboard → Plugins → Notifications**, every registered type exposes its
plugin-provided default destinations/priority and a simulated example. Administrators
can override those defaults and edit a bounded text/image/font canvas, including
layer order, placement, keyboard movement, alignment and undo/redo. Save defaults
and layout explicitly; stale revisions require reload. Previews send nothing.
Only native authorized thumbnails and plain-text placeholders are supported—no
external image URLs, arbitrary CSS, HTML or scripts. Actions and audiences remain
owned by the producer; studio edits never change permissions. Critical alerts
stay mandatory/high priority, and personal preferences override ordinary defaults.

The designer separates the canvas, layers and native property inspector. Select
a layer without changing the layout, drag to move it, or use its corner handle to
resize it. Arrow keys move the selected layer; Shift uses larger steps. **Ctrl+Z**
undoes and **Ctrl+Y** redoes (no dedicated Undo/Redo buttons). These shortcuts are
scoped to the studio; text inputs keep their own native editing shortcuts.
Duplicate/reorder controls retain the twelve-layer limit. Message/delivery behavior
is grouped separately, and invalid geometry prevents saving. The unsaved indicator
clears only for the acknowledged layout, not newer edits made during a save.

Dragging and resizing snap to nearby layer/card edges and centers, with visible
guide lines. The tolerance is screen-pixel based so it remains useful on narrow
and wide canvases. **Alt** temporarily bypasses snapping; **Snap to layers** toggles
it without changing the design. Under **Align edges & centers**, choose the card
or another layer as the reference, then align Left/Center/Right or Top/Middle/Bottom.
These are geometric layer alignments, separate from text alignment. They are
undoable and stay within the card; an impossible bounded alignment is identified
rather than falsely shown as centered. Guides never appear in saved/live cards.

The default card reserves a left poster column and a separate text column with a
clear gap; posters use Contain so the whole image stays inside that column. New
text layers start in the text column. Live cards place their timestamp below the
canvas, and the canvas image rules override compact-inbox thumbnail sizing.
Both current Notifications and legacy Hub library events use the same native,
permission-checked image path. Saving an opened, untouched designer saves these
displayed defaults rather than a different null-layout fallback. Existing custom
layouts are not silently rewritten: use **Reset studio**, then **Save layout**,
to apply the defaults to one of those layouts.

## Watchlisted and Favorited media

Under **New episodes and movies → Library categories**:

- **Watchlisted media**: the item's native `Likes` flag, or its parent series'
  flag for new episodes. This is the watchlist flag used by Enhanced, not played
  state, watch history or a guessed Seerr requester link.
- **Favorited media**: native `IsFavorite` on the item or accessible parent series.
  The existing `category.library.favourite` preference key is retained, so saved
  Favorites settings are not discarded or renamed in storage.
- **Requested media** remains separate and needs verified requester evidence.

All additions is enabled by default; the special-interest filters are opt-in.
Turn **All additions** off and enable Watchlisted/Favorited to receive only those
categories. Choose their importance separately. A watchlisted series does not
become a Favorite, or vice versa; notifications only read those native flags.
New accessible physical episodes/movies are classified once at intake. Existing
notices are not reclassified or replayed when a watchlist/favorite changes.
Native access is required even for watchlisted/favorited media. Private membership
arrays never enter shared Discord announcements. Update Notifications/Hub/Bridge together for
the new enrichment schema; older pending notices retain their frozen IDs/content.

## Blocked notices and restoration

Published notices suppressed by in-app/category preferences are retained in a
recipient-scoped **Blocked** tab. The administrator configures **Blocked
notifications retained per user** under Notifications delivery settings:
**500** by default, bounded **0–1000**, also subject to the configured history
period (default **90 days**, 7–365). Oldest suppressed presentation rows roll out
at publication/read to enforce the bound. This does not purge the producers'
domain journals or rewrite native Jellyfin history.

Enabling a category restores eligible retained rows to the inbox when it is read.
Restoration rechecks current administrator/media permissions and preferences,
matches the immutable event ID to avoid an upsert race, preserves recipient/row
identity and marks the row retroactive. It does **not** replay old Discord,
webhook, Push or popup deliveries. Blocked and archived views expose no executable
commands; newly restored notices must obtain fresh domain command projections
before an action is possible. Unread counts exclude blocked rows.

This retention applies to published, natively authorized recipients. It does not
invent notices a disabled/uninstalled producer never emitted or retain media for
users who lacked permission at publication. External delivery failures remain in
their existing delivery log/inbox; a Blocked inbox row specifically means in-app
delivery was disabled by preferences. Muted rows migrate with the existing
notification state and original encryption-key handling.

## References

- [Notifications installation and migration](../plugins/notifications/README.md)
- [Library capture and native interests](library-notifications.md) · [Web Push](web-push.md)
- [Deletion Queue safeguards](../plugins/deletion-queue/README.md)
- [Theme acceptance](theme-acceptance.md) · [Current evidence](review-status.md)
