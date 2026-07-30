import { defineConfig } from 'vite';

const backend = 'http://127.0.0.1:5079';

export default defineConfig({
  base: '/demos/npm/',
  server: {
    proxy: {
      '/GridJs': { target: backend, changeOrigin: true },
    },
  },
  build: {
    outDir: '../../wwwroot/demos/npm',
    emptyOutDir: true,
  },
});
