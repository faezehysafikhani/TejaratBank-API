# Deployment Configuration — Tejarat Bank API

What to set when installing this API at a new site (e.g. a bank environment). None of these
steps touch source code, and none require a rebuild.

## 1. Set the frontend's origin (CORS)

Edit `appsettings.Production.json` — the only file this install step touches:

```json
{
  "Cors": {
    "AllowedOrigins": ["https://<bank-host-or-ip>:<frontend-port>"]
  }
}
```

Use the exact scheme + host + port the frontend is served from. If the frontend and this API
are served from the same public address (a reverse proxy in front of both), you can leave this
empty — same-origin requests don't need CORS at all.

This file is loaded automatically: `ASPNETCORE_ENVIRONMENT` defaults to `Production` when unset,
which is the normal case for a real install (outside an IDE launch profile).

## 2. Set the JWT signing key

> **Every environment except local Development** (Staging, UAT and so on) now requires a real
> `Jwt__SigningKey`, and a database created there needs `Identity__AdminPassword` (at least 12
> characters, not `Admin@12345`). Only `ASPNETCORE_ENVIRONMENT=Development` may use the built-in
> development key and the default `admin` password.

Never put a real signing key in any `appsettings*.json` file — it must not be committed. Set it
as an environment variable instead:

```
Jwt__SigningKey=<a real secret, at least 32 characters>
```

Set it however this environment sets process environment variables for the app — an IIS
Application Pool's environment variables, a Windows service's environment, a systemd unit file,
etc. If this is missing, a known development placeholder, or shorter than 32 characters, **the
app refuses to start** — this is intentional (the check in `Program.cs`) and is not a bug; set
the key and start it again.

## 3. Set the database connection string (if not using the local default)

`appsettings.json`'s default (`Server=.`, i.e. this same machine) works if SQL Server runs on
the same host. Otherwise, add to `appsettings.Production.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=<db-host>;Database=TejaratBank;Trusted_Connection=True;TrustServerCertificate=True;",
    "Chat": "Server=<db-host>;Database=TejaratBank;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

## 4. AllowedHosts

`"AllowedHosts": "*"` in `appsettings.json` (the base file) accepts any Host header, which is
the standard ASP.NET Core default and fine behind a reverse proxy/firewall. Tighten it in
`appsettings.Production.json` only if this site's policy requires restricting it to a specific
host name.

## 5. Start/restart

Restart the API (IIS site recycle, service restart, etc.). No `dotnet build`, no source change.

## 6. Organization structure schema (deputies, companies, positions)

Nothing to do by default: with `Database:InitializeOrgStructureSchema` = `true` (in
`appsettings.json`) the API creates the `organization` and `org_structure` tables on a new
database and applies pending migrations on an existing one, at start-up. It never creates a
table or column that already exists.

Where schema changes must be run by a DBA instead, set it to `false` in
`appsettings.Production.json` and run the scripts in `db/` (each needs `-I`, QUOTED_IDENTIFIER):

| Database | Script |
|---|---|
| New (no `organization` / `org_structure` tables yet) — after the API has started once | `db/new-database-org-structure.sql` |
| Already has them (an earlier version) | `db/upgrade-1-organization.sql`, then `db/upgrade-2-org-structure.sql`, then `db/upgrade-3-unit-positions.sql`, then `db/upgrade-4-position-companies.sql`, then `db/upgrade-5-posts.sql`, then `db/upgrade-6-task-posts.sql`, then `db/upgrade-7-substitution.sql`, then `db/upgrade-8-post-parent.sql` |
| Sign-in sessions (security update, any version before `0.1.0.1405080301-security1`) | `db/upgrade-9-refresh-token-family.sql` (the API also adds these two columns at start-up when `Database:SeedOnStartup` is `true`) |

```
sqlcmd -S <db-host> -d TejaratBank -I -i db/upgrade-1-organization.sql
```

Positions are per company, and a company's head is not chosen on the company: whoever is placed
directly in the company with its position that has no superior («رئیس کل») is its head. The
upgrade gives each position the companies it is used in, and gives today's heads that position.
Positions it found with no superior keep none: give each one its superior (only the head's
position stays without one).

Posts (upgrade 5): each post has a code (unique in its company) and one person. A position
several people held in a deputy becomes one post each; people placed directly in a company with
a position get a post there (the head's included); every post gets a number of its company.

## 7. Tasks are given to posts

`OrgStructure:AssignTasksToPosts` is `true` in `appsettings.json`: a task's creator, responsible
people and access list are posts, and their holders are the task's people. When someone leaves a
post or a new person takes it, the post's tasks go with it. A vacant post can't be given a new
task, and someone with no post can't be given one. When the API starts it gives tasks that have
no posts yet to their people's main posts (people with no post stay on them as people). Set it
to `false` in `appsettings.Production.json` to give tasks to people again.

When a post falls vacant, its open tasks show in «کارتابل جانشینی» to the managers above the post
and to holders of the permission «مدیریت کارتابل جانشینی». They may put another post of the
company in charge of it (سرپرستی): its holder sees those tasks and comments on them as a stand-in
(«جانشین ...»), nothing more. When someone takes the vacant post the stand-in ends by itself.

Disabling a user's account takes them out of their posts: the posts fall vacant and their tasks
go to «کارتابل جانشینی». Their deputy and set manager stay; enabled again, they need to be put
back in a post.

## 7.1 Giving tasks only to one's own level and below

`OrgStructure:LimitTaskAssignment` is `true` in `appsettings.json`: in the org chart, a user makes
responsible for a task only themselves, the people below them, the people of their own deputy at
their level or lower, and the people of the company at their own level (an expert to an expert of
another deputy; not someone in a higher position of their own deputy) - never anyone above. A
person's level is that of their position: how many superior positions («سمت مافوق», set in «سمت‌ها»)
are above it up to «رئیس کل» - the highest position they hold; someone with no position is measured
by how many managers are above them in the chart. People with no
place in the chart yet are not limited (and anyone may give them tasks); the permission «ارجاع
فعالیت به همه‌ی سطوح سازمانی» lifts the limit for its holders (the Administrator role has it).
Set it to `false` in `appsettings.Production.json` to let anyone give tasks to anyone.

## 7.2 Posts under posts

A post can sit under another post of the same deputy (or, for posts directly in the company,
another of those), at any depth: e.g. «کارشناس» under «معاون». The holder of the post above is
then the manager of the post's holder: they see their tasks in reports, may give them tasks with
the limit above on, and see the post in «کارتابل جانشینی» when it falls vacant - but get no
rights over the chart. A vacant post above is passed over to the next one up (or the deputy's
manager). Set in the chart's post panel («پست مافوق»); a post with posts under it can't be removed.

## 7.3 Each company on its own

`OrgStructure:LimitToOwnCompany` is `true` in `appsettings.json`: everyone is kept to their own
company - the companies they are placed in or head. Others' people are not in their user lists,
chat, work teams or reports; they cannot be given a task, access to one or a message; the org chart
and its panels show and change only their own company; «مدیریت همهٔ وظایف» shows only the tasks
of their own people. Within the company, anyone may message anyone (a manager above too), and
tasks follow 7.1 (an expert may give one to an expert of another deputy, never to someone above).
Someone with no place in any company reaches nobody but themselves; people with no place yet stay
in the user list, so they can be placed. The permission «دسترسی به همه‌ی شرکت‌ها» lifts the limit
for its holders (the Administrator role has it). Set it to `false` in `appsettings.Production.json`
to open the companies to one another.

## 7.4 The switches at a glance

Each one is on its own: turn off what is not wanted, the rest keeps working.

| Setting (`OrgStructure:`) | On in this API | What it does | Lifted for holders of |
| --- | --- | --- | --- |
| `LimitTaskAssignment` | yes | tasks only to one's own level and below (7.1) | «ارجاع فعالیت به همه‌ی سطوح سازمانی» |
| `AssignTasksToPosts` | yes | tasks given to posts, «کارتابل جانشینی» (7) | «مدیریت کارتابل جانشینی» (every vacant post) |
| `LimitToOwnCompany` | yes | everyone kept to their own company (7.3) | «دسترسی به همه‌ی شرکت‌ها» |

Work teams are always kept to one's own deputy with the org structure; «افزودن اعضای سایر
معاونت‌ها به تیم کاری» lifts that (still within one's own company when 7.3 is on).

## 8. Reminders and recurring tasks

`TaskManagement:Scheduler:Enabled` is `true` in `appsettings.json`: recurring tasks, due-soon /
overdue reminders and SMS held back during the quiet hours are sent by this API. Set it to
`false` in `appsettings.Production.json` only on a second server of the same database that should
not send them (one is enough; two would not send anything twice).

## 9. Taking a newer NexusCore

The NexusCore modules come as packages from `./packages` (see `nuget.config`), built in the Core
repository with `eng/pack-core-modules.ps1`. To update:

1. Copy the new `.nupkg` files of the packages this API uses into `./packages` (and the
   `SHA256SUMS.txt` that comes with them).
2. Set `<NexusCoreVersion>` in `TejaratBank-API.csproj` to the new version.
3. Build and deploy. The schema changes of Task Management, Notifications and Chat are applied by
   the modules at start-up; those of the organization modules as described in section 6.

## 10. Security settings

These keys are read from `appsettings.Production.json` or from environment variables (a `:` in a
key is `__` in a variable name). Everything has a safe default; the ones in **bold** need a value
at a real install.

### 10.1 Reverse proxy, client addresses and rate limits

Rate limits (sign-in, refresh, password recovery, SMS, uploads, writes) are on in this API. They
count by the caller's address, so the API has to know the real one:

```json
{
  "NexusCore": {
    "ForwardedHeaders": {
      "KnownProxies": ["10.0.0.5"],
      "KnownNetworks": ["10.0.1.0/24"],
      "ForwardLimit": 1
    }
  }
}
```

* **`KnownProxies` / `KnownNetworks`** — the address(es) of the reverse proxy (IIS ARR, nginx) in
  front of this API. `X-Forwarded-For` is believed **only** when the request really comes from one
  of them; sent anywhere else it is ignored. Empty (the default) trusts nothing but this machine:
  behind a proxy every user would then share the proxy's address and one quota, so set it.
* The proxy must send `X-Forwarded-For` and `X-Forwarded-Proto`, and pass `Set-Cookie` through.
* Limits per minute (`NexusCore:RateLimiting:<Policy>:PermitLimit` / `WindowMinutes`): sign-in and
  refresh 10, password recovery 5 per 15 min, SMS test 5 per 5 min, whole API 600 (counted per
  signed-in user, per address when anonymous, so a branch behind one NAT address does not share a
  single quota). Watch the 429 answers for two weeks and adjust.
* Several API instances behind a load balancer count separately (the limiter is in memory).

### 10.2 Sign-in sessions and the refresh-token cookie

* The browser keeps the refresh token in the cookie `nexus_rt` (`HttpOnly; Secure; SameSite=Strict;
  Path=/api/identity/auth`). Serve the front end and the API from **one address** (the proxy sends
  `/api` and `/hubs` here); with different origins set `Cors:AllowedOrigins` and see the
  front end's README.
* HTTPS is required outside Development (the cookie is `Secure`).
* `Jwt:AccessTokenMinutes` is `15`. A sign-in lasts at most `Identity:Session:RefreshTokenDays`
  (`14`) days; a refresh does not extend it. A refresh token that was already exchanged and is shown
  again after `Identity:Session:ReuseGraceSeconds` (`10`) seconds ends the whole sign-in and is
  recorded as `identity.refresh_token_reuse` in the audit log.
* Transition for clients that are not browsers: `Identity:Session:ReturnRefreshTokenInBody` and
  `AcceptRefreshTokenInBody` are `true`. When no such client exists, set both to `false`.
* After the first deployment every user signs in once more (old sessions lived in `localStorage`).
* Changing the password or the mobile number ends all the person's other sessions.
* Changing the mobile number needs the password and a code sent by SMS to the **new** number: the
  SMS panel must be set up. The text is the template «کد تأیید تغییر شماره موبایل» (`phone_change`).

### 10.3 Encryption keys (SMS API key, LDAP password)

```json
{
  "DataProtection": {
    "KeysPath": "D:\\Keys\\TejaratBank",
    "ApplicationName": "TejaratBank",
    "ProtectWithDpapi": true
  }
}
```

Without `KeysPath` the keys may live only in memory and every restart makes the stored SMS key and
LDAP password unreadable (they have to be entered again). Use a folder **outside** the application
folder, writable by the application pool identity only, and **back it up**. On Windows the keys are
also encrypted at rest with DPAPI (machine scope). **Before the first deployment of this version,
copy the keys the application pool user has now** (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` of
that user, or the registry location for pools without a profile) into this folder, so the stored
secrets stay readable. Several instances share one folder.

### 10.4 Where administrators can point the server

* **SMS gateway address** — only the provider's own host (e.g. `api.kavenegar.com`, https), or a
  host you list: `Security:OutboundAddresses:AllowedHosts` (`["sms.bank.local", "*.gw.bank.local"]`;
  listed hosts may be inside the network; `AllowHttp` allows plain http for them). Changing the
  address drops the stored key, so it has to be typed again. If outgoing calls go through a proxy,
  list the proxy's host too.
* **LDAP server** — `Security:OutboundAddresses:LdapAllowedPorts` (`389, 636, 3268, 3269`) and
  optionally `LdapAllowedHosts`. Loopback and link-local addresses are never accepted.
* The generic settings API no longer reads or writes `Notifications.*`, `Identity.*` and
  `TaskManagement.*` keys; those have their own screens.

### 10.5 Organization separation

Every query of the identity, organization, org-structure, task, chat and notification tables is
confined to the signed-in user's organization by the database layer, as a second line behind the
conditions in the code. Holders of `tenants.manage_all`, background services and sign-in are not
confined. To switch it off in an emergency: `Security:TenantFilter:Enabled` = `false`, or one
context only: `Security:TenantFilter:DisabledContexts` = `["ChatDbContext"]`.

### 10.6 Responses, logs, limits

* HSTS is sent outside Development; responses of `/api/identity/*` carry `Cache-Control: no-store`.
* **IIS log**: SignalR sends its token in the query string (`/hubs/...?access_token=`). In IIS
  Manager → Logging → Select Fields, turn **off** *URI Query (cs-uri-query)*, so tokens stay out of
  the log. Do the same on the reverse proxy.
* A request body is at most about 21 MB; an upload at most 20 MB (and the size set in the system
  settings, 200 KB by default). Page sizes are capped at 200.
* The front end reports Content-Security-Policy violations to `POST /api/platform/csp-report`
  (logged as warnings, `NexusCore.SecurityReports`) while the policy is in report-only mode.
* Password rules: at least 10 characters, three kinds of characters, no common passwords. The
  sign-in CAPTCHA has six letters and digits.

### 10.7 Packages

Checksums of the packages in `./packages` are in `packages/SHA256SUMS.txt`; compare before use
(`sha256sum -c SHA256SUMS.txt` in that folder, or `Get-FileHash` on Windows). Packages built by the
Core pipeline are signed when a signing certificate is configured there (`dotnet nuget verify`).

---

### What NOT to do
- Don't edit `appsettings.json` (the base file) for a specific deployment — it's meant to stay
  environment-independent so it's identical across every install. Environment-specific values go
  in `appsettings.Production.json` (this environment) or `appsettings.Development.json`
  (developers' machines only).
- Don't hardcode an IP/domain in `Program.cs` — CORS and hosts are already fully
  configuration-driven; there's nothing to touch there.
