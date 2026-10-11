// Dependency-free compatibility tooling. No browser, child process, registry, network or credentials.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const cases = JSON.parse(await readFile(new URL('./fixtures/v1.json', import.meta.url), 'utf8'));
const schema = JSON.parse(await readFile(new URL('../../src/AriaGui.Browser.Contracts/schema/v1.schema.json', import.meta.url), 'utf8'));
const encoder = new TextEncoder();
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const reasons = ['unsupported_protocol', 'invalid_request', 'unsupported_url', 'invalid_filename', 'destination_conflict', 'destination_unavailable', 'integration_disabled', 'not_ready', 'shutting_down', 'busy', 'storage_unavailable', 'request_id_conflict'];
const capabilities = ['addPublicGet', 'getRequestStatus'];
const invalid = () => { throw new Error('Invalid contract'); };
const fields = (value, required, optional = []) => {
  if (!value || typeof value !== 'object' || Array.isArray(value) || required.some(key => !Object.hasOwn(value, key)) || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key))) invalid();
};
const id = value => { if (typeof value !== 'string' || !idPattern.test(value) || value === '00000000-0000-0000-0000-000000000000') invalid(); };
const version = value => { if (typeof value !== 'string' || value.length > 64 || !/^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$/.test(value)) invalid(); };
const integer = (value, min, max) => { if (!Number.isInteger(value) || value < min || value > max) invalid(); };

// JSON.parse alone silently accepts duplicate keys. Walk the already syntactically validated text,
// tracking decoded property names so escaped duplicates are rejected too.
function uniqueAndBounded(text) {
  let offset = 0;
  const whitespace = () => { while (/\s/.test(text[offset] ?? '') && offset < text.length) offset++; };
  const string = () => {
    const start = offset++;
    while (offset < text.length) { const char = text[offset++]; if (char === '\\') offset++; else if (char === '"') return JSON.parse(text.slice(start, offset)); }
    invalid();
  };
  const value = depth => {
    whitespace(); const char = text[offset];
    if (char === '{' || char === '[') {
      if (depth > 8) invalid(); offset++; whitespace(); const end = char === '{' ? '}' : ']';
      const names = new Set();
      while (text[offset] !== end) {
        if (char === '{') { const name = string(); if (names.has(name)) invalid(); names.add(name); whitespace(); offset++; }
        value(depth + 1); whitespace(); if (text[offset] === ',') { offset++; whitespace(); } else break;
      }
      offset++;
    } else if (char === '"') string();
    else { while (offset < text.length && !/[\s,\]}]/.test(text[offset])) offset++; }
  };
  value(1);
}
function parse(text, kind) {
  const bytes = encoder.encode(text); if (bytes.length < 1 || bytes.length > 65536) invalid();
  const message = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  uniqueAndBounded(text);
  fields(message, kind === 'request' ? ['protocol', 'requestId', 'action', 'payload'] : ['protocol', 'requestId', 'result']);
  id(message.requestId); fields(message.protocol, ['major', 'minor']);
  integer(message.protocol.major, 0, 2147483647); integer(message.protocol.minor, 0, 2147483647);
  if (message.protocol.major !== 1) invalid();
  if (kind === 'request') {
    const payload = message.payload;
    if (message.action === 'hello') { fields(payload, ['installationId', 'extensionVersion']); id(payload.installationId); version(payload.extensionVersion); }
    else if (message.action === 'getRequestStatus') { fields(payload, ['installationId', 'targetRequestId']); id(payload.installationId); id(payload.targetRequestId); }
    else if (message.action === 'addPublicGet') {
      fields(payload, ['installationId', 'createdAt', 'sourceUrl', 'trigger'], ['suggestedFileName']); id(payload.installationId);
      if (typeof payload.createdAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(payload.createdAt) || !Number.isFinite(Date.parse(payload.createdAt))) invalid();
      if (Number(payload.createdAt.slice(0, 4)) < 1 || new Date(payload.createdAt).toISOString().slice(0, 19) !== payload.createdAt.slice(0, 19)) invalid();
      if (payload.trigger !== 'linkContextMenu') invalid();
      const source = payload.sourceUrl;
      if (typeof source !== 'string' || encoder.encode(source).length > 16384 || /[\s\x00-\x1f\x7f-\x9f\\]/u.test(source) || /%(?![0-9a-f]{2})/i.test(source) || /%(?:0[0-9a-f]|1[0-9a-f]|7f)/i.test(source)) invalid();
      const url = new URL(source);
      if (!['http:', 'https:'].includes(url.protocol) || !url.hostname || url.username || url.password || !/^https?:\/\//i.test(source) || source.split('://')[1].split(/[/?#]/)[0].includes('@')) invalid();
      payload.sourceUrl = source.split('#')[0];
      if (Object.hasOwn(payload, 'suggestedFileName')) {
        const name = payload.suggestedFileName;
        if (typeof name !== 'string' || name.length < 1 || name.length > 240 || /[\x00-\x1f<>:"/\\|?*]/u.test(name) || /[. ]$/.test(name) || /^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)/i.test(name) || ['.', '..'].includes(name)) invalid();
      }
    } else invalid();
  } else {
    const result = message.result;
    if (result?.code === 'ready') {
      fields(result, ['code', 'appVersion', 'capabilities', 'maxMessageBytes']); version(result.appVersion); integer(result.maxMessageBytes, 1, 65536);
      if (!Array.isArray(result.capabilities) || result.capabilities.length > 2 || new Set(result.capabilities).size !== result.capabilities.length || result.capabilities.some(value => !capabilities.includes(value))) invalid();
    } else if (result?.code === 'accepted') {
      fields(result, ['code', 'jobId', 'state', 'duplicate']); id(result.jobId);
      if (!['queued', 'active', 'paused', 'completed', 'failed', 'interrupted', 'engine_uncertain'].includes(result.state) || typeof result.duplicate !== 'boolean') invalid();
    } else if (result?.code === 'rejected') { fields(result, ['code', 'reason']); if (!reasons.includes(result.reason)) invalid(); }
    else if (result?.code === 'processing') { fields(result, ['code', 'retryAfterMs']); integer(result.retryAfterMs, 1, 10000); }
    else { fields(result, ['code']); if (!['not_found', 'expired', 'unavailable', 'unknown'].includes(result.code)) invalid(); }
  }
  return message;
}
for (const fixture of cases) {
  test(`v1 shared fixture: ${fixture.name}`, () => {
    const text = fixture.raw ?? JSON.stringify(fixture.json);
    if (!fixture.valid) { assert.throws(() => parse(text, fixture.kind)); return; }
    const message = parse(text, fixture.kind);
    assert.deepEqual(parse(JSON.stringify(message), fixture.kind), message);
    if (fixture.sourceUrl) assert.equal(message.payload.sourceUrl, fixture.sourceUrl);
  });
}
test('UTF-8 frame lengths are bytes; both Windows transports use little endian', () => {
  const body = encoder.encode(JSON.stringify(cases[1].json));
  assert.ok(body.length > JSON.stringify(cases[1].json).length);
  const frame = new Uint8Array(4 + body.length); new DataView(frame.buffer).setUint32(0, body.length, true); frame.set(body, 4);
  assert.equal(new DataView(frame.buffer).getUint32(0, true), body.length);
  assert.deepEqual(parse(new TextDecoder().decode(frame.subarray(4)), 'request'), parse(JSON.stringify(cases[1].json), 'request'));
});
test('Published structural schema accepts shared valid fixtures and rejects closed-shape violations', () => {
  // Only the keywords used by this local schema; not a replacement for a general JSON Schema validator.
  const matches = (value, rule) => {
    if (rule.$ref) return matches(value, schema.$defs[rule.$ref.split('/').at(-1)]);
    if (rule.oneOf && rule.oneOf.filter(item => matches(value, item)).length !== 1) return false;
    if (Object.hasOwn(rule, 'const') && value !== rule.const) return false;
    if (rule.enum && !rule.enum.includes(value)) return false;
    if (rule.not && matches(value, rule.not)) return false;
    if (rule.type === 'object') {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
      if (rule.required?.some(key => !Object.hasOwn(value, key))) return false;
      if (rule.additionalProperties === false && Object.keys(value).some(key => !Object.hasOwn(rule.properties, key))) return false;
      if (Object.entries(value).some(([key, item]) => rule.properties[key] && !matches(item, rule.properties[key]))) return false;
    } else if (rule.type === 'array') {
      if (!Array.isArray(value) || value.length > (rule.maxItems ?? Infinity) || rule.uniqueItems && new Set(value).size !== value.length || value.some(item => !matches(item, rule.items))) return false;
    } else if (rule.type === 'string') {
      if (typeof value !== 'string' || value.length < (rule.minLength ?? 0) || value.length > (rule.maxLength ?? Infinity) || rule.pattern && !new RegExp(rule.pattern, 'u').test(value)) return false;
    } else if (rule.type === 'integer') {
      if (!Number.isInteger(value) || value < (rule.minimum ?? -Infinity) || value > (rule.maximum ?? Infinity)) return false;
    } else if (rule.type === 'boolean' && typeof value !== 'boolean') return false;
    return true;
  };
  for (const fixture of cases.filter(item => item.valid)) assert.ok(matches(fixture.json, schema), fixture.name);
  for (const name of ['major-mismatch', 'unsupported-action', 'headers-forbidden', 'response-extra-field']) assert.equal(matches(cases.find(item => item.name === name).json, schema), false, name);
});
