// Browser-free A1 model. This directory is deliberately outside the packaged src tree.
export const phases = Object.freeze(['observed', 'preparing', 'prepared', 'pause-requested',
  'cancel-requested', 'cancelled-unconfirmed', 'accepted', 'aborted', 'expired', 'cannot-confirm']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const stamp = value => typeof value === 'string' && Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value;
const keys = ['version', 'requestId', 'installationId', 'callerId', 'profileId', 'grantId', 'browser',
  'itemId', 'startTime', 'phase', 'createdAt', 'updatedAt', 'pauseRequested', 'cancelRequested', 'receipt'];

// Strict storage boundary: do not spread DownloadItem, URLs, filenames or arbitrary errors.
export function durable(record) {
  if (!record || Object.keys(record).some(key => !keys.includes(key)) || record.version !== 1 ||
      !['requestId', 'installationId', 'callerId', 'profileId', 'grantId'].every(key => uuid.test(record[key])) ||
      !['chrome', 'edge', 'firefox'].includes(record.browser) || !Number.isSafeInteger(record.itemId) || record.itemId < 0 ||
      ![record.startTime, record.createdAt, record.updatedAt].every(stamp) || !phases.includes(record.phase) ||
      typeof record.pauseRequested !== 'boolean' || typeof record.cancelRequested !== 'boolean') throw new Error('invalid recovery record');
  if (record.receipt !== null && (!record.receipt || Object.keys(record.receipt).length !== 1 || !uuid.test(record.receipt.jobId)))
    throw new Error('invalid receipt');
  if ((record.phase === 'accepted') !== (record.receipt !== null) ||
      (['cancel-requested', 'cancelled-unconfirmed'].includes(record.phase) && !record.cancelRequested) ||
      (record.phase === 'pause-requested' && !record.pauseRequested)) throw new Error('inconsistent recovery record');
  return structuredClone(record);
}

export function observed(identity, now) {
  return durable({version: 1, requestId: identity.requestId, installationId: identity.installationId,
    callerId: identity.callerId, profileId: identity.profileId, grantId: identity.grantId,
    browser: identity.browser, itemId: identity.itemId, startTime: identity.startTime,
    phase: 'observed', createdAt: now, updatedAt: now, pauseRequested: false, cancelRequested: false, receipt: null});
}

// Events are internal validated facts, never incoming wire/browser JSON.
export function reduce(record, event, now) {
  durable(record);
  if (record.phase === 'accepted') return durable(record); // Acceptance is monotonic even after opt-out/lost ACK.
  const next = {...record, updatedAt: now};
  switch (event.type) {
    case 'prepare':
      if (record.phase !== 'observed') throw new Error('invalid transition');
      next.phase = 'preparing'; break;
    case 'prepared':
      if (!['preparing', 'prepared', 'pause-requested', 'cannot-confirm'].includes(record.phase) || record.cancelRequested) throw new Error('invalid transition');
      next.phase = 'prepared'; break;
    case 'pause':
      if (record.phase !== 'prepared' || record.pauseRequested) throw new Error('invalid transition');
      next.phase = 'pause-requested'; next.pauseRequested = true; break;
    case 'cancel':
      if (!['prepared', 'pause-requested'].includes(record.phase) || record.cancelRequested) throw new Error('invalid transition');
      next.phase = 'cancel-requested'; next.cancelRequested = true; break;
    case 'cancelled':
      if (!record.cancelRequested || !['cancel-requested', 'cannot-confirm', 'cancelled-unconfirmed'].includes(record.phase)) throw new Error('invalid transition');
      next.phase = 'cancelled-unconfirmed'; break;
    case 'accepted': next.phase = 'accepted'; next.receipt = {jobId: event.jobId}; break;
    case 'aborted': case 'expired': next.phase = event.type; break;
    case 'unknown': next.phase = 'cannot-confirm'; break;
    default: throw new Error('invalid event');
  }
  return durable(next);
}
