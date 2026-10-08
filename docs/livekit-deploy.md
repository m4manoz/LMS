# Live classes on the test server

The test server (`http://37.60.228.196:8081`) can run its own LiveKit server next to the app. It is off until you turn it on.

## 1. Turn it on (once, on the server)

Edit `~/kalash/lms/.env` and add:

```
COMPOSE_PROFILES=live
LIVEKIT_NODE_IP=37.60.228.196
LIVEKIT_API_KEY=APIlms1a2b3c4d
LIVEKIT_API_SECRET=<at least 32 characters>
```

Make the key and secret with `echo "APIlms$(openssl rand -hex 4)"; openssl rand -hex 24`.

Open these ports in the server firewall **and** in the hosting provider's firewall: **7880/tcp, 7881/tcp, 7882/udp**.

Then start it: `cd ~/kalash/lms && docker compose up -d`. The next deploy keeps it running.
Check: `docker compose logs livekit` shows "starting LiveKit server" and the port.

## 2. Tell the app about it

Sign in as an administrator, then **Integrations → Live classes → LiveKit** and enter:

- Server address: `ws://37.60.228.196:7880`
- API key and API secret: the same as in `.env`

Plain `ws://` is allowed on this server by `Integrations__AllowInsecureLiveClassHosts` in the compose file. A server with a domain and certificate should use `wss://` and remove that setting.

## 3. Camera and microphone on a plain http address

Browsers only give cameras and microphones to `https://` pages (and `localhost`). On the test address each tester must allow it once:

1. Open `chrome://flags/#unsafely-treat-insecure-origin-as-secure` (Edge: `edge://flags/...`).
2. Add `http://37.60.228.196:8081`, set the flag to **Enabled**, restart the browser.

Without this, people can join a class and watch but not send video or sound. A domain with a certificate removes the need.

## 4. Try a class

Schedule a class linked to a course, join as the teacher in one browser and as an enrolled learner in another (or a private window). Check that:

- both see and hear each other (the ports above are open),
- attendance shows both people (the webhook reaches the app),
- the teacher's mute buttons switch the learner's microphone off.

## Recordings

Recording needs LiveKit's separate recording service (Egress), Redis and a shared folder. It is not part of this setup; see `scripts/livekit-dev.ps1` and the README section "Recording LiveKit classes" for what it needs.
