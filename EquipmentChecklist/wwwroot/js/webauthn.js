/*
 * WebAuthn helpers for Equipment Checklist.
 *
 *   WebAuthnClient.register({ deviceLabel, antiForgeryToken, statusEl })
 *      → starts a CredentialCreate ceremony with the server
 *
 *   WebAuthnClient.signIn({ email, statusEl })
 *      → starts a CredentialGet ceremony, returns server JSON on success
 *        (caller decides whether to redirect / cache voucher / etc.)
 */
(function () {
    'use strict';

    if (!window.PublicKeyCredential) {
        window.WebAuthnClient = {
            isSupported: false,
            register : () => Promise.reject(new Error('WebAuthn not supported in this browser.')),
            signIn   : () => Promise.reject(new Error('WebAuthn not supported in this browser.'))
        };
        return;
    }

    // ── base64url ↔ ArrayBuffer ──────────────────────────────────────────────
    function b64uToBytes(b64u) {
        const pad = '='.repeat((4 - (b64u.length % 4)) % 4);
        const b64 = (b64u + pad).replace(/-/g, '+').replace(/_/g, '/');
        const bin = atob(b64);
        const out = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out.buffer;
    }
    function bytesToB64u(buf) {
        const bytes = new Uint8Array(buf);
        let s = '';
        for (let i = 0; i < bytes.byteLength; i++) s += String.fromCharCode(bytes[i]);
        return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    // Convert server JSON → browser CredentialCreationOptions
    function reviveCreateOptions(opts) {
        opts.challenge = b64uToBytes(opts.challenge);
        opts.user.id   = b64uToBytes(opts.user.id);
        if (opts.excludeCredentials) {
            opts.excludeCredentials = opts.excludeCredentials.map(c => ({
                ...c, id: b64uToBytes(c.id)
            }));
        }
        return opts;
    }

    // Convert server JSON → browser CredentialRequestOptions
    function reviveRequestOptions(opts) {
        opts.challenge = b64uToBytes(opts.challenge);
        if (opts.allowCredentials) {
            opts.allowCredentials = opts.allowCredentials.map(c => ({
                ...c, id: b64uToBytes(c.id)
            }));
        }
        return opts;
    }

    function getCsrfToken() {
        const el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : '';
    }

    function setStatus(el, msg, kind) {
        if (!el) return;
        el.textContent = msg;
        el.style.color =
            kind === 'error' ? 'var(--red-nogo, #b91c1c)' :
            kind === 'ok'    ? 'var(--green-go, #166534)' :
                               'var(--text-muted, #6b7280)';
    }

    // ── Registration ─────────────────────────────────────────────────────────
    async function register(opts) {
        opts = opts || {};
        const status = opts.statusEl || null;
        const label  = (opts.deviceLabel || '').trim() || 'Unnamed device';

        try {
            setStatus(status, 'Asking your device for a new credential…');

            const startResp = await fetch('/Security/RegisterStart', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'RequestVerificationToken': getCsrfToken() }
            });
            if (!startResp.ok) throw new Error('Server refused to start registration (' + startResp.status + ').');
            const optionsJson = await startResp.json();
            const publicKey   = reviveCreateOptions(optionsJson);

            setStatus(status, 'Confirm with your fingerprint / Face ID…');
            const cred = await navigator.credentials.create({ publicKey });
            if (!cred) throw new Error('No credential returned by the browser.');

            // Shape the response for Fido2NetLib
            const attestation = {
                id:    cred.id,
                rawId: bytesToB64u(cred.rawId),
                type:  cred.type,
                response: {
                    attestationObject: bytesToB64u(cred.response.attestationObject),
                    clientDataJSON:    bytesToB64u(cred.response.clientDataJSON)
                },
                extensions: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {}
            };

            setStatus(status, 'Saving to your account…');
            const completeResp = await fetch('/Security/RegisterComplete', {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': getCsrfToken()
                },
                body: JSON.stringify({ attestationResponse: attestation, deviceLabel: label })
            });
            const body = await completeResp.json().catch(() => ({}));
            if (!completeResp.ok || body.ok !== true) {
                throw new Error(body.error || 'Registration failed.');
            }
            setStatus(status, '✓ Device registered.', 'ok');
            return body;
        } catch (err) {
            setStatus(status, '✕ ' + (err.message || err), 'error');
            throw err;
        }
    }

    // ── Sign in ──────────────────────────────────────────────────────────────
    async function signIn(opts) {
        opts = opts || {};
        const status = opts.statusEl || null;
        const email  = (opts.email || '').trim();

        try {
            setStatus(status, 'Preparing biometric challenge…');
            const startResp = await fetch('/Security/AssertStart', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ email })
            });
            if (!startResp.ok) throw new Error('Server refused to start sign-in (' + startResp.status + ').');
            const optionsJson = await startResp.json();
            const publicKey   = reviveRequestOptions(optionsJson);

            setStatus(status, 'Confirm with your fingerprint / Face ID…');
            const assertion = await navigator.credentials.get({ publicKey });
            if (!assertion) throw new Error('No assertion returned by the browser.');

            const body = {
                id:    assertion.id,
                rawId: bytesToB64u(assertion.rawId),
                type:  assertion.type,
                response: {
                    authenticatorData: bytesToB64u(assertion.response.authenticatorData),
                    clientDataJSON:    bytesToB64u(assertion.response.clientDataJSON),
                    signature:         bytesToB64u(assertion.response.signature),
                    userHandle:        assertion.response.userHandle ? bytesToB64u(assertion.response.userHandle) : null
                },
                extensions: assertion.getClientExtensionResults ? assertion.getClientExtensionResults() : {}
            };

            setStatus(status, 'Verifying with server…');
            const completeResp = await fetch('/Security/AssertComplete', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ assertionResponse: body })
            });
            const reply = await completeResp.json().catch(() => ({}));
            if (!completeResp.ok || reply.ok !== true) throw new Error(reply.error || 'Sign-in failed.');

            // Cache the voucher in IndexedDB so OfflineVoucher (and Phase 4's
            // Service Worker) can verify it locally for offline sign-in.
            try {
                if (reply.voucher && window.OfflineVoucher) {
                    await window.OfflineVoucher.setStored(reply.voucher, reply.expires_at);
                }
            } catch (_) { /* signature failed verification — silently drop */ }

            setStatus(status, '✓ Signed in.', 'ok');
            return reply;
        } catch (err) {
            setStatus(status, '✕ ' + (err.message || err), 'error');
            throw err;
        }
    }

    // ── PIN setup ────────────────────────────────────────────────────────────
    async function setPin(credentialId, pin) {
        const resp = await fetch('/Security/SetPin', {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': getCsrfToken()
            },
            body: JSON.stringify({ credentialId: credentialId, pin: pin })
        });
        const body = await resp.json().catch(() => ({}));
        if (!resp.ok || body.ok !== true) throw new Error(body.error || 'Could not save PIN.');
        return body;
    }

    window.WebAuthnClient = {
        isSupported: true,
        register, signIn, setPin
    };
})();
