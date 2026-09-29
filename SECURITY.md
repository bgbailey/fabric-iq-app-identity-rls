# Security

This repository is an educational sample that uses synthetic data. It is not a supported product.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting ("Report a
vulnerability" on the repository's **Security** tab) with a description, the affected file and steps to
reproduce. Do not include real credentials, tokens, tenant data or customer data.

## Using the sample safely

- Keep the demo token issuer (`Auth:Mode = Development`) to local development only. Use your own
  identity provider (`Oidc`) anywhere else.
- Keep certificates, token caches and `appsettings.Local.json` out of source control and out of synced
  folders. The sample stores local state under `%LOCALAPPDATA%`.
- The service principal is a workspace Admin. Treat its certificate as a high-value secret, and keep the
  gateway always sending the RLS role.
- Use synthetic or approved data only while you evaluate the pattern.
