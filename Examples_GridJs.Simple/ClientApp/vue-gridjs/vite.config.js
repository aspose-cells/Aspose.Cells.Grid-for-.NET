import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const backend = 'http://127.0.0.1:5079';

export default defineConfig({
  base: '/demos/vue/',
  plugins: [vue()],
  server: {
    proxy: {
      '/GridJs': { target: backend, changeOrigin: true },
      '/GridJs2': { target: backend, changeOrigin: true },
    },
  },
  build: {
    outDir: '../../wwwroot/demos/vue',
    emptyOutDir: true,
  },
});
