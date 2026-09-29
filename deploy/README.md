# Building and hosting on the home server

Pushing to the **`deploy`** branch builds, tests and deploys Cards on the server
(`jacktheripper`, Ubuntu 24.04). A GitHub Actions runner on the server watches the
branch; it only makes outgoing connections to GitHub, so nothing on the router opens.

```
git push origin development:deploy        # deploy what is on development
```

Each deploy is published to its own folder under `/srv/cards/releases/`, and
`/srv/cards/current` is switched to it. If the new release does not answer its health
check, the previous one is switched back in and the run fails. The last five releases are
kept. Builds and their logs are on the repo's **Actions** tab.

People on the home network open **http://cards.local** (announced by the server over
mDNS) and Caddy routes each name to its app.

Everything below is one-time setup, and assumes steps 1–2 of the earlier plan are done:
system updates, firewall, .NET 10 SDK in `/usr/share/dotnet` with the `wasm-tools`
workload, and the `cards` system user.

## 1. Packages

```bash
sudo apt install -y caddy avahi-daemon avahi-utils libfontconfig1 fonts-dejavu-core rsync
```

`libfontconfig1` and a font are for the tests, which draw cards with SkiaSharp.

## 2. A user for the runner, and the app's folders

The runner builds and publishes as its own user, `ghrunner`; the app runs as `cards`
and only reads what was published.

```bash
sudo useradd --create-home --shell /bin/bash ghrunner
sudo mkdir -p /srv/cards/releases
sudo chown -R ghrunner:cards /srv/cards
sudo chmod -R 755 /srv/cards
sudo install -m 0440 deploy/sudoers.d/cards-deploy /etc/sudoers.d/ && sudo visudo -c
```

(The sudoers file lets `ghrunner` run exactly one privileged command:
`systemctl restart cards`.)

## 3. The app's service

```bash
sudo cp deploy/cards.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable cards          # started by the first deploy
```

`/srv/cards/current` does not exist until the first deploy, so the service is enabled now
and started by the deploy.

The service's `StateDirectory=cards` gives it `/var/lib/cards`, where shared tables are
kept (`rooms/`) so a restart — every deploy is one — does not end the games being played.
A server set up before that line existed needs the unit copied again (the three commands
above, then `sudo systemctl restart cards`); until then it runs, but forgets its tables
on each deploy. Away from a checkout, the runner's own copy is the one to take:
`/home/ghrunner/actions-runner/_work/cards/cards/deploy/cards.service`. The log says which: `Rooms are kept in /var/lib/cards/rooms`.

## 4. Names and the front door

```bash
sudo cp deploy/avahi-alias@.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now avahi-alias@cards      # cards.local -> this server

sudo cp deploy/Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

`.local` names resolve on Windows 10/11, macOS, iOS and Linux. Android is patchy; when it
matters, add a record on the router (or a Pi-hole) pointing `cards.home` — or a wildcard
`*.home` — at the server. The Caddyfile already answers to `cards.home`.

## 5. The GitHub Actions runner

On GitHub: the repo → **Settings → Actions → Runners → New self-hosted runner** → Linux,
x64. It shows a download command and a `config.sh` command with a one-time token. Run
them as `ghrunner`:

```bash
sudo -iu ghrunner
mkdir actions-runner && cd actions-runner
# … the download and extract commands GitHub shows …
./config.sh --url https://github.com/gazer77/cards --token <TOKEN> \
            --name jacktheripper --labels jacktheripper --unattended
exit
cd /home/ghrunner/actions-runner
sudo ./svc.sh install ghrunner
sudo ./svc.sh start
```

The runner is now a service; it appears as **Idle** under Settings → Actions → Runners.

## 6. First deploy

```bash
git push origin development:deploy
```

Watch it on the **Actions** tab. When it finishes, open http://cards.local.

## Day to day

| To… | Do |
|---|---|
| Deploy | `git push origin development:deploy` (or run the workflow from the Actions tab) |
| See the app's log | `journalctl -u cards -f` |
| See what is live | `readlink /srv/cards/current` |
| Roll back | `sudo -u ghrunner ln -sfn /srv/cards/releases/<older> /srv/cards/current && sudo systemctl restart cards` |

A deploy restarts the server, and shared games live in memory — **a deploy ends any game
in progress.** Deploy between games until shared games are saved (on the backlog).

## Troubleshooting

**The runner service fails with `status=200/CHDIR`.** It was set up somewhere `ghrunner`
cannot open — typically inside your own home folder, which other users cannot enter on
Ubuntu 24.04. Move it to `ghrunner`'s home and reinstall the service:

```bash
cd <where the runner is> && sudo ./svc.sh stop && sudo ./svc.sh uninstall && cd ~
sudo mv <where the runner is> /home/ghrunner/actions-runner
sudo chown -R ghrunner:ghrunner /home/ghrunner/actions-runner
sudo bash -c 'cd /home/ghrunner/actions-runner && ./svc.sh install ghrunner && ./svc.sh start && ./svc.sh status'
```

**`cards.local` does not resolve.** Check `hostname -I`: the alias announces the first
address listed. If that is not the LAN address, put the LAN address in
`/etc/systemd/system/avahi-alias@.service` in place of the `$(hostname -I …)` part, then
`sudo systemctl daemon-reload && sudo systemctl restart avahi-alias@cards`.

**A deploy fails in a test step.** The log on the Actions tab names the test. Tests had
never run on Linux before this server; a font or native-library problem there shows up
as a rendering test failing, not as a game bug.

## Another app

Give it its own service user, a folder under `/srv/<app>`, a service file on its own port
(5281, 5282, …), an `avahi-alias@<app>` for its name, a block in the Caddyfile, and — if it
should build itself — a workflow like `.github/workflows/deploy.yml` in its repo, and a
runner for that repo. On a personal account a runner serves one repository, so install a
second one beside the first — same `ghrunner` user, its own folder
(`/home/ghrunner/actions-runner-<app>`), registered from that repo's Settings → Actions →
Runners with the `jacktheripper` label, and its own `svc.sh install ghrunner`. (Under a
GitHub organisation, one runner registered to the organisation can serve every repo.)
Its sudoers line restarts only its own service.

## Later: open to the internet

Point a domain at your home IP and forward ports 80 and 443 to the server — or use a
Cloudflare Tunnel and forward nothing. Then give the app's Caddyfile block its real name
without `http://`, and Caddy gets the certificate itself:

```
cards.example.com {
    reverse_proxy 127.0.0.1:5280
}
```
