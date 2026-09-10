// window.authClient — the browser side of BuildingBlocks.Auth.
//
// Holds the short-lived access token in memory only. The refresh token lives in an
// HttpOnly cookie scoped to /api/auth that JavaScript cannot read; the browser
// sends it automatically on /api/auth/refresh. authFetch() attaches the access
// token and, on a 401, silently refreshes once and retries.
//
// Loaded as a plain <script> in <head> so it is defined before the page's inline
// script runs. No dependencies.
window.authClient = (function () {
  let accessToken = null;
  let mustChangePassword = false;
  let twoFactorEnabled = false;
  let username = null;
  let role = null;
  let permissions = []; // perm claims from the token's user block

  function apply(body) {
    if (body && body.accessToken) {
      accessToken = body.accessToken;
      mustChangePassword = !!body.mustChangePassword;
      if (body.user) {
        if (typeof body.user.twoFactorEnabled === "boolean") twoFactorEnabled = body.user.twoFactorEnabled;
        if (body.user.username) username = body.user.username;
        if (body.user.role) role = body.user.role;
        if (Array.isArray(body.user.permissions)) permissions = body.user.permissions;
      }
    }
    return body;
  }

  async function login(user, password) {
    try {
      const r = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: user, password: password }),
      });
      const body = await r.json().catch(function () { return {}; });
      if (r.ok && body.accessToken) {
        apply(body);
        return { ok: true, mustChangePassword: !!body.mustChangePassword };
      }
      if (r.ok && body.ticket) {
        return { ok: false, twoFactor: true, ticket: body.ticket };
      }
      if (r.status === 401) return { ok: false, error: "Wrong username or password" };
      if (r.status === 429) return { ok: false, error: "Too many attempts — wait a few minutes and try again." };
      return { ok: false, error: (body && body.detail) || "Could not sign in" };
    } catch (_) {
      return { ok: false, error: "Could not reach the server — try again" };
    }
  }

  // Second step of a 2FA sign-in: exchange the ticket + code for tokens.
  async function completeTwoFactor(ticket, code) {
    try {
      const r = await fetch("/api/auth/login/2fa", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ticket: ticket, code: code }),
      });
      const body = await r.json().catch(function () { return {}; });
      if (r.ok && body.accessToken) {
        apply(body);
        return { ok: true, mustChangePassword: !!body.mustChangePassword };
      }
      return { ok: false, error: "That code is not right — try again." };
    } catch (_) {
      return { ok: false, error: "Could not reach the server — try again" };
    }
  }

  // --- 2FA management (all need a valid access token) ---
  async function setupTwoFactor() {
    const r = await authFetch("/api/auth/2fa/setup", { method: "POST" });
    return r.ok ? r.json() : null;
  }
  async function confirmTwoFactor(code) {
    const r = await authFetch("/api/auth/2fa/confirm", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code: code }),
    });
    const body = await r.json().catch(function () { return {}; });
    if (r.ok) { twoFactorEnabled = true; return { ok: true, recoveryCodes: body.recoveryCodes || [] }; }
    return { ok: false, error: (body && body.detail) || "Could not turn on two-factor" };
  }
  async function disableTwoFactor(currentPassword) {
    const r = await authFetch("/api/auth/2fa/disable", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ currentPassword: currentPassword }),
    });
    if (r.ok) { twoFactorEnabled = false; return { ok: true }; }
    const body = await r.json().catch(function () { return {}; });
    return { ok: false, error: (body && body.detail) || "Could not turn off two-factor" };
  }

  async function changeUsername(newUsername, currentPassword) {
    const r = await authFetch("/api/auth/change-username", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ newUsername: newUsername, currentPassword: currentPassword }),
    });
    const body = await r.json().catch(function () { return {}; });
    if (r.ok) { apply(body); return { ok: true }; }
    return { ok: false, error: (body && body.detail) || "Could not change username" };
  }

  // A refresh token is single-use and rotates, so two overlapping refreshes would
  // make the second one look like a replayed (stolen) token. Collapse concurrent
  // callers onto one in-flight call.
  let refreshing = null;
  function refresh() {
    if (refreshing) return refreshing;
    refreshing = (async function () {
      try {
        const r = await fetch("/api/auth/refresh", { method: "POST" });
        if (!r.ok) { accessToken = null; return false; }
        apply(await r.json().catch(function () { return {}; }));
        return !!accessToken;
      } catch (_) {
        return false;
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  }

  async function authFetch(url, opts) {
    opts = opts || {};
    function withAuth() {
      const h = Object.assign({}, opts.headers || {});
      if (accessToken) h["Authorization"] = "Bearer " + accessToken;
      return Object.assign({}, opts, { headers: h });
    }
    let r = await fetch(url, withAuth());
    if (r.status === 401 && (await refresh())) {
      r = await fetch(url, withAuth());
    }
    return r;
  }

  async function logout() {
    try { await fetch("/api/auth/logout", { method: "POST" }); } catch (_) {}
    accessToken = null;
    mustChangePassword = false;
  }

  return {
    login: login,
    completeTwoFactor: completeTwoFactor,
    refresh: refresh,
    authFetch: authFetch,
    logout: logout,
    apply: apply,
    setupTwoFactor: setupTwoFactor,
    confirmTwoFactor: confirmTwoFactor,
    disableTwoFactor: disableTwoFactor,
    changeUsername: changeUsername,
    isSignedIn: function () { return !!accessToken; },
    mustChangePassword: function () { return mustChangePassword; },
    clearMustChangePassword: function () { mustChangePassword = false; },
    twoFactorEnabled: function () { return twoFactorEnabled; },
    username: function () { return username; },
    canWrite: function () { return permissions.indexOf("state.write") !== -1; },
    canAdmin: function () { return role === "Admin" || permissions.indexOf("users.admin") !== -1; },
    // Test hook: drop the in-memory access token so the next authFetch has to go
    // through the silent-refresh path.
    _expireForTest: function () { accessToken = null; },
  };
})();
