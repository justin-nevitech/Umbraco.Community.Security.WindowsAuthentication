// Test-site only. Exercises each way a backoffice extension can call an authenticated API, so you can see which ones
// make it through IIS Windows Authentication with the package in place. Plain JS on the backoffice import map, no build step.
import { css, html, nothing } from '@umbraco-cms/backoffice/external/lit';
import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';
import { UMB_MANAGEMENT_API_SERVER_EVENT_CONTEXT } from '@umbraco-cms/backoffice/management-api';

const WHOAMI_URL = '/umbraco/windows-authentication/api/v1/whoami';

function xhrGet(url, token) {
	return new Promise((resolve) => {
		const xhr = new XMLHttpRequest();
		xhr.open('GET', url);
		xhr.withCredentials = true;
		xhr.setRequestHeader('Authorization', `Bearer ${token}`);
		xhr.onload = () => resolve({ status: xhr.status, body: xhr.responseText });
		xhr.onerror = () => resolve({ status: 0, body: 'network error' });
		xhr.send();
	});
}

export default class WindowsAuthenticationDiagnosticsElement extends UmbLitElement {
	static properties = {
		_results: { state: true },
		_signalRConnected: { state: true },
		_state: { state: true },
	};

	#authContext;

	constructor() {
		super();
		this._results = [];
		this._signalRConnected = undefined;
		this._state = window.__umbWindowsAuthentication;

		this.consumeContext(UMB_AUTH_CONTEXT, (context) => {
			this.#authContext = context;
			this.#run();
		});

		this.consumeContext(UMB_MANAGEMENT_API_SERVER_EVENT_CONTEXT, (context) => {
			this.observe(context?.isConnected, (connected) => (this._signalRConnected = connected));
		});
	}

	async #run() {
		if (!this.#authContext) return;
		const token = await this.#authContext.getLatestToken();

		const checks = [
			[
				'umbHttpClient (core + generated package clients)',
				async () => {
					const { data, response } = await umbHttpClient.get({ url: WHOAMI_URL, security: [{ scheme: 'bearer', type: 'http' }] });
					return { status: response?.status ?? 0, body: data };
				},
			],
			[
				'Hand-rolled fetch with getLatestToken()',
				async () => {
					const response = await fetch(WHOAMI_URL, { credentials: 'include', headers: { Authorization: `Bearer ${token}` } });
					return { status: response.status, body: await response.text() };
				},
			],
			['XMLHttpRequest (tryXhrRequest, axios, etc.)', () => xhrGet(WHOAMI_URL, token)],
		];

		this._results = await Promise.all(
			checks.map(async ([name, check]) => {
				try {
					const { status, body } = await check();
					return { name, status, body: typeof body === 'string' ? body : JSON.stringify(body) };
				} catch (error) {
					return { name, status: 0, body: String(error) };
				}
			}),
		);
		this._state = window.__umbWindowsAuthentication;
	}

	render() {
		const state = this._state;
		return html`
			<uui-box headline="Client script">
				${state
					? html`<p>
								Installed after <b>${Math.round(state.installedAt)} ms</b>. Rewrites: fetch <b>${state.rewrites.fetch}</b>, XHR
								<b>${state.rewrites.xhr}</b>.
							</p>
							<p>Management API requests started before install: ${state.preInstallRequests.join(', ') || 'none'}</p>`
					: html`<p class="fail">The client script is not installed on this page.</p>`}
			</uui-box>
			<uui-box headline="Authenticated calls">
				<uui-button look="primary" label="Run again" @click=${this.#run}></uui-button>
				<table>
					${this._results.map(
						(result) => html`<tr>
							<td>${result.name}</td>
							<td class=${result.status === 200 ? 'pass' : 'fail'}>${result.status}</td>
							<td><code>${result.body}</code></td>
						</tr>`,
					)}
				</table>
			</uui-box>
			<uui-box headline="SignalR server events">
				${this._signalRConnected === undefined
					? nothing
					: html`<p class=${this._signalRConnected ? 'pass' : 'fail'}>${this._signalRConnected ? 'Connected' : 'Not connected'}</p>`}
			</uui-box>
		`;
	}

	static styles = css`
		:host {
			display: grid;
			gap: var(--uui-size-layout-1);
			padding: var(--uui-size-layout-1);
		}
		table {
			margin-top: var(--uui-size-space-4);
			border-collapse: collapse;
		}
		td {
			padding: var(--uui-size-space-2) var(--uui-size-space-4);
			border-bottom: 1px solid var(--uui-color-divider);
			vertical-align: top;
		}
		code {
			word-break: break-all;
		}
		.pass {
			color: var(--uui-color-positive);
		}
		.fail {
			color: var(--uui-color-danger);
		}
	`;
}

customElements.define('windows-authentication-diagnostics', WindowsAuthenticationDiagnosticsElement);
