/**
 * Post-build script that updates umbraco-package.json with the hashed entry point filename.
 * This ensures browser cache busting when the package is updated.
 */
import { readFileSync, writeFileSync, readdirSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(__dirname, '../../wwwroot/App_Plugins/WindowsAuthentication');
const manifestPath = resolve(outDir, 'umbraco-package.json');

// Find the hashed entry point filename
const files = readdirSync(outDir);
const entryFile = files.find(f => f.startsWith('windows-authentication-') && f.endsWith('.js') && !f.endsWith('.map'));

if (!entryFile) {
  console.error('Could not find hashed entry point file in', outDir);
  process.exit(1);
}

// Read and update the manifest
const manifest = JSON.parse(readFileSync(manifestPath, 'utf-8'));
const jsPath = `/App_Plugins/WindowsAuthentication/${entryFile}`;

for (const ext of manifest.extensions) {
  if (ext.type === 'appEntryPoint') {
    ext.js = jsPath;
  }
}

writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
console.log(`Updated umbraco-package.json: js → ${jsPath}`);
