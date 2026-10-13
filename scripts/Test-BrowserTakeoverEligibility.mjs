// Narrow real Chromium-family API probe. Fresh profiles and synthetic loopback downloads only.
import {createRequire} from 'node:module';
import {readFile, writeFile, mkdir, cp, stat} from 'node:fs/promises';
import path from 'node:path';
import {createServer} from 'node:https';
import {createServer as createHttpServer} from 'node:http';
import {createHash, randomUUID, X509Certificate} from 'node:crypto';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';
import assert from 'node:assert/strict';

const [family, executable, output, playwrightDirectory, certificate, privateKey] = process.argv.slice(2);
if (!['chrome', 'edge', 'chromium', 'firefox'].includes(family) || !privateKey) throw new Error('family exe NEW-output playwright-package cert key required');
const root = path.resolve(output), repository = fileURLToPath(new URL('../', import.meta.url));
// Never accept an existing profile, and keep every write under a new workspace artifact directory.
assert.ok(root.startsWith(path.join(repository, 'artifacts') + path.sep));
try { await stat(root); throw new Error('output must not exist'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
await mkdir(root, {recursive: true});
const playwright = createRequire(import.meta.url)(playwrightDirectory);
const cert = await readFile(certificate), key = await readFile(privateKey);
const pin = createHash('sha256').update(new X509Certificate(cert).publicKey.export({type: 'spki', format: 'der'})).digest('base64');
const token = randomUUID(), requests = [], sockets = new Set(), streams = new Set();
const small = Buffer.alloc(4096, 0x61), slowLength = 32 * 1024 * 1024;
const serve = (request, response) => {
  const url = new URL(request.url, 'https://127.0.0.1');
  if (!url.pathname.startsWith(`/${token}/`)) { response.writeHead(404).end(); return; }
  const route = url.pathname.split('/').at(-1);
  if (route === 'page') {
    response.writeHead(200, {'content-type': 'text/html'}).end('<!doctype html><title>Synthetic A1 only</title><a id="download">synthetic</a>'); return;
  }
  requests.push({method: request.method, resource: url.pathname + url.search,
    cookiePresent: !!request.headers.cookie, authorizationPresent: !!request.headers.authorization});
  if (route === 'redirect') { response.writeHead(302, {location: url.pathname.replace('redirect', 'slow') + url.search}).end(); return; }
  response.writeHead(200, {'content-type': 'application/octet-stream', 'content-disposition': 'attachment; filename="synthetic-a1.bin"',
    'content-length': route === 'slow' ? slowLength : small.length, 'cache-control': 'no-store'});
  if (route !== 'slow') { response.end(small); return; }
  let sent = 0;
  const timer = setInterval(() => { if (sent >= slowLength) { clearInterval(timer); streams.delete(timer); response.end(); }
    else { response.write(Buffer.alloc(32768, 0x61)); sent += 32768; } }, 20);
  streams.add(timer); response.on('close', () => {clearInterval(timer); streams.delete(timer);});
};
const server = family === 'firefox' ? createHttpServer(serve) : createServer({cert, key}, serve);
server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket));});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const base = `${family === 'firefox' ? 'http' : 'https'}://127.0.0.1:${server.address().port}/${token}/`;
const extension = path.join(root, 'extension'), profile = path.join(root, 'profile');
await mkdir(extension); await mkdir(profile);
await cp(path.join(repository, 'extensions/aria-gui/experiments/takeover'), extension, {recursive: true});
const addonId = 'synthetic-a1@example.invalid', addonUuid = randomUUID();
await writeFile(path.join(extension, 'manifest.json'), JSON.stringify({manifest_version: 3, name: 'Synthetic A1 API probe only', version: '0.0.1',
  ...(family === 'firefox' ? {browser_specific_settings: {gecko: {id: addonId}}} : {background: {service_worker: 'background.mjs', type: 'module'}}),
  permissions: ['downloads', 'storage'], incognito: 'not_allowed'}));
await writeFile(path.join(extension, 'background.mjs'), "import {adapter,cancellation} from './adapters.mjs'; import {observed,reduce} from './state.mjs'; globalThis.a1Probe={adapter,cancellation,observed,reduce}; globalThis.probeDownloads=globalThis.browser?.downloads??chrome.downloads; globalThis.probeStorage=globalThis.browser?.storage??chrome.storage;");
await writeFile(path.join(extension, 'probe.html'), '<!doctype html><title>Synthetic A1 extension only</title><script type="module" src="background.mjs"></script>');
const report = {family, executable, profile, personalProfileUsed: false,
  certificateException: family === 'firefox' ? 'none; HTTP-only API observation, HTTPS eligibility unproven' : 'isolated exact synthetic SPKI',
  productionIntegrationChanged: false, realAppPrepareCommit: false, checks: {}, cases: [], status: 'inconclusive'};
let processHandle, browser, firefoxContext;
try {
  let context, worker, session;
  if (family === 'firefox') {
    firefoxContext = await playwright.firefox.launchPersistentContext(profile, {executablePath: executable, headless: true,
      acceptDownloads: true, downloadsPath: path.join(root, 'downloads'), firefoxUserPrefs: {
        'extensions.webextensions.uuids': JSON.stringify({[addonId]: addonUuid}), 'browser.download.useDownloadDir': true,
        'browser.download.folderList': 2, 'browser.download.dir': path.join(root, 'downloads')}});
    context = firefoxContext;
    report.browserVersion = context.browser().version();
    const debugging = await context.newPage(); await debugging.goto('about:debugging#/runtime/this-firefox', {waitUntil: 'commit', timeout: 10000});
    const chooser = debugging.waitForEvent('filechooser', {timeout: 10000});
    await debugging.getByRole('button', {name: /Load Temporary Add-on/}).click({timeout: 10000});
    await (await chooser).setFiles(path.join(extension, 'manifest.json'));
    await debugging.getByText('Synthetic A1 API probe only', {exact: true}).waitFor({timeout: 10000});
    report.extensionId = addonId;
    worker = await context.newPage(); await worker.goto(`moz-extension://${addonUuid}/probe.html`);
    report.workerUrl = worker.url();
    report.certificateException = 'none; HTTP-only Firefox API observation, HTTPS eligibility unproven';
  } else {
  processHandle = spawn(executable, ['--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-sync',
    '--disable-background-networking', '--disable-component-update', '--enable-unsafe-extension-debugging', '--remote-debugging-port=0',
    `--user-data-dir=${profile}`, `--ignore-certificate-errors-spki-list=${pin}`, 'about:blank'], {windowsHide: true, stdio: 'ignore'});
  processHandle.on('error', error => {report.launchError = error.code;});
  let portFile;
  for (let i = 0; i < 80; i++) { try {portFile = await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8'); break;} catch {}
    if (processHandle.exitCode !== null) throw new Error('owned browser exited before debugger readiness'); await delay(250); }
  if (!portFile) throw new Error('owned debugger not ready');
  const [port, endpoint] = portFile.trim().split(/\r?\n/);
  browser = await playwright.chromium.connectOverCDP(`ws://127.0.0.1:${port}${endpoint}`);
  report.browserVersion = browser.version();
  session = await browser.newBrowserCDPSession();
  const loaded = await session.send('Extensions.loadUnpacked', {path: extension}); report.extensionId = loaded.id;
  context = browser.contexts()[0];
  worker = context.serviceWorkers().find(value => value.url().includes(loaded.id)) ??
    await context.waitForEvent('serviceworker', {predicate: value => value.url().includes(loaded.id), timeout: 10000});
  report.workerUrl = worker.url();
  }
  let workerReady = false;
  for (let i = 0; i < 40; i++) {if (await worker.evaluate(() => !!globalThis.a1Probe)) {workerReady = true; break;} await delay(100);}
  assert.ok(workerReady, 'isolated extension worker ready');
  if (session) await session.send('Browser.setDownloadBehavior', {behavior: 'allow', downloadPath: path.join(root, 'downloads'), eventsEnabled: true});
  const page = await context.newPage(); await page.goto(base + 'page');
  const search = url => worker.evaluate(url => probeDownloads.search({url}), url);
  async function itemFor(url, expected, excluded = []) {
    for (let i = 0; i < 80; i++) {const items = (await search(url)).filter(item => !excluded.includes(item.id)); if (items.length && (!expected || items.at(-1).state === expected)) return items.at(-1); await delay(100);}
    throw new Error('synthetic item not observed');
  }
  async function click(url) { await page.evaluate(url => {const anchor = document.querySelector('#download'); anchor.href = url; anchor.click();}, url); }
  const repeat = base + 'small?case=repeat'; const hashes = [];
  for (let i = 0; i < 2; i++) {
    const previous = (await search(repeat)).map(item => item.id);
    await click(repeat); const item = await itemFor(repeat, 'complete', previous);
    hashes.push(createHash('sha256').update(await readFile(item.filename)).digest('hex'));
    report.cases.push({case: `repeat-${i}`, item});
  }
  assert.equal(hashes[0], hashes[1]);
  assert.equal(hashes[0], createHash('sha256').update(small).digest('hex'));
  report.repeatedBodySha256 = hashes;
  report.checks.controlledRepeatedGetSameBytes = requests.filter(value => value.resource.endsWith('small?case=repeat') && value.method === 'GET').length === 2;
  const slow = base + 'slow?case=cancel'; await click(slow); const before = await itemFor(slow, 'in_progress');
  assert.ok(before.incognito === false && before.danger === 'safe' && !before.byExtensionId && (family === 'firefox' || before.finalUrl === slow));
  const cancelled = await worker.evaluate(async ({id, startTime, source, modelBrowser}) => {
    const snapshot = (await probeDownloads.search({id}))[0];
    if (!snapshot || snapshot.startTime !== startTime || snapshot.url !== source || !source.includes('/slow?case=cancel') ||
        (snapshot.finalUrl !== undefined && snapshot.finalUrl !== source) || snapshot.state !== 'in_progress' || snapshot.incognito || snapshot.danger !== 'safe' || snapshot.byExtensionId)
      throw new Error('synthetic cancellation identity refused');
    const now = new Date().toISOString();
    let record = a1Probe.observed({requestId: crypto.randomUUID(), installationId: crypto.randomUUID(), callerId: crypto.randomUUID(),
      profileId: crypto.randomUUID(), grantId: crypto.randomUUID(), browser: modelBrowser, itemId: id, startTime}, now);
    record = a1Probe.reduce(record, {type: 'prepare'}, now);
    record = a1Probe.reduce(record, {type: 'prepared'}, now); // Model only: no app reservation exists in this probe.
    record = a1Probe.reduce(record, {type: 'cancel'}, now);
    await probeStorage.local.set({pending: [record]});
    await probeDownloads.cancel(id);
    const adapter = a1Probe.adapter(modelBrowser, {readFresh: async id => (await probeDownloads.search({id}))[0]});
    const fresh = await adapter.readFresh(id);
    const recognized = a1Probe.cancellation(record, fresh, {source, filename: snapshot.filename});
    return {native: (await probeDownloads.search({id}))[0], recognized, local: await probeStorage.local.get(null)};
  }, {id: before.id, startTime: before.startTime, source: slow, modelBrowser: family === 'chromium' ? 'chrome' : family});
  report.cases.push({case: 'synthetic-cancel', before, after: cancelled.native});
  report.checks.freshCancellationRecognized = cancelled.recognized;
  report.checks.extensionStorageHasNoSourceOrFilename = !/https?:/.test(JSON.stringify(cancelled.local)) && !JSON.stringify(cancelled.local).includes('synthetic-a1.bin');
  const redirected = base + 'redirect?case=redirect'; await click(redirected); const redirectItem = await itemFor(redirected, 'in_progress');
  report.cases.push({case: 'redirect-observation', item: redirectItem});
  report.checks.effectiveUrlDistinguishesRedirect = redirectItem.finalUrl === redirected.replace('redirect?', 'slow?') && redirectItem.url === redirected;
  // POST contrast uses exactly the repeat GET URL: item URL is insufficient method evidence.
  const prePostIds = (await search(repeat)).map(item => item.id);
  await page.evaluate(url => {const form = document.createElement('form'); form.method = 'POST'; form.action = url; document.body.append(form); form.submit();}, repeat);
  const postItem = await itemFor(repeat, 'complete', prePostIds);
  report.cases.push({case: 'same-url-post', item: postItem});
  report.checks.urlDoesNotProveGet = requests.some(value => value.method === 'POST' && value.resource.endsWith('small?case=repeat'));
  report.checks.syntheticRequestsCredentialFree = requests.every(value => !value.cookiePresent && !value.authorizationPresent);
  report.checks.methodAbsentFromDownloadItem = !Object.hasOwn(before, 'method');
  if (family === 'firefox') {
    report.status = 'blocked-effective-source-proof';
    report.blocker = 'Native DownloadItem effective-source evidence absent; HTTP observation does not pass HTTPS grant policy.';
  } else {assert.ok(Object.values(report.checks).every(value => value === true)); report.status = 'narrow-controlled-api-proof-only';}
} catch (error) { report.error = error.message; process.exitCode = 1; }
finally {
  if (firefoxContext) {try {await firefoxContext.close(); report.ownedBrowserClosed = true;} catch {report.ownedBrowserClosed = false;}}
  if (browser) {try { await browser.close(); } catch {}}
  if (processHandle && processHandle.exitCode === null) {
    // PID was obtained solely from our newly spawned isolated browser; never kill by name.
    await new Promise(resolve => {const terminate = spawn('taskkill', ['/PID', String(processHandle.pid), '/T', '/F'], {windowsHide: true, stdio: 'ignore'}); terminate.once('close', resolve);});
  }
  if (processHandle) {
    for (let i = 0; i < 20 && processHandle.exitCode === null; i++) await delay(100);
    report.ownedBrowserClosed = processHandle.exitCode !== null;
  }
  if (report.ownedBrowserClosed === false) { report.status = 'inconclusive-cleanup'; report.error = 'owned browser termination unconfirmed'; process.exitCode = 1; }
  for (const timer of streams) clearInterval(timer);
  for (const socket of sockets) socket.destroy();
  await new Promise(resolve => server.close(resolve));
  report.requests = requests;
  report.harnessSha256 = createHash('sha256').update(await readFile(new URL(import.meta.url))).digest('hex');
  report.browserSha256 = createHash('sha256').update(await readFile(executable)).digest('hex');
  await writeFile(path.join(root, 'evidence.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({family, status: report.status, checks: report.checks, error: report.error ?? null, evidence: path.join(root, 'evidence.json')}));
}
