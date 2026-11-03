import "./ChoiceSelect.scss";

// The choice is the submit; the button is only for browsers without script.
document.addEventListener('change', event => {
    if (event.target instanceof HTMLSelectElement && event.target.classList.contains('choice-select__control')) event.target.form?.requestSubmit();
});
