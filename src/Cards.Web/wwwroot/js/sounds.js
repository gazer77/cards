// The table's sounds, through Web Audio.
//
// The sounds themselves are made in C# (SoundGenerator) and handed over once as WAV
// data; this only decodes and plays them. Web Audio rather than <audio> elements so a
// sound can overlap itself — a deal is many cards — and start without a delay.
//
// A browser will not make a sound before the person has touched the page, so the audio
// context is woken by the first tap or key, and anything asked for before that is
// simply not heard.

let context = null;
const buffers = new Map();
const pending = new Map();

function wake() {
    if (!context) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return;
        context = new Ctor();
    }
    if (context.state === 'suspended') context.resume();
    for (const [name, base64] of pending) decode(name, base64);
    pending.clear();
}

async function decode(name, base64) {
    try {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        buffers.set(name, await context.decodeAudioData(bytes.buffer));
    } catch {
        // A sound that will not decode is a sound not heard; the table goes on.
    }
}

export function init(sounds) {
    for (const [name, base64] of Object.entries(sounds)) pending.set(name, base64);
    for (const type of ['pointerdown', 'keydown']) window.addEventListener(type, wake, { capture: true });
    // Arriving at a table from the setup screen, the person has already tapped "Start":
    // the browser counts that, and the opening shuffle can be heard.
    if (context || navigator.userActivation?.hasBeenActive) wake();
}

export function play(name, volume) {
    if (!context || context.state !== 'running') return;
    const buffer = buffers.get(name);
    if (!buffer) return;
    const source = context.createBufferSource();
    const gain = context.createGain();
    gain.gain.value = volume;
    source.buffer = buffer;
    source.connect(gain).connect(context.destination);
    source.start();
}
