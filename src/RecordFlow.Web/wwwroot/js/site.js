// RecordFlow client script. No inline scripts are used anywhere (strict Content-Security-Policy).
(() => {
    'use strict';

    const csrfToken = document.querySelector('meta[name="csrf-token"]')?.content ?? '';

    async function api(url, method = 'POST', body = undefined) {
        const response = await fetch(url, {
            method,
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': csrfToken },
            body: body === undefined ? undefined : JSON.stringify(body),
        });
        if (response.status === 204) return null;
        const data = await response.json().catch(() => null);
        if (!response.ok) {
            const message = data?.title
                ?? (response.status === 429 ? 'Too many requests. Please wait a moment and try again.'
                    : response.status === 401 ? 'Your sign-in has expired. Please sign in again.'
                        : 'Something went wrong. Please try again.');
            const error = new Error(message);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    function toast(message, variant = 'success') {
        const host = document.getElementById('toastHost');
        if (!host || !window.bootstrap) { return; }
        const el = document.createElement('div');
        el.className = `toast align-items-center text-bg-${variant} border-0`;
        el.setAttribute('role', variant === 'danger' ? 'alert' : 'status');
        const wrap = document.createElement('div');
        wrap.className = 'd-flex';
        const body = document.createElement('div');
        body.className = 'toast-body';
        body.textContent = message;
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn-close btn-close-white me-2 m-auto';
        close.setAttribute('data-bs-dismiss', 'toast');
        close.setAttribute('aria-label', 'Close');
        wrap.append(body, close);
        el.append(wrap);
        host.append(el);
        const t = new bootstrap.Toast(el, { delay: 3500 });
        el.addEventListener('hidden.bs.toast', () => el.remove());
        t.show();
    }

    function handleApiError(error) {
        toast(error.message, 'danger');
        if (error.status === 410 || error.status === 401) { setTimeout(() => window.location.reload(), 1500); }
    }

    // Progress bars (widths are set here because inline styles are blocked by CSP).
    document.querySelectorAll('[data-progress]').forEach(el => { el.style.width = `${Number(el.dataset.progress) || 0}%`; });

    // Confirmation prompts for destructive forms.
    document.querySelectorAll('form[data-confirm]').forEach(form => {
        form.addEventListener('submit', e => { if (!window.confirm(form.dataset.confirm)) { e.preventDefault(); } });
    });

    // Print buttons.
    document.querySelectorAll('.js-print').forEach(btn => btn.addEventListener('click', () => window.print()));

    // Checkbox that enables a button (e.g. "I confirm" on the verification page).
    document.querySelectorAll('[data-enables]').forEach(checkbox => {
        const target = document.querySelector(checkbox.dataset.enables);
        if (!target || target.dataset.locked === 'true') { return; }
        const sync = () => { target.disabled = !checkbox.checked; };
        checkbox.addEventListener('change', sync);
        sync();
    });

    // Dashboard: Response (Y/N) and editable admin columns save immediately.
    document.querySelectorAll('.js-response').forEach(select => {
        select.addEventListener('change', async () => {
            try {
                await api(`/api/workspace/records/${encodeURIComponent(select.dataset.key)}/response`, 'POST', { value: select.value });
                toast('Response saved');
            } catch (e) { handleApiError(e); }
        });
    });
    document.querySelectorAll('.js-admin-col').forEach(input => {
        input.addEventListener('change', async () => {
            try {
                await api(`/api/workspace/records/${encodeURIComponent(input.dataset.key)}/columns/${input.dataset.slot}`, 'POST', { value: input.value });
                input.classList.remove('is-invalid');
                toast('Saved');
            } catch (e) { input.classList.add('is-invalid'); handleApiError(e); }
        });
    });

    // CSV upload: show file name and check type/size before sending.
    const uploadForm = document.getElementById('uploadForm');
    if (uploadForm) {
        const input = document.getElementById('csvFile');
        const nameEl = document.getElementById('csvFileName');
        const errorEl = document.getElementById('csvFileError');
        const submit = document.getElementById('uploadSubmit');
        const maxBytes = Number(uploadForm.dataset.maxBytes) || 5 * 1024 * 1024;
        const validate = () => {
            const file = input.files?.[0];
            errorEl.textContent = '';
            nameEl.textContent = file ? `${file.name} (${(file.size / 1024).toFixed(0)} KB)` : '';
            if (!file) { return false; }
            if (!file.name.toLowerCase().endsWith('.csv')) { errorEl.textContent = 'Please choose a .csv file.'; return false; }
            if (file.size > maxBytes) { errorEl.textContent = `The file is larger than ${(maxBytes / 1048576).toFixed(0)} MB.`; return false; }
            if (file.size === 0) { errorEl.textContent = 'The file is empty.'; return false; }
            return true;
        };
        input.addEventListener('change', validate);
        uploadForm.addEventListener('submit', e => {
            if (!validate()) {
                e.preventDefault();
                if (!input.files?.length) { errorEl.textContent = 'Please choose a CSV file.'; }
                return;
            }
            submit.disabled = true;
            submit.textContent = 'Processing…';
        });
    }

    // Password strength meter.
    document.querySelectorAll('[data-password-strength]').forEach(input => {
        const meter = document.querySelector(input.dataset.passwordStrength);
        if (!meter) { return; }
        const bar = meter.querySelector('.progress-bar');
        const label = meter.querySelector('.meter-label');
        const hint = label.textContent;
        input.addEventListener('input', () => {
            const v = input.value;
            const checks = [v.length >= 10, /[a-z]/.test(v), /[A-Z]/.test(v), /\d/.test(v), /[^A-Za-z0-9]/.test(v), v.length >= 14];
            const score = checks.filter(Boolean).length;
            const levels = [
                { min: 0, text: 'Too weak', cls: 'bg-danger' },
                { min: 3, text: 'Weak', cls: 'bg-danger' },
                { min: 5, text: 'Good', cls: 'bg-warning' },
                { min: 6, text: 'Strong', cls: 'bg-success' },
            ];
            const level = levels.filter(l => score >= l.min).pop();
            bar.style.width = v ? `${Math.max(10, score / 6 * 100)}%` : '0';
            bar.className = `progress-bar ${level.cls}`;
            label.textContent = v ? `Password strength: ${level.text}` : hint;
        });
    });

    // Share Form modal.
    const shareModal = document.getElementById('shareModal');
    if (shareModal) {
        const endpoint = shareModal.dataset.endpoint;
        const store = shareModal.dataset.store;
        const q = sel => shareModal.querySelector(sel);
        const loading = q('.share-loading'), ready = q('.share-ready'), errorBox = q('.share-error');
        const linkInput = q('#shareLink'), expiry = q('.share-expiry'), submitted = q('.share-submitted');
        const whatsapp = q('.js-share-whatsapp'), sms = q('.js-share-sms'), nativeBtn = q('.js-share-native');
        const emailForm = q('.share-email-form');
        let current = null;

        const message = url => `Please review and complete the store information for ${store} using this secure link: ${url}`;
        const recordChannel = channel => api(`${endpoint}/share-channel`, 'POST', { channel }).catch(() => { });

        function render(data) {
            current = data;
            linkInput.value = data.url;
            expiry.textContent = `Link expires ${data.expires} or when your working session ends.`;
            submitted.classList.toggle('d-none', !data.submitted);
            whatsapp.href = `https://wa.me/?text=${encodeURIComponent(message(data.url))}`;
            sms.href = `sms:?&body=${encodeURIComponent(message(data.url))}`;
            nativeBtn.classList.toggle('d-none', !navigator.share);
            loading.classList.add('d-none');
            errorBox.classList.add('d-none');
            ready.classList.remove('d-none');
        }

        async function load(regenerate) {
            loading.classList.remove('d-none');
            ready.classList.add('d-none');
            try {
                render(await api(`${endpoint}/share-link`, 'POST', { regenerate }));
            } catch (e) {
                loading.classList.add('d-none');
                errorBox.textContent = e.message;
                errorBox.classList.remove('d-none');
                if (e.status === 410) { setTimeout(() => window.location.reload(), 1500); }
            }
        }

        shareModal.addEventListener('show.bs.modal', () => { if (!current) { load(false); } });

        q('.js-copy-link').addEventListener('click', async () => {
            try { await navigator.clipboard.writeText(linkInput.value); }
            catch { linkInput.select(); document.execCommand('copy'); }
            toast('Secure link copied');
            recordChannel('copy');
        });
        whatsapp.addEventListener('click', () => recordChannel('whatsapp'));
        sms.addEventListener('click', () => recordChannel('sms'));
        nativeBtn.addEventListener('click', async () => {
            try {
                await navigator.share({ title: 'Secure form', text: message(current.url), url: current.url });
                recordChannel('native');
            } catch { /* user dismissed the share sheet */ }
        });
        q('.js-share-email-toggle').addEventListener('click', () => {
            emailForm.classList.toggle('d-none');
            if (!emailForm.classList.contains('d-none')) { q('#shareEmailTo').focus(); }
        });
        emailForm.addEventListener('submit', async e => {
            e.preventDefault();
            const to = q('#shareEmailTo');
            if (!to.checkValidity()) { to.classList.add('is-invalid'); return; }
            to.classList.remove('is-invalid');
            const button = emailForm.querySelector('button[type="submit"]');
            button.disabled = true;
            try {
                await api(`${endpoint}/share-email`, 'POST', { email: to.value, message: q('#shareEmailNote').value });
                toast(`Email sent to ${to.value}`);
                emailForm.reset();
                emailForm.classList.add('d-none');
            } catch (err) { handleApiError(err); }
            finally { button.disabled = false; }
        });
        q('.js-regenerate').addEventListener('click', async () => {
            if (!window.confirm('Create a new link? The current link will stop working immediately.')) { return; }
            await load(true);
            if (current) { toast('New secure link created'); }
        });
    }

    // Form page: watch for the recipient's submission and announce it.
    const statusBadge = document.getElementById('statusBadge');
    if (statusBadge && statusBadge.dataset.currentStatus === 'SharedPending') {
        const timer = setInterval(async () => {
            if (document.hidden) { return; }
            try {
                const s = await api(statusBadge.dataset.statusEndpoint, 'GET');
                if (s.recipientSubmitted && s.status === 'ReadyForVerification') {
                    clearInterval(timer);
                    statusBadge.textContent = s.label;
                    document.getElementById('recipientBanner')?.classList.remove('d-none');
                    toast('The recipient submitted the form — reload to see their updates.', 'primary');
                }
            } catch (e) {
                if (e.status === 410) { clearInterval(timer); }
            }
        }, 15000);
    }
})();
