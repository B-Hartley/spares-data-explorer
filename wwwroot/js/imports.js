(() => {
  'use strict';
  const names = ['CustomerRequests.csv', 'custreqparts.csv', 'products.csv', 'parts.csv', 'baseparts.csv', 'manufacturers.csv', 'factories.csv'];
  const byId = id => document.getElementById(id);
  const csrf = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  const selected = new Map();
  let previewToken = null;
  let busy = false;
  const number = new Intl.NumberFormat('en-GB');
  function el(tag, text, className) { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; }
  function message(text, error = false) { const node = byId('import-message'); node.textContent = text; node.classList.toggle('error', error); node.hidden = false; }
  function setBusy(value) { busy = value; byId('validate-import').disabled = busy || selected.size !== names.length; byId('apply-import').disabled = busy; byId('csv-files').disabled = busy; }
  function checklist() {
    const list = byId('file-checklist'); list.replaceChildren();
    for (const name of names) { const row = el('li'); const file = selected.get(name); row.append(el('span', name, 'file-name'), el('span', file ? `✓ ${(file.size / 1024).toFixed(0)} KB` : 'Waiting for file', `file-status${file ? ' ready' : ''}`)); list.append(row); }
    setBusy(busy);
  }
  function choose(files) {
    if (busy) return;
    let invalid = false;
    for (const file of files) { const name = names.find(x => x.toLowerCase() === file.name.toLowerCase()); if (!name) { invalid = true; continue; } selected.set(name, file); }
    previewToken = null; byId('preview-panel').hidden = true; checklist();
    if (invalid) message('Only the seven named CSV exports are accepted. Other files were ignored.', true);
    else byId('import-message').hidden = true;
  }
  async function api(path, options = {}) {
    const response = await fetch(path, { credentials: 'same-origin', ...options, headers: { 'X-CSRF-TOKEN': csrf, ...options.headers } });
    if (response.status === 401) { location.assign('/Login?returnUrl=%2FImports'); throw new Error('Please sign in again.'); }
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || body.detail || (response.status === 403 ? 'Imports require an administrator account from an allowed IP address.' : 'The import could not be completed.'));
    return body;
  }
  function stats(target, metadata) { target.replaceChildren(); for (const [value, label] of [[metadata.requestCount, 'Customer requests'], [metadata.lineCount, 'Requested part lines']]) { const item = el('div', undefined, 'import-stat'); item.append(el('strong', number.format(value || 0)), el('span', label)); target.append(item); } }
  function dateLabel(value) { return value ? new Date(`${value.slice(0, 10)}T12:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Europe/London' }) : '—'; }
  function showCurrent(metadata) {
    byId('current-description').textContent = metadata.hasData ? `${dateLabel(metadata.requestDateFrom)} – ${dateLabel(metadata.requestDateTo)} · request dates` : 'No dataset has been loaded yet.';
    stats(byId('current-counts'), metadata);
    const files = byId('current-files'); files.replaceChildren(); for (const file of metadata.sourceFiles || []) { const row = el('div'); row.append(el('span', file.name), el('span', `${number.format(file.rows)} rows`)); files.append(row); }
  }
  byId('csv-files').addEventListener('change', event => choose(event.target.files));
  const dropzone = byId('dropzone');
  for (const event of ['dragenter', 'dragover']) dropzone.addEventListener(event, e => { e.preventDefault(); dropzone.classList.add('dragover'); });
  for (const event of ['dragleave', 'drop']) dropzone.addEventListener(event, e => { e.preventDefault(); dropzone.classList.remove('dragover'); });
  dropzone.addEventListener('drop', e => choose(e.dataTransfer.files));
  byId('validate-import').addEventListener('click', async () => {
    setBusy(true); byId('validate-import').textContent = 'Validating files…'; byId('import-message').hidden = true;
    try {
      const data = new FormData(); for (const [name, file] of selected) data.append('files', file, name);
      const result = await api('/api/imports/preview', { method: 'POST', body: data }); previewToken = result.token;
      byId('preview-description').textContent = `${dateLabel(result.metadata.requestDateFrom)} – ${dateLabel(result.metadata.requestDateTo)} · preview valid for 20 minutes`;
      stats(byId('preview-counts'), result.metadata); const diagnostics = byId('preview-diagnostics'); diagnostics.replaceChildren();
      for (const diagnostic of result.metadata.diagnostics || []) diagnostics.append(el('div', diagnostic.message, `import-diagnostic ${diagnostic.severity}`));
      if (!diagnostics.childNodes.length) diagnostics.append(el('div', 'The seven CSV tables validated successfully.', 'import-diagnostic'));
      byId('preview-panel').hidden = false; byId('preview-panel').scrollIntoView({ behavior: 'smooth', block: 'start' });
    } catch (error) { message(error.message, true); }
    finally { setBusy(false); byId('validate-import').textContent = 'Validate snapshot'; }
  });
  byId('apply-import').addEventListener('click', async () => {
    if (!previewToken || busy) return; setBusy(true); byId('apply-import').textContent = 'Applying dataset…';
    try {
      const metadata = await api('/api/imports/apply', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token: previewToken }) });
      showCurrent(metadata); previewToken = null; selected.clear(); byId('csv-files').value = ''; byId('preview-panel').hidden = true; checklist();
      message('The new dataset is now available to all signed-in users. Return to the explorer to drill into demand and fulfilment.'); window.scrollTo({ top: 0, behavior: 'smooth' });
    } catch (error) { message(error.message, true); }
    finally { setBusy(false); byId('apply-import').textContent = 'Use this dataset'; }
  });
  checklist(); api('/api/imports/status').then(showCurrent).catch(error => message(error.message, true));
})();
