const { before, after, test } = require('node:test');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const source = path.resolve(process.env.CARAVEL_BROWSER_APP_ROOT || path.join(__dirname, '../../samples/Caravel.Identity'), 'wwwroot');
const origin = 'http://localhost:54383';
const first = '11111111-1111-1111-1111-111111111111', second = '33333333-3333-3333-3333-333333333333';
const id = '22222222-2222-2222-2222-222222222222';
let browser;
before(async () => { browser = await chromium.launch({ headless: true, channel: process.env.PLAYWRIGHT_CHANNEL || undefined }); });
after(async () => { await browser?.close(); });

async function fixture(t) {
  const context = await browser.newContext(), page = await context.newPage();
  t.after(() => context.close());
  let hook = async () => null;
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  t.after(() => assert.deepEqual(errors, []));
  await page.route(`${origin}/**`, async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname;
    const intercepted = await hook(pathname, request);
    if (intercepted) return route.fulfill({ contentType: 'application/json', ...intercepted });
    const asset = { '/work-items': ['work-items.html', 'text/html'], '/work-items.js': ['work-items.js', 'text/javascript'], '/work-items.css': ['work-items.css', 'text/css'] }[pathname];
    if (asset) return route.fulfill({ contentType: asset[1], body: fs.readFileSync(path.join(source, asset[0]), 'utf8') });
    let body, status = 200;
    if (pathname === '/auth/csrf') body = { token: 'synthetic', headerName: 'RequestVerificationToken' };
    else if (pathname === '/auth/login' || pathname === '/auth/logout') status = 204;
    else if (pathname === '/workspaces/') body = [{ id: first, name: 'First workspace' }, { id: second, name: 'Second workspace' }];
    else if (pathname.endsWith('/items')) body = { canComplete: true, items: [{ id, title: 'Select item', revision: 1, completed: false }] };
    else if (pathname.endsWith(`/items/${id}`)) body = { id, title: 'Current item', revision: 1, completed: false };
    else { status = 404; body = {}; }
    return route.fulfill({ status, contentType: 'application/json', body: status === 204 ? undefined : JSON.stringify(body) });
  });
  return { page, hook: value => { hook = value; }, open: () => page.goto(`${origin}/work-items`) };
}
function hold() {
  let release, signal;
  return { started: new Promise(resolve => { signal = resolve; }), released: new Promise(resolve => { release = resolve; }), release: () => release(), signal: () => signal() };
}
async function login(page) {
  await page.locator('[name=userName]').fill('user@example.invalid');
  await page.locator('[name=password]').fill('Synthetic-password!123');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}
async function settled(page, response) {
  await response;
  // Fetch's continuation and DOM work complete before this two-frame observation.
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}

for (const loader of ['detail', 'items']) test(`Obsolete ${loader} denial preserves a newer workspace and retry command`, async t => {
  const f = await fixture(t), delay = hold(); let attempts = [];
  f.hook(async (pathname, request) => {
    if (pathname === `/workspaces/${first}/items${loader === 'detail' ? `/${id}` : ''}`) {
      delay.signal(); await delay.released; return { status: 403, body: '{"title":"Old denial"}' };
    }
    if (pathname.endsWith('/complete')) { attempts.push([request.headers()['idempotency-key'], request.headers()['if-match'], request.postData()]); return { status: 503, body: '{}' }; }
  });
  await f.open();
  if (loader === 'detail') await f.page.getByRole('button', { name: 'Select item', exact: true }).click();
  await delay.started;
  await f.page.locator('#workspace').selectOption(second);
  await f.page.getByRole('button', { name: 'Select item', exact: true }).click();
  await f.page.locator('textarea').fill('Preserve the new draft');
  await f.page.locator('#submit').click();
  await f.page.getByRole('button', { name: 'Retry the same command' }).waitFor();
  const response = f.page.waitForResponse(r => r.status() === 403);
  delay.release(); await settled(f.page, response);
  assert(await f.page.locator('#workspace-view').isVisible());
  assert.equal(await f.page.locator('textarea').inputValue(), 'Preserve the new draft');
  const retried = f.page.waitForResponse(r => r.url().endsWith('/complete'));
  await f.page.locator('#submit').click();
  await settled(f.page, retried);
  assert.equal(attempts.length, 2); assert.deepEqual(attempts[1], attempts[0]);
});

test('Old session detail denial cannot clear a successful new sign-in', async t => {
  const f = await fixture(t), delay = hold();
  f.hook(async pathname => { if (pathname.endsWith(`/items/${id}`)) { delay.signal(); await delay.released; return { status: 403, body: '{}' }; } });
  await f.open(); await f.page.getByRole('button', { name: 'Select item', exact: true }).click(); await delay.started;
  await f.page.locator('#logout').click();
  await f.page.waitForFunction(() => document.getElementById('message').textContent === 'Signed out.');
  await login(f.page); await f.page.getByRole('button', { name: 'Select item', exact: true }).waitFor();
  const response = f.page.waitForResponse(r => r.status() === 403);
  delay.release(); await settled(f.page, response);
  assert(await f.page.locator('#workspace-view').isVisible());
  assert.equal(await f.page.locator('#message').innerText(), 'Signed in.');
});

for (const signingIn of [false, true]) test(`Current initial items access loss is handled (sign-in: ${signingIn})`, async t => {
  const f = await fixture(t); let lists = 0;
  f.hook(async pathname => {
    if (pathname === '/workspaces/' && ++lists === 1 && signingIn) return { status: 401, body: '{}' };
    if (pathname.endsWith('/items')) return { status: signingIn ? 403 : 401, body: '{"title":"Current access lost"}' };
  });
  await f.open();
  if (signingIn) await login(f.page);
  await f.page.waitForFunction(() => document.getElementById('message').textContent.includes('session or access changed'));
  assert(await f.page.locator('#workspace-view').isHidden());
  assert(await f.page.locator('#login').isVisible());
});

test('Delayed reload success cannot replace the signed-out state or message', async t => {
  const f = await fixture(t), delay = hold(); let details = 0;
  f.hook(async pathname => {
    if (pathname.endsWith(`/items/${id}`) && ++details === 2) { delay.signal(); await delay.released; return { status: 200, body: JSON.stringify({ id, title: 'Obsolete item', revision: 1, completed: false }) }; }
  });
  await f.open(); await f.page.getByRole('button', { name: 'Select item', exact: true }).click();
  await f.page.locator('#reload').click(); await delay.started;
  await f.page.locator('#logout').click();
  await f.page.waitForFunction(() => document.getElementById('message').textContent === 'Signed out.');
  const response = f.page.waitForResponse(r => r.url().endsWith(`/items/${id}`));
  delay.release(); await settled(f.page, response);
  assert(await f.page.locator('#workspace-view').isHidden());
  assert.equal(await f.page.locator('#message').innerText(), 'Signed out.');
});
