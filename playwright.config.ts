import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  globalTeardown: './tests/audit-report.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: [
    ['list'],
    ['html', { open: 'never' }],
    ['monocart-reporter', {
      name: 'Objective Alignment — coverage',
      outputFile: './coverage/index.html',
      coverage: {
        reports: ['v8', 'console-summary'],
        // keep only the page's own inline script; drop browser internals and the xlsx CDN
        entryFilter: (entry: any) => /Objective(%20| )Alignment/.test(entry.url),
        sourceFilter: (sourcePath: string) => /Objective|v1[01]/.test(sourcePath),
      },
    }],
  ],

  use: {
    baseURL: 'http://localhost:4173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  webServer: {
    command: 'npx http-server . -p 4173 -c-1 --silent',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox',  use: { ...devices['Desktop Firefox'] } },
    { name: 'webkit',   use: { ...devices['Desktop Safari'] } },
  ],
});
