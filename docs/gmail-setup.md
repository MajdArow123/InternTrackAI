# Gmail status suggestions — Google OAuth setup

InternTrackAI can watch your inbox for mail from companies you've applied to and turn interview
invitations, offers and rejections into one-line suggestions on the dashboard, in the application drawer
and as a dot on board cards. Accepting one applies the status (and the interview time when the email names
one) and notes the change on the application.

The integration is **off until both Google keys are present**. Without them the app runs exactly as it
otherwise would: no Gmail card, no Connect button, no background job, and the `/Integrations/*` endpoints
answer 404. Nothing below is needed to run the rest of the app.

---

## 1. Create or reuse a Google Cloud project

Go to the [Google Cloud console](https://console.cloud.google.com/) and create a project, or pick an
existing one. Everything below happens inside it.

## 2. Enable the Gmail API

**APIs & Services → Library** → search for **Gmail API** → **Enable**.

## 3. Configure the OAuth consent screen

**APIs & Services → OAuth consent screen**:

- User type: **External**.
- Leave the publishing status as **Testing**. Do not submit for verification — see
  [Why Testing mode](#why-testing-mode-is-the-right-setting-here) below.
- Under **Scopes**, add exactly one: `https://www.googleapis.com/auth/gmail.readonly`.
- Under **Test users**, add every Google account that will connect its inbox — including your own.
  Google allows up to 100.

An account that is not on the test-user list cannot complete the flow; Google stops it with an "app has
not completed verification" screen rather than returning an error to the app.

## 4. Create the OAuth client

**APIs & Services → Credentials → Create credentials → OAuth client ID**:

- Application type: **Web application**.
- **Authorized redirect URIs** — add both, so the same client works locally and in production:
  - `http://localhost:5240/Integrations/Gmail/Callback`
  - `https://<your-app>.up.railway.app/Integrations/Gmail/Callback`
  - …and one line per **additional** hostname the app answers on. The redirect URI is derived per request from the host, so every live hostname needs its own entry; removing one breaks the flow for anyone arriving on it. For this deployment both `https://interntrackai.majdarow.com/Integrations/Gmail/Callback` and the original `up.railway.app` one are registered.

Google matches the redirect URI exactly, including scheme, host, port and path.

## 5. Store the client id and secret

Locally, in user secrets:

```bash
dotnet user-secrets set "Google:ClientId" "1234567890-abc.apps.googleusercontent.com"
dotnet user-secrets set "Google:ClientSecret" "GOCSPX-..."
```

In production, as the two environment variables `Google__ClientId` and `Google__ClientSecret` — see
[deployment.md](deployment.md) for the full variable table.

## 6. Connect

Restart the app and open **Profile → Connected accounts → Connect Gmail**.

The background sync then runs every `Gmail__SyncIntervalMinutes` (default 30). **Sync now** on the profile
page runs one immediately and reports what it found.

---

## Why Testing mode is the right setting here

Publishing the consent screen would mean submitting the app to Google for verification: a review process
intended for apps distributed to the public, requiring a homepage, a privacy policy, a demonstration video
and a security assessment for restricted scopes like Gmail.

This is a portfolio project connecting a handful of known inboxes, so none of that applies. Testing mode
allows up to 100 explicitly listed test users, which is far more than it needs, and the only cost is the
unverified-app warning shown once during consent. **Leave it in Testing.**

## Troubleshooting `redirect_uri_mismatch`

Every time the flow starts, the app logs the exact redirect URI it sends to Google:

```
Starting Gmail OAuth flow ... redirect_uri ...
```

Compare that line against the entries in the Google console — they must match character for character.

Behind Railway the app derives the `https://` origin from the proxy's `X-Forwarded-Proto` and
`X-Forwarded-Host` headers, so it normally gets this right on its own. If the logged URI is ever wrong,
`Google__RedirectBaseUrl` pins the origin verbatim.

## Privacy

- Only `gmail.readonly` is ever requested. The app **cannot** send, modify, label or delete mail.
- Email bodies are read once for classification and **never stored**. A suggestion keeps the subject,
  sender, date, Gmail message id and the AI's one-sentence summary — nothing else.
- OAuth tokens are encrypted at rest with ASP.NET Data Protection.
- **Disconnect** revokes the grant with Google before deleting the stored row. Pending suggestions are
  deleted with it; ones you already accepted or dismissed are kept, since they are part of your history.
- Each classification counts against the same per-user AI rate limit as every other AI feature.
