// A PIN confirms itself once its configured length is entered.
for (const input of document.querySelectorAll<HTMLInputElement>('input[data-pin-length]')) {
    const length = Number(input.dataset.pinLength);
    input.addEventListener('input', () => {
        input.value = input.value.replace(/\D/g, '');
        if (input.value.length === length) input.form?.requestSubmit();
    });
}
