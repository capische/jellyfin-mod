# JellyfinMod workspace rules

## Before work

- Read the child repository's `CLAUDE.md`, applicable `.claude/` files and nested instructions.
- Follow `jellyfin-web/docs/jellyfinmod/PLAN.md` and the current `PHASE*.md`; read `README.md` for architecture and `UX.md` for UI work.
- Preserve existing edits and other agents' work. Change only files required by the task.
- Plugin repository: `master` only. Web repository: `master` is the production fork and `jellyfin-mod` carries all mod work.
- `jellyfin-web` stays a fork of upstream Jellyfin Web and keeps merging upstream releases; keep changes additive and the upstream patch surface listed.
- Delete merged and stale branches, locally and on the remote, rather than accumulating them.

## Do not over-engineer

User rule, 2026-10-09: **do not over-engineer.** Most of any product is used rarely, and about 20% of it carries 80% of the use, so build for that 20%. Hold this in design, implementation, tests and reports alike.

- Build the simplest thing that covers the common path the user actually uses. Add an option, a setting, an abstraction, a fallback or an edge-case branch only when a real, current case needs it, not for a case that could happen.
- Prefer a small change to the existing code over a new component, layer, hook or file. Extend what is there before adding something new, and delete what the change makes unused.
- Do not tune past "good enough to judge". Offer one setting, or at most two, not a grid of variants. Stop when the user can see it works; they will say if it needs more.
- Do not widen a task. A fix fixes the reported problem; related polish, refactors and "while I am here" changes are listed for the user and left alone.
- Size the work to the use: a rarely used path gets a plain, correct implementation and its normal checks, not special infrastructure. The test gates, the lease rules and the safety of destructive paths (retention delete, data restore) are not scaled down by this rule; it limits what gets built, not how carefully it is verified.
- If a simpler design exists and it loses something, say what it loses in one sentence and choose the simple one unless the user objects.

## Agents and pacing

- Pause all work at 80% of the 5-hour usage limit.
- When the session (5-hour) or weekly limit has less than 5% left, prepare to stop and wait until the limit resets: start no new live step, finish or safely abandon the current one, restore any instance you changed, commit finished work, update the handover, then stop. This applies even when the user has lifted the 80% pause for an agent, so a hard cut-off never lands mid-step.
- Opus 5.5 at high effort designs and implements; **every code review uses Codex GPT-6.1 Sol at high reasoning effort** (user, 2026-10-01; replaced GPT-6 Astra, which replaced Fable on 2026-09-26); Sonnet executes mechanical, fully specified work. Do not use Fable: the user judged Opus 5.5 high comparable in quality and cheaper (2026-09-24), and Codex now takes code review.
- Choose the model by the brief: if the success criterion can be written as a checklist before the agent starts, use Sonnet; if the first step is "investigate", use Opus. Say in each report which model and effort were chosen.
- Plans are written by an Opus agent before implementation; a different agent may execute them.
- **Acceptance is a gate** (user, 2026-09-25): a phase or slice is done only when its live acceptance checklist has passed on the isolated instance, not when it is built and its suites pass. Do not start a phase that depends on another until that one is accepted, and do not change the platform under accepted-but-untested code without first saying so to the user. Record the state honestly in `PLAN.md` as one of: planned, built (not accepted), accepted, released, postponed.
- One owner per behaviour: when phases overlap (for example versions across Phases 6, 10, V1 and 12), name the owning slice in `PLAN.md` and route changes and acceptance through it.
- **One task at a time** (user, 2026-09-26): run a single implementation task at once; start the next only when the current one is finished (reviewed, verified, merged). Its own review and verification agents may run as part of that task. Tasks already running on 2026-09-26 finish first, and nothing new starts alongside them.
- Run phase work in separate background agents, one per phase. Agents that may edit the same repository at the same time work in their own git worktrees: a shared working tree lets one agent's `git add` sweep up another's in-progress edits, which has already happened.
- When agents must share a checkout, each stages only files it created or was told to change, and says so in its report.
- Tell the user when everything is finished and ready for their own testing; they want a complete product, not fragments.
- The whole-code-base review before release is **on hold** (user, 2026-09-25): the user will run it with another AI. Do not start it; when its findings arrive, a separate Opus 5.5 high agent fixes them. Per-slice and phase code reviews run on Codex GPT-6.1 Sol high as below; the whole-code-base review may use the same recipe when the user asks for it.
- Get genuine high effort from an agent definition with `effort: high` in its frontmatter (`jellyfinmod-opus-high` for design and implementation, `jellyfinmod-sonnet-high` for checklist verification); a sentence in the prompt does not change effort. Verify it from the agent's transcript, which records `"effort"` on every request.

### Sonnet

- Suits running suites and reporting output verbatim, the Chrome acceptance re-run once Chromium passes, deployment and its fixed verification checklist, parity sweeps and inventory diffs, fixture cleanup, branch pruning, doc edits from settled decisions, and applying a fix specified line by line.
- **Checklist verification runs on Sonnet at high effort** (`jellyfinmod-sonnet-high`), not Opus medium: the pass/fail criterion is written before the agent starts, and effort governs tool calls, which is what verification consists of. The verifier reports failures verbatim and never diagnoses or relaxes a check.
- Verification that needs judgement — whether evidence is sufficient, classifying a failure (harness, upstream or mod), diagnosing why a check failed, adversarially trying to break a change — runs on Opus 5.5 high, or Codex GPT-6.1 Sol high when it is a code review. Opus medium is only the budget fallback when about 30% of the weekly allowance is left.
- Keep off Sonnet: the retention delete path, diagnosing a failure from symptoms, judging whether evidence is sufficient to call something done, and any task whose instruction would be "work out the right approach". A Sonnet agent asked to diagnose the browse-grid shortfall returned a confident wrong root cause that then had to be undone.

### Opus

- High is the default for coding, implementation and evidence-gathering runs. Anthropic's own guidance for Opus 5 is to start at high and step down only where evals show quality holds; effort also governs tool calls, and lower effort makes fewer and terser ones, which is exactly the verification this project runs on.
- Drop to medium only to conserve budget, when about 30% of the weekly allowance is left, so work can continue until the reset. It is a cost decision, not a quality-neutral one.
- Use low effort for mechanical steps.
- If a medium agent returns a confident answer resting on an unchecked premise, tell the user and re-run that task at high rather than absorbing it quietly.

### Code review (Codex GPT-6.1 Sol, high)

- **When a review runs** (user, 2026-10-09): run the Codex review **once, at the end**: only after every test and matrix for the change has passed, the user has approved the tested change, and it is ready to push or merge to its target branch (plugin `master`, web `jellyfin-mod`). Do not run it on a first look, between fix rounds, after each small change or to check a hunch. Codex usage is limited and a review per round costs both that and time. When the review finds P1/P2 issues, fix them and re-review only that fix delta, once; if the fix changes what the user sees, it goes back through the first-look rule instead. The user can still ask for a review earlier, and a design that sits on a destructive path (the retention delete path) may be adversarially reviewed before it is built on, when the brief says so.
- Every code review runs on Codex with model `gpt-6.1-sol` at high reasoning effort (user rule, 2026-10-01; it needs Codex CLI 0.159.3 or later — older CLIs reject it with "not supported when using Codex with a ChatGPT account"): phase and slice reviews, re-reviews of fix deltas, adversarial verification of critical findings — especially the retention delete path.
- Run it read-only from the worktree that holds the branch, never the main checkout, and save the output as the review record:
  `codex exec -m gpt-6.1-sol -c model_reasoning_effort='"high"' -s read-only "<review brief naming the diff, e.g. git diff <base>..<tip>>" > <scratchpad>/review-<slice>.md`
  Always redirect stdin from `/dev/null` (`codex exec … < /dev/null > out.md 2>&1`): run in the background without it, `codex exec` waits forever on "Reading additional input from stdin" (2026-09-26, a review hung for six hours).
  `codex review` cannot take custom instructions together with `--base` (CLI 0.156.1 rejects the combination), so use `codex exec` for every briefed review; plain `codex review --base <ref>` (no brief) is only a quick extra pass. Confirm `model: gpt-6.1-sol` and `reasoning effort: high` in the output header and say so in the report.
- The review brief names the diff, the decisions it must respect, what to look hard at, and asks for findings ranked P1/P2/P3 with file:line, a failure scenario and a fix direction, plus a verdict.
- **Stop Codex at 80% usage** (user, 2026-09-26, so ChatGPT credits remain for the user): before every Codex run, run `.claude/scripts/codex-usage.sh`. It reads the plan's rate limits from the latest Codex session record and exits 1 when the 5-hour or weekly Codex window is at or above 80%. On exit 1, start no Codex run: queue the review, tell the user, and resume after the reset time it prints (Sydney). The figure is as of the previous Codex run, so treat 70–80% as "one more review at most", and refresh it after a reset (or when the record may be stale) with a tiny call: `codex exec --skip-git-repo-check -m gpt-6.1-sol -c model_reasoning_effort='"low"' -s read-only "Reply with exactly: OK" < /dev/null`. This 80% stop also applies to long runs such as the whole-code-base review (user, 2026-09-29).
- Run Codex reviews **one at a time**, never in parallel (user, 2026-09-26): the ChatGPT plan's Codex usage limit was hit by two parallel reviews. If the limit is hit, wait for the reset time Codex prints and re-run; do not fall back to another model without asking the user.
- Codex sends the code to OpenAI under the user's ChatGPT plan; never include `.env`, tokens or other secrets in the brief or the reviewed tree.
- A reviewer only reviews: no fixes, no deployments, no instance changes. Fixes go to an Opus 5.5 high agent, and the fix delta gets its own Codex re-review.
- **Cap review scope** (user, 2026-10-08): product code is reviewed until Codex approves. Test runners, simulations and other test-only scripts get one review pass in which Codex reports only real defects (a wrong verdict, a run that could touch production or another instance, lost user data or secrets); hardening ideas go on a follow-up list in the phase document, not into another round. Phase 9 spent four of its eight rounds on its live runner.

### Design (Opus 5.5 high)

- Opus 5.5 at high effort takes design work: the upstream patch surface, whose decisions bind every future merge; phase plans; UI and UX design proposals.
- **Show the design before implementing it** (user, 2026-10-09): when a new feature, a bug fix or a change needs a design decision that changes how something looks or is laid out, first make a simple static proposal: a plain HTML/CSS page or an image mockup, no build, no deploy, no app code. Show the user how it is going to look (screenshots, or the file to open) and stop. Implement only after the user approves the design. Offer one proposal, or at most two when the choice is genuinely open, not a grid of variants.
  - **Skip it only when the user says so explicitly**, for example "go implement without showing the design" or "implement it directly". A vague go-ahead ("do it", "fix it") is not that: if the change needs a design decision, propose it first. A change with no visual decision in it (backend, data, a bug whose fix looks the same as before) needs no proposal.
  - **A small style tweak to something already built** (a colour, a spacing, a blur value) takes the live-styles route in Testing instead of a mockup: inject the values into the deployed page and screenshot it.
  - After approval the work follows the gates in Testing: first look on one instance, the rest, re-verify, then review. The approved mockup is the reference the first look is compared against, and the report says where it differs.
  - A brief to another session for a change like this asks for the proposal first and says to wait for the approval, which comes through the session that started it.

## Testing

- Develop and test on the development machine; use the Pi only for Linux-specific suites and for deployment.
- Browser work iterates on Playwright's own bundled Chromium, headless. Final acceptance always runs the same checks on real Google Chrome, and nothing is finished without that pass.
- Iterate with fast test settings (for example a minute-scale retention window), then confirm the behaviour with the real settings.
- **Re-run only what a fix touches** (user, 2026-10-08): during review-fix rounds, re-run the Chromium checks and the live steps the change affects, and say which steps were skipped and why. Run the full live list and the real-Chrome pass once, on the final reviewed code, before acceptance. A full live run plus Chrome after every round cost Phase 9 about an hour per round.
- **Show first, then the rest, then review** (user, 2026-10-09): a change the user can see goes through four gates, in this order. Never skip one, and never start a later gate before the user has approved the earlier one.
  1. **First look:** implement only a representative first part of the change (the user names it, for example the computer and TV layouts; if not named, pick the one the change is mainly about) and verify it on **one** instance, the one the user will look at (18096 by default, or a single local environment), in Playwright Chromium. Then stop and show: screenshots, the key numbers, the URL to open and what to look for. Only cheap, low-risk checks come before approval: typecheck, lint, a smoke run and the screenshots.
  2. **The rest:** after the user approves the first look, implement the remaining parts (for example phone and the other themes), copy the same build to the remaining instances and environments (no rebuild unless the code changed), and run the full matrix: all browsers, layouts and themes, and the real-Chrome pass. Then report the results and stop again.
  3. **Re-verify:** the user checks the finished change. A change they ask for here that alters what they see goes back to gate 1 for that part; one that does not (a fix to a test, a comment, a doc) does not.
  4. **Review:** only after the user approves the tested change, run the one Codex code review (see Code review above) and then push.
  - **The first-look instance stays leased** while the user looks (renew it, release it when told). Each report says which gate it is, and says what has not been run. Record the slice in `PLAN.md` as built (not accepted) until the full matrix has passed and the user has approved it.
  - **A brief to another session** that is a user-visible change states this rule and names the first part, so the session stops after each gate and waits for the user's approval, which comes through the session that started it. The rule does not apply to changes with nothing to look at (backend, migrations, refactors, tooling), which follow the normal flow, nor when the user asks for the full run.
- **Style changes are tested simply** (user, 2026-10-09): a change to styles, markup or copy needs no test run, runner or user-data step. Find the values live: load the page that is already deployed in a browser, inject the candidate CSS into it (a style tag or setting the properties on the element), screenshot the page, and repeat until it looks right, with no rebuild or redeploy per try. Put the chosen values into the source once, build once, deploy by copying the build, and take the page screenshot (Home, scrolled where the change shows) as the first look. Read computed styles from the page when a number is needed. Run the hero, playback or full matrix only at the later gates, and only for behaviour, not for how something looks.
- **Suites on the Mac first** (user, 2026-10-08): run the plugin suites that pass on macOS on the Mac (currently Phase 0 and 1 smoke, Phase 2, Phase 3, Q16 Trakt, Phase 9 and Phase 10), in parallel where they share no ports or folders. Send only the Linux-specific suites to the Pi (hardlinks, Transmission seed protection, the root-only Phase 4 run, Phase 5 with its bind-mount aliases, and any suite that fails on macOS for platform reasons). The full Pi run of all 14 suites in sequence took 29 minutes.
- **Batch service restarts** (user, 2026-10-08): live runners that must stop the instance to change its database group those changes into as few stop/start cycles as the checks allow, because each Jellyfin restart on the Pi costs a minute or more. Never trade a check's meaning for speed: a step that must prove behaviour across a restart keeps its own restart.
- Leave no test fixtures behind: no `JellyfinMod …` titles or files in any library or catalog once a run ends, and no test libraries. Remove a test library only through `DELETE /Library/VirtualFolders` (never by deleting its folder on disk, which leaves an orphan view), then prove cleanup with `GET /UserViews` for `oleksii`: only the instance's own libraries (Movies, Shows) may remain. Leftover "JellyfinMod V1 …", "Movies Alt" and "Movies B" views reached the user on 2026-09-28.
- Before calling the new interface a replacement, compare it against stock: every movie and show present, and audio-track and subtitle switching working.
- Test new functionality through real E2E or integration boundaries. Do not add unit tests.
- Exercise plugin APIs through an actual HTTP host, authentication, authorization, serialization, migrations and SQLite.
- Exercise the built web application in a real browser against running Jellyfin; cover desktop, mobile and TV layouts.
- Use a real HTTP boundary test server when controlling dependencies such as TMDB.
- Do not accept mocked clients/responses, reflection, helper tests or status-code-only probes as E2E evidence.
- Require passing E2E acceptance before declaring functionality complete. Builds, lint and static checks are supporting evidence only.
- Sign in as `oleksii` with an empty password. Never invent, store or request a password for this account.

## Isolation and production

- Run JellyfinMod development, deployment and E2E on `jellyfinmod-test`, port `18096` by default. The acceptance instance `jellyfinmod-acceptance` (`28096`) and the Phase 5/6 live instance (`48096`) are also allowed, but only under a lease (see Instance leases below). Never use 8096.
- Keep its config, DB, plugins, cache, transcodes and web bundle under `/mnt/4tb/jellyfin-mod/test` (moved off the SD card on 2026-10-01; `/home/pi/media/test/jellyfinmod` is a symlink to it, so old paths still resolve, but new scripts and docs should use the `/mnt/4tb` path and never write test data to the SD card); keep production-media mounts read-only.
- Never deploy phase work to, restart or mutate production service `jellyfin`, port `8096`.
- Clone production configuration only to establish or deliberately refresh the test baseline; keep production running.
- When cloning an instance's config, copy all of `/config` including `metadata` (People, Studio and the rest), and confirm no referenced image files are missing before testing. A clone without `metadata` made every Cast & Crew portrait 404 on 28096 and looked like a server bug.
- For unrelated production fixes, use a clean worktree based on `origin/master`, rebuild the published Git revision and deploy to production. Do not route these fixes through the mod test instance.

## Instance leases

Every mod instance (`18096` test, `28096` acceptance, `48096` live Phase 5/6) has a **lease**, so two sessions never run on, deploy to or restart the same instance at once (user, 2026-10-08). `18096` is used only when a session needs it; an unleased instance is free for any session to take.

- **Tool:** `.claude/scripts/lease.sh`, run from the workspace root. It reads and writes the lease files on the Pi under `/mnt/4tb/jellyfinmod-leases/`, so every session sees the same state, and all changes happen under one lock, so two simultaneous claims cannot both win. Run it with no arguments for usage.
- **What a lease file holds** (`<port>.json`): `instance`, `label`, `owner` (your session name), `task` (one line: what you will do), `note` (optional: branch, worktree, bundle path), `claimed_at`, `renewed_at`, `ttl_minutes`, and `expires_at` (UTC, with epoch copies). Waiters are kept in `<port>.queue.json` next to it. Every claim, renew, release, takeover and queue change is appended to `history.log` in the same folder. The tool prints times in Sydney time.
- **Claim before you touch it:** before any run of a live test or E2E suite, a deploy of a plugin or web bundle, a restart or a config change, run `lease.sh claim <port> --owner <session-name> --task "<what you will do>" --ttl-minutes <N>`. Use your session name as the owner every time, or set `LEASE_OWNER`. Reading (health checks, `GET`s, screenshots of a page nobody is driving) needs no lease.
- **Held by someone else:** the claim fails with exit 1 and prints who holds it and who is queued. Pick another free instance (`lease.sh free` lists those nobody holds or queues for), or wait in line: `lease.sh claim <port> … --wait-minutes N` joins the instance's queue and claims the lease when it is your turn; `lease.sh claim --any 18096,48096 … --wait-minutes N` queues on several and takes whichever reaches you first. Run it in the background and do other work meanwhile; it polls every 10 seconds on the Pi, so a sleeping Mac does not break it.
- **The queue is first come, first served** (user, 2026-10-08): a released or expired lease goes only to the first waiter in its queue, and a plain `claim` fails (exit 1) while anyone is queued, so nobody can overtake a waiter. `lease.sh status` shows each queue with owner, task and time waited; every join, leave, drop and timeout goes to `history.log`. A waiter leaves the queue when it claims, times out, is killed or loses its ssh connection; an entry without a heartbeat for 2 minutes is dropped. `lease.sh wait <port> --owner <name>` queues without claiming and, once free, holds the instance for that owner for 5 minutes so the next `claim` succeeds; `lease.sh leave <port> --owner <name>` gives up a queue place or such a hold, and a running waiter for that place stops with exit 1 without claiming. A successful `claim` likewise stops all your own queued claim waiters, for every instance, so they cannot overwrite it or take a second instance later; to hold a second instance, claim it afterwards. No wait lasts more than 24 hours.
- **Keep it honest:** the default lease lasts 2 hours (120 minutes; user, 2026-10-08). Renew it (`lease.sh renew <port> --owner <name>`) before it runs out if the work is still going, and never run past an expired lease without renewing; renewing sets a fresh TTL from that moment. Renew before a long step starts rather than partway through it. An expired lease can be taken over by another session, which is logged. Pass a shorter `--ttl-minutes` for a short check, so a forgotten lease frees itself sooner.
- **Release when done, always** (user, 2026-10-08): the last step of every run on an instance, whether it passed, failed or was abandoned, is `lease.sh release <port> --owner <name>`, after restoring anything you changed (user data, settings, bundle, fixtures, test libraries), because the next session starts from what you left. Release also when you stop for a usage limit or finish early. Confirm with `lease.sh status <port>` that it shows FREE (or the next waiter) and say in the report that you released it. Nothing releases a lease for you when a job ends: it only frees itself when its TTL runs out. A lease is not a reason to leave an instance changed.
- **Never clear someone else's lease** by editing the file or with `release --force`. `--force --reason "<why>"` is only for a dead session and only with the user's approval. If a lease looks stale, tell the user.
- **Say which instance** you used, and when you held the lease, in every report.
- A session that starts another session names the instance it wants in the brief and tells it to claim the lease under its own session name; a lease is held by the session doing the work, never inherited.
- **Prefer a free instance to waiting** (user, 2026-10-08): when the instance you want is held and another (`lease.sh free`) can run the work, claim that one instead of queueing. Before changing it, back up everything the run will change (plugin folder, XML, secret store, database, web bundle), because plugin migrations are forward-only; restore it byte for byte before releasing. Wait only when the work truly needs that instance, or the user says to.
- **Roll over the instances** (user, 2026-10-08): when you need an instance and no particular one, try them in the order `18096`, `28096`, `48096` and take the first that is free.
  1. Run `lease.sh status` (it shows holders and queues). For a held lease, check whether its owner really uses the port: look the owner up in the linked sessions (`list_sessions {"linked": true}`) and read its state. An owner that is idle or finished while still holding the lease is probably done: ask it by `SendMessage` to release, and never release it yourself or use `--force`. A running owner counts as busy.
  2. If all three are busy, wait for the sooner of the earliest lease expiry and 5 minutes, then repeat from step 1. One call does it: `lease.sh claim --any 18096,28096,48096 … --wait-minutes 5` keeps your queue place on all three and takes the first to free; if it times out, run it again. In a session that has nothing else to do, `/loop 5m` re-runs the whole check.
  3. Take a specific instance only when the work truly needs it (the same exception as above); then queue for it with `--wait-minutes N`.

## Session status in the title

Every session shows its current state as a prefix on its session title (user, 2026-10-08), so the sidebar shows at a glance who is blocked on what. Update the title whenever the state changes, including when a subagent you started changes state, since the session owns the work. Times are Sydney time, `HH:MM`.

- **Working:** `<session name>`, with no prefix.
- **Waiting for Codex** (the usage gate is shut or Codex hit its limit): `codex (<reset time>): <session name>`, e.g. `codex (12:59): Settings rows and Title Case`.
- **Waiting for a lease** (another session holds the instance): `lease (<time it expires or is expected free>): <session name>`.
- **Testing** (a live or E2E suite is running on an instance under your lease): `testing (<port>): <session name>`, e.g. `testing (28096): Settings rows and Title Case`.

The prefix is only for display. The lease `--owner` and anything else that identifies the session always use the bare session name without the prefix, so the owner never changes with the status. Peers message a session by its `local_…` id, which stays the same when the title changes.

### Session names

- Rename every new session as soon as its task is clear (usually after the first message), with `mcp__ccd_session_mgmt__set_session_title`, to a short, memorable name of two to four words that says what the session fixes or builds, for example "Scope-Ratio 4K Fix" or "Settings Rows and Title Case". Name the thing, not the activity.
- Never reuse a title another session has. Keep the name for the life of the session; rename only when its scope clearly changes. The status prefixes above go in front of it, and the name is the `<name>` part (and the lease `--owner`).
- When you start another session, give it a name of the same kind.

## Credentials

- Load `TMDB_READ_ACCESS_TOKEN` from ignored `plugin/.env` for mod E2E. Keep file permissions `0600`.
- Commit only the empty `plugin/.env.sample`; never commit the real `.env` or credentials.
- Never print credentials in commands, logs, test output or reports. Configure the TMDB test token only on port `18096`.
- Keep host settings in ignored `jellyfin-web/jellyfin-sync.env`; never publish local deployment paths in either child repo.

## Versioning

- **The plugin version stays 0.1.0.0** (user, 2026-09-27) until the user raises it by hand or says "publish / release 0.1.0.0 and move to the next version". Never bump it in a task, a fix or a release on your own; all merged work ships as 0.1.0.0 and republishing overwrites `ghcr.io/capische/jellyfin-mod:0.1.0.0` and `:latest`. Once the user releases 0.1.0.0 and moves on, version tags are never overwritten again.
- **2026-10-03 (user): "Publish 0.1.0.0 but do not go to the next version yet."** 0.1.0.0 is published (index `sha256:b9703e07c452…`, whole-review fixes, detach and keep only). The version still stays 0.1.0.0 and the next version has NOT started. Because the user has now declared it published, ask the user before overwriting `0.1.0.0` or `:latest` again; do not bump, tag or start next-version work (the manual download cleanup tool) until the user says so.

## Git commits

- Commit coherent, validated slices regularly; stage explicit files and preserve unrelated edits.
- Use `type(component,phase.task): description`, all lowercase, with no spaces around commas.
- Example: `feat(catalog,p2.r1): reconcile native library bindings`.
- Use plan task IDs. Abbreviate consecutive tasks in the same phase/task series: `feat(catalog,p1.p5-6): add entries and discovery`.
- List nonconsecutive tasks or different phases separately; prefer separate task commits for new work.
- Use an imperative description without a trailing full stop. Never add a Codex/GPT co-author.
- Every commit should stand on its own. When a later change fixes a commit that does not, amend it rather than stacking a correction; make a follow-up commit instead when amending would cost disproportionate work, and say which was chosen.
- Push when a feature is finished or a bug is fixed, using the branch layout above. Back up work before discarding it.
- Rewrite published history only with explicit authorization; verify remote tips, use explicit force-with-lease, then verify pushed commits.

## Pi deployment

- **Deploying for the user's verification is pre-approved** (user, 2026-10-09): when the user needs a build running on a mod test instance to verify it, deploy it without asking first. It covers `18096`, `28096` and `48096`, under your lease, with `jellyfin-sync` configured explicitly for that instance, a backup of what is there first, and a stated restore command. For a web-only change that means copying the build into the instance's web folder with no restart. Where the instance serves its web from the plugin's zip (28096), replacing the zip and restarting only that one container is part of the same approval. The plugin assembly and database are not covered: a plugin or migration change follows the normal gates. It never covers production `8096`, another instance's lease, or deleting data. Say what was deployed, the live build marker and the restore command in the report. This is the user's decision recorded here; if the harness's own permission check still blocks the command, do not retry it another way: report it to the user and ask them to allow or run it.
- Connect with `ssh pi`. Jellyfin runs in Docker; inspect Compose services and mounts before deployment.
- Build the plugin against its pinned versions. Copy the assembly and required runtime dependencies into the target's persistent `plugins/JellyfinMod/` directory.
- Match native dependencies to the Pi architecture. Preserve other plugins, configuration and data; do not rebuild the Jellyfin image just to install the plugin.
- Use `jellyfin-web/jellyfin-sync --help` to select the target. Explicitly configure the isolated service and paths for mod deployments; defaults may target production.
- Restart only the intended service to load the assembly; copying a DLL does not reload it. A restart interrupts playback.
- Verify startup logs, Dashboard plugin/configuration pages, authenticated `/JellyfinMod/Health` and DB persistence after deployment.
