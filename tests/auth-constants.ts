import path from 'node:path';

/** Shared Playwright account and where its saved session lives. Not a test file. */
export const TEST_USER = 'playwright';
export const TEST_PASS = 'pw-test-secret-42';
export const STORAGE_STATE = path.join('tests', '.auth', 'state.json');
