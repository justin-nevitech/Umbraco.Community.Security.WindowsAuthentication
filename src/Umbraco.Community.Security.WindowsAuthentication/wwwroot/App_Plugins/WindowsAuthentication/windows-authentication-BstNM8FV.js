//#region src/windows-authentication.ts
var e = "X-Umb-Authorization", t = "X-Umb-Authorization-Status", n = [
	"/server/status",
	"/server/configuration",
	"/manifest/manifest/public",
	"/security/back-office/"
];
function r() {
	let e = document.querySelector("umb-app")?.getAttribute("server-url");
	if (e) try {
		return new URL(e, document.baseURI).origin;
	} catch {
		return;
	}
}
function i(e) {
	try {
		let t = new URL(e, document.baseURI);
		return (t.origin === location.origin || t.origin === r()) && t.pathname.toLowerCase().startsWith("/umbraco/");
	} catch {
		return !1;
	}
}
function a(e) {
	return e !== null && /^bearer +\S/i.test(e);
}
function o(e, t) {
	return e === 403 && t === "401";
}
function s(e, n) {
	if (!o(e.status, e.headers.get(t))) return e;
	n.unauthorizedRestored++;
	let r = new Headers(e.headers);
	r.delete(t);
	let i = new Response(e.body, {
		status: 401,
		statusText: "Unauthorized",
		headers: r
	});
	return Object.defineProperties(i, {
		url: { value: e.url },
		redirected: { value: e.redirected }
	}), i;
}
function c(t) {
	let n = window.fetch.bind(window);
	window.fetch = (r, o) => {
		if (!i(r instanceof Request ? r.url : r)) return n(r, o);
		let c = new Headers(o?.headers ?? (r instanceof Request ? r.headers : void 0)), l = c.get("Authorization");
		return a(l) ? (c.set(e, l), c.delete("Authorization"), t.rewrites.fetch++, n(r, {
			...o,
			headers: c
		}).then((e) => s(e, t))) : n(r, o);
	};
}
function l(n) {
	let r = /* @__PURE__ */ new WeakSet(), s = /* @__PURE__ */ new WeakSet(), c = /* @__PURE__ */ new WeakSet(), l = XMLHttpRequest.prototype, u = l.open, d = l.setRequestHeader, f = l.getResponseHeader, p = Object.getOwnPropertyDescriptor(l, "status").get, m = Object.getOwnPropertyDescriptor(l, "statusText").get;
	l.open = function(e, t, ...n) {
		return i(t) ? r.add(this) : r.delete(this), s.delete(this), c.delete(this), u.call(this, e, t, ...n);
	}, l.setRequestHeader = function(t, i) {
		return r.has(this) && t.toLowerCase() === "authorization" && a(i) ? (n.rewrites.xhr++, s.add(this), d.call(this, e, i)) : d.call(this, t, i);
	};
	let h = (e) => !s.has(e) || !o(p.call(e), f.call(e, t)) ? !1 : (c.has(e) || (c.add(e), n.unauthorizedRestored++), !0);
	Object.defineProperty(l, "status", {
		configurable: !0,
		enumerable: !0,
		get: function() {
			return h(this) ? 401 : p.call(this);
		}
	}), Object.defineProperty(l, "statusText", {
		configurable: !0,
		enumerable: !0,
		get: function() {
			return h(this) ? "Unauthorized" : m.call(this);
		}
	});
}
function u(e) {
	if (typeof PerformanceObserver > "u") return;
	let t = new PerformanceObserver((t) => {
		for (let r of t.getEntries()) {
			if (r.startTime >= e.installedAt || !r.name.includes("/umbraco/management/api/")) continue;
			let t = new URL(r.name).pathname;
			e.preInstallRequests.push(t), n.some((e) => t.includes(e)) || console.warn(`[WindowsAuthentication] ${t} started before the client script was installed and may have been sent with a bearer token.`);
		}
	});
	t.observe({
		type: "resource",
		buffered: !0
	}), setTimeout(() => t.disconnect(), 3e4);
}
if (!window.__umbWindowsAuthentication) {
	let e = {
		installedAt: performance.now(),
		rewrites: {
			fetch: 0,
			xhr: 0
		},
		unauthorizedRestored: 0,
		preInstallRequests: []
	};
	c(e), l(e), u(e), window.__umbWindowsAuthentication = e, console.debug(`[WindowsAuthentication] installed after ${Math.round(e.installedAt)} ms`);
}
//#endregion

//# sourceMappingURL=windows-authentication-BstNM8FV.js.map