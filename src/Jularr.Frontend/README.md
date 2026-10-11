# Jularr.Frontend

A new Razor Pages frontend, isolated from the existing Jularr.Web application.

- `Framework/Page.cs`: `JularrPage`, Page, reusable component tree, MasterDetail and Tabs.
- `Framework/BaseField.cs`: input field metadata from the backend's canonical `[DataField]` attributes, including required, length and numeric bounds.
- `Pages/Shared/_Layout.cshtml`: shared guest/authenticated shell, same theme tokens for Clean and Original.
- `Pages/Account/Login.cshtml`: **presentation only**. Credentials are disabled and no POST handler or authentication implementation is provided.
- `Pages/ShellPreview.cshtml`: development-only shell preview; unavailable outside Development.

The frontend project references `Jularr.Backend` for the existing DataField contracts, but does not modify any backend code or database. The backend's login operation, authenticated shell context, localized backend error catalog and service-bound list/paging contracts are not yet present. No fake sign-in or bypass is installed.

Until the backend supplies these contracts, do not enable login submission or represent development shell fixtures as authenticated application data. Razor Pages are used directly; no SPA router or self-HTTP loop is introduced.

To run the presentation preview once .NET 10 SDK is available:

```sh
dotnet run --project src/Jularr.Frontend/Jularr.Frontend.csproj
```

Visit `/Account/Login` or, only in Development, `/ShellPreview`.
