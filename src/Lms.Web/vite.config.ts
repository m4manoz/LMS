import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The browser tests start their own API on another port and point the dev server at it.
const api = process.env.LMS_API_URL ?? 'http://localhost:5106'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  // These are only loaded when a video plays or a class room opens. Preparing them up front stops the dev server from
  // discovering them mid-session and reloading the page.
  optimizeDeps: { include: ['livekit-client', 'hls.js'] },
  server: {
    port: 5173,
    proxy: {
      '/api': api,
      '/health': api,
    },
  },
})
