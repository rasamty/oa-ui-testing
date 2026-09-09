import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
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
        // the page's own inline script is served from the app root now
        entryFilter: (entry: any) =>
          /localhost:4173\/(?:$|\?|#)/.test(entry.url) || /Objective/i.test(entry.url),
        sourceFilter: (sourcePath: string) => !/node_modules/.test(sourcePath),
      },
    }],
  ],

  use: {
    baseURL: 'http://localhost:4173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  // The ASP.NET Core app serves the page AND the /api. It owns the SQLite database.
  webServer: {
    command: 'dotnet run --project src/Alignment.Api -c Release --no-launch-profile',
    url: 'http://localhost:4173/health',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    env: {
      ASPNETCORE_URLS: 'http://localhost:4173',
      ASPNETCORE_ENVIRONMENT: 'Development',
      Alignment__TestMode: 'true',
    },
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox',  use: { ...devices['Desktop Firefox'] } },
    { name: 'webkit',   use: { ...devices['Desktop Safari'] } },
  ],
});
