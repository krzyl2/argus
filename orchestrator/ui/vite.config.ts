import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [preact()],
  base: './',
  build: {
    // MUST resolve to exactly .../Argus.Orchestrator/wwwroot — emptyOutDir wipes this
    // directory on every build. Never point this above wwwroot (would delete .cs files).
    outDir: resolve(__dirname, '../Argus.Orchestrator/wwwroot'),
    emptyOutDir: true,
  },
});
