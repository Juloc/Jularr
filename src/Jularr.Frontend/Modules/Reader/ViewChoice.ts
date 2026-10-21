import { choiceOf, viewAddress, type ViewChoice } from './ViewSwitch';

// The view switch of a book with a PDF: Automatic, Smart Book or Original pages. The choice is saved for the profile on all its devices, then the book opens in that view at the same page.
export function initializeViewChoice(reader: HTMLElement, currentPage: () => number | null): void {
    const buttons = Array.from(reader.querySelectorAll<HTMLButtonElement>('[data-view-choice]'));
    const url = reader.dataset.viewUrl;
    if (buttons.length === 0 || !url) return;
    const current = choiceOf(reader.dataset.viewChosen);
    const failure = reader.querySelector<HTMLElement>('[data-role=view-error]');
    buttons.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.viewChoice === current)));
    buttons.forEach(button => button.addEventListener('click', async () => {
        const choice = button.dataset.viewChoice as ViewChoice;
        if (choice === current) return;
        if (failure) failure.hidden = true;
        try {
            const response = await fetch(url, {
                method: 'PUT', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ readingViewTypeId: choice === 'smart' ? 1 : choice === 'original' ? 2 : null })
            });
            if (!response.ok) throw new Error('The view was not saved.');
        } catch {
            if (failure) failure.hidden = false;
            return;
        }

        // Automatic leaves the decision to the server; the page the profile is on goes along.
        const page = currentPage();
        const address = choice === 'auto' ? new URL(location.pathname, location.origin) : null;
        if (address) {
            if (page !== null) address.searchParams.set('pg', String(page));
            location.href = address.pathname + address.search;
        } else location.href = viewAddress(location.pathname + location.search, choice as 'smart' | 'original', page);
    }));
}
