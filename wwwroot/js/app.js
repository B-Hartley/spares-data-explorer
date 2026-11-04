/* Click Spares — authenticated demand explorer. No third-party browser dependencies. */
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  if (!$('breakdownBody')) return;

  const dimensions = [
    { key: 'manufacturers', label: 'Manufacturer', plural: 'manufacturers' },
    { key: 'factories', label: 'Factory', plural: 'factories' },
    { key: 'products', label: 'Product', plural: 'products' },
    { key: 'baseParts', label: 'BasePart', plural: 'BaseParts' },
    { key: 'statuses', label: 'Request status', plural: 'statuses' },
    { key: 'customers', label: 'Customer / retailer', plural: 'customers' }
  ];
  const groups = {
    manufacturer: { name: 'manufacturer', plural: 'manufacturers', heading: 'Manufacturer', dimension: 'manufacturers', next: 'factory', hint: 'Select a manufacturer to explore its factories, products and parts.' },
    factory: { name: 'factory', plural: 'factories', heading: 'Factory', dimension: 'factories', next: 'product', hint: 'Select a factory to see its products, then the parts behind their demand.' },
    product: { name: 'product', plural: 'products', heading: 'Product', dimension: 'products', next: 'basePart', hint: 'Select a product to see which of its BaseParts drive demand.' },
    basePart: { name: 'BasePart', plural: 'BaseParts', heading: 'BasePart', dimension: 'baseParts', next: 'basePart', hint: 'Compare BaseParts across the current selection. Select one to inspect its requests.' }
  };
  const metrics = {
    units: { name: 'requested units', noun: 'units', label: 'Part demand', total: 'units', fulfilled: 'fulfilledUnits', unfulfilled: 'unfulfilledUnits', rate: 'unitFulfilment' },
    requests: { name: 'customer requests', noun: 'requests', label: 'Request demand', total: 'requests', fulfilled: 'fulfilledRequests', unfulfilled: 'unfulfilledRequests', rate: 'requestFulfilment' },
    lines: { name: 'part request lines', noun: 'lines', label: 'Part-line demand', total: 'lines', fulfilled: 'fulfilledLines', unfulfilled: 'unfulfilledLines', rate: 'lineFulfilment' }
  };
  const defaultState = {
    from: null, to: null, dateBasis: 'request', manufacturers: [], factories: [], products: [], baseParts: [], statuses: [], customers: [],
    outcome: 'all', search: '', groupBy: 'factory', metric: 'units', sort: 'demand', direction: 'desc', page: 1, pageSize: 25, trendBucket: 'week'
  };
  const preferenceKey = 'clickspares.spares-explorer.preferences.v1';
  const permittedPanes = ['trendPanel', 'outcomePanel', 'guidancePanel'];
  const preferences = readPreferences();
  let state = { ...defaultState, ...preferences.state };
  let appliedState = cloneState(state);
  let data = null;
  let metadata = null;
  let controller = null;
  let querySequence = 0;
  let filterTimer = null;
  let filtersDirty = false;
  let drillHistory = [];
  let toastTimer = null;
  let exporting = false;
  let requestController = null;
  let requestSequence = 0;
  let requestState = null;
  let requestPage = 1;
  let requestPages = 1;
  const hiddenPanes = new Set(preferences.hiddenPanes);
  const menus = new Map();
  const labelCache = Object.fromEntries(dimensions.map(d => [d.key, new Map()]));
  const countFormatter = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 2 });
  const rateFormatter = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const canManage = document.body.dataset.canManage === 'true';
  const NS = 'http://www.w3.org/2000/svg';

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined && text !== null) element.textContent = String(text);
    return element;
  }

  function svgNode(tag, attributes = {}) {
    const element = document.createElementNS(NS, tag);
    Object.entries(attributes).forEach(([key, value]) => element.setAttribute(key, String(value)));
    return element;
  }

  function chevron() {
    const svg = svgNode('svg', { viewBox: '0 0 24 24', 'aria-hidden': 'true' });
    svg.append(svgNode('path', { d: 'm9 6 6 6-6 6' }));
    return svg;
  }

  function finite(value) { const result = Number(value); return Number.isFinite(result) ? result : 0; }
  function count(value) { return countFormatter.format(finite(value)); }
  function rate(value, demand = 1) { return demand > 0 ? `${rateFormatter.format(finite(value))}%` : '—'; }
  function cloneState(value) { return { ...value, ...Object.fromEntries(dimensions.map(d => [d.key, [...(value[d.key] || [])]])) }; }
  function normalise(value) { return String(value ?? '').toLocaleLowerCase('en-GB'); }
  function hasValue(list, value) { return list.some(item => normalise(item) === normalise(value)); }

  function readPreferences() {
    try {
      const stored = JSON.parse(localStorage.getItem(preferenceKey) || '{}');
      const saved = {};
      if (['units', 'requests', 'lines'].includes(stored.metric)) saved.metric = stored.metric;
      if (['manufacturer', 'factory', 'product', 'basePart'].includes(stored.groupBy)) saved.groupBy = stored.groupBy;
      if (['day', 'week', 'month'].includes(stored.trendBucket)) saved.trendBucket = stored.trendBucket;
      if ([10, 25, 50, 100].includes(stored.pageSize)) saved.pageSize = stored.pageSize;
      return { state: saved, hiddenPanes: Array.isArray(stored.hiddenPanes) ? stored.hiddenPanes.filter(p => permittedPanes.includes(p)) : [] };
    } catch { return { state: {}, hiddenPanes: [] }; }
  }

  function savePreferences() {
    try {
      // Save presentation choices only. Results, identifiers and filter selections stay in memory.
      localStorage.setItem(preferenceKey, JSON.stringify({ metric: state.metric, groupBy: state.groupBy, trendBucket: state.trendBucket, pageSize: state.pageSize, hiddenPanes: [...hiddenPanes] }));
    } catch { /* A blocked browser storage policy does not prevent use of the dashboard. */ }
  }

  function csrfToken() { return document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''; }

  async function api(path, body, signal) {
    const method = body === undefined ? 'GET' : 'POST';
    const headers = { Accept: 'application/json' };
    if (method === 'POST') {
      headers['Content-Type'] = 'application/json';
      headers['X-CSRF-TOKEN'] = csrfToken();
    }
    const response = await fetch(path, { method, credentials: 'same-origin', headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
    if (response.status === 401) {
      const error = new Error('Your sign-in session has ended. Sign in again to continue.');
      error.signIn = true;
      throw error;
    }
    if (response.status === 403) throw new Error('Your account does not have access to this view. Please contact a Click Spares administrator.');
    if (!response.ok) {
      let message = `The server returned ${response.status}. Please try again.`;
      const contentType = response.headers.get('content-type') || '';
      if (contentType.includes('json')) {
        try {
          const problem = await response.json();
          message = problem.detail || problem.message || problem.error || problem.title || message;
          if (typeof message !== 'string') message = 'The request could not be completed. Check your filters and try again.';
        } catch { /* Keep the safe generic error. */ }
      }
      throw new Error(message.slice(0, 600));
    }
    if (response.status === 204) return null;
    return response.json();
  }

  function formatDate(value, options = {}) {
    if (!value) return '—';
    const date = new Date(`${String(value).slice(0, 10)}T12:00:00Z`);
    if (Number.isNaN(date.getTime())) return String(value).slice(0, 10);
    return new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC', ...options }).format(date);
  }

  function dateRangeText(from, to) {
    if (!from && !to) return 'All available dates';
    if (!from) return `Up to ${formatDate(to)}`;
    if (!to) return `From ${formatDate(from)}`;
    if (from === to) return formatDate(from);
    if (String(from).slice(0, 7) === String(to).slice(0, 7)) return `${Number(String(from).slice(8, 10))}–${formatDate(to)}`;
    return `${formatDate(from, { year: undefined })} – ${formatDate(to)}`;
  }

  function dateBounds(selected = state) {
    if (!metadata) return { from: selected.from, to: selected.to };
    return selected.dateBasis === 'despatch'
      ? { from: selected.from || metadata.despatchDateFrom, to: selected.to || metadata.despatchDateTo }
      : { from: selected.from || metadata.requestDateFrom, to: selected.to || metadata.requestDateTo };
  }

  function metricCounts(value, metric = appliedState.metric) {
    const definition = metrics[metric] || metrics.units;
    const total = finite(value?.[definition.total]);
    const fulfilled = finite(value?.[definition.fulfilled]);
    const unfulfilled = value?.[definition.unfulfilled] === undefined ? Math.max(0, total - fulfilled) : finite(value[definition.unfulfilled]);
    const percentage = total > 0 ? fulfilled / total * 100 : 0;
    return { total, fulfilled, unfulfilled, percentage };
  }

  function showError(error, title = 'We couldn’t update the dashboard.') {
    $('errorTitle').textContent = title;
    $('errorMessage').replaceChildren(document.createTextNode(error.message || 'Please check your connection and try again.'));
    if (error.signIn) {
      const link = node('a', '', 'Sign in again');
      link.href = '/Login?returnUrl=%2F';
      $('errorMessage').append(document.createTextNode(' '), link);
    }
    $('errorBanner').hidden = false;
  }

  function showToast(message) {
    clearTimeout(toastTimer);
    $('toast').textContent = message;
    $('toast').hidden = false;
    toastTimer = setTimeout(() => { $('toast').hidden = true; }, 4300);
  }

  function setLoading(loading) {
    document.body.classList.toggle('is-loading', loading);
    $('kpiGrid').setAttribute('aria-busy', String(loading));
    $('breakdownTable').setAttribute('aria-busy', String(loading));
    $('refreshButton').disabled = loading;
    $('refreshButton').querySelector('span').textContent = loading ? 'Updating…' : 'Refresh';
  }

  function hasValidDates() {
    if (state.from && state.to && state.from > state.to) {
      showError(new Error('The From date must be on or before the To date.'), 'Check the selected date range.');
      return false;
    }
    return true;
  }

  async function loadData() {
    clearTimeout(filterTimer);
    if (!hasValidDates()) return false;
    controller?.abort();
    controller = new AbortController();
    const sequence = ++querySequence;
    const submitted = cloneState(state);
    setLoading(true);
    $('errorBanner').hidden = true;
    try {
      const result = await api('/api/explore', submitted, controller.signal);
      if (sequence !== querySequence) return false;
      data = result;
      appliedState = submitted;
      metadata = result.metadata;
      filtersDirty = false;
      if (Number.isFinite(result.page)) state.page = result.page;
      updateControls();
      render(result);
      return true;
    } catch (error) {
      if (error.name === 'AbortError' || sequence !== querySequence) return false;
      showError(error);
      if (!data) renderTableState('The dashboard is unavailable', 'Your source data has not been changed. Try again when the connection is restored.');
      return false;
    } finally {
      if (sequence === querySequence) setLoading(false);
    }
  }

  function filtersChanged({ delay = 180, force = false, retainHistory = false } = {}) {
    controller?.abort();
    querySequence++;
    setLoading(false);
    state.page = 1;
    filtersDirty = true;
    if (!retainHistory) drillHistory = [];
    if ($('requestsDialog').open) $('requestsDialog').close();
    updateControls();
    renderActiveFilters();
    renderDrillPath();
    clearTimeout(filterTimer);
    if ($('liveFilters').checked || force) filterTimer = setTimeout(loadData, delay);
    else {
      $('applyFilters').disabled = false;
      $('applyFilters').textContent = 'Apply changes';
      $('scopeSummary').textContent = 'Showing the last applied filters. Apply your changes to update the results.';
    }
  }

  function resetFilters() {
    state = { ...defaultState, metric: state.metric, groupBy: state.groupBy, pageSize: state.pageSize, trendBucket: state.trendBucket };
    state = cloneState(state);
    dimensions.forEach(d => { menus.get(d.key).search.value = ''; });
    closeMenus();
    filtersChanged({ delay: 0, force: true });
  }

  function buildFilterMenus() {
    const host = $('dimensionFilters');
    for (const dimension of dimensions) {
      const container = node('div', 'filter-menu');
      const trigger = node('button', 'filter-menu-trigger');
      trigger.type = 'button';
      trigger.setAttribute('aria-expanded', 'false');
      trigger.setAttribute('aria-controls', `filter-${dimension.key}`);
      const text = node('span');
      const title = node('small', '', dimension.label);
      const value = node('strong', '', `All ${dimension.plural}`);
      text.append(title, value);
      const arrow = svgNode('svg', { viewBox: '0 0 24 24', 'aria-hidden': 'true' });
      arrow.append(svgNode('path', { d: 'm6 9 6 6 6-6' }));
      trigger.append(text, arrow);
      const popover = node('div', 'filter-popover');
      popover.id = `filter-${dimension.key}`;
      popover.hidden = true;
      const search = node('input');
      search.type = 'search';
      search.placeholder = `Find ${dimension.plural}…`;
      search.setAttribute('aria-label', `Search ${dimension.plural}`);
      search.autocomplete = 'off';
      const tools = node('div', 'filter-popover-tools');
      const number = node('span', '', 'Loading options…');
      const clear = node('button', 'text-button', 'Clear selection');
      clear.type = 'button';
      tools.append(number, clear);
      const options = node('div', 'filter-options');
      popover.append(search, tools, options);
      container.append(trigger, popover);
      host.append(container);
      menus.set(dimension.key, { container, trigger, value, popover, search, options, number, values: [] });

      trigger.addEventListener('click', () => {
        const shouldOpen = popover.hidden;
        closeMenus(dimension.key);
        popover.hidden = !shouldOpen;
        trigger.setAttribute('aria-expanded', String(shouldOpen));
        if (shouldOpen) { renderMenuOptions(dimension.key); search.focus(); }
      });
      search.addEventListener('input', () => renderMenuOptions(dimension.key));
      clear.addEventListener('click', () => {
        state[dimension.key] = [];
        renderMenuOptions(dimension.key);
        filtersChanged();
      });
    }
  }

  function closeMenus(except) {
    for (const [key, menu] of menus) {
      if (key !== except) { menu.popover.hidden = true; menu.trigger.setAttribute('aria-expanded', 'false'); }
    }
  }

  function renderMenuOptions(key) {
    const menu = menus.get(key);
    if (!menu) return;
    const search = normalise(menu.search.value.trim());
    const selected = state[key];
    const available = [...menu.values];
    for (const value of selected) {
      if (!available.some(option => normalise(option.value) === normalise(value))) available.unshift({ value, label: labelCache[key].get(value) || value, requests: 0 });
    }
    const matches = available.filter(option => normalise(`${option.label} ${option.value}`).includes(search));
    if (!search) matches.sort((a, b) => Number(hasValue(selected, b.value)) - Number(hasValue(selected, a.value)));
    const visible = matches.slice(0, 200);
    menu.number.textContent = matches.length > 200 ? `Showing 200 of ${count(matches.length)} · search to narrow` : `${count(matches.length)} option${matches.length === 1 ? '' : 's'}`;
    const activeValue = menu.options.contains(document.activeElement) ? document.activeElement.dataset.value : null;
    const fragment = document.createDocumentFragment();
    for (const option of visible) {
      const label = node('label', 'filter-option');
      const checkbox = node('input');
      checkbox.type = 'checkbox';
      checkbox.value = String(option.value);
      checkbox.dataset.value = String(option.value);
      checkbox.checked = hasValue(selected, option.value);
      const name = node('span', 'filter-option-label', option.label || option.value);
      const number = node('span', 'filter-option-count', count(option.requests));
      number.title = 'Matching requests';
      label.append(checkbox, name, number);
      checkbox.addEventListener('change', () => {
        const current = state[key].filter(value => normalise(value) !== normalise(option.value));
        if (checkbox.checked) current.push(String(option.value));
        state[key] = current;
        filtersChanged();
      });
      fragment.append(label);
    }
    if (!visible.length) fragment.append(node('p', 'filter-options-empty', 'No matching options. Try a different search.'));
    menu.options.replaceChildren(fragment);
    if (activeValue !== null) {
      const input = [...menu.options.querySelectorAll('input')].find(item => item.dataset.value === activeValue);
      input?.focus({ preventScroll: true });
    }
    updateMenuTrigger(key);
  }

  function updateMenuTrigger(key) {
    const dimension = dimensions.find(d => d.key === key);
    const menu = menus.get(key);
    const selected = state[key];
    menu.trigger.classList.toggle('has-selection', selected.length > 0);
    let label = `All ${dimension.plural}`;
    if (selected.length === 1) label = labelCache[key].get(selected[0]) || selected[0];
    else if (selected.length > 1) label = `${count(selected.length)} selected`;
    menu.value.textContent = label;
    menu.value.title = label;
    menu.trigger.setAttribute('aria-label', `${dimension.label}: ${label}`);
  }

  function updateFacets(facets) {
    for (const dimension of dimensions) {
      const values = Array.isArray(facets?.[dimension.key]) ? facets[dimension.key] : [];
      menus.get(dimension.key).values = values;
      values.forEach(option => labelCache[dimension.key].set(String(option.value), String(option.label || option.value)));
      renderMenuOptions(dimension.key);
    }
  }

  function updateControls() {
    const bounds = dateBounds(state);
    $('dateFrom').value = state.from || bounds.from || '';
    $('dateTo').value = state.to || bounds.to || '';
    $('dateBasis').value = state.dateBasis;
    $('dateBasisNote').hidden = state.dateBasis !== 'despatch';
    if ($('globalSearch') !== document.activeElement) $('globalSearch').value = state.search;
    $('outcome').value = state.outcome;
    $('metric').value = state.metric;
    $('sort').value = `${state.sort}:${state.direction}`;
    $('pageSize').value = String(state.pageSize);
    $('applyFilters').hidden = $('liveFilters').checked;
    $('applyFilters').disabled = !filtersDirty;
    $('applyFilters').textContent = filtersDirty ? 'Apply changes' : 'Applied';
    document.querySelectorAll('[data-group]').forEach(button => {
      const selected = button.dataset.group === state.groupBy;
      button.id = `group-${button.dataset.group}`;
      button.setAttribute('aria-controls', 'breakdownTableWrap');
      button.classList.toggle('is-selected', selected);
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
    });
    $('breakdownTableWrap').setAttribute('role', 'tabpanel');
    $('breakdownTableWrap').setAttribute('aria-labelledby', `group-${state.groupBy}`);
    document.querySelectorAll('[data-trend]').forEach(button => {
      const selected = button.dataset.trend === state.trendBucket;
      button.classList.toggle('is-selected', selected);
      button.setAttribute('aria-pressed', String(selected));
    });
    dimensions.forEach(d => updateMenuTrigger(d.key));
  }

  function renderActiveFilters() {
    const fragment = document.createDocumentFragment();
    let filterCount = 0;
    const chip = (label, action) => {
      const wrapper = node('span', 'filter-chip');
      const text = node('span', '', label);
      text.title = label;
      const button = node('button', '', '×');
      button.type = 'button';
      button.setAttribute('aria-label', `Remove filter: ${label}`);
      button.addEventListener('click', () => { action(); filtersChanged({ delay: 0 }); });
      wrapper.append(text, button);
      fragment.append(wrapper);
      filterCount++;
    };
    for (const dimension of dimensions) {
      const selected = state[dimension.key];
      if (selected.length <= 3) {
        selected.forEach(value => chip(`${dimension.label}: ${labelCache[dimension.key].get(value) || value}`, () => { state[dimension.key] = state[dimension.key].filter(item => item !== value); }));
      } else chip(`${dimension.label}: ${count(selected.length)} selected`, () => { state[dimension.key] = []; });
    }
    if (state.from || state.to) {
      const bounds = dateBounds(state);
      chip(dateRangeText(bounds.from, bounds.to), () => { state.from = null; state.to = null; });
    }
    if (state.outcome !== 'all') chip(state.outcome === 'fulfilled' ? 'Fulfilled only' : 'Not fulfilled only', () => { state.outcome = 'all'; });
    if (state.search.trim()) chip(`Search: ${state.search.trim()}`, () => { state.search = ''; });
    if (state.dateBasis !== 'request') chip('Despatch date', () => { state.dateBasis = 'request'; });
    $('activeFilters').replaceChildren(fragment);
    $('activeFilters').hidden = filterCount === 0;
    $('activeFilterCount').textContent = count(filterCount);
    $('activeFilterCount').hidden = filterCount === 0;
  }

  function render(result) {
    renderMetadata(result.metadata);
    updateFacets(result.facets);
    renderActiveFilters();
    renderSummary(result.summary);
    renderTrend(result.trend || []);
    renderStatuses(result.statuses || []);
    renderBreakdown(result);
    renderDrillPath();
    $('exportButton').disabled = !result.metadata?.hasData || !result.totalRows || exporting;
    $('viewRequests').disabled = !result.summary?.requests;
    savePreferences();
  }

  function renderMetadata(value) {
    if (!value) return;
    const hasData = Boolean(value.hasData);
    $('datasetStatusDot').className = `status-dot ${hasData ? 'ready' : 'warning'}`;
    $('datasetLabel').textContent = hasData ? dateRangeText(value.requestDateFrom, value.requestDateTo) : 'No dataset loaded yet';
    $('datasetMeta').textContent = hasData ? `${count(value.requestCount)} requests · ${(value.sourceFiles || []).length} source files` : 'An administrator can validate and upload the seven CSV files';
    const diagnostics = value.diagnostics || [];
    const issues = diagnostics.filter(item => ['warning', 'error'].includes(normalise(item.severity)));
    $('qualityButtonLabel').textContent = diagnostics.length ? `Data checks · ${diagnostics.length} note${diagnostics.length === 1 ? '' : 's'}` : 'Data checks passed';
    $('qualityButton').classList.toggle('no-issues', issues.length === 0);
    if (value.loadedAt && hasData) {
      const timestamp = new Date(value.loadedAt);
      if (!Number.isNaN(timestamp.getTime())) $('lastUpdated').textContent = `Updated ${new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/London' }).format(timestamp)} · UK time`;
    }
    const checks = document.createDocumentFragment();
    if (!diagnostics.length) {
      const card = node('div', `quality-check ${hasData ? 'success' : ''}`);
      card.append(node('strong', '', hasData ? 'No validation issues reported' : 'Waiting for source files'));
      card.append(node('p', '', hasData ? 'The active snapshot passed its import checks.' : 'Source validation runs before a dataset becomes active.'));
      checks.append(card);
    }
    diagnostics.forEach(item => {
      const severity = ['warning', 'error'].includes(normalise(item.severity)) ? normalise(item.severity) : '';
      const card = node('div', `quality-check ${severity}`);
      const title = node('div');
      const code = String(item.code || item.severity || 'Information').replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2');
      title.append(node('strong', '', code.charAt(0).toUpperCase() + code.slice(1)));
      if (finite(item.count) > 0) title.append(node('span', 'quality-count', count(item.count)));
      card.append(title, node('p', '', item.message));
      checks.append(card);
    });
    $('qualityChecks').replaceChildren(checks);
    const sources = document.createDocumentFragment();
    (value.sourceFiles || []).forEach(file => {
      const wrapper = node('div', 'source-file');
      const left = node('div');
      left.append(node('strong', '', file.name));
      if (finite(file.repairedRows) > 0) left.append(node('small', '', `${count(file.repairedRows)} rows repaired during CSV parsing`));
      wrapper.append(left, node('span', '', `${count(file.rows)} rows`));
      sources.append(wrapper);
    });
    if (!(value.sourceFiles || []).length) sources.append(node('p', 'muted', 'No active source files.'));
    $('sourceFiles').replaceChildren(sources);
  }

  function renderSummary(summary = {}) {
    const selected = metrics[appliedState.metric];
    const values = metricCounts(summary);
    const hasData = metadata?.hasData;
    $('demandKpiLabel').textContent = selected.label;
    $('demandKpi').textContent = hasData ? count(values.total) : '—';
    $('fulfilledKpi').textContent = hasData ? count(values.fulfilled) : '—';
    $('unfulfilledKpi').textContent = hasData ? count(values.unfulfilled) : '—';
    $('rateKpi').textContent = hasData ? rate(values.percentage, values.total) : '—';
    $('demandKpiSub').textContent = appliedState.metric === 'requests' ? `Containing ${count(summary.units)} requested units` : `Across ${count(summary.requests)} distinct requests`;
    $('fulfilledKpiSub').textContent = values.total > 0 ? `${rate(values.percentage)} of ${selected.noun}` : 'No matching demand';
    $('unfulfilledKpiSub').textContent = values.total > 0 ? `${rate(100 - values.percentage)} of ${selected.noun}` : 'No matching demand';
    $('rateKpiSub').textContent = `By ${selected.name}`;
    const bounds = dateBounds(appliedState);
    $('scopeSummary').textContent = `${dateRangeText(bounds.from, bounds.to)} · ${appliedState.dateBasis === 'despatch' ? 'Despatch date' : 'Request date'}`;
    const facts = document.createDocumentFragment();
    const entries = [['requests', 'requests'], ['units', 'part units'], ['lines', 'part lines'], ['manufacturers', 'manufacturers'], ['factories', 'factories'], ['products', 'products'], ['baseParts', 'BaseParts']];
    entries.forEach(([key, label]) => { const span = node('span'); span.append(node('strong', '', count(summary[key])), document.createTextNode(` ${label}`)); facts.append(span); });
    $('scopeFacts').replaceChildren(facts);
  }

  function niceMaximum(value) {
    if (value <= 0) return 4;
    const magnitude = 10 ** Math.floor(Math.log10(value));
    const ratio = value / magnitude;
    const step = ratio <= 2 ? magnitude / 2 : ratio <= 5 ? magnitude : magnitude * 2;
    return Math.ceil(value / step) * step;
  }

  function shortNumber(value) {
    if (value >= 1000000) return `${Number((value / 1000000).toFixed(1))}m`;
    if (value >= 1000) return `${Number((value / 1000).toFixed(1))}k`;
    return count(value);
  }

  function trendDateLabel(value) {
    if (appliedState.trendBucket === 'month') return formatDate(value, { day: undefined, month: 'short', year: '2-digit' });
    return formatDate(value, { day: '2-digit', month: '2-digit', year: undefined });
  }

  function renderTrend(rows) {
    const measure = metrics[appliedState.metric];
    $('trendSubtitle').textContent = `${measure.name.charAt(0).toUpperCase() + measure.name.slice(1)} by ${appliedState.dateBasis === 'despatch' ? 'despatch' : 'request'} date`;
    $('trendCaption').textContent = appliedState.trendBucket === 'week' ? 'Week commencing Monday. Partial weeks include only the selected dates.' : appliedState.trendBucket === 'month' ? 'Calendar months. Partial months include only the selected dates.' : 'Daily totals within the selected date range.';
    const host = $('trendChart');
    if (!rows.length || !rows.some(row => metricCounts(row).total > 0)) {
      host.replaceChildren(node('div', 'chart-placeholder', metadata?.hasData ? 'No demand matches these filters.' : 'Your demand trend will appear after an import.'));
      host.setAttribute('aria-label', 'No demand to plot.');
      return;
    }
    const width = 680, height = 205, left = 45, right = 8, top = 13, bottom = 34;
    const plotHeight = height - top - bottom;
    const plotWidth = width - left - right;
    const max = niceMaximum(Math.max(...rows.map(row => metricCounts(row).total)));
    const svg = svgNode('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': `Demand by ${appliedState.trendBucket}. Teal is fulfilled, amber is not fulfilled.` });
    const title = svgNode('title');
    title.textContent = `${measure.name} across ${rows.length} ${appliedState.trendBucket} periods`;
    svg.append(title);
    for (let tick = 0; tick <= 4; tick++) {
      const value = max * tick / 4;
      const y = top + plotHeight - plotHeight * tick / 4;
      svg.append(svgNode('line', { x1: left, x2: width - right, y1: y, y2: y, class: 'chart-gridline' }));
      const label = svgNode('text', { x: left - 10, y: y + 3.5, 'text-anchor': 'end', class: 'chart-axis-text' });
      label.textContent = shortNumber(value);
      svg.append(label);
    }
    const slot = plotWidth / rows.length;
    const barWidth = Math.min(46, Math.max(.5, slot * .65));
    const labelStride = Math.max(1, Math.ceil(rows.length / 8));
    rows.forEach((row, index) => {
      const values = metricCounts(row);
      const x = left + slot * index + (slot - barWidth) / 2;
      const fulfilledHeight = Math.max(0, values.fulfilled / max * plotHeight);
      const unfulfilledHeight = Math.max(0, values.unfulfilled / max * plotHeight);
      const group = svgNode('g', { class: 'trend-bar' });
      if (rows.length <= 60) group.setAttribute('tabindex', '0');
      const label = `${row.label || formatDate(row.date)}: ${count(values.total)} ${measure.noun}; ${count(values.fulfilled)} fulfilled; ${count(values.unfulfilled)} not fulfilled.`;
      group.setAttribute('aria-label', label);
      const tooltip = svgNode('title'); tooltip.textContent = label; group.append(tooltip);
      group.append(svgNode('rect', { x, y: top + plotHeight - fulfilledHeight, width: barWidth, height: fulfilledHeight, rx: 1.5, class: 'chart-bar-fulfilled' }));
      group.append(svgNode('rect', { x, y: top + plotHeight - fulfilledHeight - unfulfilledHeight, width: barWidth, height: unfulfilledHeight, rx: 1.5, class: 'chart-bar-unfulfilled' }));
      svg.append(group);
      if (index % labelStride === 0 || (index === rows.length - 1 && rows.length > 1 && rows.length % labelStride > 1)) {
        const date = svgNode('text', { x: left + slot * (index + .5), y: height - 10, 'text-anchor': 'middle', class: 'chart-axis-text' });
        date.textContent = trendDateLabel(row.date);
        svg.append(date);
      }
    });
    host.replaceChildren(svg);
    host.setAttribute('aria-label', `${appliedState.trendBucket}ly ${measure.name}, with fulfilled and not fulfilled quantities.`);
  }

  function statusPresentation(status) {
    switch (normalise(status).replace(/\s+/g, '')) {
      case 'satisfied': case 'satified': return { label: 'Fulfilled', detail: status, className: 'status-fulfilled' };
      case 'notreported': case 'notreports': return { label: 'Demand only', detail: status, className: 'status-demand-only' };
      case 'notsatisfied': return { label: 'Not despatched', detail: status, className: 'status-not-despatched' };
      case 'processing': return { label: 'Processing', detail: '', className: 'status-processing' };
      default: return { label: status || 'Unspecified', detail: '', className: 'status-other' };
    }
  }

  function renderStatuses(rows) {
    const sorted = [...rows].sort((a, b) => metricCounts(b).total - metricCounts(a).total);
    const total = sorted.reduce((sum, row) => sum + metricCounts(row).total, 0);
    $('outcomesSubtitle').textContent = `Share of ${metrics[appliedState.metric].name}`;
    const svg = svgNode('svg', { viewBox: '0 0 1000 9', preserveAspectRatio: 'none' });
    let offset = 0;
    const fragment = document.createDocumentFragment();
    sorted.forEach(row => {
      const amount = metricCounts(row).total;
      const presentation = statusPresentation(row.key || row.label);
      const percentage = total > 0 ? amount / total * 100 : 0;
      if (amount > 0 && total > 0) {
        svg.append(svgNode('rect', { x: offset, y: 0, width: percentage * 10, height: 9, class: presentation.className }));
        offset += percentage * 10;
      }
      const wrapper = node('div', 'status-row');
      const dot = node('i', `legend-dot ${presentation.className}`);
      dot.setAttribute('aria-hidden', 'true');
      const label = node('span', 'status-label', presentation.label);
      label.title = String(row.key || row.label);
      if (presentation.detail) label.append(node('small', '', presentation.detail));
      wrapper.append(dot, label, node('span', 'status-value', count(amount)), node('span', 'status-percent', rate(percentage, total)));
      fragment.append(wrapper);
    });
    if (!sorted.length) fragment.append(node('p', 'muted', 'No matching request outcomes.'));
    $('statusBar').replaceChildren(svg);
    $('statusList').replaceChildren(fragment);
  }

  function renderTableState(title, description, action) {
    const tr = node('tr');
    const td = node('td', 'table-state');
    td.colSpan = 7;
    td.append(node('strong', '', title), document.createTextNode(description));
    if (action) {
      td.append(node('br'));
      const button = node('button', 'button button-small', action.label);
      button.type = 'button';
      button.addEventListener('click', action.run);
      td.append(button);
    }
    tr.append(td);
    $('breakdownBody').replaceChildren(tr);
  }

  function renderBreakdown(result) {
    const group = groups[appliedState.groupBy];
    const measure = metrics[appliedState.metric];
    $('breakdownHeading').textContent = `Demand by ${group.name}`;
    $('breakdownSubtitle').textContent = group.hint;
    $('groupColumnHeading').textContent = group.heading;
    $('demandColumnHeading').textContent = appliedState.metric === 'units' ? 'Part units' : appliedState.metric === 'requests' ? 'Requests' : 'Part lines';
    $('basePartCountNote').hidden = appliedState.groupBy !== 'basePart' || appliedState.metric !== 'requests';
    document.querySelectorAll('[data-sort]').forEach(button => {
      button.classList.toggle('is-sorted', button.dataset.sort === appliedState.sort);
      button.dataset.direction = appliedState.direction;
      button.closest('th').setAttribute('aria-sort', button.dataset.sort === appliedState.sort ? (appliedState.direction === 'asc' ? 'ascending' : 'descending') : 'none');
    });
    const fragment = document.createDocumentFragment();
    (result.rows || []).forEach((row, index) => {
      const values = metricCounts(row);
      const tr = node('tr', 'group-row');
      const rankCell = node('td', 'rank-cell', count((finite(result.page || 1) - 1) * finite(result.pageSize || 25) + index + 1));
      const nameCell = node('td');
      const link = node('button', 'group-link');
      link.type = 'button';
      link.append(node('span', 'group-name', row.label || row.key));
      if (row.subtitle && row.subtitle !== row.label) link.append(node('span', 'group-subtitle', row.subtitle));
      link.setAttribute('aria-label', `${row.label || row.key}: ${appliedState.groupBy === 'basePart' ? 'view requests' : `drill into ${groups[group.next].plural}`}`);
      link.addEventListener('click', () => drillInto(row));
      nameCell.append(link);
      const demand = node('td', 'numeric demand-cell', count(values.total));
      const fulfilled = node('td', 'numeric fulfilled-cell', count(values.fulfilled));
      const unfulfilled = node('td', 'numeric unfulfilled-cell', count(values.unfulfilled));
      const rateCell = node('td', 'rate-cell');
      const rateDisplay = node('div', `rate-display${values.percentage < 50 ? ' rate-low' : ''}`);
      const bar = svgNode('svg', { viewBox: '0 0 100 6', 'aria-hidden': 'true', preserveAspectRatio: 'none' });
      bar.append(svgNode('rect', { width: 100, height: 6, class: 'rate-background' }), svgNode('rect', { width: Math.min(100, Math.max(0, values.percentage)), height: 6, class: 'rate-fill' }));
      rateDisplay.append(bar, node('span', '', rate(values.percentage, values.total)));
      rateCell.append(rateDisplay);
      const actionCell = node('td', 'row-action-column');
      const action = node('button', 'row-drill');
      action.type = 'button';
      action.append(chevron());
      action.setAttribute('aria-label', `Explore ${row.label || row.key}`);
      action.title = appliedState.groupBy === 'basePart' ? 'View matching requests' : `View ${groups[group.next].plural}`;
      action.addEventListener('click', () => drillInto(row));
      actionCell.append(action);
      tr.append(rankCell, nameCell, demand, fulfilled, unfulfilled, rateCell, actionCell);
      fragment.append(tr);
    });
    $('breakdownBody').replaceChildren(fragment);
    if (!(result.rows || []).length) {
      if (!metadata?.hasData) renderTableState('Ready for your first dataset', canManage ? 'Upload the seven CSV exports to start exploring demand.' : 'Ask an administrator to upload the seven CSV exports.', canManage ? { label: 'Go to data imports', run: () => { window.location.href = '/Imports'; } } : null);
      else renderTableState('No matching demand', 'Try clearing a filter or widening the date range.', { label: 'Reset filters', run: resetFilters });
    }
    const total = finite(result.totalRows);
    const page = finite(result.page) || 1;
    const pages = Math.max(1, finite(result.totalPages));
    const from = total ? (page - 1) * finite(result.pageSize) + 1 : 0;
    const to = Math.min(total, page * finite(result.pageSize));
    $('resultCount').textContent = total ? `${count(from)}–${count(to)} of ${count(total)} ${group.plural} · ${measure.name}` : `0 ${group.plural}`;
    $('pageLabel').textContent = `Page ${count(page)} of ${count(pages)}`;
    $('previousPage').disabled = page <= 1;
    $('nextPage').disabled = page >= pages;
  }

  async function drillInto(row) {
    const grouping = groups[state.groupBy];
    const wasBasePart = state.groupBy === 'basePart';
    drillHistory.push({ before: cloneState(state), label: String(row.label || row.key), dimension: grouping.dimension, value: String(row.key) });
    state[grouping.dimension] = [String(row.key)];
    state.groupBy = grouping.next;
    state.page = 1;
    state.sort = 'demand';
    state.direction = 'desc';
    filtersDirty = true;
    closeMenus();
    updateControls();
    renderActiveFilters();
    renderDrillPath();
    const success = await loadData();
    if (success && wasBasePart) openRequests();
    else if (success) $('breakdownHeading').focus({ preventScroll: true });
  }

  function renderDrillPath() {
    $('drillPath').hidden = drillHistory.length === 0;
    const fragment = document.createDocumentFragment();
    fragment.append(node('span', 'breadcrumb-root', 'All demand'));
    drillHistory.forEach(entry => {
      fragment.append(node('span', 'breadcrumb-separator', '›'), node('span', '', entry.label));
    });
    $('breadcrumbs').replaceChildren(fragment);
  }

  function drillBack() {
    const previous = drillHistory.pop();
    if (!previous) return;
    state = cloneState(previous.before);
    updateControls();
    renderActiveFilters();
    renderDrillPath();
    loadData();
  }

  function applyPaneVisibility() {
    permittedPanes.forEach(id => { $(id).hidden = hiddenPanes.has(id); });
    const visibleCharts = ['trendPanel', 'outcomePanel'].filter(id => !hiddenPanes.has(id)).length;
    $('chartGrid').classList.toggle('single-panel', visibleCharts === 1);
    $('chartGrid').classList.toggle('no-panels', visibleCharts === 0);
    $('restorePanes').hidden = hiddenPanes.size === 0;
  }

  function openDialog(id) {
    closeMenus();
    const dialog = $(id);
    if (!dialog.open) dialog.showModal();
  }

  function openRequests() {
    if (!data) return;
    requestState = cloneState(appliedState);
    requestPage = 1;
    const parts = dimensions.flatMap(d => requestState[d.key].length ? [`${requestState[d.key].length} ${d.label.toLowerCase()} selection${requestState[d.key].length === 1 ? '' : 's'}`] : []);
    const bounds = dateBounds(requestState);
    $('requestsSubtitle').textContent = `${dateRangeText(bounds.from, bounds.to)}${parts.length ? ` · ${parts.join(' · ')}` : ' · All matching requests'}`;
    openDialog('requestsDialog');
    loadRequests();
  }

  async function loadRequests() {
    requestController?.abort();
    requestController = new AbortController();
    const sequence = ++requestSequence;
    $('requestRows').replaceChildren(node('p', 'muted', 'Loading matching requests…'));
    $('previousRequestPage').disabled = true;
    $('nextRequestPage').disabled = true;
    try {
      const result = await api('/api/requests', { ...requestState, page: requestPage, pageSize: 50 }, requestController.signal);
      if (sequence !== requestSequence) return;
      requestPage = finite(result.page) || 1;
      requestPages = Math.max(1, finite(result.totalPages));
      const fragment = document.createDocumentFragment();
      (result.rows || []).forEach(request => {
        const card = node('article', 'request-card');
        const header = node('div', 'request-card-header');
        header.append(node('span', 'request-key', `Request ${request.key}`), node('span', `request-status${request.fulfilled ? ' fulfilled' : ''}`, request.status || 'Unspecified'));
        const product = node('p', 'request-card-product');
        product.append(node('strong', '', `${request.product} · ${request.productName || 'Unnamed product'}`));
        if (request.factory || request.manufacturer) product.append(node('br'), document.createTextNode([request.manufacturer, request.factory].filter(Boolean).join(' / ')));
        const meta = node('div', 'request-card-meta');
        meta.append(node('span', '', `Requested ${formatDate(request.date)}`), node('span', '', `${count(request.units)} units`), node('span', '', `${count(request.lines)} part lines`));
        if (request.despatchDate) meta.append(node('span', '', `Despatch date ${formatDate(request.despatchDate)}`));
        card.append(header, product, meta);
        if ((request.parts || []).length) {
          const details = node('details');
          details.append(node('summary', '', `View ${count(request.parts.length)} part line${request.parts.length === 1 ? '' : 's'}`));
          const wrap = node('div', 'request-parts-table-wrap');
          const table = node('table', 'request-parts-table');
          const thead = node('thead');
          const head = node('tr');
          ['PartID', 'BasePart / description', 'Quantity'].forEach(label => { const th = node('th', '', label); th.scope = 'col'; head.append(th); });
          thead.append(head);
          const tbody = node('tbody');
          request.parts.forEach(part => {
            const row = node('tr');
            const name = node('td', '', part.basePart || 'Unmapped part');
            if (part.description) name.append(node('span', 'part-description', part.description));
            row.append(node('td', '', part.partId), name, node('td', '', count(part.quantity)));
            tbody.append(row);
          });
          table.append(thead, tbody); wrap.append(table); details.append(wrap); card.append(details);
        } else card.append(node('p', 'request-card-meta', 'No part lines were supplied for this request.'));
        fragment.append(card);
      });
      if (!(result.rows || []).length) fragment.append(node('p', 'muted', 'No requests match the current filters.'));
      $('requestRows').replaceChildren(fragment);
      $('requestResultCount').textContent = `${count(result.totalRows)} matching requests`;
      $('requestPageLabel').textContent = `${count(requestPage)} / ${count(requestPages)}`;
      $('previousRequestPage').disabled = requestPage <= 1;
      $('nextRequestPage').disabled = requestPage >= requestPages;
    } catch (error) {
      if (error.name === 'AbortError' || sequence !== requestSequence) return;
      const notice = node('div', 'notice notice-error');
      notice.append(node('p', '', error.message));
      const retry = node('button', 'button button-small', 'Try again');
      retry.type = 'button'; retry.addEventListener('click', loadRequests); notice.append(retry);
      $('requestRows').replaceChildren(notice);
    }
  }

  function csvCell(value) {
    let text = value === null || value === undefined ? '' : String(value);
    // Spreadsheet programs execute leading =, +, -, and @ in imported text cells.
    if (typeof value === 'string' && /^[\s\u0000-\u001f]*[=+\-@]/.test(text)) text = `'${text}`;
    return `"${text.replace(/"/g, '""')}"`;
  }

  async function exportResults() {
    if (!data || exporting) return;
    exporting = true;
    const button = $('exportButton');
    button.disabled = true;
    button.querySelector('span').textContent = 'Preparing…';
    try {
      const selected = cloneState(appliedState);
      const first = await api('/api/explore', { ...selected, page: 1, pageSize: 100 });
      const rows = [...(first.rows || [])];
      const pages = Math.max(1, finite(first.totalPages));
      if (pages > 1000) throw new Error('This export is very large. Narrow your filters before exporting.');
      for (let page = 2; page <= pages; page += 4) {
        const numbers = Array.from({ length: Math.min(4, pages - page + 1) }, (_, index) => page + index);
        const responses = await Promise.all(numbers.map(number => api('/api/explore', { ...selected, page: number, pageSize: 100 })));
        for (const response of responses) {
          if (response.metadata?.loadedAt !== first.metadata?.loadedAt) throw new Error('The source data changed while this export was being prepared. Please export again for a consistent snapshot.');
          rows.push(...(response.rows || []));
        }
      }
      const headers = [`${groups[selected.groupBy].heading} key`, 'Name', 'Details', 'Requests', 'Fulfilled requests', 'Not fulfilled requests', 'Requested units', 'Fulfilled units', 'Not fulfilled units', 'Part lines', 'Fulfilled lines', 'Not fulfilled lines', `Fulfilment by ${metrics[selected.metric].noun} (%)`];
      const lines = [headers.map(csvCell).join(',')];
      rows.forEach(row => {
        const metric = metricCounts(row, selected.metric);
        lines.push([row.key, row.label || row.key, row.subtitle || '', finite(row.requests), finite(row.fulfilledRequests), finite(row.unfulfilledRequests), finite(row.units), finite(row.fulfilledUnits), finite(row.unfulfilledUnits), finite(row.lines), finite(row.fulfilledLines), finite(row.unfulfilledLines), metric.total > 0 ? Number(metric.percentage.toFixed(2)) : ''].map(csvCell).join(','));
      });
      const blob = new Blob(['\uFEFF', lines.join('\r\n')], { type: 'text/csv;charset=utf-8;' });
      const url = URL.createObjectURL(blob);
      const link = node('a');
      const bounds = dateBounds(selected);
      link.href = url;
      link.download = `spares-${selected.groupBy}-${bounds.from || 'all'}-${bounds.to || 'dates'}.csv`;
      document.body.append(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      showToast(`Exported all ${count(rows.length)} matching ${groups[selected.groupBy].plural}.`);
    } catch (error) { showError(error, 'The export could not be completed.'); }
    finally {
      exporting = false;
      button.querySelector('span').textContent = 'Export results';
      button.disabled = !data?.totalRows;
    }
  }

  async function checkForNewData() {
    if (document.hidden || !data || filtersDirty) return;
    try {
      const latest = await api('/api/metadata');
      if (latest.loadedAt !== metadata?.loadedAt) await loadData();
    } catch { /* Visible queries report connection problems; background checks remain quiet. */ }
  }

  async function heartbeat() {
    if (document.hidden) return;
    try { await api('/api/session/heartbeat', {}); } catch { /* The next user action will report an expired session. */ }
  }

  function bindEvents() {
    $('dateFrom').addEventListener('change', event => { state.from = event.target.value || null; filtersChanged({ delay: 0 }); });
    $('dateTo').addEventListener('change', event => { state.to = event.target.value || null; filtersChanged({ delay: 0 }); });
    $('dateBasis').addEventListener('change', event => { state.dateBasis = event.target.value; filtersChanged({ delay: 0 }); });
    $('allDates').addEventListener('click', () => { state.from = null; state.to = null; filtersChanged({ delay: 0 }); });
    $('globalSearch').addEventListener('input', event => { state.search = event.target.value; filtersChanged({ delay: 300 }); });
    $('outcome').addEventListener('change', event => { state.outcome = event.target.value; filtersChanged({ delay: 0 }); });
    $('metric').addEventListener('change', event => { state.metric = event.target.value; filtersChanged({ delay: 0, force: true, retainHistory: true }); });
    $('sort').addEventListener('change', event => { [state.sort, state.direction] = event.target.value.split(':'); filtersChanged({ delay: 0, force: true, retainHistory: true }); });
    $('pageSize').addEventListener('change', event => { state.pageSize = Number(event.target.value); filtersChanged({ delay: 0, force: true, retainHistory: true }); });
    $('liveFilters').addEventListener('change', () => { updateControls(); if ($('liveFilters').checked && filtersDirty) loadData(); });
    $('applyFilters').addEventListener('click', loadData);
    $('resetFilters').addEventListener('click', resetFilters);
    $('refreshButton').addEventListener('click', loadData);
    $('retryButton').addEventListener('click', loadData);
    $('exportButton').addEventListener('click', exportResults);
    $('drillBack').addEventListener('click', drillBack);
    $('previousPage').addEventListener('click', () => { if (state.page > 1) { state.page--; loadData(); } });
    $('nextPage').addEventListener('click', () => { if (data && state.page < data.totalPages) { state.page++; loadData(); } });
    $('viewRequests').addEventListener('click', openRequests);
    $('previousRequestPage').addEventListener('click', () => { if (requestPage > 1) { requestPage--; loadRequests(); } });
    $('nextRequestPage').addEventListener('click', () => { if (requestPage < requestPages) { requestPage++; loadRequests(); } });
    $('requestsDialog').addEventListener('close', () => { requestController?.abort(); });
    $('qualityButton').addEventListener('click', () => openDialog('qualityDialog'));
    $('filterCollapse').addEventListener('click', () => {
      const expanded = $('filterCollapse').getAttribute('aria-expanded') === 'true';
      $('filterCollapse').setAttribute('aria-expanded', String(!expanded));
      $('filterCollapse').setAttribute('aria-label', expanded ? 'Expand filters' : 'Collapse filters');
      $('filterContents').hidden = expanded;
      $('filterCollapse').closest('.filter-panel').classList.toggle('is-collapsed', expanded);
      closeMenus();
    });
    document.querySelectorAll('[data-group]').forEach(button => {
      button.addEventListener('click', () => { state.groupBy = button.dataset.group; filtersChanged({ delay: 0, force: true, retainHistory: true }); });
      button.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const tabs = [...document.querySelectorAll('[data-group]')];
        const index = tabs.indexOf(button);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
        tabs[next].focus(); tabs[next].click();
      });
    });
    document.querySelectorAll('[data-trend]').forEach(button => button.addEventListener('click', () => { state.trendBucket = button.dataset.trend; filtersChanged({ delay: 0, force: true, retainHistory: true }); }));
    document.querySelectorAll('[data-sort]').forEach(button => button.addEventListener('click', () => {
      const sort = button.dataset.sort;
      state.direction = state.sort === sort ? (state.direction === 'asc' ? 'desc' : 'asc') : (sort === 'name' || sort === 'fulfilment' ? 'asc' : 'desc');
      state.sort = sort;
      filtersChanged({ delay: 0, force: true, retainHistory: true });
    }));
    document.querySelectorAll('[data-hide-pane]').forEach(button => button.addEventListener('click', () => { hiddenPanes.add(button.dataset.hidePane); applyPaneVisibility(); savePreferences(); }));
    $('restorePanes').addEventListener('click', () => { hiddenPanes.clear(); applyPaneVisibility(); savePreferences(); });
    document.querySelectorAll('[data-close-dialog]').forEach(button => button.addEventListener('click', () => $(button.dataset.closeDialog).close()));
    document.querySelectorAll('dialog').forEach(dialog => dialog.addEventListener('click', event => { if (event.target === dialog) { const rect = dialog.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close(); } }));
    document.addEventListener('click', event => { if (!event.target.closest('.filter-menu')) closeMenus(); });
    document.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      for (const menu of menus.values()) if (!menu.popover.hidden) { menu.popover.hidden = true; menu.trigger.setAttribute('aria-expanded', 'false'); menu.trigger.focus(); event.preventDefault(); }
    });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) { heartbeat(); checkForNewData(); } });
  }

  buildFilterMenus();
  updateControls();
  applyPaneVisibility();
  bindEvents();
  loadData();
  heartbeat();
  setInterval(checkForNewData, 30000);
  setInterval(heartbeat, 60000);
})();
