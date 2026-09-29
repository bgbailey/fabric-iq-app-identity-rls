import { defineConfig } from 'vitest/config';

export default defineConfig({
  base: '/',
  build: {
    outDir: '../SemanticGateway/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5187',
      '/dev-idp': 'http://localhost:5187',
      '/mcp': 'http://localhost:5187',
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    clearMocks: true,
    restoreMocks: true,
  },
});
