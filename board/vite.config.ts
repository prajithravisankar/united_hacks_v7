import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The board is served by nginx at the single origin in prod; in dev we proxy /api and /ws to the running
// stack (nginx on :8888) so the same relative URLs work here and there.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': { target: 'http://127.0.0.1:8888', changeOrigin: true },
      '/control': { target: 'http://127.0.0.1:8888', changeOrigin: true },
      '/ws': { target: 'ws://127.0.0.1:8888', ws: true },
    },
  },
})
