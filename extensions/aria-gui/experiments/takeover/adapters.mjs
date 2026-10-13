// Injected readFresh/pause/cancel interface. No chrome/browser globals or listeners.
export function adapter(browser, io) {
  if (!['chrome', 'edge', 'firefox'].includes(browser)) throw new Error('unsupported browser');
  return Object.freeze({browser, fixtureOnly: io.fixtureOnly === true,
    readFresh: async id => {
      const raw = await io.readFresh(id);
      if (!raw) return null;
      // Require complete snapshots; absence of a privacy/danger field is not proof of safety.
      // Mozilla's documented DownloadItem has no finalUrl. Missing effective source fails
      // matching below; never invent a no-redirect proof by falling back to raw.url.
      return {id: raw.id, startTime: raw.startTime, source: raw.url, effectiveSource: raw.finalUrl,
        filename: raw.filename, state: raw.state, error: raw.error, paused: raw.paused,
        private: raw.incognito, danger: raw.danger, extensionId: raw.byExtensionId,
        container: browser === 'firefox' ? raw.cookieStoreId : undefined};
    }, pause: id => io.pause(id), cancel: id => io.cancel(id)});
}

export function matches(record, item, grant) {
  return !!item && item.id === record.itemId && item.startTime === record.startTime &&
    item.source === grant.source && item.effectiveSource === grant.source && item.filename === grant.filename;
}

export function safeContext(item) {
  return !!item && item.private === false && item.danger === 'safe' && !item.extensionId &&
    (item.container === undefined || item.container === 'firefox-default') && typeof item.paused === 'boolean';
}

export function cancellation(record, item, grant) {
  // Durable intent proves only our attempt; USER_CANCELED cannot attribute who won a race.
  return record.cancelRequested && matches(record, item, grant) && safeContext(item) &&
    item.state === 'interrupted' && item.error === 'USER_CANCELED';
}

// Grants are issued only by the test-controlled integration, independently of DownloadItem.
// No production grant issuer/consent integration is supplied by this experiment.
export function fixtureGrantRegistry(grants, policy) {
  // A grant ID is immutable in this trusted fixture issuer. Reacquisition uses this registry,
  // not a new assertion copied from browser JSON. Revocation/consent remain live policy checks.
  const issued = structuredClone(grants);
  if (new Set(issued.map(grant => grant.id)).size !== issued.length) throw new Error('duplicate grant identity');
  return Object.freeze({fixtureOnly: true, resolve(record, now) {
    const grant = issued.find(value => value.id === record.grantId);
    if (!grant || !Number.isFinite(Date.parse(now)) || policy.consent !== true || grant.issuer !== policy.issuer || grant.revision !== policy.revision ||
        grant.method !== 'GET' || grant.repeatable !== true || grant.context !== 'public-no-auth-no-cookies' ||
        !Number.isFinite(Date.parse(grant.expiresAt)) || Date.parse(grant.expiresAt) <= Date.parse(now) ||
        !['requestId', 'installationId', 'callerId', 'profileId', 'browser', 'itemId', 'startTime'].every(key => grant[key] === record[key])) return null;
    let url;
    try { url = new URL(grant.source); } catch { return null; }
    if (url.protocol !== 'https:' || url.username || url.password || url.hash || url.origin !== policy.origin ||
        typeof grant.filename !== 'string' || !grant.filename) return null;
    return structuredClone(grant);
  }});
}
