(() => {
  "use strict";

  const elements = {
    accountLabel: document.querySelector("#account-label"),
    authPanel: document.querySelector("#auth-panel"),
    emptyState: document.querySelector("#empty-state"),
    errorMessage: document.querySelector("#error-message"),
    errorPanel: document.querySelector("#error-panel"),
    filter: document.querySelector("#project-filter"),
    projectCount: document.querySelector("#project-count"),
    refreshButton: document.querySelector("#refresh-button"),
    registry: document.querySelector("#registry"),
    retryButton: document.querySelector("#retry-button"),
    rows: document.querySelector("#project-rows"),
    signInButton: document.querySelector("#sign-in-button"),
    signOutButton: document.querySelector("#sign-out-button"),
    stateDot: document.querySelector("#state-dot"),
    stateLabel: document.querySelector("#state-label"),
    template: document.querySelector("#project-row-template")
  };

  let authClient;
  let discovery;
  let projects = [];

  function setState(label, kind) {
    elements.stateLabel.textContent = label;
    elements.stateDot.className = `state-dot${kind ? ` ${kind}` : ""}`;
  }

  function setBusy(button, busy) {
    button.disabled = busy;
    button.setAttribute("aria-busy", String(busy));
  }

  function showAuthenticated(account) {
    elements.accountLabel.textContent = account.username || account.name || "Signed in";
    elements.accountLabel.hidden = false;
    elements.refreshButton.hidden = false;
    elements.signOutButton.hidden = false;
    elements.authPanel.hidden = true;
  }

  function showUnauthenticated() {
    elements.accountLabel.hidden = true;
    elements.refreshButton.hidden = true;
    elements.signOutButton.hidden = true;
    elements.registry.hidden = true;
    elements.errorPanel.hidden = true;
    elements.authPanel.hidden = false;
    setState("Sign-in required", "");
  }

  function showError(error) {
    const message = error?.message || "The project registry could not be loaded.";
    elements.errorMessage.textContent = message;
    elements.errorPanel.hidden = false;
    elements.registry.hidden = true;
    setState("Unavailable", "error");
  }

  function validRepositoryUrl(value) {
    if (!value) return null;
    try {
      const url = new URL(value);
      return url.protocol === "https:" || url.protocol === "http:" ? url : null;
    } catch {
      return null;
    }
  }

  function formatCreatedAt(value) {
    const date = new Date(value);
    if (Number.isNaN(date.valueOf())) return { text: "Unknown", machine: "" };
    return {
      text: new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date),
      machine: date.toISOString()
    };
  }

  function renderProjects() {
    const filter = elements.filter.value.trim().toLocaleLowerCase();
    const visible = projects.filter((project) =>
      [project.name, project.repositoryUrl, project.id]
        .filter(Boolean)
        .some((value) => String(value).toLocaleLowerCase().includes(filter)));

    elements.rows.replaceChildren();
    for (const project of visible) {
      const row = elements.template.content.cloneNode(true);
      row.querySelector(".project-name").textContent = project.name;
      row.querySelector(".project-id").textContent = project.id;

      const repositoryCell = row.querySelector(".repository-cell");
      const repositoryUrl = validRepositoryUrl(project.repositoryUrl);
      if (repositoryUrl) {
        const link = document.createElement("a");
        link.className = "repository-link";
        link.href = repositoryUrl.href;
        link.target = "_blank";
        link.rel = "noreferrer";
        link.textContent = project.repositoryUrl;
        repositoryCell.append(link);
      } else {
        const empty = document.createElement("span");
        empty.className = "repository-empty";
        empty.textContent = "Not provided";
        repositoryCell.append(empty);
      }

      const created = formatCreatedAt(project.createdAt);
      const time = row.querySelector(".created-at");
      time.textContent = created.text;
      time.dateTime = created.machine;
      elements.rows.append(row);
    }

    elements.emptyState.hidden = visible.length !== 0;
  }

  async function acquireAccessToken() {
    const account = authClient.getActiveAccount() || authClient.getAllAccounts()[0];
    if (!account) return null;
    authClient.setActiveAccount(account);
    const request = { account, scopes: [discovery.delegatedScope] };
    try {
      return (await authClient.acquireTokenSilent(request)).accessToken;
    } catch (error) {
      if (error instanceof msal.InteractionRequiredAuthError) {
        await authClient.acquireTokenRedirect(request);
        return null;
      }
      throw error;
    }
  }

  async function loadProjects() {
    elements.errorPanel.hidden = true;
    setState("Loading", "");
    const token = await acquireAccessToken();
    if (!token) {
      showUnauthenticated();
      return;
    }

    const response = await fetch("/v1/projects", {
      headers: { Authorization: `Bearer ${token}` }
    });
    if (!response.ok) throw new Error(`Project request failed with status ${response.status}.`);

    projects = await response.json();
    projects.sort((left, right) => new Date(right.createdAt) - new Date(left.createdAt));
    elements.projectCount.textContent = String(projects.length);
    elements.registry.hidden = false;
    renderProjects();
    setState("Connected", "ready");
  }

  async function signIn() {
    setBusy(elements.signInButton, true);
    try {
      await authClient.loginRedirect({ scopes: [discovery.delegatedScope], prompt: "select_account" });
    } catch (error) {
      showError(error);
      setBusy(elements.signInButton, false);
    }
  }

  async function initialize() {
    try {
      discovery = await fetch("/.well-known/factlineage-mcp.json").then((response) => {
        if (!response.ok) throw new Error("Instance discovery is unavailable.");
        return response.json();
      });
      authClient = new msal.PublicClientApplication({
        auth: {
          clientId: discovery.clientId,
          authority: discovery.authority,
          redirectUri: `${window.location.origin}/projects/`,
          postLogoutRedirectUri: `${window.location.origin}/projects/`
        },
        cache: { cacheLocation: "sessionStorage" }
      });
      await authClient.initialize();
      const redirectResult = await authClient.handleRedirectPromise();
      if (redirectResult?.account) authClient.setActiveAccount(redirectResult.account);

      const account = authClient.getActiveAccount() || authClient.getAllAccounts()[0];
      if (!account) {
        showUnauthenticated();
        return;
      }
      authClient.setActiveAccount(account);
      showAuthenticated(account);
      await loadProjects();
    } catch (error) {
      showError(error);
    }
  }

  elements.filter.addEventListener("input", renderProjects);
  elements.signInButton.addEventListener("click", signIn);
  elements.retryButton.addEventListener("click", () => loadProjects().catch(showError));
  elements.refreshButton.addEventListener("click", async () => {
    setBusy(elements.refreshButton, true);
    try { await loadProjects(); } catch (error) { showError(error); }
    finally { setBusy(elements.refreshButton, false); }
  });
  elements.signOutButton.addEventListener("click", () => authClient.logoutRedirect({ account: authClient.getActiveAccount() }));

  window.addEventListener("load", initialize, { once: true });
})();
