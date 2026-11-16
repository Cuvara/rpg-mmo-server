# Runbook: operating the k8s deployment

This file holds the operator procedures that are specific to the Kubernetes
deployment (`k8s/`). The local compose stack has its own runbook,
[`RUNBOOK-local-dev.md`](RUNBOOK-local-dev.md).

Sections:

- [Studio hub token](#studio-hub-token): mint, check, rotate, revoke, read the cluster CA, the SSH forward.
- [Studio hub: node access](#studio-hub-node-access): enable and disable the opt-in node ClusterRole.
- [Studio hub: checking the grant](#studio-hub-checking-the-grant)

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
`20-rbac.yaml`). The ServiceAccount has `automountServiceAccountToken: false` and
nothing runs in its namespace, so the **only** credential that can act as the hub is
a token an operator mints by hand with the procedure below.

A token is an **operator credential**. With the namespaced grant it can restart
and delete pods and patch Deployments (so it can change the gateway's and Nakama's
image, env and command), and together with `pods/log` that is an indirect path to a
Secret value (see "The costs" in `../k8s/README.md`). Mint it for a person or a
machine you would trust with the gateway's environment.

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

Look at the line `cluster.hub_rbac`. How it behaves, and what it cannot see (for
example, a RoleBinding that names the account in a namespace outside the expected
list), is in `../k8s/verify/README.md`, "The Studio hub check".

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

## If something goes wrong

| Symptom | Likely cause | Look at |
|---|---|---|
| `verify` FAILs `cluster.hub_rbac` with "ServiceAccount is absent" | `k8s/hub` was never applied on this cluster, or the account was deleted | Run `backend/deploy/k8s/dev-up.sh` |
| `verify` FAILs `cluster.hub_rbac` with `rpg-k8s-realtime-<game>: Role studio-hub is absent` | That game's render predates the hub, or it was never applied | Re-render and apply it: `python backend/deploy/k8s/games/render.py --game <game> --nakama-tls <on\|off> \| kubectl --context <admin-context> apply -f -` (read `k8s/games/README.md` before re-applying a render) |
| The hub reports 401 | Token expired, revoked, or the binding Secret was deleted | Mint a new token |
| The hub reports a TLS name error | It dialled a name or address the API certificate does not carry | Dial `127.0.0.1` through the SSH forward, or add the name to `tls-san` (an operator action on the host) |
| The hub reports 403 for a node call | Node access is not enabled | "Studio hub: node access" |
| The hub shows a game namespace missing | The hub is configured with a fixed list of namespaces and the game is not on it | Add the namespace to the hub's configuration |
