// What an account carries, read from and written to this browser's storage.
//
// The account holds storage entries as they are: the settings blob and each saved game.
// Settings that belong to this device rather than to the player — its id, its seats at
// shared tables, its sizes — are taken out on the way up and kept on the way down.

const SETTINGS = 'cards.settings';
const SAVES    = 'cards.save.';
const DEVICE   = [/^client_id$/, /^shared_seats$/, /^show_diagnostics$/, /^size:/];

const isDevice = key => DEVICE.some(re => re.test(key));

function settingsOf(raw, keepDevice) {
    let all = {};
    try { all = JSON.parse(raw || '{}') || {}; } catch { all = {}; }
    const out = {};
    for (const [k, v] of Object.entries(all))
        if (isDevice(k) === keepDevice) out[k] = v;
    return out;
}

/** Everything the account carries, from this browser. */
export function read() {
    const out = {};
    out[SETTINGS] = JSON.stringify(settingsOf(localStorage.getItem(SETTINGS), false));
    for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (key && key.startsWith(SAVES)) out[key] = localStorage.getItem(key);
    }
    return out;
}

/** Replaces what the account carries in this browser with what it holds, keeping this device's own settings. */
export function write(storage) {
    const mine = settingsOf(localStorage.getItem(SETTINGS), true);
    const theirs = settingsOf(storage[SETTINGS], false);
    localStorage.setItem(SETTINGS, JSON.stringify({ ...theirs, ...mine }));

    const old = [];
    for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (key && key.startsWith(SAVES)) old.push(key);
    }
    for (const key of old) localStorage.removeItem(key);
    for (const [key, value] of Object.entries(storage))
        if (key.startsWith(SAVES)) localStorage.setItem(key, value);
}

export function getRecord() { return localStorage.getItem('cards.account'); }
export function setRecord(json) { localStorage.setItem('cards.account', json); }
