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

## 7.1 Giving tasks only to one's own level and below

`OrgStructure:LimitTaskAssignment` is `true` in `appsettings.json`: in the org chart, a user makes
responsible for a task only themselves, the people below them, and the people at their own level
(except someone in a higher position of their own deputy) - never anyone above. People with no
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

## 8. Reminders and recurring tasks

`TaskManagement:Scheduler:Enabled` is `true` in `appsettings.json`: recurring tasks, due-soon /
overdue reminders and SMS held back during the quiet hours are sent by this API. Set it to
`false` in `appsettings.Production.json` only on a second server of the same database that should
not send them (one is enough; two would not send anything twice).

## 9. Taking a newer NexusCore

The NexusCore modules come as packages from `./packages` (see `nuget.config`), built in the Core
repository with `eng/pack-core-modules.ps1`. To update:

1. Copy the new `.nupkg` files of the packages this API uses into `./packages`.
2. Set `<NexusCoreVersion>` in `TejaratBank-API.csproj` to the new version.
3. Build and deploy. The schema changes of Task Management, Notifications and Chat are applied by
   the modules at start-up; those of the organization modules as described in section 6.

---

### What NOT to do
- Don't edit `appsettings.json` (the base file) for a specific deployment — it's meant to stay
  environment-independent so it's identical across every install. Environment-specific values go
  in `appsettings.Production.json` (this environment) or `appsettings.Development.json`
  (developers' machines only).
- Don't hardcode an IP/domain in `Program.cs` — CORS and hosts are already fully
  configuration-driven; there's nothing to touch there.
