import { test as setup, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { TEST_USER, TEST_PASS, STORAGE_STATE } from './auth-constants';

/**
 * Runs once before every other project (see playwright.config.ts `dependencies`).
 * Makes sure the shared Playwright account exists and can sign in.
 *
 * It deliberately does NOT persist a session: Phase 3 refresh tokens are
 * single-use and rotate, so one saved cookie shared across parallel workers would
 * self-destruct on first use. Each test signs in for itself (see `openApp`).
 */
setup('ensure the test account exists', async ({ request }) => {
  const u = await request.post('/api/test/user', {
    data: { username: TEST_USER, password: TEST_PASS },
  });
  expect(u.ok(), 'POST /api/test/user should succeed').toBeTruthy();

  const login = await request.post('/api/auth/login', {
    data: { username: TEST_USER, password: TEST_PASS },
  });
  expect(login.ok(), 'the test user should be able to sign in').toBeTruthy();
  expect((await login.json()).accessToken, 'login returns an access token').toBeTruthy();

  // An empty storage state so the `storageState` option on each project resolves.
  fs.mkdirSync(path.dirname(STORAGE_STATE), { recursive: true });
  fs.writeFileSync(STORAGE_STATE, JSON.stringify({ cookies: [], origins: [] }));
});
