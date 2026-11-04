#!/usr/bin/env node
// Build the portable synthetic demo. Real imports are deliberately not accepted as input.
import { readFile, writeFile, mkdir, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sampleDirectory = path.join(root, 'SampleData');
const args = process.argv.slice(2);
if (args.some(arg => arg !== '--preview')) throw new Error('Usage: node Scripts/Build-Demo.mjs [--preview]. Input is always the synthetic SampleData directory.');

const tables = [
  ['CustomerRequests.csv', 'customerrequests', ['Date', 'Key', 'Product', 'Satisfied', 'DespatchDate', 'customer']],
  ['custreqparts.csv', 'custreqparts', ['CR_Key', 'PartID', 'Qty']],
  ['products.csv', 'products', ['Catalogue', 'Product', 'BaseProduct', 'Description', 'Manufacturer', 'Factory']],
  ['parts.csv', 'parts', ['Product', 'PartID', 'Qty_Rqd', 'BasePart']],
  ['baseparts.csv', 'baseparts', ['BasePart', 'Description']],
  ['manufacturers.csv', 'manufacturers', ['ManufacturerCode', 'Manufacturer', 'ChargeTo']],
  ['factories.csv', 'factories', ['FactoryID', 'FactoryName', 'Location', 'Retailer', 'Current']]
];

function parseCsv(text, filename) {
  text = text.replace(/^\uFEFF/, '');
  const records = [];
  let record = [], field = '', quoted = false, closedQuote = false;
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (quoted) {
      if (character === '"') {
        if (text[index + 1] === '"') { field += '"'; index++; }
        else { quoted = false; closedQuote = true; }
      } else field += character;
      continue;
    }
    if (character === '"') {
      if (field.length || closedQuote) throw new Error(`${filename}: unexpected quotation mark.`);
      quoted = true;
    } else if (character === ',') {
      record.push(field); field = ''; closedQuote = false;
    } else if (character === '\r' || character === '\n') {
      record.push(field); field = ''; closedQuote = false;
      if (record.some(value => value.length)) records.push(record);
      record = [];
      if (character === '\r' && text[index + 1] === '\n') index++;
    } else {
      if (closedQuote && character.trim()) throw new Error(`${filename}: text after a closing quote.`);
      if (!closedQuote) field += character;
    }
  }
  if (quoted) throw new Error(`${filename}: an unclosed quoted field was found.`);
  if (field.length || record.length || closedQuote) {
    record.push(field);
    if (record.some(value => value.length)) records.push(record);
  }
  if (!records.length) throw new Error(`${filename}: no CSV header.`);
  const header = records.shift().map(value => value.trim());
  if (new Set(header).size !== header.length) throw new Error(`${filename}: duplicate headers.`);
  const rows = records.map((values, index) => {
    if (values.length !== header.length) throw new Error(`${filename}: row ${index + 2} has ${values.length} fields; expected ${header.length}.`);
    return Object.fromEntries(header.map((name, column) => [name, values[column].trim()]));
  });
  return { header, rows };
}

function requireMatch(rows, field, pattern, table) {
  for (const [index, row] of rows.entries()) {
    if (!pattern.test(String(row[field] || ''))) throw new Error(`Synthetic-data guard: ${table} row ${index + 2}, ${field} is not an approved demo value.`);
  }
}

const source = {};
const canonicalSampleDirectory = await realpath(sampleDirectory);
if (canonicalSampleDirectory !== sampleDirectory) throw new Error('Synthetic SampleData directory must not be a symlink.');
for (const [filename, key, headers] of tables) {
  const filenamePath = path.join(sampleDirectory, filename);
  if (await realpath(filenamePath) !== filenamePath) throw new Error(`Synthetic sample ${filename} must not be a symlink.`);
  const table = parseCsv(await readFile(filenamePath, 'utf8'), filename);
  if (table.header.join('\u0000') !== headers.join('\u0000')) throw new Error(`${filename}: expected the synthetic sample schema. No alternate or extra columns are permitted.`);
  if (!table.rows.length) throw new Error(`${filename}: the demo sample is empty.`);
  source[key] = table.rows;
}

// Fail closed if real business exports are accidentally placed in SampleData.
const demoId = /^DEMO(?:-|$)[A-Z0-9-]*$/;
const demoLabel = /^Demo(?:\s|$)/;
for (const field of ['Key', 'Product']) requireMatch(source.customerrequests, field, demoId, 'CustomerRequests');
requireMatch(source.customerrequests, 'customer', /^DEMO$/, 'CustomerRequests');
requireMatch(source.customerrequests, 'Satisfied', /^(Satisfied|Satified|NotSatisfied|NotReported|NotReports|Processing)$/, 'CustomerRequests');
requireMatch(source.custreqparts, 'CR_Key', demoId, 'custreqparts');
for (const field of ['Product', 'BaseProduct']) requireMatch(source.products, field, demoId, 'products');
for (const field of ['Description', 'Manufacturer', 'Factory']) requireMatch(source.products, field, demoLabel, 'products');
requireMatch(source.products, 'Catalogue', /^DEMO$/, 'products');
for (const field of ['Product', 'BasePart']) requireMatch(source.parts, field, demoId, 'parts');
requireMatch(source.baseparts, 'BasePart', demoId, 'baseparts');
requireMatch(source.baseparts, 'Description', demoLabel, 'baseparts');
requireMatch(source.manufacturers, 'ManufacturerCode', demoId, 'manufacturers');
requireMatch(source.manufacturers, 'Manufacturer', demoLabel, 'manufacturers');
requireMatch(source.manufacturers, 'ChargeTo', /^DEMO$/, 'manufacturers');
requireMatch(source.factories, 'FactoryID', demoId, 'factories');
requireMatch(source.factories, 'FactoryName', demoLabel, 'factories');
requireMatch(source.factories, 'Location', /^Example location$/, 'factories');
requireMatch(source.factories, 'Retailer', /^DEMO$/, 'factories');
for (const key of ['custreqparts', 'parts']) requireMatch(source[key], 'PartID', /^(?:[0-9]{1,3}|K\+L|\*)$/, key);

const [template, stylesheet, application, engine, favicon] = await Promise.all([
  readFile(path.join(root, 'Pages', 'Index.cshtml'), 'utf8'),
  readFile(path.join(root, 'wwwroot', 'css', 'app.css'), 'utf8'),
  readFile(path.join(root, 'wwwroot', 'js', 'app.js'), 'utf8'),
  readFile(path.join(root, 'demo', 'demo-engine.js'), 'utf8'),
  readFile(path.join(root, 'wwwroot', 'favicon.svg'), 'utf8')
]);

const doctype = template.indexOf('<!doctype html>');
if (doctype < 0) throw new Error('Dashboard template has no doctype.');
let html = template.slice(doctype);
let removedAdminBlocks = 0;
html = html.replace(/^[ \t]*@if\s*\(canManage\)\s*\r?\n[ \t]*\{[\s\S]*?^[ \t]*\}[ \t]*\r?\n/gm, () => { removedAdminBlocks++; return ''; });
if (removedAdminBlocks !== 2) throw new Error('The admin navigation changed. Review the demo transformation before publishing.');
html = html.replace('data-can-manage="@(canManage ? "true" : "false")"', 'data-can-manage="false" data-synthetic-demo="true"');
html = html.replace('@Html.AntiForgeryToken()', '<input type="hidden" name="__RequestVerificationToken" value="synthetic-demo-only">');
html = html.replace(/@initials\b/g, 'D').replace(/@email\b/g, 'Synthetic demo').replace('@(canManage ? "Administrator" : "Team member")', 'Illustrative dataset');
html = html.replace(/<form\b[^>]*asp-page="\/Logout"[^>]*>[\s\S]*?<\/form>/g, '');
html = html.replace(/asp-page="\/Index"/g, 'href="#"');
html = html.replace('<strong>Private workspace</strong>', '<strong>Offline demonstration</strong>');
html = html.replace('Approved Click Spares accounts.<br>Secure, shared data.', 'Invented example data.<br>No sign-in or imports in this demo.');
html = html.replace('<span class="prototype-label">Prototype</span>', '<span class="prototype-label">DEMO DATA</span>');
html = html.replace('<title>Demand &amp; fulfilment · Click Spares</title>', '<title>Interactive spares prototype · Synthetic demo data</title>');
html = html.replace('<meta name="description" content="Explore spare parts demand and fulfilment by manufacturer, factory, product and BasePart.">', '<meta name="description" content="Offline interactive prototype with invented spare parts demand and fulfilment data.">');
html = html.replace(/<link\b[^>]*rel="icon"[^>]*>/, `<link rel="icon" type="image/svg+xml" href="data:image/svg+xml;base64,${Buffer.from(favicon).toString('base64')}">`);
html = html.replace(/<link\b[^>]*href="~\/css\/app\.css"[^>]*>/, '');
html = html.replace(/<script\b[^>]*src="~\/js\/app\.js"[^>]*><\/script>/, '');

if (/@(?:if|inject|page|model|Html|email|initials)\b|@\(|asp-page=|asp-append-version=/.test(html)) throw new Error('Unprocessed Razor syntax remains in the demo.');
if (/<form\b/i.test(html) || /href="\/(?:Users|Usage|Imports|Login|Logout)/i.test(html)) throw new Error('A server-only navigation or form remains in the demo.');

const banner = `<div class="demo-banner" role="note"><span class="demo-banner-label">DEMO DATA</span><p><strong>Interactive prototype · synthetic data.</strong> Real CSVs are protected by staff login in the server application.</p><a href="https://github.com/B-Hartley/spares-data-explorer" target="_blank" rel="noopener noreferrer">Source on GitHub ↗</a></div>`;
html = html.replace('<main id="main-content" class="workspace">', `<main id="main-content" class="workspace">\n        ${banner}`);
const demoStyles = `
.demo-banner{position:sticky;top:12px;z-index:45;display:flex;align-items:center;gap:12px;background:#edf7f1;border:1px solid #cfe5d9;border-radius:9px;padding:12px 15px;margin-bottom:24px;box-shadow:0 3px 14px #183f2910;color:#537767}
.demo-banner-label{flex-shrink:0;padding:3px 6px;border:1px solid #b6d8c3;border-radius:4px;font-size:9px;font-weight:700;letter-spacing:.5px;color:#427a58}
.demo-banner p{font-size:10px;line-height:1.6;flex:1}.demo-banner strong{font-weight:650}.demo-banner a{font-size:10px;white-space:nowrap;color:#327857;font-weight:600}
@media(max-width:700px){.demo-banner{top:5px;gap:8px;flex-wrap:wrap;padding:11px 12px}.demo-banner p{min-width:190px}.demo-banner a{margin-left:auto;font-size:9px}.demo-banner-label{font-size:8px}}
@media print{.demo-banner{position:static;box-shadow:none}}
`;

function scriptSafe(text) { return text.replace(/<\/script/gi, '<\\/script').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029'); }
const serialisedData = JSON.stringify(source).replace(/</g, '\\u003c').replace(/&/g, '\\u0026').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
const policy = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; font-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">`;
html = html.replace('</head>', `${policy}\n<style>\n${stylesheet}\n${demoStyles}\n</style>\n</head>`);
html = html.replace('</body>', `<script>window.SPARES_DEMO_DATA=${serialisedData};</script>\n<script>\n${scriptSafe(engine)}\n</script>\n<script>\n${scriptSafe(application)}\n</script>\n</body>`);

html = html.replace(/[\t ]+$/gm, '');
const outputDirectory = path.join(root, 'demo');
await mkdir(outputDirectory, { recursive: true });
await writeFile(path.join(outputDirectory, 'spares-prototype.html'), html, 'utf8');
if (args.includes('--preview')) {
  const previewRoot = path.resolve(root, '..', 'demo-preview');
  await mkdir(path.join(previewRoot, 'dist'), { recursive: true });
  await mkdir(path.join(previewRoot, '.openai'), { recursive: true });
  await writeFile(path.join(previewRoot, 'dist', 'index.html'), html, 'utf8');
  await writeFile(path.join(previewRoot, '.openai', 'hosting.json'), JSON.stringify({ static: { directory: 'dist' } }, null, 2) + '\n', 'utf8');
}
console.log(`Built demo/spares-prototype.html (${Buffer.byteLength(html).toLocaleString('en-GB')} bytes) from ${source.customerrequests.length} synthetic requests. No production data read.`);
