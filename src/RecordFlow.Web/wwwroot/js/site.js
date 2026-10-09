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

    // Call desk: clicking a store (or "Next call") opens its CSV details and starts the call timer (Time);
    // "End call" sets Close Time and "Call again" starts a new call for the same store.
    const pad = n => String(n).padStart(2, '0');
    const formatDuration = ms => {
        const s = Math.max(0, Math.floor(ms / 1000));
        const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
        return h ? `${h}:${pad(m)}:${pad(s % 60)}` : `${pad(m)}:${pad(s % 60)}`;
    };
    const callApi = (key, action, body) => api(`/api/workspace/records/${encodeURIComponent(key)}/call/${action}`, 'POST', body);
    const findRow = key => [...document.querySelectorAll('.js-record-row')].find(tr => tr.dataset.key === key);
    const activeCallBanner = () => document.querySelector('.active-call');
    // The store whose call is running right now (row on this page, or the "On call" bar for any page).
    const liveCallKey = () => document.querySelector('.js-record-row.call-active')?.dataset.key ?? activeCallBanner()?.dataset.key ?? null;
    const dashboardUrl = params => `/dashboard?${new URLSearchParams(params)}`;

    // A store to open once the caller has dealt with the outcome of the call that just ended.
    let pendingNext = null;   // { key, dial }

    const recordModalEl = document.getElementById('recordModal');
    let openRecord = null;
    if (recordModalEl && window.bootstrap) {
        const modal = bootstrap.Modal.getOrCreateInstance(recordModalEl);
        const q = sel => recordModalEl.querySelector(sel);
        const title = q('#recordModalTitle'), sub = q('.js-rm-sub'), statusBadge = q('.js-rm-status');
        const startEl = q('.js-rm-start'), endEl = q('.js-rm-end'), durationEl = q('.js-rm-duration');
        const loading = q('.js-rm-loading'), fieldsTitle = q('.js-rm-fields-title'), fieldsEl = q('.js-rm-fields'), purged = q('.js-rm-purged');
        const historyTitle = q('.js-rm-history-title'), historyEl = q('.js-rm-history');
        const endButton = q('.js-rm-end-call'), againButton = q('.js-rm-call-again'), actionButton = q('.js-rm-action');
        const dialButton = q('.js-rm-dial'), dialNumber = q('.js-rm-dial-number');
        const outcomeSection = q('.js-rm-outcome'), outcomeFor = q('.js-rm-outcome-for'), outcomeState = q('.js-rm-outcome-state');
        const outcomeHint = q('.js-rm-outcome-hint');
        const outcomeOptions = [...recordModalEl.querySelectorAll('.js-rm-outcome-option')], notesEl = q('.js-rm-notes');
        const saveOutcomeButton = q('.js-rm-outcome-save'), clearOutcomeButton = q('.js-rm-outcome-clear');
        const nextPrompt = q('.js-rm-next'), nextText = q('.js-rm-next-text'), nextGo = q('.js-rm-next-go'), nextSkip = q('.js-rm-next-skip');
        let outcomeDirty = false;
        let row = null, timer = null, startedAt = null, endedAt = null, changed = false, onCall = false, callCount = 0;

        const tick = () => { durationEl.textContent = startedAt ? formatDuration((endedAt ?? new Date()) - startedAt) : '—'; };
        const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) { e.className = cls; } if (text !== undefined) { e.textContent = text; } return e; };

        function render(d) {
            title.textContent = d.storeName || `Contact ID ${d.contactId}`;
            sub.textContent = `Contact ID ${d.contactId}`;
            statusBadge.textContent = d.statusLabel;
            statusBadge.className = `badge rounded-pill ms-auto ${row.querySelector('.js-status-badge')?.className.replace('js-status-badge', '') ?? ''}`;
            startEl.textContent = d.callStarted ? `${d.callStartedDate} ${d.callStartedText}` : '—';
            endEl.textContent = d.callEnded ? d.callEndedText : '—';
            startedAt = d.callStarted ? new Date(d.callStarted) : null;
            endedAt = d.callEnded ? new Date(d.callEnded) : null;
            onCall = !!startedAt && !endedAt;
            callCount = d.calls.length;
            dialButton.classList.toggle('d-none', !d.phone);
            if (d.phone) {
                dialButton.href = d.phone.tel;
                dialButton.title = d.phone.label;
                dialNumber.textContent = d.phone.number;
            }
            clearInterval(timer);
            tick();
            if (onCall) { timer = setInterval(tick, 1000); }
            endButton.classList.toggle('d-none', !onCall);
            endButton.disabled = false;
            againButton.classList.toggle('d-none', onCall || d.calls.length === 0);
            againButton.disabled = false;

            fieldsEl.replaceChildren();
            const hasValues = d.fields.some(f => f.value);
            for (const f of d.fields) {
                const dt = el('dt', 'col-sm-5 text-truncate', f.label);
                dt.title = f.label;
                const dd = el('dd', 'col-sm-7');
                if (f.tel) {
                    const link = el('a', 'dial-link js-dial', f.value);
                    link.href = f.tel;
                    link.setAttribute('aria-label', `Call ${f.label} ${f.value}`);
                    dd.append(link);
                } else {
                    dd.append(f.value ? document.createTextNode(f.value) : el('span', 'text-muted fst-italic', 'missing'));
                }
                fieldsEl.append(dt, dd);
            }
            historyEl.replaceChildren();
            for (const c of [...d.calls].reverse()) {
                const li = el('li', c.outcome || c.notes ? 'has-detail' : '');
                li.append(el('span', 'fw-semibold', `Call ${c.number}`), el('span', 'text-muted', c.started));
                li.append(c.ended ? el('span', 'font-monospace', c.duration) : el('span', 'call-live', 'live'));
                if (c.outcome || c.notes) {
                    const detail = el('div', 'call-detail');
                    if (c.outcome) { detail.append(el('span', `badge rounded-pill me-1 ${c.outcomeClass}`, c.outcome)); }
                    if (c.notes) { detail.append(document.createTextNode(c.notes)); }
                    li.append(detail);
                }
                historyEl.append(li);
            }

            // Outcome & notes belong to the latest call and are only offered once that call has ended.
            const latest = d.calls[d.calls.length - 1];
            outcomeSection.classList.toggle('d-none', !latest || onCall);
            outcomeHint.classList.toggle('d-none', !onCall);
            outcomeFor.textContent = latest ? `(call ${latest.number})` : '';
            outcomeOptions.forEach(o => { o.checked = o.value === d.lastOutcome; });
            notesEl.value = d.lastNotes ?? '';
            outcomeDirty = false;
            outcomeState.textContent = d.lastOutcome || d.lastNotes ? 'Saved' : 'No outcome yet';
            const outcomeCell = row.querySelector('.js-call-outcome');
            outcomeCell.replaceChildren(latest?.outcome
                ? el('span', `badge rounded-pill ${latest.outcomeClass}`, latest.outcome)
                : el('span', 'text-muted', '—'));
            loading.classList.add('d-none');
            fieldsTitle.classList.toggle('d-none', !hasValues);
            fieldsEl.classList.toggle('d-none', !hasValues);
            purged.classList.toggle('d-none', hasValues || !d.closed);
            historyTitle.classList.toggle('d-none', d.calls.length === 0);

            // Keep the queue row in sync with the latest call.
            row.querySelector('.js-call-date').textContent = d.callStarted ? d.callStartedDate : row.querySelector('.js-call-date').textContent;
            row.querySelector('.js-call-start').textContent = d.callStarted ? d.callStartedText : '—';
            row.querySelector('.js-call-end').textContent = d.callEnded ? d.callEndedText : '—';
            row.querySelector('.js-call-count').textContent = d.calls.length || '—';
            row.classList.toggle('call-active', onCall);
            const banner = activeCallBanner();
            if (!onCall && banner?.dataset.key === row.dataset.key) { banner.remove(); }

            // After a call ends on the way to another store: ask for the outcome, then continue.
            const showNext = !!pendingNext && !onCall && pendingNext.key !== row.dataset.key;
            nextPrompt.classList.toggle('d-none', !showNext);
            if (showNext) {
                const nextId = findRow(pendingNext.key)?.getAttribute('aria-label')?.replace('Open details for ', '');
                nextText.textContent = `Call ended. Add its outcome, then continue to ${nextId ? `Contact ID ${nextId}` : 'the next store'}.`;
            }
        }

        // Bootstrap moves focus to the dialog when it finishes opening, so focus the outcome panel after that.
        let modalShown = false;
        recordModalEl.addEventListener('shown.bs.modal', () => { modalShown = true; });
        recordModalEl.addEventListener('hide.bs.modal', () => { modalShown = false; });
        function focusOutcome() {
            if (outcomeSection.classList.contains('d-none')) { return; }
            const focus = () => {
                outcomeSection.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
                outcomeSection.focus({ preventScroll: true });
            };
            if (modalShown) { focus(); } else { recordModalEl.addEventListener('shown.bs.modal', focus, { once: true }); }
        }

        async function run(action, body) {
            try { render(await callApi(row.dataset.key, action, body)); changed = true; return true; }
            catch (e) { modal.hide(); handleApiError(e); return false; }
        }

        // dial: the caller pressed a phone link, so make sure a call is running (a new one if the last call ended).
        // focusOutcome: put the caller straight into the outcome panel (the call has just ended).
        openRecord = async (tr, dial = false, { focusOutcome: wantOutcome = false } = {}) => {
            // One call at a time: a running call on another store is ended first and its outcome asked for.
            const live = liveCallKey();
            if (live && live !== tr.dataset.key) { await endThenAskOutcome(live, { key: tr.dataset.key, dial }); return; }

            row = tr;
            const action = row.querySelector('.js-row-action');
            actionButton.textContent = action?.textContent.trim() || 'Open';
            actionButton.classList.toggle('d-none', !action);
            title.textContent = row.getAttribute('aria-label')?.replace('Open details for ', 'Contact ID ') ?? 'Record';
            sub.textContent = '';
            statusBadge.textContent = '';
            [fieldsTitle, fieldsEl, purged, historyTitle, endButton, againButton, dialButton, outcomeSection, outcomeHint, nextPrompt].forEach(x => x.classList.add('d-none'));
            outcomeDirty = false;
            historyEl.replaceChildren();
            loading.classList.remove('d-none');
            modal.show();
            if (!(await run('start'))) { return; }
            if (dial && !onCall) { await run('again'); }
            if (wantOutcome) { focusOutcome(); }
        };

        // Ends the running call, then shows that store with its outcome panel; `next` opens afterwards.
        async function endThenAskOutcome(liveKey, next) {
            try { await callApi(liveKey, 'end'); }
            catch (e) { handleApiError(e); return; }
            changed = true;
            pendingNext = next;
            const liveRow = findRow(liveKey);
            if (liveRow) {
                await openRecord(liveRow, false, { focusOutcome: true });
                toast('Previous call ended — add its outcome first');
            } else {
                window.location.href = dashboardUrl({ call: liveKey, outcome: '1', next: next.key });
            }
        }

        function goToNext() {
            const next = pendingNext;
            pendingNext = null;
            if (!next) { return; }
            const tr = findRow(next.key);
            if (tr) { openRecord(tr, next.dial); } else { window.location.href = dashboardUrl({ call: next.key }); }
        }
        nextGo.addEventListener('click', async () => {
            nextGo.disabled = true;
            const hasOutcome = outcomeOptions.some(o => o.checked) || notesEl.value.trim() !== '';
            const saved = !outcomeDirty || !hasOutcome || await saveOutcome();
            nextGo.disabled = false;
            if (saved) { outcomeDirty = false; goToNext(); }
        });
        nextSkip.addEventListener('click', () => { outcomeDirty = false; goToNext(); });

        document.querySelectorAll('.js-record-row').forEach(tr => {
            tr.addEventListener('click', e => {
                // Controls inside the row (Response, editable columns, Proceed) keep their own behavior.
                if (e.target.closest('a, button, select, input, textarea, label, form')) { return; }
                openRecord(tr);
            });
            tr.querySelector('.js-dial')?.addEventListener('click', () => openRecord(tr, true));   // the tel: link still opens the dialer
            tr.addEventListener('keydown', e => {
                if (e.target === tr && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openRecord(tr); }
            });
        });

        const saveOutcome = () => run('outcome', { outcome: outcomeOptions.find(o => o.checked)?.value ?? null, notes: notesEl.value });
        const markDirty = () => { outcomeDirty = true; outcomeState.textContent = 'Unsaved changes'; };
        outcomeOptions.forEach(o => o.addEventListener('change', markDirty));
        notesEl.addEventListener('input', markDirty);
        clearOutcomeButton.addEventListener('click', () => { outcomeOptions.forEach(o => { o.checked = false; }); notesEl.value = ''; markDirty(); });
        saveOutcomeButton.addEventListener('click', async () => {
            saveOutcomeButton.disabled = true;
            if (await saveOutcome()) { toast('Outcome saved'); }
            saveOutcomeButton.disabled = false;
        });

        // Ending the call reveals the outcome panel, ready for the caller to fill in.
        endButton.addEventListener('click', async () => {
            endButton.disabled = true;
            if (await run('end')) {
                toast('Call ended — add the outcome and notes');
                focusOutcome();
            }
        });
        // Unsaved outcome/notes of the ended call are saved before a new call starts.
        againButton.addEventListener('click', async () => {
            againButton.disabled = true;
            if (outcomeDirty && !(await saveOutcome())) { return; }
            if (await run('again')) { toast('New call started'); }
        });
        actionButton.addEventListener('click', () => row?.querySelector('.js-row-action')?.click());
        // Dialing from the popup (store phone button or any phone number) starts a call when none is running.
        recordModalEl.addEventListener('click', e => {
            if (!e.target.closest('.js-dial') || !row || onCall) { return; }
            run(callCount ? 'again' : 'start');
        });
        recordModalEl.addEventListener('hidden.bs.modal', async () => {
            clearInterval(timer);
            timer = null;
            pendingNext = null;   // closing the popup abandons the move to the next store
            if (outcomeDirty && row) { await saveOutcome(); }
            // Refresh the stats and the active-call banner after calls changed.
            if (changed) { window.location.reload(); }
        });
    }

    // "Next call": open the next store straight away when it is on this page (a running call is ended first).
    document.querySelectorAll('.js-next-call').forEach(link => link.addEventListener('click', e => {
        const tr = findRow(link.dataset.key);
        if (tr && openRecord) { e.preventDefault(); openRecord(tr); }
    }));

    // ?call=<key> opens a store (the server shows the page that holds it); &outcome=1 focuses its outcome panel and
    // &next=<key> continues to another store once that outcome is saved or skipped.
    const params = new URLSearchParams(window.location.search);
    const requested = params.get('call');
    if (requested && openRecord) {
        const tr = findRow(requested);
        if (tr) {
            if (params.get('next')) { pendingNext = { key: params.get('next'), dial: false }; }
            openRecord(tr, false, { focusOutcome: params.has('outcome') });
        }
        ['call', 'next', 'outcome'].forEach(k => params.delete(k));
        history.replaceState(null, '', window.location.pathname + (params.size ? `?${params}` : ''));
    }

    // Active call banner: live duration, open the store, end the call.
    const activeCall = document.querySelector('.active-call');
    if (activeCall) {
        const started = new Date(activeCall.dataset.started);
        const out = activeCall.querySelector('.js-active-duration');
        const update = () => { out.textContent = formatDuration(new Date() - started); };
        update();
        setInterval(update, 1000);
        activeCall.querySelector('.js-active-open').addEventListener('click', () => {
            const tr = findRow(activeCall.dataset.key);
            if (tr && openRecord) { openRecord(tr); } else { window.location.href = `/dashboard?call=${encodeURIComponent(activeCall.dataset.key)}`; }
        });
        // Ending from the bar opens the store with its outcome panel, so the outcome is never skipped.
        const endFromBar = activeCall.querySelector('.js-active-end');
        endFromBar.addEventListener('click', async () => {
            const key = activeCall.dataset.key;
            endFromBar.disabled = true;
            try { await callApi(key, 'end'); }
            catch (err) { endFromBar.disabled = false; handleApiError(err); return; }
            const tr = findRow(key);
            if (tr && openRecord) {
                await openRecord(tr, false, { focusOutcome: true });
                toast('Call ended — add the outcome and notes');
            } else {
                window.location.href = dashboardUrl({ call: key, outcome: '1' });
            }
        });
    }

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
