# Contributing

Thanks for helping improve this sample. Its purpose is to teach one pattern clearly, so the most useful
contributions keep it small and readable.

- **Keep it a how-to.** Prefer one clear way of doing something over options and abstractions. Put
  verification tooling under `dev/`, not `src/`.
- **Synthetic data only.** Never commit real tenant, workspace or application IDs, tokens, certificates
  or customer data. Local settings belong in `appsettings.Local.json`, which git ignores.
- **Say what you verified.** If a change depends on service behavior, note whether you tested it live.
  The evidence in `docs/evidence/` records what ran and what did not.

Before opening a pull request:

```powershell
dotnet build fabric-iq-app-identity-rls.sln
npm --prefix src/web run build
npm --prefix src/web test
```

By contributing you agree that your contribution is licensed under the repository's MIT license, and you
agree to follow the [code of conduct](CODE_OF_CONDUCT.md).
