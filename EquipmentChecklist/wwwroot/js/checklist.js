// Belfast Equipment Checklist – offline-first JS
// Handles: submission, offline queue, sync, status UI

const SYNC_ENDPOINT = '/api/sync/push';
const OFFLINE_QUEUE_KEY = 'belfastPendingSubmissions';

// ── Connectivity ─────────────────────────────────────────────────────────────
const syncDot = document.querySelector('.sync-dot');
const syncLabel = document.querySelector('.sync-label');

function updateOnlineStatus() {
  if (navigator.onLine) {
    syncDot?.classList.remove('offline');
    if (syncLabel) syncLabel.textContent = 'Online';
    flushOfflineQueue();
  } else {
    syncDot?.classList.add('offline');
    if (syncLabel) syncLabel.textContent = 'Offline – saving locally';
  }
}

window.addEventListener('online',  updateOnlineStatus);
window.addEventListener('offline', updateOnlineStatus);
document.addEventListener('DOMContentLoaded', updateOnlineStatus);

// ── Offline queue ─────────────────────────────────────────────────────────────
function queueOffline(payload) {
  const queue = JSON.parse(localStorage.getItem(OFFLINE_QUEUE_KEY) || '[]');
  queue.push({ ...payload, queuedAt: new Date().toISOString() });
  localStorage.setItem(OFFLINE_QUEUE_KEY, JSON.stringify(queue));
  console.log('[Offline] Queued submission', payload.LocalId);
}

async function flushOfflineQueue() {
  const queue = JSON.parse(localStorage.getItem(OFFLINE_QUEUE_KEY) || '[]');
  if (!queue.length) return;

  try {
    const res = await fetch(SYNC_ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Submissions: queue })
    });
    if (res.ok) {
      localStorage.removeItem(OFFLINE_QUEUE_KEY);
      console.log('[Sync] Flushed', queue.length, 'offline submissions');
    }
  } catch (e) {
    console.warn('[Sync] Could not flush queue', e);
  }
}

// ── Checklist form ────────────────────────────────────────────────────────────
const checklistForm = document.getElementById('checklistForm');

if (checklistForm) {
  // Live status preview
  checklistForm.addEventListener('change', updateLiveStatus);

  checklistForm.addEventListener('submit', async (e) => {
    e.preventDefault();

    if (!document.getElementById('fitnessDeclaration')?.checked) {
      showAlert('You must sign the fitness-to-operate declaration before submitting.', 'danger');
      return;
    }

    const payload = buildPayload();

    if (navigator.onLine) {
      await submitOnline(payload);
    } else {
      queueOffline(payload);
      showOfflineResult(payload);
    }
  });
}

function buildPayload() {
  const form = checklistForm;
  const items = [];

  document.querySelectorAll('[data-item-id]').forEach(row => {
    const id = parseInt(row.dataset.itemId);
    const selected = row.querySelector('input[type=radio]:checked');
    items.push({
      TemplateItemId: id,
      Status: selected ? parseInt(selected.value) : 1,
      Notes: row.querySelector('.item-notes')?.value || null
    });
  });

  return {
    LocalId: crypto.randomUUID(),
    MachineId: parseInt(form.querySelector('[name=MachineId]').value),
    Shift: parseInt(form.querySelector('[name=Shift]').value),
    KmOrHourMeter: parseInt(form.querySelector('[name=KmOrHourMeter]')?.value) || null,
    OperatorRemarks: form.querySelector('[name=OperatorRemarks]')?.value || null,
    FitnessDeclarationSigned: true,
    Items: items
  };
}

async function submitOnline(payload) {
  try {
    const res = await fetch('/api/checklist/submit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (res.ok) {
      const data = await res.json();
      window.location.href = `/Checklist/Result/${data.id}`;
    } else {
      const err = await res.text();
      showAlert('Submission failed: ' + err, 'danger');
    }
  } catch (e) {
    // Fallback to offline
    queueOffline(payload);
    showOfflineResult(payload);
  }
}

function showOfflineResult(payload) {
  const defects = payload.Items.filter(i => i.Status === 2).length;
  const status = defects === 0 ? 'go' : 'gobut';
  const label  = defects === 0 ? '✅ GO' : '⚠ GO-BUT (Offline)';
  const msg    = defects === 0
    ? 'All items in order. Submission saved offline and will sync when online.'
    : `${defects} defect(s) found. Submission saved offline and will sync when online.`;

  document.querySelector('.checklist-form').innerHTML = `
    <div class="result-card ${status}">
      <div class="result-icon">${status === 'go' ? '✅' : '⚠️'}</div>
      <div class="result-text">${label}</div>
      <p style="margin-top:12px;color:var(--text-muted)">${msg}</p>
    </div>
    <div class="alert alert-warning">
      <strong>Offline Mode:</strong> This submission is queued and will be sent to the server when internet connectivity is restored.
    </div>
    <a href="/Checklist" class="btn btn-ghost">← Back to Dashboard</a>
  `;
}

function updateLiveStatus() {
  const defects = document.querySelectorAll('input[value="2"]:checked').length;
  const preview = document.getElementById('statusPreview');
  if (!preview) return;

  if (defects === 0) {
    preview.className = 'badge badge-go';
    preview.textContent = '✓ GO';
  } else {
    const noGoDefects = document.querySelectorAll('.no-go-item input[value="2"]:checked').length;
    if (noGoDefects > 0) {
      preview.className = 'badge badge-nogo';
      preview.textContent = '✕ NO-GO – ' + noGoDefects + ' critical defect(s)';
    } else {
      preview.className = 'badge badge-gobut';
      preview.textContent = '⚠ GO-BUT – ' + defects + ' defect(s)';
    }
  }
}

// ── Utilities ─────────────────────────────────────────────────────────────────
function showAlert(msg, type = 'info') {
  const el = document.createElement('div');
  el.className = `alert alert-${type}`;
  el.textContent = msg;
  checklistForm?.prepend(el);
  setTimeout(() => el.remove(), 6000);
}

// ── Dashboard live refresh ─────────────────────────────────────────────────────
async function refreshDashboard() {
  try {
    const res = await fetch('/api/checklist/dashboard');
    if (!res.ok) return;
    const data = await res.json();

    document.getElementById('stat-total')?.textContent  != data.totalMachines;
    const el = (id) => document.getElementById(id);
    if (el('stat-total'))   el('stat-total').textContent   = data.totalMachines;
    if (el('stat-nogo'))    el('stat-nogo').textContent    = data.immobilisedMachines;
    if (el('stat-go'))      el('stat-go').textContent      = data.goMachines;
    if (el('stat-defects')) el('stat-defects').textContent = data.pendingDefects;
    if (el('stat-today'))   el('stat-today').textContent   = data.todaySubmissions;
  } catch (_) { /* ignore if offline */ }
}

if (document.getElementById('stat-total')) {
  setInterval(refreshDashboard, 30_000);
}
