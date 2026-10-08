#!/bin/bash
# Instance leases: one session at a time may run tests on, deploy to, restart or change a JellyfinMod
# instance. The lease records who holds it. Leases live on the Pi, so every session on every machine sees
# the same state. All changes are made under one flock, so two sessions can never both win a claim.
# Waiters queue first come, first served: a released or expired lease goes to the head of its queue.
#
# Usage:
#   lease.sh status [instance] [--json]                       (leases and their wait queues)
#   lease.sh claim   <instance> --owner NAME --task TEXT [--ttl-minutes N] [--wait-minutes N] [--note TEXT]
#   lease.sh claim   --any 18096,48096 --owner NAME --task TEXT [--wait-minutes N] ...
#                                                             (whichever listed instance frees first)
#   lease.sh renew   <instance> --owner NAME [--ttl-minutes N]
#   lease.sh release <instance> --owner NAME
#   lease.sh release <instance> --force --reason TEXT         (user-approved clean-up of a dead session)
#   lease.sh wait    <instance>|--any LIST [--owner NAME] [--task TEXT] [--timeout-minutes N]
#                                     (queues until free; with an owner, holds it for that owner 5 minutes)
#   lease.sh queue   [instance] [--json]                      (the wait queues only)
#   lease.sh leave   <instance>|--any LIST --owner NAME       (drop your queue places; their waiters stop)
#   lease.sh free                                             (instances nobody holds and nobody queues for)
#
# --owner defaults to $LEASE_OWNER. Instances: 18096 (test), 28096 (acceptance), 48096 (live Phase 5/6).
# Production 8096 is never leased because it is never touched.
# claim --wait-minutes and wait join the instance's queue; a plain claim cannot overtake anyone queued, and
# a claim that succeeds stops all of the same owner's claim waiters, for every instance.
# A waiter that dies, disconnects or times out leaves the queue; no wait lasts more than 24 h. Joins, leaves
# and drops go to history.log.
# Exit codes: 0 ok, 1 held by someone else, queued for by someone else, timed out or left; 2 usage error.
# Testing only: LEASE_ROOT=<scratch dir> with LEASE_SCRATCH_INSTANCES=99001,99002 (and LEASE_POLL_SECONDS,
# LEASE_MAX_WAIT_SECONDS).
# Rules for sessions are in the workspace CLAUDE.md, section "Instance leases".

[ $# -ge 1 ] || { sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

b64json() { python3 -c 'import sys, json, base64; print(base64.b64encode(json.dumps(sys.argv[1:]).encode()).decode())' "$@"; }
args_b64=$(b64json "$@")
env_b64=$(b64json "${LEASE_OWNER:-}" "$(hostname -s 2>/dev/null)" "${LEASE_ROOT:-}" "${LEASE_SCRATCH_INSTANCES:-}" "${LEASE_POLL_SECONDS:-}" "${LEASE_MAX_WAIT_SECONDS:-}")

# The remote side is plain `python3 -` so the Pi's fish login shell never has to parse our arguments.
{
    echo "ARGS_B64 = '$args_b64'"
    echo "ENV_B64 = '$env_b64'"
    cat <<'PY'
import base64, datetime, fcntl, json, os, re, signal, socket, sys, time, uuid, zoneinfo

ARGS = json.loads(base64.b64decode(ARGS_B64))
DEFAULT_OWNER, CLIENT_HOST, ROOT_OVERRIDE, SCRATCH, POLL_OVERRIDE, MAX_WAIT_OVERRIDE = json.loads(base64.b64decode(ENV_B64))
DEFAULT_ROOT = "/mnt/4tb/jellyfinmod-leases"
ROOT = ROOT_OVERRIDE or DEFAULT_ROOT
INSTANCES = {"18096": "test", "28096": "acceptance", "48096": "live Phase 5/6"}
if SCRATCH:
    if os.path.realpath(ROOT) == os.path.realpath(DEFAULT_ROOT):
        print("LEASE_SCRATCH_INSTANCES needs LEASE_ROOT set to a scratch directory", file=sys.stderr)
        sys.exit(2)
    for i in (i.strip() for i in SCRATCH.split(",")):
        if i and (not re.fullmatch(r"[0-9]+", i) or i in INSTANCES or i == "8096"):
            print(f"scratch instance {i!r} must be digits and not a real instance", file=sys.stderr)
            sys.exit(2)
    INSTANCES.update({i.strip(): "scratch" for i in SCRATCH.split(",") if i.strip()})
SYD = zoneinfo.ZoneInfo("Australia/Sydney")
POLL_SECONDS = float(POLL_OVERRIDE or 10)
STALE_SECONDS = 120          # a waiter whose heartbeat is older than this has left the queue
RESERVE_SECONDS = 300        # how long a finished `wait --owner` holds a free instance for its owner
MAX_WAIT_SECONDS = float(MAX_WAIT_OVERRIDE) if SCRATCH and MAX_WAIT_OVERRIDE else 86400
                             # no waiter lives longer: past it, it stops before it could rejoin or claim
KEEP_SECONDS = 2 * MAX_WAIT_SECONDS  # cancellations and stale drops outlive every waiter they concern
DEFAULT_TTL = 120
HOST = socket.gethostname()


def die(msg, code=2):
    print(msg, file=sys.stderr)
    sys.exit(code)


def parse(argv):
    cmd = argv[0]
    pos, opts, i = [], {}, 1
    flags = {"json", "force"}
    while i < len(argv):
        a = argv[i]
        if a.startswith("--"):
            k = a[2:]
            if k in flags:
                opts[k] = True
            else:
                if i + 1 >= len(argv):
                    die(f"--{k} needs a value")
                opts[k] = argv[i + 1]
                i += 1
        else:
            pos.append(a)
        i += 1
    return cmd, pos, opts


def num(opts, key, default):
    try:
        v = float(opts.get(key, default))
    except ValueError:
        die(f"--{key} must be a number")
    if v < 0:
        die(f"--{key} must not be negative")
    return v


def check_instance(inst):
    if inst == "8096":
        die("8096 is production and is never leased or touched by mod work")
    if inst not in INSTANCES:
        die(f"unknown instance {inst}; known: " + ", ".join(INSTANCES))
    return inst


def instance_arg(pos):
    if len(pos) != 1:
        die("give exactly one instance: " + ", ".join(INSTANCES))
    return check_instance(pos[0])


def instances_arg(pos, opts):
    """One instance, or several with --any in the order given."""
    if "any" not in opts:
        return [instance_arg(pos)]
    if pos:
        die("give either one instance or --any LIST, not both")
    insts = [i.strip() for i in opts["any"].split(",") if i.strip()]
    if not insts or len(set(insts)) != len(insts):
        die("--any needs a comma-separated list of distinct instances, e.g. --any 18096,48096")
    return [check_instance(i) for i in insts]


def in_root(name):
    """A file directly inside ROOT; anything resolving elsewhere is refused."""
    p = os.path.join(ROOT, name)
    if os.path.dirname(os.path.realpath(p)) != os.path.realpath(ROOT):
        die(f"refusing a lease file outside {ROOT}: {p}")
    return p


def path(inst):
    return in_root(inst + ".json")


def qpath(inst):
    return in_root(inst + ".queue.json")


def read(inst):
    try:
        with open(path(inst)) as f:
            return json.load(f)
    except FileNotFoundError:
        return None


def write_json(p, data):
    tmp = p + f".tmp{os.getpid()}"
    with open(tmp, "w") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    os.replace(tmp, p)


def write(inst, lease):
    write_json(path(inst), lease)


def read_q(inst):
    try:
        with open(qpath(inst)) as f:
            return json.load(f)
    except FileNotFoundError:
        return []


def write_q(inst, q):
    if q:
        write_json(qpath(inst), q)
    elif os.path.exists(qpath(inst)):
        os.remove(qpath(inst))


def read_map(name):
    """cancelled.json or stale.json: "<instance>:<waiter id>" -> record with at_epoch."""
    try:
        with open(in_root(name)) as f:
            return json.load(f)
    except FileNotFoundError:
        return {}


def update_map(name, now, add=None, remove=()):
    """Add and remove records, dropping those older than KEEP_SECONDS. Call under the lock."""
    m = {k: v for k, v in read_map(name).items() if now - v["at_epoch"] < KEEP_SECONDS and k not in remove}
    m.update(add or {})
    if m:
        write_json(in_root(name), m)
    elif os.path.exists(in_root(name)):
        os.remove(in_root(name))


def cancel(inst, wid, reason, now):
    """Remember that waiter wid's place on inst was removed on purpose, so its running process stops instead of
    rejoining the queue as it does after a stale drop. Call under the lock."""
    update_map("cancelled.json", now, {f"{inst}:{wid}": {"at_epoch": now, "reason": reason}})


def log(event, inst, **kw):
    line = {"at": iso(time.time()), "event": event, "instance": inst, **kw}
    with open(in_root("history.log"), "a") as f:
        f.write(json.dumps(line) + "\n")


def iso(ts):
    return datetime.datetime.fromtimestamp(ts, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def syd(ts):
    return datetime.datetime.fromtimestamp(ts, SYD).strftime("%a %d %b %H:%M")


def hm(ts):
    return datetime.datetime.fromtimestamp(ts, SYD).strftime("%H:%M")


def expired(lease, now=None):
    return lease is not None and lease["expires_epoch"] <= (now or time.time())


def proc_start(pid):
    try:
        with open(f"/proc/{pid}/stat") as f:
            return f.read().rsplit(")", 1)[1].split()[19]
    except (OSError, IndexError):
        return None


def drop_reason(e, now):
    if e.get("reserved_until"):
        return "reservation lapsed" if now > e["reserved_until"] else None
    if e.get("host") == HOST and proc_start(e["pid"]) != e.get("pid_start"):
        return "waiter process gone"
    if now - e["heartbeat_epoch"] > STALE_SECONDS:
        return f"no heartbeat for {int(now - e['heartbeat_epoch'])} s"
    return None


def load_q(inst, now):
    """The queue with dead waiters and lapsed reservations dropped. Call under the lock."""
    q, kept = read_q(inst), []
    for e in q:
        reason = drop_reason(e, now)
        if reason:
            log("queue-drop", inst, owner=e["owner"], waiter=e["id"], reason=reason,
                waited_minutes=round((now - e["joined_epoch"]) / 60, 1))
            if reason.startswith("no heartbeat"):   # a stalled waiter may resume: keep who it was for leave/claim
                update_map("stale.json", now, {f"{inst}:{e['id']}": {"at_epoch": now, "owner": e["owner"],
                                                                      "kind": e["kind"]}})
        else:
            kept.append(e)
    if len(kept) != len(q):
        write_q(inst, kept)
    return kept


def mins(seconds):
    m = int(seconds // 60)
    return f"{m} min" if m < 120 else f"{m // 60} h {m % 60:02d} min"


def describe_q(q, now, inst):
    lines = []
    for n, e in enumerate(q, 1):
        what = (f"holds it until {hm(e['reserved_until'])} Sydney (wait finished)" if e.get("reserved_until")
                else f"{'claim' if e['kind'] == 'claim' else 'wait'}, waiting {mins(now - e['joined_epoch'])}")
        also = f", also queued for {', '.join(i for i in e['any'] if i != inst)}" if len(e.get("any", [])) > 1 else ""
        where = f" from {e['client_host']}" if e.get("client_host") else ""
        lines.append(f"      {n}. {e['owner']} ({what}{also}{where})" + (f"\n         task: {e['task']}" if e.get("task") else ""))
    return lines


def describe(inst, lease, q=None, now=None):
    now = now or time.time()
    q = q or []
    label = f"{inst} ({INSTANCES[inst]})"
    if lease is None:
        if not q:
            s = f"{label}: FREE"
        elif q[0].get("reserved_until"):
            s = f"{label}: FREE, held for {q[0]['owner']} until {hm(q[0]['reserved_until'])} Sydney"
        else:
            s = f"{label}: FREE, next in line {q[0]['owner']}"
    else:
        left = int((lease["expires_epoch"] - now) / 60)
        state = (f"EXPIRED {-left} min ago, " + (f"goes to {q[0]['owner']} next" if q else "free to take over")
                 if left <= 0 else f"held, {left} min left")
        s = (f"{label}: {state}\n    owner {lease['owner']}  since {syd(lease['claimed_epoch'])} Sydney"
             f"  expires {syd(lease['expires_epoch'])} Sydney\n    task: {lease['task']}")
        if lease.get("note"):
            s += f"\n    note: {lease['note']}"
    if q:
        s += f"\n    queue ({len(q)}):\n" + "\n".join(describe_q(q, now, inst))
    return s


class Lock:
    def __enter__(self):
        os.makedirs(ROOT, exist_ok=True)
        self.f = open(in_root(".lock"), "w")
        fcntl.flock(self.f, fcntl.LOCK_EX)

    def __exit__(self, *a):
        fcntl.flock(self.f, fcntl.LOCK_UN)
        self.f.close()


def owner_of(opts):
    o = (opts.get("owner") or DEFAULT_OWNER).strip()
    if not o:
        die("give --owner NAME (your session name) or set LEASE_OWNER")
    return o


def ssh_session_pid():
    """The sshd process serving this run, so a waiter notices when its client goes away."""
    pid = os.getppid()
    while pid > 1:
        try:
            with open(f"/proc/{pid}/stat") as f:
                stat = f.read()
        except OSError:
            return None
        if stat.split("(", 1)[1].rsplit(")", 1)[0].startswith("sshd"):
            return pid, proc_start(pid)
        pid = int(stat.rsplit(")", 1)[1].split()[1])
    return None


def cmd_status(pos, opts):
    insts = [instance_arg(pos)] if pos else list(INSTANCES)
    with Lock():
        now = time.time()
        leases = {i: read(i) for i in insts}
        queues = {i: load_q(i, now) for i in insts}
    if opts.get("json"):
        out = {i: (None if l is None else {**l, "expired": expired(l, now), "queue": queues[i]}) for i, l in leases.items()}
        print(json.dumps(out, indent=2))
    else:
        for i in insts:
            print(describe(i, leases[i], queues[i], now))
    return 0


def cmd_queue(pos, opts):
    insts = [instance_arg(pos)] if pos else list(INSTANCES)
    with Lock():
        now = time.time()
        queues = {i: load_q(i, now) for i in insts}
    if opts.get("json"):
        print(json.dumps(queues, indent=2))
    else:
        for i in insts:
            q = queues[i]
            print(f"{i} ({INSTANCES[i]}): " + ("nobody queued" if not q else f"{len(q)} queued"))
            if q:
                print("\n".join(describe_q(q, now, i)))
    return 0


def cmd_free(pos, opts):
    with Lock():
        now = time.time()
        unheld = [i for i in INSTANCES if read(i) is None or expired(read(i), now)]
        queued = {i: load_q(i, now) for i in unheld}
    free = [i for i in unheld if not queued[i]]
    print(" ".join(free) if free else "none")
    for i in unheld:
        if queued[i]:
            print(f"{i} is not held but {queued[i][0]['owner']} is first in its queue", file=sys.stderr)
    return 0


def take(inst, cur, owner, task, opts, ttl, now, q, me=None):
    """Write the lease for owner, drop owner's reservations from q and cancel owner's other claim waiters for inst
    (me is the waiter that is taking it, if any). Call under the lock."""
    note = ""
    if cur is not None and cur["owner"] != owner:
        note = f" (took over the expired lease of {cur['owner']})"
        log("takeover", inst, previous_owner=cur["owner"], by=owner)
    lease = {
        "instance": inst, "label": INSTANCES[inst], "owner": owner, "task": task,
        "note": opts.get("note", ""), "client_host": CLIENT_HOST,
        "claimed_at": iso(now), "claimed_epoch": cur["claimed_epoch"] if cur and cur["owner"] == owner else now,
        "renewed_at": iso(now), "ttl_minutes": ttl,
        "expires_at": iso(now + ttl * 60), "expires_epoch": now + ttl * 60,
    }
    write(inst, lease)
    log("claim", inst, owner=owner, task=task, ttl_minutes=ttl)
    rest = []
    for e in q:
        if e["owner"] == owner and e.get("reserved_until"):
            log("queue-leave", inst, owner=owner, waiter=e["id"], reason="claimed its reservation")
        else:
            rest.append(e)
    write_q(inst, rest)
    cancel_claim_waiters(inst, owner, now, me)
    print(f"claimed {inst} for {owner} until {syd(lease['expires_epoch'])} Sydney{note}", flush=True)


def cancel_claim_waiters(inst, owner, now, me):
    """Owner now holds inst, so owner's other `claim --wait-minutes` waiters would only claim it again or take
    another instance. Remove all of them from every queue, including places dropped while they were stalled, and
    make them stop. A second instance is held only through a claim made afterwards. me is the waiter now taking
    inst, which cleans up its own places."""
    reason = f"owner claimed {inst}"
    stale = read_map("stale.json")
    for other in INSTANCES:
        q = read_q(other)
        gone = [e for e in q if e["owner"] == owner and e["kind"] == "claim" and e["id"] != me]
        if gone:
            write_q(other, [e for e in q if e not in gone])
        dropped = [k for k, v in stale.items() if k.split(":")[0] == other and v["owner"] == owner
                   and v["kind"] == "claim" and k.split(":", 1)[1] != me]
        for wid in [e["id"] for e in gone] + [k.split(":", 1)[1] for k in dropped]:
            log("queue-leave", other, owner=owner, waiter=wid, reason=reason)
            cancel(other, wid, reason, now)
        if dropped:
            update_map("stale.json", now, remove=dropped)


def may_take(cur, q, owner, now, me=None):
    """Owner may take the lease: it is theirs and unexpired, or it is free (or expired) and they are first in line."""
    if cur is not None and not expired(cur, now):
        return cur["owner"] == owner
    return not q or (q[0]["id"] == me if me else q[0]["owner"] == owner)


def new_entry(owner, task, kind, insts, now):
    return {"id": uuid.uuid4().hex[:12], "owner": owner, "task": task, "kind": kind, "any": insts,
            "client_host": CLIENT_HOST, "host": HOST, "pid": os.getpid(), "pid_start": proc_start(os.getpid()),
            "joined_at": iso(now), "joined_epoch": now, "heartbeat_epoch": now}


def leave_all(entry, insts, reason, event="queue-leave"):
    with Lock():
        now = time.time()
        for inst in insts:
            q = read_q(inst)
            if any(e["id"] == entry["id"] for e in q):
                write_q(inst, [e for e in q if e["id"] != entry["id"]])
                log(event, inst, owner=entry["owner"], waiter=entry["id"], reason=reason,
                    waited_minutes=round((now - entry["joined_epoch"]) / 60, 1))


def join(insts, owner, task, kind, now):
    """Put a new waiter at the back of every queue in insts and return it. Call under the lock."""
    def stop(signum, frame):
        raise SystemExit(128 + signum)
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGHUP, stop)
    entry = new_entry(owner, task, kind, insts, now)
    for inst in insts:
        q = load_q(inst, now)
        q.append(entry)
        write_q(inst, q)
        log("queue-join", inst, owner=owner, waiter=entry["id"], kind=kind, task=task, position=len(q),
            client_host=CLIENT_HOST, also=[i for i in insts if i != inst])
    return entry


def queue_wait(entry, deadline, on_free):
    """Wait as the joined waiter entry to be first in line where the lease is free, then call
    on_free(inst, cur, q, entry, now) under the lock. Leaves every queue on success, timeout, signal or a lost
    client. Stops without claiming once every place was removed on purpose (leave, or the owner claimed), or
    once the deadline or the MAX_WAIT_SECONDS lifetime has passed, checked before any rejoin or claim.
    Returns on_free's exit code, or 1 on timeout or removal."""
    insts, owner = entry["any"], entry["owner"]
    session = ssh_session_pid()
    done, reason, last, active = False, "stopped", None, list(insts)
    # A positive timeout is enforced before every rejoin, claim or reservation, including the first poll after a
    # stall. A zero timeout (deadline equal to the join time) still gets one availability check.
    positive = deadline is not None and deadline > entry["joined_epoch"]
    try:
        while True:
            if session and proc_start(session[0]) != session[1]:
                reason = "client disconnected"
                return 1
            with Lock():
                now = time.time()
                if positive and now >= deadline:
                    reason = "timed out"
                    print("timed out waiting for " + " or ".join(insts), file=sys.stderr)
                    return 1
                if now - entry["joined_epoch"] >= MAX_WAIT_SECONDS:
                    reason = f"wait limit of {mins(MAX_WAIT_SECONDS)} reached"
                    print(f"gave up waiting: no wait lasts longer than {mins(MAX_WAIT_SECONDS)}", file=sys.stderr)
                    return 1
                cancelled = read_map("cancelled.json")
                positions = {}
                for inst in list(active):
                    why = cancelled.get(f"{inst}:{entry['id']}")
                    if why:                     # removed on purpose (leave, or the owner claimed): do not rejoin
                        active.remove(inst)
                        print(f"your place in the {inst} queue was removed ({why['reason']})", file=sys.stderr,
                              flush=True)
                        continue
                    q = load_q(inst, now)
                    mine = next((e for e in q if e["id"] == entry["id"]), None)
                    if mine is None:            # dropped while we were stalled: rejoin at the back
                        mine = dict(entry)
                        q.append(mine)
                        log("queue-rejoin", inst, owner=owner, waiter=entry["id"], position=len(q))
                        if f"{inst}:{entry['id']}" in read_map("stale.json"):
                            update_map("stale.json", now, remove=[f"{inst}:{entry['id']}"])
                    mine["heartbeat_epoch"] = now
                    write_q(inst, q)
                    cur = read(inst)
                    if may_take(cur, q, owner, now, me=entry["id"]):
                        rc = on_free(inst, cur, [e for e in q if e["id"] != entry["id"]], mine, now)
                        done = True
                        for other in active:
                            oq = read_q(other)
                            if other != inst and any(e["id"] == entry["id"] for e in oq):
                                write_q(other, [e for e in oq if e["id"] != entry["id"]])
                                log("queue-leave", other, owner=owner, waiter=entry["id"], reason=f"got {inst}")
                        return rc
                    positions[inst] = (q.index(mine) + 1, cur, q)
                if not active:
                    reason = "cancelled"
                    print("stopped waiting without claiming: no queue place left", file=sys.stderr)
                    return 1
            state = {i: p[0] for i, p in positions.items()}
            if state != last:
                for i, (n, cur, q) in positions.items():
                    ahead = ", ".join(e["owner"] for e in q[:n - 1])
                    holder = f"held by {cur['owner']} until {hm(cur['expires_epoch'])} Sydney" if cur else "free"
                    print(f"{i} is {holder}; you are #{n} in its queue" + (f" behind {ahead}" if ahead else ""),
                          flush=True)
                last = state
            if deadline is not None and time.time() >= deadline:
                reason = "timed out"
                print("timed out waiting for " + " or ".join(insts), file=sys.stderr)
                return 1
            time.sleep(POLL_SECONDS)
    finally:
        if not done:
            try:
                leave_all(entry, insts, reason, "queue-timeout" if reason == "timed out" else "queue-leave")
            except Exception:
                pass


def cmd_claim(pos, opts):
    insts = instances_arg(pos, opts)
    owner = owner_of(opts)
    task = (opts.get("task") or "").strip()
    if not task:
        die("give --task TEXT saying what you will do on the instance")
    ttl = num(opts, "ttl-minutes", DEFAULT_TTL)
    wait = num(opts, "wait-minutes", 0)
    refused = []
    with Lock():
        now = time.time()
        for inst in insts:
            cur, q = read(inst), load_q(inst, now)
            if may_take(cur, q, owner, now):
                take(inst, cur, owner, task, opts, ttl, now, q)
                return 0
            if cur is None or expired(cur, now):
                log("claim-refused", inst, owner=owner, reason=f"{q[0]['owner']} is first in the queue")
            refused.append(describe(inst, cur, q, now))
        if wait > 0:    # join under the same lock, so a direct claim by this owner meanwhile finds and stops it
            entry = join(insts, owner, task, "claim", now)
    if wait <= 0:
        why = "held or queued for by someone else"
        print(f"NOT claimed, {' or '.join(insts)} {'is' if len(insts) == 1 else 'are all'} {why}:\n"
              + "\n".join(refused), file=sys.stderr)
        return 1
    print(("Waiting in line:\n" if len(insts) == 1 else "Waiting in line for whichever frees first:\n")
          + "\n".join(refused), flush=True)

    def on_free(inst, cur, q, mine, now):
        take(inst, cur, owner, task, opts, ttl, now, q, me=mine["id"])
        log("queue-leave", inst, owner=owner, waiter=mine["id"], reason="claimed",
            waited_minutes=round((now - mine["joined_epoch"]) / 60, 1))
        return 0
    return queue_wait(entry, entry["joined_epoch"] + wait * 60, on_free)


def cmd_renew(pos, opts):
    inst = instance_arg(pos)
    owner = owner_of(opts)
    ttl = num(opts, "ttl-minutes", DEFAULT_TTL)
    with Lock():
        cur = read(inst)
        if cur is None or cur["owner"] != owner:
            print(f"NOT renewed: {inst} is not held by {owner}\n{describe(inst, cur)}", file=sys.stderr)
            return 1
        now = time.time()
        cur.update(renewed_at=iso(now), ttl_minutes=ttl, expires_at=iso(now + ttl * 60), expires_epoch=now + ttl * 60)
        write(inst, cur)
        log("renew", inst, owner=owner, ttl_minutes=ttl)
    print(f"renewed {inst} for {owner} until {syd(now + ttl * 60)} Sydney")
    return 0


def cmd_release(pos, opts):
    inst = instance_arg(pos)
    with Lock():
        cur = read(inst)
        if cur is None:
            print(f"{inst} was already free")
            return 0
        if opts.get("force"):
            reason = (opts.get("reason") or "").strip()
            if not reason:
                die("--force needs --reason TEXT, and is only for a dead session the user has approved clearing")
            log("force-release", inst, previous_owner=cur["owner"], reason=reason)
        else:
            owner = owner_of(opts)
            if cur["owner"] != owner:
                print(f"NOT released: {inst} is held by {cur['owner']}, not {owner}", file=sys.stderr)
                return 1
            log("release", inst, owner=owner)
        os.remove(path(inst))
        q = load_q(inst, time.time())
    print(f"released {inst}" + (f"; {q[0]['owner']} is next in its queue" if q else ""))
    return 0


def cmd_wait(pos, opts):
    insts = instances_arg(pos, opts)
    owner = (opts.get("owner") or DEFAULT_OWNER).strip()
    task = (opts.get("task") or "").strip()
    timeout = num(opts, "timeout-minutes", 0) * 60 if "timeout-minutes" in opts else None

    def on_free(inst, cur, q, mine, now):
        prev = " (the previous lease expired)" if cur else ""
        if cur is not None and cur["owner"] == mine["owner"] and not expired(cur, now):
            write_q(inst, q)
            log("queue-leave", inst, owner=mine["owner"], waiter=mine["id"], reason="owner already holds it")
            print(f"{inst} is already held by {mine['owner']}")
        elif owner:
            mine.update(reserved_until=now + RESERVE_SECONDS)
            write_q(inst, [mine] + q)
            log("queue-reserve", inst, owner=owner, waiter=mine["id"], until=iso(now + RESERVE_SECONDS))
            print(f"{inst} is free{prev}; held for {owner} until {hm(now + RESERVE_SECONDS)} Sydney, claim it now")
        else:
            write_q(inst, q)
            log("queue-leave", inst, owner=mine["owner"], waiter=mine["id"], reason="free (unnamed wait)")
            print(f"{inst} is free{prev}" + (f"; {q[0]['owner']} is next in its queue" if q else ""))
        return 0
    with Lock():
        entry = join(insts, owner or f"(unnamed wait from {CLIENT_HOST or 'unknown host'})", task, "wait",
                     time.time())
    return queue_wait(entry, None if timeout is None else entry["joined_epoch"] + timeout, on_free)


def cmd_leave(pos, opts):
    insts = instances_arg(pos, opts) if pos or "any" in opts else list(INSTANCES)
    owner = owner_of(opts)
    left = 0
    with Lock():
        now = time.time()
        stale = read_map("stale.json")
        for inst in insts:
            q = read_q(inst)    # raw, so a stalled waiter is cancelled before a stale drop could forget it
            mine = [e for e in q if e["owner"] == owner]
            if mine:
                write_q(inst, [e for e in q if e["owner"] != owner])
            dropped = [k for k, v in stale.items() if k.split(":")[0] == inst and v["owner"] == owner]
            for wid in [e["id"] for e in mine] + [k.split(":", 1)[1] for k in dropped]:
                log("queue-leave", inst, owner=owner, waiter=wid, reason="left by owner")
                cancel(inst, wid, "left by owner", now)
            if dropped:
                update_map("stale.json", now, remove=dropped)
            left += len(mine) + len(dropped)
    print(f"removed {left} queue place(s) of {owner}")
    return 0


COMMANDS = {"status": cmd_status, "queue": cmd_queue, "free": cmd_free, "claim": cmd_claim, "renew": cmd_renew,
            "release": cmd_release, "wait": cmd_wait, "leave": cmd_leave}
cmd, pos, opts = parse(ARGS)
if cmd not in COMMANDS:
    die("unknown command " + cmd + "; use: " + ", ".join(COMMANDS))
try:
    sys.exit(COMMANDS[cmd](pos, opts))
except KeyboardInterrupt:
    sys.exit(130)
PY
} | ssh -o ConnectTimeout=10 -o ServerAliveInterval=30 -o ServerAliveCountMax=6 pi python3 -
