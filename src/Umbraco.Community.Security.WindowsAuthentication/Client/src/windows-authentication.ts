/**
 * Windows Authentication client script for the Umbraco backoffice.
 *
 * Under IIS Windows Authentication the browser needs the Authorization header for the Negotiate/NTLM handshake. The
 * backoffice puts its bearer value there instead, so IIS rejects the request and the handshake never happens. This
 * module moves same-origin bearer values into X-Umb-Authorization, and the server-side middleware moves them back
 * before authentication runs.
 *
 * IIS also adds its Windows challenge to every 401, which would make the browser prompt for Windows credentials when a
 * backoffice session ends. The server sends those 401s as 403 with X-Umb-Authorization-Status: 401, and this module
 * turns them back into 401s before any backoffice code sees them.
 *
 * The patch is applied when the module is evaluated. It is registered as an `appEntryPoint` in a package with
 * `allowPublicAccess`, so umb-app loads it from the public manifest and waits for it before routing. Every bearer
 * request (private manifests, current user, SignalR negotiate, preview) happens after routing.
 */

const BACKOFFICE_HEADER = 'X-Umb-Authorization';
const STATUS_HEADER = 'X-Umb-Authorization-Status';
const MANAGEMENT_API_PATH = '/umbraco/management/api/';

// Management API endpoints umb-app calls anonymously before app entry points have loaded. Anything else seen before
// the client script was installed may have gone out with a bearer token.
const ANONYMOUS_BOOT_PATHS = ['/server/status', '/server/configuration', '/manifest/manifest/public', '/security/back-office/'];

export interface WindowsAuthenticationState {
	installedAt: number;
	rewrites: { fetch: number; xhr: number };
	/** 401 responses the server sent as 403 to keep IIS from adding its Windows challenge, restored for the caller. */
	unauthorizedRestored: number;
	/** Management API requests that started before the client script was installed. */
	preInstallRequests: string[];
}

declare global {
	interface Window {
		__umbWindowsAuthentication?: WindowsAuthenticationState;
	}
}

// The Management API normally shares the page's origin, but umb-app can be pointed elsewhere with its server-url attribute.
function getServerOrigin(): string | undefined {
	const serverUrl = document.querySelector('umb-app')?.getAttribute('server-url');
	if (!serverUrl) return undefined;

	try {
		return new URL(serverUrl, document.baseURI).origin;
	} catch {
		return undefined;
	}
}

// Only backoffice requests are touched; a bearer token for any other origin is left alone.
function isBackofficeOrigin(url: string | URL): boolean {
	try {
		const origin = new URL(url, document.baseURI).origin;
		return origin === location.origin || origin === getServerOrigin();
	} catch {
		return false;
	}
}

// Must match the server: the Bearer scheme followed by a token, not just the scheme name.
function isBearer(value: string | null): value is string {
	return value !== null && /^bearer +\S/i.test(value);
}

// How the server marks a 401 it had to send as 403. Only honoured on requests this module rewrote.
function isHiddenUnauthorized(status: number, marker: string | null): boolean {
	return status === 403 && marker === '401';
}

function restoreUnauthorized(response: Response, state: WindowsAuthenticationState): Response {
	if (!isHiddenUnauthorized(response.status, response.headers.get(STATUS_HEADER))) {
		return response;
	}

	state.unauthorizedRestored++;
	const headers = new Headers(response.headers);
	headers.delete(STATUS_HEADER);

	const restored = new Response(response.body, { status: 401, statusText: 'Unauthorized', headers });
	// A constructed Response has no URL of its own, and callers read it.
	Object.defineProperties(restored, {
		url: { value: response.url },
		redirected: { value: response.redirected },
	});
	return restored;
}

function patchFetch(state: WindowsAuthenticationState) {
	const originalFetch = window.fetch.bind(window);

	window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
		if (!isBackofficeOrigin(input instanceof Request ? input.url : input)) {
			return originalFetch(input, init);
		}

		// init.headers replaces a Request's headers entirely, so this is the set the request will actually carry.
		const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
		const authorization = headers.get('Authorization');

		if (!isBearer(authorization)) {
			return originalFetch(input, init);
		}

		headers.set(BACKOFFICE_HEADER, authorization);
		headers.delete('Authorization');
		state.rewrites.fetch++;

		return originalFetch(input, { ...init, headers }).then((response) => restoreUnauthorized(response, state));
	};
}

function patchXhr(state: WindowsAuthenticationState) {
	const sameOriginRequests = new WeakSet<XMLHttpRequest>();
	const rewrittenRequests = new WeakSet<XMLHttpRequest>();
	const restoredRequests = new WeakSet<XMLHttpRequest>();
	const proto = XMLHttpRequest.prototype;
	const originalOpen = proto.open as (this: XMLHttpRequest, ...args: unknown[]) => void;
	const originalSetRequestHeader = proto.setRequestHeader;
	const originalGetResponseHeader = proto.getResponseHeader;
	const statusGetter = Object.getOwnPropertyDescriptor(proto, 'status')!.get!;
	const statusTextGetter = Object.getOwnPropertyDescriptor(proto, 'statusText')!.get!;

	proto.open = function (this: XMLHttpRequest, method: string, url: string | URL, ...rest: unknown[]) {
		if (isBackofficeOrigin(url)) {
			sameOriginRequests.add(this);
		} else {
			sameOriginRequests.delete(this);
		}

		// A reopened request is a new request.
		rewrittenRequests.delete(this);
		restoredRequests.delete(this);

		// Pass the arguments through as-is: open(method, url, undefined) would make the request synchronous.
		return originalOpen.call(this, method, url, ...rest);
	} as XMLHttpRequest['open'];

	proto.setRequestHeader = function (this: XMLHttpRequest, name: string, value: string) {
		if (sameOriginRequests.has(this) && name.toLowerCase() === 'authorization' && isBearer(value)) {
			state.rewrites.xhr++;
			rewrittenRequests.add(this);
			return originalSetRequestHeader.call(this, BACKOFFICE_HEADER, value);
		}

		return originalSetRequestHeader.call(this, name, value);
	};

	const hidesUnauthorized = (xhr: XMLHttpRequest): boolean => {
		if (!rewrittenRequests.has(xhr) || !isHiddenUnauthorized(statusGetter.call(xhr), originalGetResponseHeader.call(xhr, STATUS_HEADER))) {
			return false;
		}

		if (!restoredRequests.has(xhr)) {
			restoredRequests.add(xhr);
			state.unauthorizedRestored++;
		}

		return true;
	};

	Object.defineProperty(proto, 'status', {
		configurable: true,
		enumerable: true,
		get: function (this: XMLHttpRequest) {
			return hidesUnauthorized(this) ? 401 : statusGetter.call(this);
		},
	});

	Object.defineProperty(proto, 'statusText', {
		configurable: true,
		enumerable: true,
		get: function (this: XMLHttpRequest) {
			return hidesUnauthorized(this) ? 'Unauthorized' : statusTextGetter.call(this);
		},
	});
}

function watchPreInstallRequests(state: WindowsAuthenticationState) {
	if (typeof PerformanceObserver === 'undefined') return;

	// Resource entries are only recorded when a response completes, so keep listening briefly for requests that were
	// already in flight when the client script was installed.
	const observer = new PerformanceObserver((list) => {
		for (const entry of list.getEntries()) {
			if (entry.startTime >= state.installedAt || !entry.name.includes(MANAGEMENT_API_PATH)) continue;

			const path = new URL(entry.name).pathname;
			state.preInstallRequests.push(path);

			if (!ANONYMOUS_BOOT_PATHS.some((anonymousPath) => path.includes(anonymousPath))) {
				console.warn(`[WindowsAuthentication] ${path} started before the client script was installed and may have been sent with a bearer token.`);
			}
		}
	});

	observer.observe({ type: 'resource', buffered: true });
	setTimeout(() => observer.disconnect(), 30_000);
}

if (!window.__umbWindowsAuthentication) {
	const state: WindowsAuthenticationState = {
		installedAt: performance.now(),
		rewrites: { fetch: 0, xhr: 0 },
		unauthorizedRestored: 0,
		preInstallRequests: [],
	};

	patchFetch(state);
	patchXhr(state);
	watchPreInstallRequests(state);

	window.__umbWindowsAuthentication = state;
	console.debug(`[WindowsAuthentication] installed after ${Math.round(state.installedAt)} ms`);
}
