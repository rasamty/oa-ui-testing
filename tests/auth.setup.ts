import { test as setup, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { TEST_USER, TEST_PASS, STORAGE_STATE } from './auth-constants';

/**
 * Runs once before every other project (see playwright.config.ts `dependencies`).
 * Creates the shared Playwright user against the running app's database, signs
 * in, and saves the session cookie. Every test then starts already authenticated.
 */
setup('authenticate', async ({ request }) => {
  // test-only endpoint, idempotent
  const u = await request.post('/api/test/user', {
    data: { username: TEST_USER, password: TEST_PASS },
  });
  expect(u.ok(), 'POST /api/test/user should succeed').toBeTruthy();

  const login = await request.post('/api/auth/login', {
    data: { username: TEST_USER, password: TEST_PASS },
  });
  expect(login.ok(), 'the test user should be able to sign in').toBeTruthy();

  fs.mkdirSync(path.dirname(STORAGE_STATE), { recursive: true });
  await request.storageState({ path: STORAGE_STATE });
});
