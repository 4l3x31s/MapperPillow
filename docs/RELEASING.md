# Releasing MapperPillow

MapperPillow is published to nuget.org by
[`.github/workflows/release.yml`](../.github/workflows/release.yml), triggered by a
`v*` tag and gated on a human approving the `release` environment.

**No API key exists anywhere.** Authentication is
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
the workflow exchanges a GitHub OIDC token for a NuGet key that is single-use and
lives about an hour. There is no secret in the repository, none in GitHub Actions
secrets, and none on a maintainer's machine.

That is not just tidiness. Since **2026-08-17** nuget.org caps new API keys at
**30 days** — the 365-day option is gone, and every key created before that date
expires on **2026-11-01**. Publishing with a stored key now means rotating it
roughly monthly, forever.

## The one rule that shapes everything else

**nuget.org accepts a version exactly once.** There is no unpublish and no
overwrite. A broken `1.0.0` can only be delisted, and `1.0.1` becomes the first
version anyone can actually install. Every guard below exists because of that.

## Publishing

```powershell
# 1. Pre-flight on your machine. Publishes nothing; finds the problems in minutes
#    instead of after the tag is already pushed.
./eng/Release.ps1

# 2. Tag. The tag is the trigger.
git tag -a v1.0.0 -m 'MapperPillow v1.0.0'
git push origin v1.0.0

# 3. Approve the run's `release` environment in the GitHub Actions UI.
```

The workflow re-runs everything `Release.ps1` did — tag/version agreement, the full
suite on `net8.0`/`net9.0`/`net10.0`, and `eng/Verify-Package.ps1` — **before** the
approval prompt appears, so you are never asked to approve a release that was
already broken. The `verify` job packs and uploads the artifacts; the `publish` job
downloads and pushes exactly those bytes without re-packing.

Only the `publish` job requests `id-token: write`. The long verification job never
holds a credential.

## One-time setup

### 1. The Trusted Publishing policy on nuget.org

Sign in to nuget.org → your username → **Trusted Publishing** → add a policy:

| Field | Value |
| --- | --- |
| Repository Owner | `4l3x31s` |
| Repository | `MapperPillow` |
| Workflow File | `release.yml` — **file name only**, no `.github/workflows/` prefix |
| Environment | `release` |
| Glob pattern | `MapperPillow` — an exact match, **not** `*` |

**Renaming the workflow file breaks publishing** until the policy is updated to
match. That coupling is deliberate on nuget.org's side: the file name is part of
what it trusts.

The glob is the blast radius of the policy. `*` would let this one repository's
workflow publish under *every* package the account owns — so a compromised action
in this repo becomes a compromise of everything. MapperPillow ships exactly one
package ID (`Directory.Build.props` sets `IsPackable=false` repo-wide, and EF Core
support lives inside the same package), so the exact pattern costs nothing. If a
`MapperPillow.Something` package is ever added, widen it to `MapperPillow*` then —
not in advance.

Scopes must include **push new packages** for the very first release, since the ID
does not exist yet, plus **push new versions** for everything after.

If the repository is private, the policy starts *temporarily active* for 7 days: if
no publish happens in that window it goes inactive. Publishing once locks the policy
to the repository and owner IDs permanently, which is what stops someone deleting
the repo, recreating it under the same name, and publishing as you.

### 2. The nuget.org account name

The workflow passes `user: 4l3x31s` to `NuGet/login@v1`. That is the nuget.org
**profile name**, never an email address — an email fails the token exchange. It is
public on the profile page and is not a credential, so it is written in the
workflow rather than kept in a secret. Change it there if the publishing account
changes.

### 3. The `release` environment

Repository **Settings → Environments → New environment → `release`**, and add
yourself as a **required reviewer**. That approval is the release button: the tag
starts the run, a human decides whether it ships.

### 4. After the first successful publish

[Reserve the ID prefix](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation)
`MapperPillow.` so nobody else can publish `MapperPillow.EfCore` or
`MapperPillow.Extensions` under a name users will read as yours.

## Cutting a new version

1. Bump `<Version>` in [`src/MapperPillow/MapperPillow.csproj`](../src/MapperPillow/MapperPillow.csproj).
   Follow [SemVer](https://semver.org): a changed generator *output* that alters
   observable mapping behaviour is a breaking change even though no public API moved.
2. Leave `<AssemblyVersion>`/`<FileVersion>` alone unless the **major** moves. They
   are pinned to `MAJOR.0.0.0` so a patch release is never a new assembly identity.
3. Update `<PackageReleaseNotes>`.
4. Set `<PackageValidationBaselineVersion>` to the **previous published version**.
   From then on the pack fails if the new version breaks the API that consumers
   already compiled against — the check is worth more than any changelog discipline.
5. Run `./eng/Release.ps1`, commit, tag, push the tag, approve.

## Deliberately not done

- **No strong name.** Adding one later is a binary breaking change for every
  consumer, and removing one is too. It buys nothing on .NET 5+ unless a
  strong-named assembly needs to reference MapperPillow directly. Decide once,
  before 1.0.0 is public — after that the door is closed.
- **No author signing certificate.** Repository signing by nuget.org, plus the
  SourceLink commit recorded in the `.nuspec` and the Trusted Publishing policy
  binding releases to this repository and workflow, already tie the binary to its
  source. A code-signing certificate adds cost and a renewal cliff.
- **No `workflow_dispatch` on the release workflow.** A manual trigger would let
  any branch reach the publish job. The tag is the only entry point.
