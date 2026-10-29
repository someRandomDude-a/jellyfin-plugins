import test from 'node:test';
import assert from 'node:assert/strict';
import { traceRoutes, routeInventory } from './audit-routes.mjs';
import { dependencyPaths } from './check-dependency-freshness.mjs';

test('route trace retains action overrides, absolute routes and combined verbs without claiming authorization', () => {
    const text = '[ApiController, Authorize, Route("Owner")] public sealed class ExampleController : ControllerBase {\n[AllowAnonymous, HttpPost("redeem"), HttpPut("/absolute")] public Task<IActionResult> Redeem() => CheckAnonymous();\n}';
    const rows = traceRoutes('example.cs', text);
    assert.equal(rows.length, 2); assert.equal(rows[0].route, '/Owner/redeem'); assert.equal(rows[1].route, '/absolute');
    assert(rows[0].actionAttributes.includes('AllowAnonymous')); assert(rows[0].guardTraceCandidates.includes('CheckAnonymous'));
    assert(rows[0].verification.includes('do not certify'));
    const forwarded = traceRoutes('forward.cs', '[Authorize] public sealed class ForwardController : ControllerBase {\n[Route("first/{**tail}")]\n[Route("second/{**tail}")] public Task Forward() => adapter.ExecuteAsync();\n}');
    assert.equal(forwarded.length, 2); assert.equal(forwarded[0].method, 'ANY');
    assert.equal(forwarded[1].route, '/second/{**tail}');
});
test('all six plugins and shared minimal handlers are traced; inactive lifecycle controllers are excluded', async () => {
    const result = await routeInventory(await dependencyPaths());
    for (const plugin of ['account-hub', 'notifications', 'discord-bridge', 'private-syncplay', 'deletion-queue', 'automatic-deletion-requests'])
        assert(result.routes.some(row => row.path.startsWith('plugins/' + plugin + '/src/')), plugin);
    assert(result.routes.some(row => row.kind === 'minimal-handler'));
    assert(!result.routes.some(row => row.path.startsWith('modules/user-management/')));
    assert(result.excluded.length > 0);
    assert(result.routes.some(row => row.route === '/DiscordBridge/admin/v1/settings' && row.method === 'PUT'));
});
