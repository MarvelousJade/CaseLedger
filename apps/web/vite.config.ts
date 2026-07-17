import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    clearMocks: true,
    restoreMocks: true,
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5150',
        changeOrigin: true,
      },
      '/graphql': {
        target: 'http://localhost:5150',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5150',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
