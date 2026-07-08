import { defineConfig } from 'vitest/config';
import preact from '@preact/preset-vite';

export default defineConfig({
  plugins: [preact()],
  test: {
    environment: 'jsdom',
    globals: true,
    // Node 22+'s built-in experimental Web Storage API shadows jsdom's
    // window.localStorage with a stub that throws without --localstorage-file.
    // Disable it so jsdom's per-test localStorage implementation is used instead.
    execArgv: ['--no-experimental-webstorage'],
  },
});
