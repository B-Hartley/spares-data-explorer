/*
 * DEMO ONLY — a portable, read-only browser adapter for the bundled synthetic DEMO tables.
 * Production authentication and authoritative calculations remain in the .NET application.
 * This file never reads files, sends network requests, imports data or changes accounts.
 * Load window.SPARES_DEMO_DATA first, then this adapter, then the unchanged application JS.
 */
(() => {
  'use strict';

  const tableNames = ['customerrequests', 'custreqparts', 'products', 'parts', 'baseparts', 'manufacturers', 'factories'];
  const fileNames = { customerrequests: 'CustomerRequests.csv', custreqparts: 'custreqparts.csv', products: 'products.csv', parts: 'parts.csv', baseparts: 'baseparts.csv', manufacturers: 'manufacturers.csv', factories: 'factories.csv' };
  const countFormat = new Intl.NumberFormat('en-GB');
  const monthFormat = new Intl.DateTimeFormat('en-GB', { month: 'short', year: 'numeric', timeZone: 'UTC' });
  const text = value => String(value ?? '').trim();
  const normalise = value => text(value).toUpperCase();
  const fulfilledStatus = status => ['SATISFIED', 'SATIFIED'].includes(normalise(status));
  const compare = (a, b) => text(a).localeCompare(text(b), 'en-GB', { sensitivity: 'base' });
  const uniqueCount = values => new Set(values.map(normalise)).size;
  const sumUnits = parts => parts.reduce((sum, part) => sum + part.quantity, 0);
  const fallback = (value, other) => text(value) || other;

  let snapshot = null;
  let startupError = null;
  try { snapshot = makeSnapshot(window.SPARES_DEMO_DATA); }
  catch (error) { startupError = error instanceof Error ? error : new Error('The synthetic dataset could not be loaded.'); }

  function field(row, name) {
    const key = Object.keys(row).find(candidate => normalise(candidate) === normalise(name));
    return key === undefined ? '' : text(row[key]);
  }

  function indexUnique(rows, name, table) {
    const index = new Map();
    for (const row of rows) {
      const key = normalise(field(row, name));
      if (!key || index.has(key)) throw new Error(`The synthetic ${table} table has a missing or duplicate ${name}.`);
      index.set(key, row);
    }
    return index;
  }

  function syntheticId(value) { return /^DEMO[-_]/i.test(text(value)); }

  function makeSnapshot(source) {
    if (!source || !tableNames.every(name => Array.isArray(source[name])))
      throw new Error('The seven bundled synthetic DEMO tables are required.');
    if (tableNames.reduce((sum, name) => sum + source[name].length, 0) > 25000)
      throw new Error('This portable demonstration accepts only the bounded bundled sample.');
    const tables = Object.fromEntries(tableNames.map(name => [name, source[name].map(row => Object.freeze({ ...row }))]));
    if (tables.customerrequests.some(row => !syntheticId(field(row, 'Key')) || !syntheticId(field(row, 'Product')) || normalise(field(row, 'customer')) !== 'DEMO') ||
      tables.products.some(row => !syntheticId(field(row, 'Product')) || !syntheticId(field(row, 'BaseProduct'))) ||
      tables.baseparts.some(row => !syntheticId(field(row, 'BasePart'))) ||
      tables.parts.some(row => !syntheticId(field(row, 'Product')) || !syntheticId(field(row, 'BasePart'))) ||
      tables.custreqparts.some(row => !syntheticId(field(row, 'CR_Key'))))
      throw new Error('Only the bundled synthetic DEMO identifiers are accepted in this demonstration.');

    const products = indexUnique(tables.products, 'Product', 'products');
    const baseParts = indexUnique(tables.baseparts, 'BasePart', 'baseparts');
    const manufacturerCodes = indexUnique(tables.manufacturers, 'ManufacturerCode', 'manufacturers');
    const factoryIds = indexUnique(tables.factories, 'FactoryID', 'factories');
    const factoriesByName = new Map(tables.factories.map(row => [normalise(field(row, 'FactoryName')), row]));
    const mappings = new Map();
    for (const row of tables.parts) {
      const key = JSON.stringify([normalise(field(row, 'Product')), normalise(field(row, 'PartID'))]);
      if (mappings.has(key)) throw new Error('The synthetic Parts table has a duplicate Product/PartID mapping.');
      mappings.set(key, row);
    }

    const requests = [];
    const requestsByKey = new Map();
    for (const row of tables.customerrequests) {
      const key = field(row, 'Key');
      if (requestsByKey.has(normalise(key))) throw new Error('The synthetic request keys must be unique.');
      const productCode = field(row, 'Product');
      const product = products.get(normalise(productCode));
      let manufacturer = fallback(product && field(product, 'Manufacturer'), 'Unknown');
      if (manufacturerCodes.has(normalise(manufacturer))) manufacturer = fallback(field(manufacturerCodes.get(normalise(manufacturer)), 'Manufacturer'), manufacturer);
      let factory = fallback(product && field(product, 'Factory'), 'Unknown');
      let factoryRow = factoryIds.get(normalise(factory));
      if (factoryRow) factory = fallback(field(factoryRow, 'FactoryName'), factory);
      else factoryRow = factoriesByName.get(normalise(factory));
      const status = fallback(field(row, 'Satisfied') || field(row, 'Satified'), 'Unknown');
      const request = {
        key, date: dataDate(field(row, 'Date')), despatchDate: dataDate(field(row, 'DespatchDate'), true),
        product: productCode, productName: fallback(product && field(product, 'Description'), productCode),
        baseProduct: field(row, 'BaseProduct') || (product ? field(product, 'BaseProduct') : ''),
        manufacturer, factory, customer: fallback(field(row, 'customer'), 'Unknown'), status,
        fulfilled: fulfilledStatus(status), factoryLocation: factoryRow ? field(factoryRow, 'Location') : '', parts: []
      };
      requests.push(request);
      requestsByKey.set(normalise(key), request);
    }
    for (const row of tables.custreqparts) {
      const request = requestsByKey.get(normalise(field(row, 'CR_Key')));
      if (!request) throw new Error('A synthetic part line has no matching request.');
      const quantity = Number(field(row, 'Qty'));
      if (!field(row, 'Qty') || !Number.isFinite(quantity) || quantity < 0 || quantity > Number.MAX_SAFE_INTEGER)
        throw new Error('A synthetic part quantity is invalid.');
      const partId = field(row, 'PartID');
      const mapping = mappings.get(JSON.stringify([normalise(request.baseProduct), normalise(partId)]));
      const basePart = mapping ? field(mapping, 'BasePart') : `Unmapped: ${request.baseProduct || request.product} / ${partId}`;
      const definition = baseParts.get(normalise(basePart));
      request.parts.push(Object.freeze({ partId, basePart, description: definition ? fallback(field(definition, 'Description'), basePart) : mapping ? 'Description unavailable' : 'No matching product-part mapping', quantity }));
    }
    requests.forEach(request => { Object.freeze(request.parts); Object.freeze(request); });
    Object.freeze(requests);

    const dates = requests.map(request => request.date).sort();
    const despatchDates = requests.map(request => request.despatchDate).filter(Boolean).sort();
    const diagnostics = [{ severity: 'info', code: 'synthetic-demo', message: 'All figures in this portable demonstration are generated synthetic examples. They do not describe Click Spares demand or fulfilment.', count: requests.length }];
    const noParts = requests.filter(request => request.parts.length === 0).length;
    if (noParts) diagnostics.push({ severity: 'info', code: 'requests-without-parts', message: 'Requests with no recorded part lines remain in request totals, with zero part units.', count: noParts });
    const versionSource = JSON.stringify(tables);
    let hash = 2166136261;
    for (let i = 0; i < versionSource.length; i++) hash = Math.imul(hash ^ versionSource.charCodeAt(i), 16777619) >>> 0;
    const metadata = Object.freeze({
      version: `synthetic-demo-${hash.toString(16).padStart(8, '0')}`, loadedAt: new Date().toISOString(),
      hasData: requests.length > 0, requestCount: requests.length, lineCount: tables.custreqparts.length,
      requestDateFrom: dates[0] || null, requestDateTo: dates.at(-1) || null,
      despatchDateFrom: despatchDates[0] || null, despatchDateTo: despatchDates.at(-1) || null,
      sourceFiles: tableNames.map(name => ({ name: fileNames[name], rows: tables[name].length, repairedRows: 0 })), diagnostics
    });
    return Object.freeze({ requests, metadata });
  }

  function calendarDate(year, month, day) {
    const date = new Date(0);
    date.setUTCFullYear(year, month - 1, day);
    date.setUTCHours(0, 0, 0, 0);
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day)
      throw new Error('A synthetic date is not a valid calendar date.');
    return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
  }

  function dataDate(value, optional = false) {
    value = text(value);
    if (!value && optional) return null;
    const uk = /^(\d{1,2})\/(\d{1,2})\/(\d{4})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/.exec(value);
    const iso = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?)?$/.exec(value);
    const match = uk || iso;
    if (!match || Number(match[4] || 0) > 23 || Number(match[5] || 0) > 59 || Number(match[6] || 0) > 59)
      throw new Error('The synthetic dates must use UK or ISO format.');
    return uk ? calendarDate(Number(uk[3]), Number(uk[2]), Number(uk[1])) : calendarDate(Number(iso[1]), Number(iso[2]), Number(iso[3]));
  }

  function filterDate(value, name) {
    if (value === undefined || value === null || text(value) === '') return null;
    if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value.trim())) throw new Error(`${name} must be a date in yyyy-MM-dd format.`);
    return dataDate(value);
  }

  function prepare(input) {
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('The filter request must be an object.');
    const source = Object.fromEntries(Object.entries(input).map(([key, value]) => [key.toLowerCase(), value]));
    const choice = (name, options) => {
      const value = text(source[name]).toLowerCase() || options[0];
      if (!options.includes(value)) throw new Error(`${name} must be one of: ${options.join(', ')}.`);
      return value;
    };
    const selection = name => {
      const values = source[name] ?? [];
      if (!Array.isArray(values) || values.some(value => typeof value !== 'string')) throw new Error(`${name} must be an array of strings.`);
      return new Set(values.map(normalise).filter(Boolean));
    };
    const integer = (name, defaultValue, maximum) => {
      const value = source[name] ?? defaultValue;
      if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isInteger(value)) throw new Error(`${name} must be an integer.`);
      return Math.min(maximum, Math.max(1, value));
    };
    const from = filterDate(source.from, 'From'), to = filterDate(source.to, 'To');
    if (from && to && from > to) throw new Error('From must be on or before To.');
    const search = text(source.search);
    if (search.length > 500) throw new Error('Search must be 500 characters or fewer.');
    return {
      from, to, dateBasis: choice('datebasis', ['request', 'despatch']),
      manufacturers: selection('manufacturers'), factories: selection('factories'), products: selection('products'),
      baseParts: selection('baseparts'), statuses: selection('statuses'), customers: selection('customers'),
      outcome: choice('outcome', ['all', 'fulfilled', 'unfulfilled']), search: normalise(search),
      groupBy: choice('groupby', ['factory', 'manufacturer', 'product', 'basepart']),
      metric: choice('metric', ['requests', 'units', 'lines']), sort: choice('sort', ['demand', 'unfulfilled', 'fulfilment', 'name']),
      direction: choice('direction', ['desc', 'asc']), trendBucket: choice('trendbucket', ['week', 'day', 'month']),
      page: integer('page', 1, Number.MAX_SAFE_INTEGER), pageSize: integer('pagesize', 25, 100)
    };
  }

  const basisDate = (request, basis) => basis === 'despatch' ? request.despatchDate : request.date;
  const contains = (value, search) => normalise(value).includes(search);
  const selected = (set, value) => set.size === 0 || set.has(normalise(value));

  function filtered(query, excluding = '') {
    const matches = [];
    for (const request of snapshot.requests) {
      const date = basisDate(request, query.dateBasis);
      if (!date || query.from && date < query.from || query.to && date > query.to) continue;
      if (query.outcome === 'fulfilled' && !request.fulfilled || query.outcome === 'unfulfilled' && request.fulfilled) continue;
      if (excluding !== 'manufacturer' && !selected(query.manufacturers, request.manufacturer) ||
        excluding !== 'factory' && !selected(query.factories, request.factory) ||
        excluding !== 'product' && !selected(query.products, request.product) ||
        excluding !== 'status' && !selected(query.statuses, request.status) ||
        excluding !== 'customer' && !selected(query.customers, request.customer)) continue;
      const partFilter = excluding !== 'basepart' && query.baseParts.size > 0;
      const requestSearch = !query.search || ['key', 'product', 'productName', 'baseProduct', 'manufacturer', 'factory', 'customer', 'status'].some(key => contains(request[key], query.search));
      if (!partFilter && requestSearch) { matches.push({ request, parts: request.parts }); continue; }
      const parts = request.parts.filter(part => (!partFilter || selected(query.baseParts, part.basePart)) &&
        (requestSearch || ['partId', 'basePart', 'description'].some(key => contains(part[key], query.search))));
      if (parts.length) matches.push({ request, parts });
    }
    return matches;
  }

  function counts(matches) {
    const result = { requests: matches.length, fulfilledRequests: 0, units: 0, fulfilledUnits: 0, lines: 0, fulfilledLines: 0 };
    for (const match of matches) {
      const units = sumUnits(match.parts);
      result.units += units; result.lines += match.parts.length;
      if (match.request.fulfilled) { result.fulfilledRequests++; result.fulfilledUnits += units; result.fulfilledLines += match.parts.length; }
    }
    result.unfulfilledRequests = result.requests - result.fulfilledRequests;
    result.unfulfilledUnits = result.units - result.fulfilledUnits;
    result.unfulfilledLines = result.lines - result.fulfilledLines;
    result.requestFulfilment = result.requests ? result.fulfilledRequests / result.requests * 100 : 0;
    result.unitFulfilment = result.units ? result.fulfilledUnits / result.units * 100 : 0;
    result.lineFulfilment = result.lines ? result.fulfilledLines / result.lines * 100 : 0;
    return result;
  }

  function metricValues(count, metric) {
    const suffix = metric === 'units' ? 'Units' : metric === 'lines' ? 'Lines' : 'Requests';
    return { demand: count[metric], unfulfilled: count[`unfulfilled${suffix}`], fulfilment: count[metric === 'units' ? 'unitFulfilment' : metric === 'lines' ? 'lineFulfilment' : 'requestFulfilment'] };
  }

  function bucketAdd(map, key, value) {
    const normal = normalise(key);
    if (!map.has(normal)) map.set(normal, { key, items: [] });
    map.get(normal).items.push(value);
  }

  function groupRows(matches, query) {
    const buckets = new Map();
    for (const match of matches) {
      if (query.groupBy === 'basepart') {
        const parts = new Map();
        match.parts.forEach(part => bucketAdd(parts, part.basePart, part));
        parts.forEach(part => bucketAdd(buckets, part.key, { request: match.request, parts: part.items }));
      } else {
        const key = query.groupBy === 'manufacturer' ? match.request.manufacturer : query.groupBy === 'product' ? match.request.product : match.request.factory;
        bucketAdd(buckets, key, match);
      }
    }
    const rows = Array.from(buckets.values(), ({ key, items }) => {
      const first = items[0], value = counts(items);
      const products = uniqueCount(items.map(item => item.request.product));
      const label = query.groupBy === 'product' ? first.request.productName : query.groupBy === 'basepart' ? first.parts[0].description : key;
      let subtitle;
      if (query.groupBy === 'product') subtitle = `${key} · ${first.request.factory}`;
      else if (query.groupBy === 'basepart') subtitle = key;
      else if (query.groupBy === 'manufacturer') subtitle = `${countFormat.format(uniqueCount(items.map(item => item.request.factory)))} factories · ${countFormat.format(products)} products`;
      else subtitle = `${first.request.factoryLocation ? first.request.factoryLocation + ' · ' : ''}${countFormat.format(products)} products`;
      return { key, label, subtitle, ...value, ...metricValues(value, query.metric) };
    });
    const direction = query.direction === 'asc' ? 1 : -1;
    return rows.sort((a, b) => {
      const comparison = query.sort === 'name' ? compare(a.label, b.label) : a[query.sort] - b[query.sort];
      return direction * comparison || compare(a.label, b.label) || compare(a.key, b.key);
    });
  }

  function facets(matches, dimension) {
    const options = new Map();
    function add(value, label) {
      const key = normalise(value);
      if (!options.has(key)) options.set(key, { value, label, requests: 0 });
      options.get(key).requests++;
    }
    for (const match of matches) {
      if (dimension === 'basepart') {
        const seen = new Set();
        for (const part of match.parts) if (!seen.has(normalise(part.basePart))) {
          seen.add(normalise(part.basePart)); add(part.basePart, `${part.basePart} · ${part.description}`);
        }
      } else {
        const key = dimension === 'manufacturer' ? 'manufacturer' : dimension === 'factory' ? 'factory' : dimension === 'product' ? 'product' : dimension === 'status' ? 'status' : 'customer';
        add(match.request[key], dimension === 'product' ? `${match.request.productName} · ${match.request.product}` : match.request[key]);
      }
    }
    return Array.from(options.values()).sort((a, b) => compare(a.label, b.label) || compare(a.value, b.value));
  }

  function dateObject(iso) { return new Date(`${iso}T00:00:00Z`); }
  function isoDay(date) { return calendarDate(date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate()); }
  function startBucket(iso, bucket) {
    const date = dateObject(iso);
    if (bucket === 'month') date.setUTCDate(1);
    else if (bucket === 'week') date.setUTCDate(date.getUTCDate() - (date.getUTCDay() + 6) % 7);
    return isoDay(date);
  }
  function nextBucket(iso, bucket) {
    const date = dateObject(iso);
    if (bucket === 'month') date.setUTCMonth(date.getUTCMonth() + 1);
    else date.setUTCDate(date.getUTCDate() + (bucket === 'week' ? 7 : 1));
    return isoDay(date);
  }
  function trend(matches, query) {
    if (!matches.length) return [];
    const buckets = new Map();
    for (const match of matches) bucketAdd(buckets, startBucket(basisDate(match.request, query.dateBasis), query.trendBucket), match);
    const periods = Array.from(buckets.keys()).sort();
    const points = [];
    for (let date = periods[0]; date <= periods.at(-1); date = nextBucket(date, query.trendBucket)) {
      if (points.length >= 5000) throw new Error('Select a shorter date range or a larger trend period.');
      const value = counts(buckets.get(date)?.items || []);
      const uk = `${date.slice(8, 10)}/${date.slice(5, 7)}/${date.slice(0, 4)}`;
      const label = query.trendBucket === 'month' ? monthFormat.format(dateObject(date)) : query.trendBucket === 'week' ? `w/c ${uk}` : uk;
      points.push({ date, label, ...value, ...metricValues(value, query.metric) });
    }
    return points;
  }

  function explore(input) {
    const query = prepare(input), matches = filtered(query), rows = groupRows(matches, query);
    const totalPages = Math.max(1, Math.ceil(rows.length / query.pageSize));
    const page = Math.min(query.page, totalPages);
    const statusBuckets = new Map();
    matches.forEach(match => bucketAdd(statusBuckets, match.request.status, match));
    return {
      summary: { ...counts(matches),
        manufacturers: uniqueCount(matches.map(match => match.request.manufacturer)), factories: uniqueCount(matches.map(match => match.request.factory)),
        products: uniqueCount(matches.map(match => match.request.product)), baseParts: uniqueCount(matches.flatMap(match => match.parts.map(part => part.basePart))),
        noPartRequests: matches.filter(match => match.request.parts.length === 0).length },
      rows: rows.slice((page - 1) * query.pageSize, page * query.pageSize), trend: trend(matches, query),
      statuses: Array.from(statusBuckets.values(), bucket => ({ key: bucket.key, label: bucket.key, ...counts(bucket.items) })).sort((a, b) => compare(a.key, b.key)),
      facets: {
        manufacturers: facets(filtered(query, 'manufacturer'), 'manufacturer'), factories: facets(filtered(query, 'factory'), 'factory'),
        products: facets(filtered(query, 'product'), 'product'), baseParts: facets(filtered(query, 'basepart'), 'basepart'),
        statuses: facets(filtered(query, 'status'), 'status'), customers: facets(filtered(query, 'customer'), 'customer')
      },
      metadata: snapshot.metadata, groupBy: query.groupBy === 'basepart' ? 'basePart' : query.groupBy,
      metric: query.metric, dateBasis: query.dateBasis, sort: query.sort, direction: query.direction, trendBucket: query.trendBucket,
      page, pageSize: query.pageSize, totalRows: rows.length, totalPages
    };
  }

  function requestDetails(input) {
    const query = prepare(input), matches = filtered(query);
    matches.sort((a, b) => compare(basisDate(b.request, query.dateBasis), basisDate(a.request, query.dateBasis)) || compare(a.request.key, b.request.key));
    const totalPages = Math.max(1, Math.ceil(matches.length / query.pageSize)), page = Math.min(query.page, totalPages);
    return {
      rows: matches.slice((page - 1) * query.pageSize, page * query.pageSize).map(match => ({
        key: match.request.key, date: match.request.date, despatchDate: match.request.despatchDate,
        product: match.request.product, productName: match.request.productName, baseProduct: match.request.baseProduct,
        manufacturer: match.request.manufacturer, factory: match.request.factory, customer: match.request.customer,
        status: match.request.status, fulfilled: match.request.fulfilled, units: sumUnits(match.parts), lines: match.parts.length,
        parts: match.parts.map(part => ({ ...part }))
      })),
      page, pageSize: query.pageSize, totalRows: matches.length, totalPages
    };
  }

  function response(value, status = 200) {
    return new Response(status === 204 ? null : JSON.stringify(value), {
      status, headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Spares-Demo': 'synthetic-only' }
    });
  }

  window.fetch = async function demoFetch(input, init = {}) {
    const requestObject = typeof Request !== 'undefined' && input instanceof Request ? input : null;
    const signal = init.signal || requestObject?.signal;
    if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError');
    await Promise.resolve();
    if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError');
    try {
      const base = new URL(window.location.href);
      const url = new URL(requestObject ? requestObject.url : String(input), base);
      if (url.origin !== base.origin || url.protocol !== base.protocol)
        return response({ message: 'External requests are disabled in the portable synthetic demonstration.' }, 403);
      const method = text(init.method || requestObject?.method || 'GET').toUpperCase();
      const route = url.pathname;
      const expectedMethod = { '/api/metadata': 'GET', '/api/explore': 'POST', '/api/requests': 'POST', '/api/session': 'GET', '/api/session/heartbeat': 'POST' }[route];
      if (!expectedMethod) return response({ message: 'This synthetic demonstration is read-only. Imports, account changes and other server routes are disabled.' }, 403);
      if (method !== expectedMethod) return response({ message: `This demonstration endpoint requires ${expectedMethod}.` }, 405);
      if (route === '/api/session') return response({ authenticated: false, isAuthenticated: false, canManage: false, email: 'Synthetic demo', role: 'Demo', isDemo: true });
      if (route === '/api/session/heartbeat') return response(null, 204);
      if (startupError) return response({ message: startupError.message }, 400);
      if (route === '/api/metadata') return response(snapshot.metadata);
      const body = init.body === undefined && requestObject ? await requestObject.clone().text() : init.body;
      const payload = typeof body === 'string' && body.trim() ? JSON.parse(body) : {};
      const result = route === '/api/explore' ? explore(payload) : requestDetails(payload);
      if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError');
      return response(result);
    } catch (error) {
      if (error?.name === 'AbortError') throw error;
      return response({ message: error instanceof Error ? error.message : 'The synthetic query could not be completed.' }, 400);
    }
  };
})();
