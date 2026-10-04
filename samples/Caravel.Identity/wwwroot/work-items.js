const element = id => document.getElementById(id);
let generation = 0, workspace = '', item = null, pending = null, canComplete = false, receiptUrl = null;
const message = text => { element('message').textContent = text; };
function clearSession() {
  generation++; workspace = ''; item = null; pending = null; receiptUrl = null;
  element('items').replaceChildren(); element('workspace').replaceChildren();
  element('complete').reset(); element('complete').hidden = true;
  element('item-title').textContent = ''; element('item-state').textContent = '';
  element('comment-error').textContent = ''; element('notice').textContent = '';
  element('login').elements.password.value = '';
  element('workspace-view').hidden = true; element('login').hidden = false;
}
async function request(path, options = {}) {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', ...options });
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    const error = new Error(data?.title || `Request failed (${response.status}).`);
    error.status = response.status; error.errors = data?.errors; throw error;
  }
  return data;
}
async function mutation(path, body, headers = {}) {
  const csrf = await request('/auth/csrf');
  return request(path, { method: 'POST', headers: { 'Content-Type': 'application/json', [csrf.headerName]: csrf.token, ...headers }, body: JSON.stringify(body) });
}
function failure(error) {
  if (error.status === 401 || error.status === 403) {
    clearSession(); message('Your session or access changed. Sign in again to continue.');
  } else message(error.message || 'The request could not be confirmed.');
}
async function loadWorkspaces(initial = false) {
  const current = ++generation;
  try {
    const spaces = await request('/workspaces/');
    if (current !== generation) return;
    element('login').hidden = true; element('workspace-view').hidden = false;
    element('workspace').replaceChildren(...spaces.map(space => new Option(space.name, space.id)));
    workspace = element('workspace').value;
    await loadItems();
  } catch (error) {
    if (current === generation && !(initial && error.status === 401)) failure(error);
  }
}
async function loadItems() {
  const current = ++generation;
  item = null; pending = null; receiptUrl = null;
  element('complete').hidden = true; element('complete').reset(); element('items').replaceChildren();
  element('notice').textContent = ''; element('comment-error').textContent = '';
  if (!workspace) { element('empty').hidden = false; return; }
  try {
    const data = await request(`/workspaces/${workspace}/items`);
    if (current !== generation) return;
    canComplete = data.canComplete; element('empty').hidden = data.items.length !== 0;
    element('items').replaceChildren(...data.items.map(row => {
      const li = document.createElement('li'), button = document.createElement('button');
      button.type = 'button'; button.textContent = row.title + (row.completed ? ' · Complete' : '');
      button.onclick = () => selectItem(row.id); li.append(button); return li;
    }));
  } catch (error) { if (current === generation) failure(error); }
}
async function selectItem(id, reload = false) {
  const current = ++generation;
  pending = null; receiptUrl = null; item = null;
  element('complete').reset(); element('complete').hidden = true;
  element('comment-error').textContent = ''; element('notice').textContent = '';
  try {
    const loaded = await request(`/workspaces/${workspace}/items/${id}`);
    if (current !== generation) return;
    item = loaded; renderItem();
    if (reload) message('Reloaded. Review the item before submitting a new command.');
  } catch (error) { if (current === generation) failure(error); }
}
function renderItem() {
  element('complete').hidden = false; element('item-title').textContent = item.title;
  element('item-state').textContent = `Revision ${item.revision} · ${item.completed ? 'Complete' : 'Open'}`;
  element('submit').disabled = item.completed || !canComplete;
  element('submit').textContent = 'Complete item'; element('complete').elements.comment.disabled = item.completed || !canComplete;
}
element('login').onsubmit = async event => {
  event.preventDefault(); const current = ++generation;
  const form = event.currentTarget, button = form.querySelector('button'); button.disabled = true;
  try {
    const result = await mutation('/auth/login', Object.fromEntries(new FormData(form)));
    if (current !== generation) return;
    form.elements.password.value = '';
    if (result?.requiresTwoFactor) { message('This account requires the account MFA flow. This small sample form supports password sign-in only.'); return; }
    message('Signed in.'); await loadWorkspaces();
  } catch (error) { if (current === generation) failure(error); }
  finally { button.disabled = false; }
};
element('workspace').onchange = () => { workspace = element('workspace').value; loadItems(); };
element('reload').onclick = () => { if (item) selectItem(item.id, true); };
async function signOut() {
  clearSession(); const current = generation;
  try {
    await mutation('/auth/logout', {});
    if (current === generation) { element('retry-logout').hidden = true; message('Signed out.'); }
  } catch (error) {
    if (current === generation) {
      element('retry-logout').hidden = error.status === 401;
      message(error.status === 401 ? 'Signed out.' : 'The view was cleared, but server sign-out could not be confirmed. Retry after reconnecting.');
    }
  }
}
element('logout').onclick = signOut;
element('retry-logout').onclick = signOut;
element('complete').onsubmit = async event => {
  event.preventDefault(); if (!item || !canComplete) return;
  const current = generation, button = element('submit');
  pending ??= { key: crypto.randomUUID(), revision: item.revision, comment: element('complete').elements.comment.value, id: item.id, workspace };
  button.disabled = true; element('complete').elements.comment.disabled = true; element('comment-error').textContent = '';
  try {
    const result = await mutation(`/workspaces/${pending.workspace}/items/${pending.id}/complete`, { comment: pending.comment },
      { 'Idempotency-Key': pending.key, 'If-Match': `"${pending.revision}"` });
    if (current !== generation) return;
    receiptUrl = `/workspaces/${workspace}/receipts/${result.receiptId}`;
    pending = null; item = result.item; renderItem();
    message('Completion saved.'); element('notice').textContent = `Notice: ${result.notice}.`;
  } catch (error) {
    if (current !== generation) return;
    if (error.status === 401 || error.status === 403) { failure(error); return; }
    if (error.status === 400 || error.status === 428) {
      pending = null; element('complete').elements.comment.disabled = false; button.disabled = false;
      element('comment-error').textContent = Object.values(error.errors || {}).flat().join(' '); failure(error);
    } else if (error.status === 409 || error.status === 412) {
      message(error.message + ' Your original edit is preserved. Use Reload to reconcile.');
    } else {
      button.disabled = false; button.textContent = 'Retry the same command';
      message('Outcome uncertain. Retry preserves the original note, revision and request key.');
    }
  }
};
window.addEventListener('focus', async () => {
  if (element('workspace-view').hidden) return;
  const current = generation;
  try {
    if (receiptUrl) {
      const result = await request(receiptUrl);
      if (current === generation) element('notice').textContent = `Notice: ${result.notice}.`;
    } else {
      await request(`/workspaces/${workspace}/items`);
    }
  } catch (error) { if (current === generation) failure(error); }
});
loadWorkspaces(true);
