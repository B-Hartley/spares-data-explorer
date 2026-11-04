import { spawn } from 'node:child_process';
import { join } from 'node:path';
const executable = process.env.DOTNET_ROOT ? join(process.env.DOTNET_ROOT, process.platform === 'win32' ? 'dotnet.exe' : 'dotnet') : 'dotnet';
const args = process.argv.slice(2);
const option = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const port = option('--port') || process.env.PORT || '3000';
const host = option('--host') || '127.0.0.1';
if (!/^\d{1,5}$/.test(port) || Number(port) > 65535 || !['localhost', '127.0.0.1', '::1'].includes(host)) {
  console.error('Use a valid local preview port and a loopback host.'); process.exit(1);
}
const url = `http://${host === '::1' ? '[::1]' : host}:${port}`;
console.log(`Starting the ASP.NET development preview at ${url}`);
const child = spawn(executable, ['run', '--no-launch-profile', '--project', 'ClickSpares.SparesExplorer.csproj', '--urls', url], {
  stdio: 'inherit', env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
});
child.on('error', error => { console.error(error.message); process.exit(1); });
child.on('exit', code => process.exit(code ?? 1));
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => child.kill(signal));
