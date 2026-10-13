import assert from 'node:assert/strict';
import {test} from 'node:test';
import {readFile, readdir} from 'node:fs/promises';
import {FixtureWorker} from '../experiments/takeover/fixture-worker.mjs';
import {adapter, cancellation, fixtureGrantRegistry} from '../experiments/takeover/adapters.mjs';
import {observed, reduce, durable, phases} from '../experiments/takeover/state.mjs';

const ids = Array.from({length: 6}, (_, i) => `10000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`);
const time = '2026-10-13T06:00:00.000Z';
const source = 'https://controlled.example.test/synthetic?token=SYNTHETIC_URL_MARKER';
const filename = 'C:\\synthetic\\SYNTHETIC_NAME_MARKER.bin';
function fixture(browser = 'chrome') {
  const identity = {requestId: ids[0], installationId: ids[1], callerId: ids[2], profileId: ids[3],
    grantId: ids[4], browser, itemId: 7, startTime: time};
  let rows = [], clock = time, status = {phase: 'not-found'};
  let item = {id: 7, startTime: time, url: source, finalUrl: source, filename,
    state: 'in_progress', incognito: false, danger: 'safe', paused: false};
  if (browser === 'firefox') item.cookieStoreId = 'firefox-default';
  const grant = {...identity, id: ids[4], source, filename, issuer: 'synthetic-integration', revision: 1,
    method: 'GET', repeatable: true, context: 'public-no-auth-no-cookies', expiresAt: '2026-10-13T06:01:00.000Z'};
  const policy = {issuer: 'synthetic-integration', revision: 1, consent: true, origin: 'https://controlled.example.test'};
  const calls = [], saved = [];
  const storage = {load: async () => structuredClone(rows), save: async value => {
    rows = structuredClone(value); saved.push(JSON.stringify(value));
  }};
  const io = {fixtureOnly: true, readFresh: async () => structuredClone(item),
    pause: async () => { assert.equal(rows[0].phase, 'pause-requested'); calls.push(['pause']); item.paused = true; },
    cancel: async () => { assert.equal(rows[0].phase, 'cancel-requested'); calls.push(['cancel']);
      item.state = 'interrupted'; item.error = 'USER_CANCELED'; }};
  const app = {fixtureOnly: true, status: async key => {calls.push(['status', key]); return structuredClone(status);},
    prepare: async (key, payload) => {calls.push(['prepare', key, payload]); status = {phase: 'prepared'}; return status;},
    commit: async (key, payload) => {calls.push(['commit', key, payload]); status = {phase: 'accepted', jobId: ids[5]}; return status;}};
  let issued;
  const registry = () => issued ??= fixtureGrantRegistry([grant], policy);
  const create = (options = {}) => new FixtureWorker({storage, app, browser: adapter(browser, io),
    grants: registry(), scope: identity, now: () => clock, enabled: true, ...options});
  return {identity, grant, policy, calls, saved, storage, io, app, create,
    rows: () => rows, setRows: value => rows = value, item: () => item, setItem: value => item = value,
    setStatus: value => status = value, setClock: value => clock = value};
}

test('A1 remains disabled by default and refuses live effect dependencies', async () => {
  const f = fixture();
  assert.equal((await f.create({enabled: false}).begin(f.identity)).phase, 'disabled');
  f.io.fixtureOnly = false;
  assert.equal((await f.create().begin(f.identity)).phase, 'disabled');
  assert.equal(f.calls.length, 0); assert.equal(f.saved.length, 0);
});

for (const browser of ['chrome', 'edge', 'firefox']) {
  test(`${browser} enriched synthetic fixture: durable pause/cancel, fresh cancellation, one stable receipt`, async () => {
    const f = fixture(browser), worker = f.create({pause: true});
    assert.equal((await worker.begin(f.identity)).phase, 'accepted');
    assert.ok(f.saved.some(value => value.includes('pause-requested')));
    assert.ok(f.saved.some(value => value.includes('cancel-requested')));
    assert.ok(f.saved.every(value => !value.includes(source) && !value.includes('SYNTHETIC_') && !value.includes('https:') && !value.includes(filename)));
    assert.equal((await f.create().recover(f.identity.requestId)).receipt.jobId, ids[5]);
    assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 1);
    assert.equal(f.calls.filter(([kind]) => kind === 'prepare').length, 1);
    assert.ok(f.calls.filter(([, key]) => key).every(([, key]) => key === f.identity.requestId));
  });
}

test('missing effective-source proof refuses every family, including documented Firefox item shape', async () => {
  for (const browser of ['chrome', 'edge', 'firefox']) {
    const f = fixture(browser); delete f.item().finalUrl;
    assert.equal((await f.create().begin(f.identity)).phase, 'refused');
    assert.equal(f.calls.length, 0); assert.equal(f.saved.length, 0);
  }
});

for (const boundary of ['persist:observed', 'persist:prepare', 'effect:status', 'effect:prepare',
  'persist:prepared', 'persist:pause', 'effect:pause', 'persist:cancel', 'effect:cancel',
  'persist:cancelled', 'effect:commit', 'persist:accepted']) {
  test(`worker loss at ${boundary}: same-key recovery never repeats cancellation or accepted commit`, async () => {
    const f = fixture(); let died = false;
    await assert.rejects(f.create({pause: true, checkpoint: async point => {
      if (point === boundary && !died) { died = true; throw new Error('synthetic worker termination'); }
    }}).begin(f.identity), /synthetic worker termination/);
    const outcome = await f.create({pause: true}).recover(f.identity.requestId);
    assert.ok(['observed', 'accepted', 'cannot-confirm'].includes(outcome.phase));
    if (boundary === 'persist:cancel') assert.equal(outcome.phase, 'cannot-confirm'); // Intent alone is not cancellation evidence.
    if (boundary === 'persist:observed') assert.equal(f.calls.length, 0);
    assert.ok(f.calls.filter(([kind]) => kind === 'pause').length <= 1);
    assert.ok(f.calls.filter(([kind]) => kind === 'cancel').length <= 1);
    assert.ok(f.calls.filter(([kind]) => kind === 'commit').length <= 1);
    assert.ok(f.calls.filter(([, key]) => key).every(([, key]) => key === f.identity.requestId));
  });
}

test('persistence failure immediately before either browser effect prevents that effect', async () => {
  for (const phase of ['pause-requested', 'cancel-requested']) {
    const f = fixture(), save = f.storage.save;
    f.storage.save = async rows => { if (rows[0].phase === phase) throw new Error('synthetic storage failure'); await save(rows); };
    await assert.rejects(f.create({pause: true}).begin(f.identity), /storage failure/);
    assert.equal(f.calls.filter(([kind]) => kind === (phase === 'pause-requested' ? 'pause' : 'cancel')).length, 0);
  }
});

const unsafe = [
  ['private', {incognito: true}], ['privacy unknown', {incognito: undefined}],
  ['dangerous', {danger: 'dangerous'}], ['danger accepted', {danger: 'accepted'}], ['danger unknown', {danger: undefined}],
  ['extension initiated', {byExtensionId: 'other-extension'}], ['complete', {state: 'complete'}],
  ['unrelated interruption', {state: 'interrupted', error: 'NETWORK_FAILED'}], ['already paused', {paused: true}],
  ['changed source', {url: source + '-changed'}], ['redirected', {finalUrl: source + '-redirect'}],
  ['changed name', {filename: filename + '-changed'}], ['changed start', {startTime: '2026-10-13T05:00:00.000Z'}],
  ['changed item', {id: 8}], ['disappeared', null]
];
for (const [name, patch] of unsafe) test(`unsafe observation ${name} refuses before persistence or effects`, async () => {
  const f = fixture(); f.setItem(patch === null ? null : {...f.item(), ...patch});
  assert.equal((await f.create({pause: true}).begin(f.identity)).phase, 'refused');
  assert.equal(f.calls.length, 0); assert.equal(f.saved.length, 0);
});

test('Firefox container is unsupported; caller GET/public flags do not establish a grant', async () => {
  const f = fixture('firefox'); f.item().cookieStoreId = 'firefox-container-1';
  assert.equal((await f.create().begin({...f.identity, method: 'GET', public: true})).phase, 'refused');
  const g = fixture(); g.grant.context = 'authenticated';
  assert.equal((await g.create().begin({...g.identity, method: 'GET', public: true})).phase, 'refused');
});

test('grant policy issuer/revision, consent, expiry, request and every scope binding are mandatory', async () => {
  for (const patch of [{issuer: 'untrusted'}, {revision: 2}, {method: 'POST'}, {repeatable: false},
    {context: 'partitioned'}, {expiresAt: time}, {requestId: ids[5]}, {installationId: ids[5]},
    {callerId: ids[5]}, {profileId: ids[5]}, {browser: 'edge'}, {itemId: 8}, {startTime: '2026-10-13T05:00:00.000Z'}]) {
    const f = fixture(); Object.assign(f.grant, patch);
    assert.equal((await f.create({pause: true}).begin(f.identity)).phase, 'refused'); assert.equal(f.calls.length, 0);
  }
  const f = fixture(); f.policy.consent = false;
  assert.equal((await f.create().begin(f.identity)).phase, 'refused');
});

for (const [name, patch] of unsafe) test(`fresh pre-effect change ${name} cannot-confirm without pause/cancellation`, async () => {
  for (const pause of [false, true]) {
    const f = fixture(); const prepare = f.app.prepare;
    f.app.prepare = async (...args) => {const reply = await prepare(...args); f.setItem(patch === null ? null : {...f.item(), ...patch}); return reply;};
    assert.equal((await f.create({pause}).begin(f.identity)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => ['pause', 'cancel', 'commit'].includes(kind)).length, 0);
  }
});

test('grant revocation/expiry or dangerous change while persisting intent stops the corresponding effect', async () => {
  for (const phase of ['pause-requested', 'cancel-requested']) for (const change of ['revoked', 'expired', 'dangerous']) {
    const f = fixture(), save = f.storage.save;
    f.storage.save = async rows => {
      await save(rows);
      if (rows[0].phase === phase) {
        if (change === 'revoked') f.policy.consent = false;
        if (change === 'expired') f.setClock('2026-10-13T06:02:00.000Z');
        if (change === 'dangerous') f.item().danger = 'dangerous';
      }
    };
    assert.equal((await f.create({pause: true}).begin(f.identity)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => kind === (phase === 'pause-requested' ? 'pause' : 'cancel')).length, 0);
  }
});

test('fresh item after cancellation-observation persistence is required before commit', async () => {
  for (const change of ['missing', 'complete', 'source', 'revoked']) {
    const f = fixture(), save = f.storage.save;
    f.storage.save = async rows => {
      await save(rows);
      if (rows[0].phase === 'cancelled-unconfirmed') {
        if (change === 'missing') f.setItem(null);
        if (change === 'complete') f.item().state = 'complete';
        if (change === 'source') f.item().url += '-changed';
        if (change === 'revoked') f.policy.consent = false;
      }
    };
    assert.equal((await f.create().begin(f.identity)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 0);
  }
});

test('cancel fulfillment with complete, absent or unrelated interruption cannot authorize commit', async () => {
  for (const patch of [{state: 'complete'}, null, {state: 'interrupted', error: 'NETWORK_FAILED'}, {state: 'in_progress'}]) {
    const f = fixture(); f.io.cancel = async () => {f.calls.push(['cancel']); f.setItem(patch === null ? null : {...f.item(), ...patch});};
    assert.equal((await f.create().begin(f.identity)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 0);
    assert.equal((await f.create().recover(f.identity.requestId)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => kind === 'cancel').length, 1);
  }
});

test('restart source/grant unavailable or changed retains recovery guidance and never recreates transfer', async () => {
  for (const change of ['missing', 'source', 'name', 'expired', 'revoked']) {
    const f = fixture(); await assert.rejects(f.create({checkpoint: async point => {if (point === 'effect:cancel') throw new Error('death');}}).begin(f.identity));
    if (change === 'missing') f.setItem(null);
    if (change === 'source') f.item().url += '-changed';
    if (change === 'name') f.item().filename += '-changed';
    if (change === 'expired') f.setClock('2026-10-13T06:02:00.000Z');
    if (change === 'revoked') f.policy.consent = false;
    assert.equal((await f.create().recover(f.identity.requestId)).phase, 'cannot-confirm');
    assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 0);
    assert.equal(f.calls.filter(([kind]) => kind === 'prepare').length, 1);
  }
});

test('trusted issuer keeps exact resource immutable under grant ID across worker restart', async () => {
  const f = fixture();
  await assert.rejects(f.create({checkpoint: async point => {if (point === 'effect:cancel') throw new Error('death');}}).begin(f.identity));
  f.grant.source += '-changed'; f.item().url = f.grant.source; f.item().finalUrl = f.grant.source;
  assert.equal((await f.create().recover(f.identity.requestId)).phase, 'cannot-confirm');
  assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 0);
});

test('worker restart losing volatile issuer does not reconstruct approval from stored grant ID', async () => {
  const f = fixture();
  await assert.rejects(f.create({checkpoint: async point => {if (point === 'effect:cancel') throw new Error('death');}}).begin(f.identity));
  const grants = fixtureGrantRegistry([], f.policy);
  assert.equal((await f.create({grants}).recover(f.identity.requestId)).phase, 'cannot-confirm');
  assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 0);
});

test('lost prepare ACK queries same key; lost commit ACK discovers acceptance despite missing browser item/grant', async () => {
  for (const action of ['prepare', 'commit']) {
    const f = fixture(), original = f.app[action];
    f.app[action] = async (...args) => {await original(...args); throw new Error('lost ACK');};
    await assert.rejects(f.create().begin(f.identity), /lost ACK/);
    f.app[action] = original;
    if (action === 'commit') {f.setItem(null); f.policy.consent = false;}
    assert.equal((await f.create().recover(f.identity.requestId)).phase, 'accepted');
    assert.equal(f.calls.filter(([kind]) => kind === action).length, 1);
  }
});

test('same-key callbacks serialize; cross-item identity and cross-profile recovery cannot mutate', async () => {
  const f = fixture(), worker = f.create();
  await Promise.all([worker.begin(f.identity), worker.begin(f.identity)]);
  assert.equal(f.calls.filter(([kind]) => kind === 'commit').length, 1);
  assert.equal((await worker.begin({...f.identity, itemId: 8})).phase, 'identity-conflict');
  const before = f.calls.length;
  assert.equal((await f.create({scope: {...f.identity, profileId: ids[5]}}).recover(f.identity.requestId)).phase, 'scope-refused');
  assert.equal(f.calls.length, before);
});

test('expiry/abort/unavailable preserve history, no auto-resume/restart; one bounded status query', async () => {
  for (const phase of ['expired', 'aborted', 'unavailable', 'unknown']) {
    const f = fixture(); await assert.rejects(f.create({checkpoint: async point => {if (point === 'effect:cancel') throw new Error('death');}}).begin(f.identity));
    f.setStatus({phase}); const before = f.calls.length;
    const result = await f.create().recover(f.identity.requestId);
    assert.equal(result.phase, ['expired', 'aborted'].includes(phase) ? phase : 'cannot-confirm');
    assert.deepEqual(f.calls.slice(before), [['status', f.identity.requestId]]);
    assert.equal(f.rows().length, 1);
  }
});

test('pure reducer checks transitions and sanitized storage rejects URL smuggling or inconsistent flags', () => {
  const record = observed(fixture().identity, time);
  assert.throws(() => reduce(record, {type: 'cancelled'}, time));
  assert.throws(() => reduce(record, {type: 'pause'}, time));
  for (const value of [{...record, url: source}, {...record, profileId: source}, {...record, phase: 'cancel-requested'},
    {...record, receipt: {jobId: ids[5], source}}, {...record, phase: 'accepted'}]) assert.throws(() => durable(value));
  let next = reduce(record, {type: 'prepare'}, time);
  next = reduce(next, {type: 'prepared'}, time);
  next = reduce(next, {type: 'cancel'}, time);
  assert.equal(cancellation(next, null, {}), false);
  next = reduce(next, {type: 'cancelled'}, time);
  next = reduce(next, {type: 'accepted', jobId: ids[5]}, time);
  assert.deepEqual(reduce(next, {type: 'expired'}, time), next);
  assert.ok(phases.includes('cannot-confirm'));
});

test('pending ledger capacity and corrupt version stop before effects', async () => {
  const f = fixture(), row = observed(f.identity, time);
  f.setRows([{...row, version: 2}]); await assert.rejects(f.create().begin(f.identity), /invalid recovery/);
  f.setRows(Array.from({length: 32}, (_, i) => ({...row, requestId: `20000000-0000-4000-8000-${String(i).padStart(12, '0')}`})));
  await assert.rejects(f.create().begin(f.identity), /pending capacity/); assert.equal(f.calls.length, 0);
});

test('packaging boundary: experiments are absent from every packaged browser tree and src imports', async () => {
  for (const browser of ['chrome', 'edge', 'firefox']) {
    const files = await readdir(new URL(`../dist/${browser}/`, import.meta.url));
    assert.ok(!files.includes('experiments') && !files.includes('fixture-worker.mjs'));
    const manifest = JSON.parse(await readFile(new URL(`../dist/${browser}/manifest.json`, import.meta.url), 'utf8'));
    assert.ok(!manifest.permissions.includes('downloads'));
  }
  for (const name of await readdir(new URL('../src/', import.meta.url))) {
    if (name.endsWith('.mjs') || name.endsWith('.js')) assert.ok(!(await readFile(new URL(`../src/${name}`, import.meta.url), 'utf8')).includes('experiments/takeover'));
  }
});
