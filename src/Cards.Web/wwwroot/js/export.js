// Getting a bug report out of the browser.
//
// Two routes on purpose: the clipboard is the quick one when the report is going
// straight into a conversation, and a file is the durable one when it is going into
// an issue or needs to survive a reload.

export async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        // The Clipboard API needs a secure context and a recent user gesture, and is
        // refused outright in some embedded views. Fall back to the old selection
        // trick rather than losing the report.
        try {
            const area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            const ok = document.execCommand('copy');
            document.body.removeChild(area);
            return ok;
        } catch {
            return false;
        }
    }
}

export function download(filename, text) {
    const blob = new Blob([text], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);

    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    // Revoking straight away cancels the download in some browsers.
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}
