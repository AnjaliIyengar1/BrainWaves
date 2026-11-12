import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/hubs': { target: 'http://localhost:5236', ws: true }, // forward hub + WebSocket to DataProcessor
    },
  },
})