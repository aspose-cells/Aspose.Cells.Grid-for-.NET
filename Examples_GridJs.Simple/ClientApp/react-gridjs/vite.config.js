import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const backend = 'http://127.0.0.1:5079';

export default defineConfig({
  base: '/demos/react/',
  plugins: [react()],
  server: {
    proxy: {
      '/GridJs': { target: backend, changeOrigin: true },
      '/GridJs2': { target: backend, changeOrigin: true },
    },
  },
  build: {
    outDir: '../../wwwroot/demos/react',
    emptyOutDir: true,
  },
});
