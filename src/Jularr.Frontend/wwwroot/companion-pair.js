(() => {
    const status = document.getElementById('status');
    const code = new URLSearchParams(location.hash.slice(1)).get('invite');
    history.replaceState(history.state, '', location.pathname + location.search);
    if (!code || code.length > 2048) {
        status.textContent = 'Die TV-Einladung fehlt oder ist ungültig. Scanne den QR-Code erneut.';
        return;
    }
    try {
        sessionStorage.setItem('jularr.companion.pendingInvite.v1', code);
        window.location.replace('/Companion');
    } catch {
        status.textContent = 'Der Browser verhindert das Kopplungsverfahren. Erlaube Sitzungsspeicher und scanne erneut.';
    }
})();
