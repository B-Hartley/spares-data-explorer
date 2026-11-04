// Deterministic, entirely synthetic examples. Never reads a real business export.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
const output = join(dirname(fileURLToPath(import.meta.url)), '..', 'SampleData');
mkdirSync(output, { recursive: true });
const quote = value => `"${String(value).replaceAll('"', '""')}"`;
function csv(name, headers, rows) { writeFileSync(join(output, name), [headers, ...rows].map(row => row.map(quote).join(',')).join('\r\n') + '\r\n'); }
let seed = 706;
const random = n => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return (seed >>> 8) % n; };
const manufacturers = ['Demo Home Furnishings', 'Demo Furniture Group', 'Demo Outdoor Living', 'Demo Living Essentials'];
const factories = ['Demo Cabinet Works', 'Demo Seating North', 'Demo Flatpack South', 'Demo Timber Studio', 'Demo Living East', 'Demo Garden Works', 'Demo Metalcraft', 'Demo Upholstery West'];
const furniture = ['Oak two-door wardrobe', 'Fabric three-seat sofa', 'White storage cabinet', 'Ash bedside table', 'Glass dining table', 'Garden lounge chair', 'Metal bed frame', 'Upholstered accent chair'];
const parts = [['DEMO-BOLT-M6', 'Demo M6 connector bolt'], ['DEMO-SCREW-40', 'Demo 40 mm wood screw'], ['DEMO-HINGE-110', 'Demo 110 degree hinge'], ['DEMO-FOOT-OAK', 'Demo tapered oak foot'], ['DEMO-HANDLE', 'Demo brushed handle'], ['DEMO-BRACKET', 'Demo corner bracket'], ['DEMO-PANEL', 'Demo replacement panel'], ['DEMO-CAP', 'Demo finishing cap']];
const products = [];
const partMappings = [];
for (let index = 0; index < 32; index++) {
  const product = `DEMO-P${String(index + 1).padStart(3, '0')}`;
  const base = `DEMO-B${String(index + 1).padStart(3, '0')}`;
  products.push(['DEMO', product, base, `Demo ${furniture[index % 8]} ${['natural', 'grey', 'white', 'walnut'][Math.floor(index / 8)]}`, manufacturers[index % 4], factories[index % 8]]);
  for (let p = 0; p < 4; p++) partMappings.push([base, ['01', '02', '03', 'K+L'][p], [4, 8, 2, 1][p], parts[(index + p) % parts.length][0]]);
}
const requests = [];
const requestParts = [];
for (let index = 0; index < 280; index++) {
  const productIndex = random(32);
  const product = products[productIndex];
  const key = `DEMO-R${String(index + 1).padStart(4, '0')}`;
  const day = 1 + random(31);
  const statusRoll = random(100);
  const status = statusRoll < 65 ? 'Satisfied' : statusRoll < 88 ? 'NotReported' : statusRoll < 98 ? 'NotSatisfied' : 'Processing';
  const date = `${String(day).padStart(2, '0')}/10/2026`;
  requests.push([date, key, product[1], status, status === 'Processing' ? '' : date, 'DEMO']);
  if (index % 53 === 0) continue;
  for (let p = 0, count = 1 + random(4); p < count; p++) requestParts.push([key, ['01', '02', '03', 'K+L'][p], 1 + random(8)]);
}
csv('CustomerRequests.csv', ['Date', 'Key', 'Product', 'Satisfied', 'DespatchDate', 'customer'], requests);
csv('custreqparts.csv', ['CR_Key', 'PartID', 'Qty'], requestParts);
csv('products.csv', ['Catalogue', 'Product', 'BaseProduct', 'Description', 'Manufacturer', 'Factory'], products);
csv('parts.csv', ['Product', 'PartID', 'Qty_Rqd', 'BasePart'], partMappings);
csv('baseparts.csv', ['BasePart', 'Description'], parts);
csv('manufacturers.csv', ['ManufacturerCode', 'Manufacturer', 'ChargeTo'], manufacturers.map((name, i) => [`DEMO-M${i + 1}`, name, 'DEMO']));
csv('factories.csv', ['FactoryID', 'FactoryName', 'Location', 'Retailer', 'Current'], factories.map((name, i) => [`DEMO-F${i + 1}`, name, 'Example location', 'DEMO', '-1']));
console.log('Generated seven synthetic sample CSV files. No real business data is included.');
