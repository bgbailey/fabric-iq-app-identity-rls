# ISV portal

React + TypeScript portal for the ISV semantic gateway sample.

It shows:

- sign-in with the development identity provider
- the resolved gateway identity and RLS role
- free-text chat over the semantic model
- streamed gateway steps, including generated DAX
- RLS-filtered result rows
- an optional Power BI Embedded report with parity comparison

## Install

```powershell
npm install
```

## Build

```powershell
npm run build
```

The Vite build writes directly to `../SemanticGateway/wwwroot`, which the ASP.NET Core gateway serves as static files.

## Local development

```powershell
npm run dev
```

The Vite dev server proxies `/api`, `/dev-idp`, and `/mcp` to `http://localhost:5187`.
