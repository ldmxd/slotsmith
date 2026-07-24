# SlotSmith

A calendar-agnostic booking system — the pitch is a Fresha alternative without Fresha's take
rate. This is a **generic product**, not tied to any one client or domain; it's currently
demo-hosted at `booking.mihoknows.com.au` (with a "Book now" link from the mihoknows.com.au
homepage) purely because that's a convenient droplet slot to demo it from. The first demo
target is [ATSI Hair Supplies](https://www.atsihairsupplies.com.au/) (Brookvale), whose owner
mentioned being unhappy with Fresha's cost — if it lands, this would move to its own domain.

ASP.NET 8 minimal API + Dapper + SQL Server, matching the stack used across Mark's other
projects (no EF Core). Sibling project to `OceanSwimmer.Api`, `DrinksExpress.Web`, deployed
the same Docker-on-droplet way as `DrinksExpress.Web` / `IceColdClassic.Site`.

## Why "calendar agnostic"

The booking engine owns its own data — services, staff, bookings — in SQL Server. Calendars
(Google or Outlook) are a sync target/source behind one interface, `ICalendarProvider`:

```
GetBusyTimesAsync()   — read the stylist's existing personal appointments, so the booking
                         engine never double-books them against something already on their
                         calendar (a haircut clashing with their dentist appointment, say).
CreateEventAsync()    — write the confirmed booking to their calendar as a normal event,
                         so they see it exactly where they already look.
CancelEventAsync()
RefreshTokenAsync()
```

`GoogleCalendarProvider` and `MicrosoftCalendarProvider` are the two implementations. Adding
a third (iCloud, CalDAV) means writing one more class — nothing else in the booking flow
changes. Both talk to their provider's REST API directly with `HttpClient` rather than pulling
in the full Google.Apis / Microsoft.Graph SDKs, to keep the dependency footprint small.

## Branding config

Staying generic means the business name isn't hardcoded anywhere in the front end. It lives in
`appsettings.json` under `App:BusinessName`, served publicly via `GET /api/business-info`, and
picked up by `booking.html` (header) and `index.html` (hero heading) on load. Pointing this at a
different client is a one-line config change, not a code change.

## Structure

```
SlotSmith.Api/
├── Program.cs                      # Minimal API endpoints (catalogue, availability, bookings, OAuth)
├── Calendar/
│   ├── ICalendarProvider.cs        # The abstraction
│   ├── GoogleCalendarProvider.cs
│   ├── MicrosoftCalendarProvider.cs
│   └── CalendarProviderFactory.cs
├── Services/
│   └── AvailabilityEngine.cs       # Pure function: business hours - busy times = bookable slots
├── Data/
│   └── BookingRepository.cs        # Dapper queries
├── Models/
│   └── Records.cs
├── sql/
│   ├── 001_schema.sql
│   ├── 002_seed_demo.sql              # ATSI-modelled demo data — NOT their real full price list
│   ├── 003_add_calendar_id.sql        # Migration: adds CalendarConnection.CalendarId
│   ├── 004_add_manage_token.sql       # Migration: adds Booking.ManageToken
│   ├── 005_add_price_rise_history.sql # Migration: adds PriceRiseHistory (once-a-year CPI rise gate)
│   ├── 006_add_staff_time_off.sql     # Migration: adds StaffTimeOff
│   └── 007_add_time_off_calendar_event.sql # Migration: adds StaffTimeOff.CalendarProvider/CalendarEventId
├── wwwroot/
│   ├── booking.html/.css/.js       # Customer-facing booking flow (services → staff → time → confirm)
│   ├── admin-login.html            # Shared-password login for the admin pages below
│   ├── bookings-admin.html         # Search bookings by client, hand off to manage-booking.html
│   ├── admin.html                  # Staff calendar-linking page
│   ├── services-admin.html         # Price/duration editor + once-a-year CPI bulk price rise
│   ├── staff-admin.html            # Add/deactivate stylists, assign services, time off
│   └── manage-booking.html         # Customer self-service: view / reschedule / cancel via emailed link
├── Dockerfile                      # ASP.NET 8, listens on 8080
└── README.md
```

## Known simplifications (this is a demo build, not production)

- **Seed data is not ATSI's real menu.** Staff names (Angelo, Tanya, Caroline, Zuzana) and a
  few service names/prices are real (from their public Fresha listing); the rest of the
  ~51-service catalogue is representative placeholder data. Get their actual price list before
  demoing this as "your new booking page."
- **"No preference" staff picking is naive** — `POST /api/bookings` takes the first eligible
  staff member rather than re-verifying they're still free at that exact instant. Fine for a
  demo, needs a proper race-condition-safe check (transaction + re-validate) before real money
  changes hands.
- **No payment integration.** Fresha takes a cut partly because it handles card-on-file /
  no-show protection. This doesn't yet — worth discussing with the salon owner what he
  actually needs there before assuming "no Fresha fee" is the whole pitch.
- **Single venue, single timezone** (`Australia/Sydney` hardcoded in `Program.cs`).
- **Admin auth is a single shared password, not per-person accounts.** `admin.html`,
  `services-admin.html`, `staff-admin.html`, and their backing `/api/admin/*` +
  `/api/calendar/*` endpoints all require logging in at `/admin-login.html` first (cookie-based,
  30-day sliding expiry). One password for everyone who needs admin access (Mark, Angelo) —
  fine for a two-person demo, not how you'd do it once there's a real staff roster with
  different permission levels. See "Admin login" below for setup.
- **Deactivating a stylist doesn't touch their existing future bookings.** `staff-admin.html`
  only hides them from new bookings (`IsActive = 0`); anything already booked with them stays
  as-is. Fine for a demo — cancelling/reassigning those would need to be a deliberate,
  separate action in a real version, not an automatic side effect of deactivating someone.
- **A calendar-linked stylist isn't required to be bookable.** Staff without a connected
  Google/Outlook calendar can still take bookings — availability just falls back to this app's
  own `Booking` table only (no overlay of their personal calendar's busy times). Worth knowing:
  it means the system can't see conflicts with anything not booked through SlotSmith itself for
  an unconnected stylist.
- **Bot mitigation is deliberately lightweight**: a honeypot field, a minimum
  time-to-submit check, and a 5-bookings-per-hour-per-IP rate limit on `POST /api/bookings`
  (`Program.cs`). This stops basic scripted abuse, not a determined attacker — there's no
  CAPTCHA. If ATSI sees real spam, the next step would be Cloudflare Turnstile in front of the
  booking form.
- **Manage-booking tokens don't expire.** `manage-booking.html?token=...` works forever once
  issued. Reasonable for a demo; a real version might expire tokens after the appointment date.
- **Confirmation/reschedule emails include a hand-built `.ics` calendar attachment** (no
  library — `BuildIcsContent` in `Program.cs`), so the customer can add the appointment to
  Google/Outlook/Apple Calendar etc. Both use the same `UID` (`{manageToken}@slotsmith`) so a
  reschedule's `.ics` can update the original event in calendar apps that support that — but
  there's no real per-booking `SEQUENCE` counter behind it (reschedule is hardcoded to
  `SEQUENCE:1`), so a second reschedule won't necessarily supersede the first correctly. Resend's
  API doesn't let you override the attachment's Content-Type to force `text/calendar`, but every
  major mail client still recognises `.ics` by file extension. The cancellation email doesn't
  attach one — there's nothing to add, and reliably auto-removing an event via a plain file
  attachment (not a full organizer/attendee invite flow) isn't guaranteed across clients anyway.
- **Customer email is format-validated, not ownership-verified.** `POST /api/bookings` rejects
  malformed addresses (via `MailAddress` + a domain-has-a-dot check) but doesn't confirm the
  customer can actually receive mail there — no confirm-your-email step before the booking is
  finalized. Same trade-off Fresha/Calendly make: verifying ownership means a magic-link/OTP step
  in the funnel, which costs more conversions than it's worth for a low-stakes salon booking. A
  bad address just means that customer doesn't get their own confirmation/reminder emails.
- **Staff photo uploads are validated but not deeply sanitized** — content-type + extension +
  5MB size check, no magic-byte sniffing or image re-encoding. Fine behind the admin login for
  two trusted uploaders; a public-facing upload surface would need more hardening.
- **Uploaded photos need a Docker volume mount in production** (`wwwroot/uploads/staff`) — see
  the deploy command below. Without it, photos vanish on the next redeploy.
- I could not compile this in the sandbox I built it in — no .NET SDK / internet access there.
  **Run `dotnet build` before deploying anywhere.**

## Local setup

### 1. Database

```bash
sqlcmd -S localhost,1433 -U sa -P <password> -Q "CREATE DATABASE SlotSmith"
sqlcmd -S localhost,1433 -U sa -P <password> -d SlotSmith -i sql/001_schema.sql
sqlcmd -S localhost,1433 -U sa -P <password> -d SlotSmith -i sql/002_seed_demo.sql
```

### 2. Connection string

```bash
export SLOTSMITH_SQL="Server=localhost,1433;Database=SlotSmith;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

### 3. Admin password

```bash
dotnet user-secrets set "Admin:Password" "<pick something>"
```

Without this, `appsettings.json` has a `YOUR_ADMIN_PASSWORD` placeholder and `/api/admin/login`
fails closed (500, not "accepts anything") — same convention as the Resend API key.

### 4. Run

```bash
dotnet run
# open http://localhost:5080/booking.html
# admin pages: http://localhost:5080/admin-login.html
```

Calendar linking (`admin.html`) will fail until you've registered OAuth apps — see below.
Everything else (browsing services, picking a time against business hours + existing bookings)
works without it.

## Admin login

`admin.html`, `services-admin.html`, and `staff-admin.html` (calendar linking, pricing, staff)
all sit behind `/admin-login.html` — a single shared password, checked against `Admin:Password`
(see setup above), backed by a cookie (`slotsmith_admin`, 30-day sliding expiry). There's no
per-person login and no password reset flow; if the password needs to change, update the config
value and everyone logs in again. Good enough for the two people (Mark, Angelo) who need access
during the demo — revisit if this becomes a real multi-tenant product with different staff
needing different permissions.

## Google Calendar setup

1. [Google Cloud Console](https://console.cloud.google.com/) → new project → enable the
   **Google Calendar API**.
2. **OAuth consent screen** → External → fill in the basics → **Testing** publish status is
   fine for a demo (up to 100 test users, no Google review needed). Add your own Google
   account and the salon owner's as test users.
3. **Credentials** → Create Credentials → OAuth client ID → Web application.
   Authorized redirect URI: `https://booking.mihoknows.com.au/api/calendar/Google/callback`
   (or `https://localhost:5001/api/calendar/Google/callback` for local testing).
4. Put the client ID/secret in `appsettings.Development.json` locally, or as environment
   variables in production (`Calendar__Google__ClientId`, `Calendar__Google__ClientSecret` —
   ASP.NET Core config binds double-underscore env vars to nested config keys).

## Outlook / Microsoft 365 setup

1. [Entra admin center](https://entra.microsoft.com/) → **App registrations** → New
   registration.
   Supported account types: **"Accounts in any organizational directory and personal
   Microsoft accounts"** — this matters if the stylist uses a plain outlook.com/hotmail
   account rather than a work 365 tenant.
   Redirect URI (Web): `https://booking.mihoknows.com.au/api/calendar/Microsoft/callback`
2. **Certificates & secrets** → New client secret → copy the value immediately (shown once).
3. **API permissions** → Add a permission → Microsoft Graph → Delegated permissions →
   `Calendars.ReadWrite`, `offline_access`, `User.Read`. Personal Microsoft accounts don't need
   admin consent; a work/school tenant might.
4. `Calendar__Microsoft__ClientId`, `Calendar__Microsoft__ClientSecret` env vars in production.

## Email confirmations (Resend)

Booking confirmations send via [Resend](https://resend.com), same provider and same account
already used by `OceanSwimmer.Api` — same `SendResendEmailAsync` pattern (plain REST call, no
SDK), just a different `Resend:ApiKey` / `FromEmail` / `FromName` config.

- Without an API key configured, `Program.cs` logs the confirmation to the console instead of
  sending — local dev works with zero setup.
- `FromEmail` defaults to `noreply@mihoknows.com.au`, but **that domain needs to be verified as
  a sending domain in Resend** (Resend dashboard → Domains → Add Domain → add the DNS
  TXT/CNAME/MX records it gives you) before mail from that address will actually deliver. Until
  that's done, you can point `FromEmail` at `noreply@oceanswimmer.com.au` instead (already
  verified) to test the flow end to end.
- Config keys: `Resend:ApiKey`, `Resend:FromEmail`, `Resend:FromName` — same
  double-underscore env var pattern as the calendar secrets in production
  (`Resend__ApiKey`, etc).
- SMS confirmations aren't implemented — email covers the demo. If it's wanted later, Twilio
  works but ClickSend/MessageMedia are Australian companies with simpler local pricing, worth
  a look first.

## Testing both providers side by side

Since staff link their own calendar individually (`/admin.html`), you can genuinely test both
in the same running instance: connect one test staff member's Google calendar and another's
Outlook calendar, then book against both from `/booking.html` and confirm events land in the
right place and busy times from each are respected.

## Picking which calendar to use

An account can have more than one calendar (a personal one and a separate work one, say), so
`GetBusyTimesAsync`/`CreateEventAsync` don't just assume the account's default. After connecting
in `/admin.html`, a dropdown of that account's calendars appears — pick the right one and hit
"Use this calendar". Until a choice is saved, it falls back to the provider's default calendar
(`primary` for Google, the mailbox's default for Outlook), so connecting still works immediately
even if you skip this step.

If you created your local database before this existed, run the migration first:

```bash
sqlcmd -S 127.0.0.1,1435 -U sa -P 'YourPassword!' -d SlotSmith -i sql/003_add_calendar_id.sql
```

## Manage-my-booking links and service pricing

Every booking gets a random `ManageToken` at creation time; the confirmation email includes a
link to `manage-booking.html?token=...` where the customer can view, reschedule, or cancel
without logging in. Reschedule is implemented as cancel-old-event + create-new-event on the
linked calendar (no `UpdateEventAsync` on `ICalendarProvider` — kept the interface smaller).

Angelo (or any staff member) edits prices and durations at `/services-admin.html`, which reads/
writes via `GET/PUT /api/admin/services` (behind the admin login — see "Admin login" above).

If you created your local database before `ManageToken` existed, run the migration first:

```bash
sqlcmd -S 127.0.0.1,1435 -U sa -P 'YourPassword!' -d SlotSmith -i sql/004_add_manage_token.sql
```

### Staff-assisted changes (phone-in requests)

`bookings-admin.html` loads with the next 100 upcoming confirmed bookings, soonest first
(`GET /api/admin/bookings/upcoming`), so something like "tomorrow's 2pm" is visible without
typing anything. Searching (`GET /api/admin/bookings/search?q=...` — a client's name, email, or
phone, `LIKE '%q%'` against `dbo.Customer`, min 2 characters) switches to matching bookings of any
status, past and present, so staff can also find something already cancelled or further out.
Rather than building a second reschedule/cancel implementation for staff, each result just links
to `/manage-booking.html?token=...` using that booking's own `ManageToken` (safe to expose here
since both endpoints are behind admin auth): staff click through and use the exact same tested
flow a customer would, on the customer's behalf. Search matching is a plain `LIKE`, so a query
containing `%` or `_` behaves like a SQL wildcard rather than a literal character — a cosmetic
edge case, not worth escaping for a demo.

### Price change history + the once-a-year CPI rise

Every price change — a single manual edit or a bulk CPI rise — gets logged to
`dbo.ServicePriceHistory` (service, old price, new price, `ChangeType` of `Manual` or `CPI`,
timestamp). `services-admin.html` has a collapsible "Price change history" section at the
bottom reading it back via `GET /api/admin/services/price-history`.

The bulk CPI rise button (`POST /api/admin/services/bulk-price-increase`) raises every service's
price by X%, rounded up to the nearest $5, and is capped to once every 365 days — the API
derives "last applied" from the most recent `CPI`-tagged row in that same history table (via
`GET /api/admin/services/price-rise-status`) rather than a separate counter, so the gate and the
audit trail can never drift out of sync. Manual single-service edits aren't subject to this
cooldown — only the bulk action is.

If you created your local database before this existed, run the migration:

```bash
sqlcmd -S 127.0.0.1,1435 -U sa -P 'YourPassword!' -d SlotSmith -i sql/005_add_price_rise_history.sql
```

## Staff time off

`staff-admin.html` has a "Time off" section per stylist (start date, end date, optional reason).
Adding a range does three things:

1. Blocks that range from new bookings/reschedules — `dbo.StaffTimeOff` rows are fed into
   `AvailabilityEngine` as another busy-interval source, right alongside existing bookings and
   the stylist's connected calendar (`GetStaffTimeOffBusyAsync` in `BookingRepository.cs`).
   `POST /api/bookings` also hard-rejects a request that lands in a time-off window, since that
   endpoint otherwise trusts the client's chosen slot rather than re-validating it server-side —
   see the comment there.
2. If the stylist has a connected calendar, creates a "Time off — <reason>" block on it for the
   whole range, so it's visible when they check their own calendar directly, not just inside
   SlotSmith. Removing a time-off entry (the "Remove" button) cancels that calendar event too.
   It's a timed event (venue-local midnight to midnight), not a native all-day event — Google/
   Outlook will show it as a long block rather than in the special all-day banner, which is
   cosmetic but worth knowing.
3. Emails everyone already booked with that stylist during the new range
   (`GET /api/admin/staff/{id}/time-off` lists existing ranges,
   `POST /api/admin/staff/{id}/time-off` creates one and does the notification). The email points
   at the customer's existing `manage-booking.html` link so they pick a new time or cancel
   themselves.

**Deliberately not automatic:** an existing booking that falls inside a new time-off range is left
completely alone — status stays `Confirmed`, its own calendar event (if any) is untouched, so the
stylist's calendar will show a real double-booking (the time-off block *and* the original
appointment) until the customer reschedules or cancels via the emailed link. There's also no
tracking of who has/hasn't responded yet; if a customer ignores the email, staff would need to
notice and follow up manually (e.g. by checking `GET /api/admin/staff/{id}/time-off` against the
booking list).

If you created your local database before this existed, run both migrations:

```bash
sqlcmd -S 127.0.0.1,1435 -U sa -P 'YourPassword!' -d SlotSmith -i sql/006_add_staff_time_off.sql
sqlcmd -S 127.0.0.1,1435 -U sa -P 'YourPassword!' -d SlotSmith -i sql/007_add_time_off_calendar_event.sql
```

## Deployment to the droplet

Now deployed on its own domain, `slotsmith.com.au` (bought July 2026) — not a subdomain of
mihoknows. Same Docker pattern as `DrinksExpress.Web`. Next free port: **8084**.

```bash
cd /var/www
sudo git clone https://github.com/ldmxd/slotsmith.git
cd slotsmith

sudo docker build -t slotsmith-web .
sudo docker run -d \
  --name slotsmith-web \
  --restart unless-stopped \
  --network oceanswimmer_default \
  -p 127.0.0.1:8084:8080 \
  -v /var/www/slotsmith-uploads:/app/wwwroot/uploads \
  -e SLOTSMITH_SQL="Server=sqlserver,1433;Database=SlotSmith;User Id=sa;Password=<password>;TrustServerCertificate=True" \
  -e Admin__Password="<...>" \
  -e Calendar__Google__ClientId="<...>" \
  -e Calendar__Google__ClientSecret="<...>" \
  -e Calendar__Microsoft__ClientId="<...>" \
  -e Calendar__Microsoft__ClientSecret="<...>" \
  -e Resend__ApiKey="<...>" \
  slotsmith-web
```

The `-v` mount matters: staff photos uploaded via `staff-admin.html` get written to
`wwwroot/uploads/staff` inside the container. Without that mount, every redeploy (`docker build`
+ fresh `docker run`) wipes them, since the rebuilt image starts from the Dockerfile's `COPY`
again with nothing in that folder.

New server block, `/etc/nginx/sites-available/slotsmith`:

```nginx
server {
    server_name slotsmith.com.au www.slotsmith.com.au;

    location / {
        proxy_pass http://127.0.0.1:8084;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
    }

    listen 80;
}
```

```bash
sudo ln -s /etc/nginx/sites-available/slotsmith /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
sudo certbot --nginx -d slotsmith.com.au -d www.slotsmith.com.au
```

Add A records for `slotsmith.com.au` and `www.slotsmith.com.au` → `170.64.145.69` at the
registrar before running certbot, same as any other domain.

**Google/Microsoft OAuth consoles** also need their allowed redirect URIs updated to
`https://slotsmith.com.au/api/calendar/{Google,Microsoft}/callback` — `appsettings.json` changing
isn't enough on its own, the provider's own app registration has to match or the OAuth callback
gets rejected.

`booking.mihoknows.com.au` (the old demo host, still has its own server block/cert) can either
keep serving the same container — add `booking.mihoknows.com.au` as another `server_name` on the
block above and it works unmodified since it's the same proxy target — or 301-redirect to
`slotsmith.com.au` if the plan is to stop using the old link entirely. Whatever's chosen, update
`MihoKnows.Site`'s "Book now" button to point at the new domain either way.

## Next steps

- Get ATSI's real service menu and pricing from the owner before demoing
- Decide on payment / no-show handling before pitching this as a serious Fresha replacement
- Harden the "no preference" staff assignment against double-booking races
- Verify `mihoknows.com.au` as a sending domain in Resend so confirmation emails come from the
  right address instead of borrowing `oceanswimmer.com.au`
