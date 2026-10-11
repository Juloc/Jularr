using System.Globalization;

namespace Jularr.Frontend.Framework;

public sealed class UiText
{
    private static readonly IReadOnlyDictionary<string, (string En, string De)> Messages =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["app.name"] = ("Jularr", "Jularr"),
            ["nav.home"] = ("Home", "Startseite"),
            ["nav.library"] = ("Library", "Bibliothek"),
            ["nav.watchlist"] = ("Watchlist", "Watchlist"),
            ["nav.calendar"] = ("Calendar", "Kalender"),
            ["nav.profile"] = ("Profile", "Profil"),
            ["nav.settings"] = ("Settings", "Einstellungen"),
            ["nav.admin"] = ("Admin", "Admin"),
            ["nav.back"] = ("Back", "Zurück"),
            ["nav.primary"] = ("Main navigation", "Hauptnavigation"),
            ["nav.mobile"] = ("Mobile navigation", "Mobile Navigation"),
            ["shell.search"] = ("Search", "Suche"),
            ["shell.notifications"] = ("Notifications", "Benachrichtigungen"),
            ["shell.skip"] = ("Skip to content", "Zum Inhalt springen"),
            ["shell.preview"] = ("Shell preview", "Shell-Vorschau"),
            ["shell.previewInfo"] = ("Presentation preview only. No account or profile data is loaded.", "Nur Darstellungsvorschau. Es werden keine Konto- oder Profildaten geladen."),
            ["account.login.title"] = ("Sign in", "Anmelden"),
            ["account.field.userName"] = ("Username or email", "Benutzername oder E-Mail"),
            ["account.field.password"] = ("Password", "Passwort"),
            ["account.login.remember"] = ("Stay signed in", "Angemeldet bleiben"),
            ["account.login.submit"] = ("Sign in", "Anmelden"),
            ["account.login.unavailable"] = ("Sign-in is not connected yet.", "Die Anmeldung ist noch nicht verbunden."),
            ["account.login.showPassword"] = ("Show password", "Passwort anzeigen"),
            ["account.login.hidePassword"] = ("Hide password", "Passwort verbergen"),
            ["account.login.options"] = ("Other sign-in options", "Weitere Anmeldeoptionen"),
            ["form.error"] = ("Something went wrong.", "Ein Fehler ist aufgetreten."),
            ["form.required"] = ("This field is required.", "Dieses Feld ist erforderlich."),
            ["form.too_long"] = ("The value is too long.", "Der Wert ist zu lang."),
            ["form.out_of_range"] = ("The value is outside the allowed range.", "Der Wert liegt außerhalb des erlaubten Bereichs."),
            ["form.invalid_type"] = ("Invalid value.", "Ungültiger Wert."),
        };

    public string this[string key] => Messages.TryGetValue(key, out var item)
        ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? item.De : item.En
        : key;
}
