# Runbook: operating the k8s deployment

This file holds the operator procedures that are specific to the Kubernetes
deployment (`k8s/`). The local compose stack has its own runbook,
[`RUNBOOK-local-dev.md`](RUNBOOK-local-dev.md).

Sections:

- [Studio hub token](#studio-hub-token): mint, check, rotate, revoke, read the cluster CA, the SSH forward.
- [Studio hub: node access](#studio-hub-node-access): enable and disable the opt-in node ClusterRole.
- [Studio hub: checking the grant](#studio-hub-checking-the-grant)
- [Studio hub: the live check](#studio-hub-the-live-check): what an operator runs, once, on the local cluster, before relying on any claim here.
- [Deploying the hub to an existing cluster](#deploying-the-hub-to-an-existing-cluster)

What the hub is allowed to do, rule by rule, and why, is in
[`../k8s/README.md`](../k8s/README.md), "The Studio hub identity". Read that first
if you are deciding whether to hand someone a token.

Conventions used below. Every command is a template: `<...>` is yours to fill in.

| Placeholder | Meaning |
|---|---|
| `<admin-context>` | a kubectl context that has cluster-admin (or at least the rights the step names). Dev: `k3d-rpg-dev`. |
| `<private-dir>` | a directory **outside the repository**, mode 700, readable only by you |
| `<node-host>` / `<ssh-user>` | the node's SSH name and login. Never write a real one into the repository. |
| `<local-port>` | a free port on the machine that runs the hub |

Nothing in the repository holds a token, a kubeconfig with a token, or a real host.
Keep it that way: the `.gitignore`d `kubeconfig.local` convention of `K3S.md` does
not extend to the hub's token. Store it only in the place the hub reads its
credentials from.

## Studio hub token

The Studio hub is an operator web tool **outside** the cluster. Its identity is the
ServiceAccount `studio-hub` in namespace `rpg-k8s-hub` (applied by
`k8s/dev-up.sh`, files `k8s/hub/00-namespace.yaml`, `10-serviceaccount.yaml`,
`15-admission-policy.yaml`, `20-rbac.yaml`). The ServiceAccount has `automountServiceAccountToken: false` and
nothing runs in its namespace, so the **only** credential that can act as the hub is
a token an operator mints by hand with the procedure below.

A token is an **operator credential**. Exactly what it can do, with and without the
admission policy, is in `../k8s/README.md` ("What the token can do"). In short:

- **With the policy `studio-hub-restart-only` in force** (the deployed state), `patch
  deployments` is a rollout restart and nothing else: an image, env, command,
  `privileged` or `hostPath` change, a `replicas` change and a label change are refused
  by the API server. The token can still **delete pods** (the databases' too), **delete
  game servers**, **scale fleets** (to zero), **edit fleet autoscalers**, **create
  allocations**, and read logs, events and workload specs.
- **Without the policy** (deleted, or never applied) the same `patch deployments` can
  rewrite the pod template, which runs arbitrary code as root on the node: **the token
  is then node-root-equivalent**. The policy needs Kubernetes 1.30 or newer;
  `cluster.hub_rbac` FAILs when it or its binding is missing or differs from the
  repository's. Do not hand out a token on a cluster where that check does not pass.

Mint it for a person or a machine you would trust with deleting pods and scaling
fleets, short-lived and revocable.

### The two ways to get a token, and the one this runbook uses

| | Bounded token (`kubectl create token`, bound to a Secret) | Secret-based token (`type: kubernetes.io/service-account-token`) |
|---|---|---|
| Lifetime | Expires. You choose the duration. | Never expires. |
| Where the value lives | Only where you put it (the hub's credential store). Kubernetes does not store it. | In etcd, as `data.token` of a Secret in the cluster. Anyone who can `get secrets` in that namespace, and anyone with etcd or backup access, can read it. |
| Revoke | Delete the binding Secret (all tokens bound to it die at once), or delete the ServiceAccount. | Delete that Secret or the ServiceAccount. |
| Cost to the hub | It must be re-minted and re-installed before expiry. | None. |

This runbook uses the **bounded token bound to an empty Secret**: the token is
time-limited and also revocable before its expiry. The Secret-based token is
rejected because a never-expiring value that sits in etcd and in every backup is
worse than the cost of rotating on a schedule. If the hub cannot be made to take a
rotated token, that is a reason to fix the hub, not to switch to the long-lived
kind.

### Mint

The binding Secret is created by command. It is **not** a manifest in the
repository and holds no value (an Opaque Secret with no data). It lives in
`rpg-k8s-hub`, next to the ServiceAccount.

Run these with `<admin-context>`. The first line is needed once; it can be left in
place for later mints.

```bash
# 1. The binding Secret: empty, Opaque, no data. Skip if it already exists.
kubectl --context <admin-context> -n rpg-k8s-hub create secret generic studio-hub-token-binding

# 2. Mint a token bound to it. 720h is 30 days: the recommended maximum.
umask 077
kubectl --context <admin-context> create token studio-hub -n rpg-k8s-hub \
  --duration=720h \
  --bound-object-kind=Secret --bound-object-name=studio-hub-token-binding \
  > <private-dir>/studio-hub.token
```

Notes:

- The output goes straight to a file in a private directory. Do not paste it into
  a terminal log, a ticket, a chat, or a shell variable that you `echo`. If you must
  hold it in a variable, never print it.
- Pick a duration you are willing to rotate on. The API server may **cap** the
  duration (`--service-account-max-token-expiration`, an API server flag). A capped
  token is not an error: it is simply shorter than you asked for. So always read
  back the expiry (next section) instead of trusting the `--duration` you typed.
- Rights are attached to the ServiceAccount, not to the token. Enabling or
  disabling node access later changes what an already minted token can do at once;
  nothing needs to be re-minted.
- The Secret `studio-hub-token-binding` is the revoke handle. If it does not exist
  when you mint, the API server refuses the request (it cannot bind to an object
  that is not there). That is Kubernetes' documented behaviour for bound tokens; it
  was not exercised against a cluster by this change.

### Check a token's expiry

A token is a JWT; the expiry is the `exp` claim of its payload. Print **only** `exp`,
never the payload or the token:

```bash
python3 -I -c "import sys,json,base64,datetime; p=open(sys.argv[1]).read().strip().split('.')[1]; e=json.loads(base64.urlsafe_b64decode(p+'='*(-len(p)%4)))['exp']; print(datetime.datetime.fromtimestamp(e, datetime.timezone.utc).isoformat())" <private-dir>/studio-hub.token
```

Compare it with now plus the duration you asked for. A date earlier than that means
the API server capped it. Write the expiry date down where the person who rotates
the token will see it (a calendar entry, not the repository). Nothing in the
deployment reminds you: `verify`'s `cluster.hub_rbac` does not look at the token
at all.

### Rotate

Rotate before the expiry, not after. Two tokens bound to the same Secret are both
valid at once, so the hub can be switched over with no gap.

1. Mint a new token (step 2 of "Mint") into a **new** file.
2. Install it where the hub reads its credentials and confirm the hub works
   ("Studio hub: checking the grant", step 3).
3. Remove the old token file from wherever it was stored.
4. To make sure the old token is dead **before** its expiry (for example because it
   was copied somewhere it should not have been), revoke as below. Deleting and
   recreating the binding Secret kills every token minted for it, the new one
   included, so do this and then mint again.

### Revoke

Pick the narrowest step that fits. Kubernetes' bound-token behaviour is what is
relied on here; it was not run against a cluster by this change.

| Situation | Command | Effect |
|---|---|---|
| Kill all outstanding tokens, keep the identity | `kubectl --context <admin-context> -n rpg-k8s-hub delete secret studio-hub-token-binding`, then recreate it (Mint, step 1) and mint a new token | Every token bound to the deleted Secret stops authenticating. A recreated Secret has a new UID, so the old tokens stay dead. |
| Emergency: kill everything that ever acted as the hub | `kubectl --context <admin-context> -n rpg-k8s-hub delete serviceaccount studio-hub`, then `backend/deploy/k8s/dev-up.sh` (it re-applies the namespace, ServiceAccount and Roles) | Every token for the old ServiceAccount stops authenticating, bound or not. The re-created account has a new UID. The Roles stay in place and name the account, so the new account has the same rights; re-mint afterwards. |
| Take the hub's rights away from one namespace | `kubectl --context <admin-context> -n <ns> delete rolebinding studio-hub` | The hub has no access there. `verify`'s `cluster.hub_rbac` FAILs until it is restored, because the deployment expects the pair (see `../k8s/verify/README.md`). Use it as an emergency measure, then restore the pair. |
| Take away node access only | "Studio hub: node access", disable | See below. |

If the ServiceAccount is deleted, `verify` FAILs `cluster.hub_rbac` ("ServiceAccount
is absent") until `dev-up.sh` has re-applied it. That is the expected signal, not a
second fault.

### Read the cluster CA

The hub must verify the API server's certificate, so it needs the cluster CA
certificate. It is public, not secret. Two sources, same content:

```bash
# From the admin kubeconfig (the cluster's entry; --raw keeps the embedded data, --minify limits it to one context)
kubectl config view --raw --minify --context <admin-context> \
  -o jsonpath='{.clusters[0].cluster.certificate-authority-data}' | base64 -d > <private-dir>/cluster-ca.crt

# Or from the ConfigMap Kubernetes publishes in every namespace
kubectl --context <admin-context> -n rpg-k8s-hub get configmap kube-root-ca.crt \
  -o jsonpath='{.data.ca\.crt}' > <private-dir>/cluster-ca.crt
```

If the admin kubeconfig has `certificate-authority` (a file path) instead of
`certificate-authority-data`, copy that file. Do not ever use the hub with
verification turned off (`insecure-skip-tls-verify`): a token sent to an
unverified endpoint is a token given away. `K3S.md` ("Why the gateway joins k3d's
own network") makes the same rule for the gateway.

### How the hub reaches the API: an SSH forward

The hub is outside the cluster and the API server port is not opened to the
internet by this repository. `VPS-SETUP.md` describes a ufw that denies incoming
traffic and opens only SSH, the gateway's TCP port and the game server's UDP port;
nothing there opens an API port. The hub reaches the API through an SSH forward to
the node's loopback address. The same pattern is already used in `VPS-SETUP.md` for
Grafana (`ssh -L 3000:127.0.0.1:3000 rpg@<vps>`).

```bash
ssh -N -L <local-port>:127.0.0.1:6443 <ssh-user>@<node-host>
```

and the hub is configured with:

| Setting | Value |
|---|---|
| API server URL | `https://127.0.0.1:<local-port>` |
| CA | `<private-dir>/cluster-ca.crt` |
| Token | the file or value from the Mint step, in the hub's own credential store |
| Namespaces | `rpg-k8s-realtime`, `rpg-k8s-data` and one `rpg-k8s-realtime-<game>` per non-default game (the hub cannot discover them, see `../k8s/README.md`) |

**The certificate must be valid for the address the hub dials.** The hub dials
`127.0.0.1`, so the API server's serving certificate must list `127.0.0.1` (an IP
SAN). Dialling by any other name or address needs that name in the certificate,
which on k3s means adding it to `tls-san` in the k3s configuration and restarting
k3s. **That is an operator action on a real host and this change does not make
it.** Do not work around a name mismatch by turning verification off.

What is verified, and what is not, about those statements:

| Statement | Status |
|---|---|
| On k3d, the cluster certificate lists `DNS:localhost` and `IP:127.0.0.1`, and k3d publishes the API on a host port of `127.0.0.1` (the example in `K3S.md` shows `https://127.0.0.1:6550`; read yours from `kubectl config view`) | Verified against `K3S.md` ("Why the gateway joins k3d's own network", the SAN list read off a live listener, and the kubeconfig example). On k3d the hub dials that published host port directly, with no SSH forward. |
| On a k3s VPS, the kubeconfig k3s writes (`/etc/rancher/k3s/k3s.yaml`) points `server:` at `127.0.0.1` | Supported by `K3S.md` ("CD wiring": "with `server:` rewritten from `127.0.0.1` to the VPS address"). |
| On a k3s VPS, the API port is `6443` and the serving certificate carries `127.0.0.1` and `localhost` | **Not stated in `K3S.md` or `VPS-SETUP.md`** (`VPS-SETUP.md` does not install k3s at all). It is k3s's upstream default and is why the template above uses `6443`. Unverified in this repository: confirm on the node with `openssl s_client -connect 127.0.0.1:6443 </dev/null 2>/dev/null \| openssl x509 -noout -ext subjectAltName` before relying on it. |
| `tls-san` is how another name is added | Supported by `K3S.md` for k3d (`--k3s-arg '--tls-san=...'`); the same k3s flag is the k3s configuration key. The change itself, and the k3s restart it needs, were not made or tested here. |

## Studio hub: node access

Without this, the hub shows its node features (list nodes, cordon, drain) as **not
granted**. It is opt-in because nodes are cluster-scoped: there is no way to
narrow the grant to `rpg-k8s-*`. `k8s/dev-up.sh` never applies it, and it sits in
`k8s/hub/optional/` so that no glob of `k8s/hub/*.yaml` can pick it up.

What you are enabling (the file is `k8s/hub/optional/node-access.yaml`: ClusterRole
`studio-hub-nodes` and ClusterRoleBinding `studio-hub-nodes`, bound to the same
ServiceAccount):

| apiGroup | resource | verbs |
|---|---|---|
| core | `nodes` | `get`, `list`, `watch`, `patch` |
| core | `pods` | `list` |
| core | `pods/eviction` | `create` |

Read the costs before you apply it (full text in `../k8s/README.md`):

- `pods list` is **cluster-wide**: the hub can list the pods of every namespace,
  `kube-system` and other stacks' namespaces included. A pod shows its images,
  commands and **literal** env values.
- `pods/eviction create` is **cluster-wide**: the hub can evict any pod in the
  cluster (PodDisruptionBudgets are respected).
- `patch nodes` covers labels, annotations and taints, not only
  `spec.unschedulable` (the cordon).

Decision: the node ClusterRole grants **no owner reads** (no `apps/daemonsets` get, no
other owner lookup). The hub's drain is therefore its own eviction loop over the pods
listed by node (`pods list`, then `pods/eviction create` per pod). A kubectl-style
drain that looks up each pod's owner is **not supported** with this identity and
answers Forbidden for DaemonSet-owned pods; run `kubectl drain` with your admin
context instead. No rule was added to the file for this.

Enable:

```bash
kubectl --context <admin-context> apply -f backend/deploy/k8s/hub/optional/node-access.yaml
```

Disable:

```bash
kubectl --context <admin-context> delete -f backend/deploy/k8s/hub/optional/node-access.yaml
```

Both take effect for tokens already in use; no re-mint. `dev-up.sh` neither
enables nor disables it, so a state you set stays across deploys. Check which state
the cluster is in with `verify`:

```bash
backend/deploy/k8s/verify/verify.sh --target k8s-dev --layer 1
```

`cluster.hub_rbac` PASSes either way and says which: "node access NOT granted
(opt-in ...)" or "node access ENABLED (ClusterRole studio-hub-nodes exact, bound to
the hub only)". It FAILs if the ClusterRole has one triple too many or too few, if
the binding exists without the ClusterRole, if the ClusterRole exists without its
binding, or if the binding points at another role.

Apply the ClusterRole and its binding together, and delete both together (the file
does this). Deleting only one leaves a state `cluster.hub_rbac` calls a fault.

## Studio hub: checking the grant

Two independent checks. Neither needs the hub's token.

**1. Ask the API server what the account may do** (needs impersonation rights, which
cluster-admin has):

```bash
kubectl --context <admin-context> auth can-i --list \
  --as=system:serviceaccount:rpg-k8s-hub:studio-hub -n <ns>

# one spot check; the answer must be "no"
kubectl --context <admin-context> auth can-i get secrets \
  --as=system:serviceaccount:rpg-k8s-hub:studio-hub -n <ns>
```

The list also shows a few rules the account gets from Kubernetes' default
bindings for every authenticated subject (self-review and discovery). Those are not
the hub's Role. Compare the rest with the rule table in `../k8s/README.md`. Do this
for `rpg-k8s-realtime`, `rpg-k8s-data` and every `rpg-k8s-realtime-<game>`.

**2. Run the deployment check**, which compares the live Roles with the allow-list in
`k3s/hub_contract.py` (rules by triples, not by name):

```bash
backend/deploy/k8s/verify/verify.sh --target k8s-dev --layer 1
```

Look at the line `cluster.hub_rbac`. It also compares the admission policy and its
binding with the repository's, and fails on a RoleBinding that names the account in a
namespace outside the expected list and on a legacy token Secret. How it behaves, and
what it cannot see (rights every ServiceAccount has through a binding to Group
`system:serviceaccounts`, `system:authenticated` or `system:unauthenticated`; whether the
policy's expressions *evaluate* as intended, which only the live check shows), is in
`../k8s/verify/README.md`, "The Studio hub check".

**3. Show that a token works** (optional). Use a kubeconfig that keeps the token in a
file rather than on the command line, so it does not show in the process list: a
user entry with `tokenFile: <private-dir>/studio-hub.token`, the cluster entry with
`certificate-authority: <private-dir>/cluster-ca.crt` and
`server: https://127.0.0.1:<local-port>` (the SSH forward must be up). Keep that
kubeconfig in `<private-dir>`, never in the repository. Then:

```bash
KUBECONFIG=<private-dir>/studio-hub.kubeconfig kubectl -n rpg-k8s-realtime get pods,fleets
KUBECONFIG=<private-dir>/studio-hub.kubeconfig kubectl -n rpg-k8s-realtime get secrets   # must be Forbidden
```

A `tokenFile` user entry is standard client-go behaviour; it was not exercised in
this change. Any 401 means an invalid or expired token (mint again); a 403 on a
listed verb means a Role is missing or wrong (run `verify`).

## Studio hub: the live check

**RUN ONCE (integration QA, round 2, on k3s v1.35.5 / k3d v5.9.0), CORRECTED SINCE, NOT
RE-RUN.** That run observed the policy's denials, that `fleets/scale` is served and that a
bound token dies when its Secret is deleted, and it found four things this list now
covers: the Deployment's own `ownerReferences` and `finalizers` (not denied then; the
policy denies them now), `cluster.hub_rbac` failing on a stock cluster's
`system:service-account-issuer-discovery` binding, the `get --raw /apis/...` calls
rewritten by Git Bash, and a `rollout restart --dry-run` line that is not a flag. The
changes made since are **not observed on a cluster**: the list below is what proves them.
Applying anything to a cluster, the local one included, waits for the owner's word.
Differences from the first list: a context guard, the token in a mode-600 file read
through a `tokenFile` kubeconfig and **never on a command line**, no `verify.sh --only`
(it does not exist; the flag is `--layer 1`), an explicit fleet name, the
admission-policy cases (a restart is allowed; an image, env, privileged/hostPath,
`hostPID`, `serviceAccountName`, `ownerReferences`, `finalizers` or strategy change is
denied, as **server-side dry runs** that persist nothing, plus two **real** patches on a
throwaway Deployment, so that "denied" is seen to leave the object alone), a `patch fleets`
negative, a revoke retry loop and cleanup on exit.

**Git Bash on Windows.** An argument that starts with `/` is rewritten into a Windows
path, so `kubectl get --raw /apis/...` asks for `C:/Program Files/Git/apis/...` and gets
NotFound. The `--raw` calls below set `MSYS2_ARG_CONV_EXCL='*'` for that one command, which
Linux ignores. Do not set `MSYS_NO_PATHCONV=1` for the whole shell: it breaks the Docker
context path of the image rebuild in `dev-up.sh`.

Context `k3d-rpg-dev` only, in WSL Ubuntu, from the repository root. Expect about
1.5-2 GB of memory while the cluster runs.

```bash
set -u
CTX=k3d-rpg-dev
case "$CTX" in k3d-rpg-dev) ;; *) echo "wrong context"; exit 1;; esac
K="kubectl --context $CTX"
SA=system:serviceaccount:rpg-k8s-hub:studio-hub
HUB=backend/deploy/k8s/hub
umask 077; W=$(mktemp -d); trap 'rm -rf "$W"; $K delete deployment qa-throwaway -n rpg-k8s-realtime --ignore-not-found >/dev/null 2>&1' EXIT

k3d cluster start rpg-dev
$K wait --for=condition=Ready node --all --timeout=180s
$K get ns rpg-k8s-realtime rpg-k8s-data >/dev/null || { echo "stack not deployed"; exit 1; }
$K version | grep -i server                     # must be v1.30 or newer (ValidatingAdmissionPolicy v1)

# 0. Is fleets/scale served by Agones? (a finding against the scale decision if not)
MSYS2_ARG_CONV_EXCL='*' $K get --raw /apis/agones.dev/v1 | grep -o '"name":"fleets/scale"' || echo "FINDING: fleets/scale NOT served"
$K api-resources --api-group=admissionregistration.k8s.io -o name | grep -x 'validatingadmissionpolicies.admissionregistration.k8s.io' || echo "FINDING: no policy API"

# 1. Apply the namespaced part and the policy BY NAME (what dev-up.sh applies). Never $HUB/optional.
for f in 00-namespace.yaml 10-serviceaccount.yaml 15-admission-policy.yaml 20-rbac.yaml; do $K apply -f "$HUB/$f" || exit 1; done
$K get validatingadmissionpolicy studio-hub-restart-only -o jsonpath='{.status.typeChecking}'; echo   # empty = the expressions type-check
$K get clusterrole,clusterrolebinding -o name | grep -c studio-hub    # want 0 (grep exits 1 on 0 matches)

# 2. can-i, yes/no tables:
#    realtime: every row of the README table yes; data: the first six rows yes, Agones no;
#    the hub namespace, kube-system, agones-system: no; cluster scope (nodes, namespaces, pods -A, secrets -A): no.
#    must be "no": create pods/log, patch pods/status, delete secrets, update deployments.apps/scale,
#    patch statefulsets.apps, patch fleets.agones.dev, create pods, create pods/exec, get secrets.

# 3. A real token, 10 minutes, bound to an empty Secret; the value only in files under $W.
$K -n rpg-k8s-hub create secret generic studio-hub-token-binding
$K create token studio-hub -n rpg-k8s-hub --duration=10m \
  --bound-object-kind=Secret --bound-object-name=studio-hub-token-binding > "$W/token"
$K config view --raw --minify -o 'jsonpath={.clusters[0].cluster.certificate-authority-data}' | base64 -d > "$W/ca.crt"
SERVER=$($K config view --raw --minify -o 'jsonpath={.clusters[0].cluster.server}')
cat > "$W/hub.kubeconfig" <<EOF
apiVersion: v1
kind: Config
clusters: [{name: c, cluster: {server: "$SERVER", certificate-authority: "$W/ca.crt"}}]
users: [{name: hub, user: {tokenFile: "$W/token"}}]
contexts: [{name: h, context: {cluster: c, user: hub}}]
current-context: h
EOF
T() { KUBECONFIG="$W/hub.kubeconfig" kubectl "$@"; }
T get pods -n rpg-k8s-realtime >/dev/null && echo "token: pods realtime OK"
T get pods -n rpg-k8s-data     >/dev/null && echo "token: pods data OK"
T get fleets.agones.dev -n rpg-k8s-realtime >/dev/null && echo "token: fleets OK"
T get secrets -n rpg-k8s-realtime 2>&1 | grep -q Forbidden && echo "token: secrets realtime Forbidden (wanted)"
T get secrets -n rpg-k8s-data     2>&1 | grep -q Forbidden && echo "token: secrets data Forbidden (wanted)"
T auth can-i --list -n rpg-k8s-realtime | head -40     # SelfSubjectRulesReview with no extra grant

# 4. Fleet scale through the subresource, to its CURRENT value, on a named fleet.
FLEET=map-servers-dotnet-k8s
REPL=$(T get fleets.agones.dev "$FLEET" -n rpg-k8s-realtime -o 'jsonpath={.spec.replicas}')
T scale fleets.agones.dev "$FLEET" -n rpg-k8s-realtime --replicas="$REPL" && echo "token: fleets/scale OK"
T patch fleets.agones.dev "$FLEET" -n rpg-k8s-realtime --type=merge -p '{}' --dry-run=server 2>&1 | grep -q Forbidden \
  && echo "token: patch fleets Forbidden (wanted)"
# The scale subresource carries no metadata: an ownerReference sent through it must come back EMPTY (ignored).
OWNER_REF='{"metadata":{"ownerReferences":[{"apiVersion":"v1","kind":"ConfigMap","name":"qa-nonexistent","uid":"00000000-0000-0000-0000-000000000001"}]}}'
SCALED=$(T patch fleets.agones.dev "$FLEET" -n rpg-k8s-realtime --subresource=scale --type=merge --dry-run=server \
  -p "$OWNER_REF" -o 'jsonpath={.metadata.ownerReferences}' 2>&1)
[ -z "$SCALED" ] && echo "token: fleets/scale ignores metadata.ownerReferences (wanted)" || echo "FINDING: fleets/scale answered: $SCALED"

# 5. THE ADMISSION POLICY. Server-side dry runs: admission runs, nothing is persisted.
RESTART='{"spec":{"template":{"metadata":{"annotations":{"kubectl.kubernetes.io/restartedAt":"2026-01-01T00:00:00Z"}}}}}'
T patch deployment gateway -n rpg-k8s-realtime --dry-run=server --type=merge -p "$RESTART" \
  && echo "policy: a restart patch is ALLOWED (wanted)"
# `kubectl rollout restart` has no --dry-run flag. To see the REAL command accepted, run it for real: it restarts
# the gateway pod of this throwaway cluster, which is what it is for.
T rollout restart deployment/gateway -n rpg-k8s-realtime \
  && T rollout status deployment/gateway -n rpg-k8s-realtime --timeout=180s \
  && echo "policy: kubectl rollout restart is ALLOWED (wanted)"
DEP=gateway
denied() { # name, then the patch args: wanted = Forbidden by the policy (on $DEP, a server-side dry run)
  local name=$1; shift
  out=$(T patch deployment "$DEP" -n rpg-k8s-realtime --dry-run=server "$@" 2>&1)
  if printf '%s' "$out" | grep -q "studio-hub-restart-only"; then echo "policy: $name DENIED by the policy (wanted)"
  else echo "FINDING: $name was not denied by the policy: $out"; fi
}
denied "image change"          --type=json  -p '[{"op":"replace","path":"/spec/template/spec/containers/0/image","value":"example.invalid/x:1"}]'
denied "env change"            --type=json  -p '[{"op":"add","path":"/spec/template/spec/containers/0/env/-","value":{"name":"QA","value":"1"}}]'
denied "privileged + hostPath" --type=json  -p '[
 {"op":"add","path":"/spec/template/spec/volumes/-","value":{"name":"qa-host","hostPath":{"path":"/"}}},
 {"op":"add","path":"/spec/template/spec/containers/0/volumeMounts/-","value":{"name":"qa-host","mountPath":"/host"}},
 {"op":"add","path":"/spec/template/spec/containers/0/securityContext","value":{"privileged":true}}]'
denied "replicas change"       --type=merge -p '{"spec":{"replicas":3}}'
denied "template label change" --type=merge -p '{"spec":{"template":{"metadata":{"labels":{"qa":"x"}}}}}'
denied "Deployment label"      --type=merge -p '{"metadata":{"labels":{"qa":"x"}}}'
denied "another annotation"    --type=merge -p '{"spec":{"template":{"metadata":{"annotations":{"qa":"x"}}}}}'
denied "hostPID"               --type=json  -p '[{"op":"add","path":"/spec/template/spec/hostPID","value":true}]'
denied "serviceAccountName"    --type=json  -p '[{"op":"add","path":"/spec/template/spec/serviceAccountName","value":"qa-other"}]'
# strategy: only a change is a test, so pick the other value than the one the Deployment has
CUR=$($K get deployment gateway -n rpg-k8s-realtime -o 'jsonpath={.spec.strategy.type}')
[ "$CUR" = Recreate ] && NEWSTRAT=RollingUpdate || NEWSTRAT=Recreate
denied "strategy $CUR -> $NEWSTRAT" --type=merge -p "{\"spec\":{\"strategy\":{\"type\":\"$NEWSTRAT\"}}}"
denied "Deployment ownerReferences (a fake owner: the garbage collector would delete it)" --type=merge \
  -p "$OWNER_REF"
denied "Deployment finalizers"      --type=merge -p '{"metadata":{"finalizers":["qa/x"]}}'
denied "Deployment generateName"    --type=merge -p '{"metadata":{"generateName":"qa-"}}'

# The two that matter most, for REAL (not a dry run) on a throwaway Deployment that an ADMIN creates (zero replicas, no
# pod). The denial must leave it alone: still there after the garbage collector had its chance, no finalizer, no owner.
# The throwaway also carries a second template annotation, so that removing the annotations map is a real test (it is
# only one when an annotation other than restartedAt exists).
$K create deployment qa-throwaway -n rpg-k8s-realtime --image=registry.k8s.io/pause:3.9 --replicas=0
$K patch deployment qa-throwaway -n rpg-k8s-realtime --type=merge \
  -p '{"spec":{"template":{"metadata":{"annotations":{"qa/second":"x"}}}}}'
DEP=qa-throwaway
denied "remove the template annotations map (a second one exists)" --type=json \
  -p '[{"op":"remove","path":"/spec/template/metadata/annotations"}]'
for body in "$OWNER_REF" '{"metadata":{"finalizers":["qa/x"]}}'; do
  T patch deployment qa-throwaway -n rpg-k8s-realtime --type=merge -p "$body" 2>&1 | grep -q "studio-hub-restart-only" \
    && echo "policy: a REAL metadata patch was DENIED (wanted)" || echo "FINDING: a real metadata patch was not denied"
done
sleep 10
$K get deployment qa-throwaway -n rpg-k8s-realtime -o 'jsonpath={.metadata.ownerReferences}{.metadata.finalizers}' \
  | grep -q . && echo "FINDING: the throwaway carries an owner or a finalizer" || echo "policy: the throwaway is intact (wanted)"
DEP=gateway
# FleetAutoscaler metadata is an ACCEPTED power (k8s/README.md, "Delete or wedge a FleetAutoscaler"): this is the dry run
# that SHOWS it, so the documented claim is checked and a future policy is not forgotten. Expect "accepted", not "denied".
FAS=$(T get fleetautoscalers.autoscaling.agones.dev -n rpg-k8s-realtime -o name | head -1)
T patch "$FAS" -n rpg-k8s-realtime --dry-run=server --type=merge -p "$OWNER_REF" \
  && echo "accepted power confirmed: a FleetAutoscaler ownerReference is accepted (documented, not a finding)"
# The same image change by an ADMIN must NOT be denied: the policy only restricts the hub.
$K patch deployment gateway -n rpg-k8s-realtime --dry-run=server --type=json \
  -p '[{"op":"replace","path":"/spec/template/spec/containers/0/image","value":"example.invalid/x:1"}]' \
  && echo "policy: an admin image change is not affected (wanted)"

# 6. Revoke: delete the binding Secret; the same token must stop working (retry a few seconds).
$K delete secret studio-hub-token-binding -n rpg-k8s-hub
for i in 1 2 3 4 5; do T get pods -n rpg-k8s-realtime 2>&1 | grep -qi Unauthorized && { echo "token after revoke: Unauthorized (wanted)"; break; }; sleep 2; done

# 7. The deployment check, then stop the cluster.
backend/deploy/k8s/verify/verify.sh --target k8s-dev --layer 1   # read the cluster.hub_rbac line
k3d cluster stop rpg-dev
```

Pass criteria: every yes/no table line as wanted; the studio-hub ClusterRole count 0;
the policy's `typeChecking` status empty; `fleets/scale` served, the scale call
succeeds and a `metadata.ownerReferences` sent through it comes back empty (a NotFound or
Forbidden here is a finding against the scale decision); `patch fleets` Forbidden;
**the restart patch and the real `kubectl rollout restart` allowed and every `denied` line
denied by the policy**, including `hostPID`, `serviceAccountName`, strategy,
`ownerReferences`, `finalizers` and `generateName`, the two real patches on the throwaway
denied and the throwaway intact afterwards, and the admin patch allowed; the FleetAutoscaler
`ownerReferences` dry run **accepted** (an accepted power, not a finding); revoke gives
Unauthorized; `cluster.hub_rbac` **PASS** with the line "node access NOT granted" (on a stock
cluster that is the first time that PASS path is seen live). A `FINDING` on a `denied` line
means a CEL expression did not do what its comment says: that is the one thing the offline
tests cannot show, and the policy must not be relied on until it is fixed. Treat a syntax
slip in this list as a script error, not a result.

Not in the list, on purpose: `metadata.managedFields`. The policy does not compare it (the
field manager rewrites it on every patch, so an equality check would deny the restart);
the table in `../k8s/README.md` says what that leaves the hub able to do (bookkeeping of
field ownership, nothing that deletes, wedges or changes a spec).

**What a previous run leaves on `rpg-dev`.** A run restarts the gateway (its template then
carries a `kubectl.kubernetes.io/restartedAt` annotation and its `generation` has moved), and
the QA that wrote this list left the hub's objects applied and the cluster stopped. That is
a matter of that local cluster and nothing in the repository: `dev-up.sh` re-applies what
the repository describes, and `k3d cluster delete rpg-dev` gives a clean one. No manifest,
test or document needs to change for it.

## Deploying the hub to an existing cluster

- **A cluster serving only the default game (`cuvara`) needs no manual step.** CD's
  `dev-up.sh` applies `k8s/hub` (the admission policy first, then the Roles) before
  `verify` runs.
- **A cluster whose `gateway-config` key `games` names a non-default game must have each
  such game's namespace re-rendered and applied before this change is deployed**
  (before the merge that triggers the deploy). Otherwise `dev-up.sh` stops in its
  preflight, before the data and app tiers change, and the run is red. Per game (read
  `k8s/games/README.md` first: the apply re-applies the game's fleet and quota):

```bash
python backend/deploy/k8s/games/render.py --game <game> --nakama-tls <on|off> \
  | kubectl --context <admin-context> apply -f -
```

- The cluster must serve `admissionregistration.k8s.io/v1` `ValidatingAdmissionPolicy`
  (Kubernetes 1.30 or newer). The documented clusters do (k3s v1.31.5 on k3d, v1.34.1 on
  docker-desktop); a VPS k3s is installed unpinned, so read `kubectl version` there.
  `dev-up.sh` stops before changing anything if it is not served.

## If something goes wrong

| Symptom | Likely cause | Look at |
|---|---|---|
| `verify` FAILs `cluster.hub_rbac` with "ServiceAccount is absent" | `k8s/hub` was never applied on this cluster, or the account was deleted | Run `backend/deploy/k8s/dev-up.sh` |
| `verify` FAILs `cluster.hub_rbac` with `ValidatingAdmissionPolicy studio-hub-restart-only is absent` (or its binding, or `differs:` lines) | The policy was deleted, edited, or never applied. While it is missing `patch deployments` is not a restart (see above) | Run `backend/deploy/k8s/dev-up.sh` (applies it before the Role). A `differs:` line names the field that was edited; `kubectl get validatingadmissionpolicy studio-hub-restart-only -o yaml` |
| `verify` FAILs `cluster.hub_rbac` with "could not be read ... 1.30" | The API server does not serve `admissionregistration.k8s.io/v1` ValidatingAdmissionPolicy (Kubernetes older than 1.30) | Upgrade the cluster. The Role must not be applied without the policy |
| The hub gets `Forbidden ... ValidatingAdmissionPolicy 'studio-hub-restart-only' ... denied request: the Studio hub may only restart a Deployment` | It sent a change other than `restartedAt` (image, env, replicas, a label ...). Working as designed | Use `kubectl rollout restart` semantics only. Change the Deployment with an admin identity and the manifests |
| `dev-up.sh` stops before applying anything: "does not serve admissionregistration.k8s.io/v1 ValidatingAdmissionPolicy" | Kubernetes older than 1.30 | Upgrade the cluster; nothing was changed |
| `dev-up.sh` stops before applying anything: "game <g> is listed in gateway-config's games, but rpg-k8s-realtime-<g> has no Role studio-hub" | That game's render predates the hub | Re-render and apply it (next row), then run `dev-up.sh` again |
| `verify` FAILs `cluster.hub_rbac` with `foreign: RoleBinding <ns>/<name> names ServiceAccount` in a namespace outside the expected list | A binding gives the hub rights where it should have none, or a game namespace is not in `gateway-config`'s `games` | Delete the binding, or add the game to `games` |
| `verify` FAILs `cluster.hub_rbac` with `legacy token: Secret rpg-k8s-hub/<name>` | A never-expiring `kubernetes.io/service-account-token` Secret exists for the account | `kubectl --context <admin-context> -n rpg-k8s-hub delete secret <name>` |
| `verify` FAILs `cluster.hub_rbac` with `rpg-k8s-realtime-<game>: Role studio-hub is absent` | That game's render predates the hub, or it was never applied | Re-render and apply it: `python backend/deploy/k8s/games/render.py --game <game> --nakama-tls <on\|off> \| kubectl --context <admin-context> apply -f -` (read `k8s/games/README.md` before re-applying a render) |
| The hub reports 401 | Token expired, revoked, or the binding Secret was deleted | Mint a new token |
| The hub reports a TLS name error | It dialled a name or address the API certificate does not carry | Dial `127.0.0.1` through the SSH forward, or add the name to `tls-san` (an operator action on the host) |
| The hub reports 403 for a node call | Node access is not enabled | "Studio hub: node access" |
| The hub shows a game namespace missing | The hub is configured with a fixed list of namespaces and the game is not on it | Add the namespace to the hub's configuration |
