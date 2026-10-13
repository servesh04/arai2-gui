import {durable, observed, reduce} from './state.mjs';
import {matches, safeContext, cancellation} from './adapters.mjs';

// Executable fixture driver, not a transport contract or production cancellation controller.
// Effect dependencies and issuer must explicitly identify as fixtures before mutations.
export class FixtureWorker {
  constructor({storage, app, browser, grants, scope, now, enabled = false, pause = false, checkpoint = async () => {}}) {
    Object.assign(this, {storage, app, browser, grants, scope, now, enabled, pause, checkpoint});
    this.queue = Promise.resolve();
  }
  serialized(action) { const result = this.queue.then(action); this.queue = result.catch(() => {}); return result; }
  active() { return this.enabled && this.browser.fixtureOnly && this.grants.fixtureOnly && this.app.fixtureOnly; }
  scoped(record) { return ['installationId', 'callerId', 'profileId'].every(key => record[key] === this.scope[key]) && record.browser === this.browser.browser; }
  async load() {
    const rows = await this.storage.load();
    if (!Array.isArray(rows) || rows.length > 32) throw new Error('invalid pending ledger');
    const clean = rows.map(durable);
    if (new Set(clean.map(row => row.requestId)).size !== clean.length) throw new Error('duplicate recovery identity');
    return clean;
  }
  async save(record, boundary) {
    const rows = await this.load(), index = rows.findIndex(row => row.requestId === record.requestId);
    if (index < 0) { if (rows.length >= 32) throw new Error('pending capacity'); rows.push(durable(record)); }
    else rows[index] = durable(record);
    await this.storage.save(rows); // Failure stops before the corresponding side effect.
    await this.checkpoint(boundary);
    return record;
  }
  async event(record, event) { return this.save(reduce(record, event, this.now()), `persist:${event.type}`); }
  begin(identity) { return this.serialized(async () => {
    if (!this.active()) return {phase: 'disabled'};
    let record = observed(identity, this.now());
    if (!this.scoped(record)) return {phase: 'refused'};
    const existing = (await this.load()).find(row => row.requestId === record.requestId);
    if (existing) {
      // Same-key callback cannot overwrite another item/profile/grant or rotate acceptance.
      if (!['installationId', 'callerId', 'profileId', 'grantId', 'browser', 'itemId', 'startTime'].every(key => existing[key] === record[key]))
        return {phase: 'identity-conflict'};
      return this.run(existing);
    }
    const grant = this.grants.resolve(record, this.now()), item = await this.browser.readFresh(record.itemId);
    if (!grant || !matches(record, item, grant) || !safeContext(item) || item.state !== 'in_progress' || item.paused) return {phase: 'refused'};
    record = await this.save(record, 'persist:observed');
    record = await this.event(record, {type: 'prepare'});
    return this.run(record);
  }); }
  recover(requestId) { return this.serialized(async () => {
    if (!this.active()) return {phase: 'disabled'};
    const record = (await this.load()).find(row => row.requestId === requestId);
    if (!record || !this.scoped(record)) return {phase: 'scope-refused'};
    return this.run(record);
  }); }
  async run(record) {
    if (record.phase === 'observed') return record; // No side effect occurred; safe to leave/drop deliberately.
    // One bounded query per invocation. Even accepted receipts query the same key, never mutate.
    const status = await this.app.status(record.requestId);
    await this.checkpoint('effect:status');
    if (status.phase === 'accepted') return this.event(record, {type: 'accepted', jobId: status.jobId});
    if (record.phase === 'accepted') return record;
    if (['aborted', 'expired'].includes(status.phase)) return this.event(record, {type: status.phase});
    if (['aborted', 'expired'].includes(record.phase)) return record;
    const grant = this.grants.resolve(record, this.now());
    let item = await this.browser.readFresh(record.itemId);
    // Source rehydration and grant acquisition are required anew on every recovery.
    if (!grant || !matches(record, item, grant) || !safeContext(item)) return this.event(record, {type: 'unknown'});
    if (record.cancelRequested) {
      if (status.phase !== 'prepared' || !cancellation(record, item, grant)) return this.event(record, {type: 'unknown'});
      record = await this.event(record, {type: 'cancelled'});
      return this.commit(record, grant);
    }
    if (item.state !== 'in_progress' || (item.paused && !record.pauseRequested)) return this.event(record, {type: 'unknown'});
    if (status.phase === 'not-found' && record.phase === 'preparing') {
      const reply = await this.app.prepare(record.requestId, {source: item.source, filename: item.filename});
      await this.checkpoint('effect:prepare');
      if (reply.phase !== 'prepared') return this.event(record, {type: ['aborted', 'expired'].includes(reply.phase) ? reply.phase : 'unknown'});
    } else if (status.phase !== 'prepared') return this.event(record, {type: 'unknown'});
    record = await this.event(record, {type: 'prepared'});
    if (this.pause && !record.pauseRequested) {
      item = await this.browser.readFresh(record.itemId);
      if (!this.grants.resolve(record, this.now()) || !matches(record, item, grant) || !safeContext(item) || item.state !== 'in_progress' || item.paused)
        return this.event(record, {type: 'unknown'});
      record = await this.event(record, {type: 'pause'});
      item = await this.browser.readFresh(record.itemId);
      if (!this.grants.resolve(record, this.now()) || !matches(record, item, grant) || !safeContext(item) || item.state !== 'in_progress' || item.paused)
        return this.event(record, {type: 'unknown'});
      await this.browser.pause(record.itemId);
      await this.checkpoint('effect:pause');
    }
    // Fresh snapshot after prepare/pause, and a live grant check immediately before cancel.
    item = await this.browser.readFresh(record.itemId);
    if (!this.grants.resolve(record, this.now()) || !matches(record, item, grant) || !safeContext(item) || item.state !== 'in_progress' ||
        (item.paused && !record.pauseRequested))
      return this.event(record, {type: 'unknown'});
    record = await this.event(record, {type: 'cancel'});
    item = await this.browser.readFresh(record.itemId);
    if (!this.grants.resolve(record, this.now()) || !matches(record, item, grant) || !safeContext(item) || item.state !== 'in_progress' ||
        (item.paused && !record.pauseRequested)) return this.event(record, {type: 'unknown'});
    await this.browser.cancel(record.itemId);
    await this.checkpoint('effect:cancel');
    item = await this.browser.readFresh(record.itemId);
    if (!this.grants.resolve(record, this.now()) || !cancellation(record, item, grant)) return this.event(record, {type: 'unknown'});
    record = await this.event(record, {type: 'cancelled'});
    return this.commit(record, grant);
  }
  async commit(record, grant) {
    // No raw source is stored; it came from the freshly identity-checked item.
    const item = await this.browser.readFresh(record.itemId);
    if (!this.grants.resolve(record, this.now()) || !cancellation(record, item, grant)) return this.event(record, {type: 'unknown'});
    const reply = await this.app.commit(record.requestId, {source: item.source, filename: item.filename});
    await this.checkpoint('effect:commit');
    return this.event(record, reply.phase === 'accepted' ? {type: 'accepted', jobId: reply.jobId} : {type: 'unknown'});
  }
}
