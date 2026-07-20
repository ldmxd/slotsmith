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
│   └── 002_seed_demo.sql           # ATSI-modelled demo data — NOT their real full price list
├── wwwroot/
│   ├── booking.html/.css/.js       # Customer-facing booking flow (services → staff → time → confirm)
│   └── admin.html                  # Staff calendar-linking page
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

### 3. Run

```bash
dotnet run
# open http://localhost:5080/booking.html
```

Calendar linking (`admin.html`) will fail until you've registered OAuth apps — see below.
Everything else (browsing services, picking a time against business hours + existing bookings)
works without it.

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

## Testing both providers side by side

Since staff link their own calendar individually (`/admin.html`), you can genuinely test both
in the same running instance: connect one test staff member's Google calendar and another's
Outlook calendar, then book against both from `/booking.html` and confirm events land in the
right place and busy times from each are respected.

## Deployment to the droplet

Same Docker pattern as `DrinksExpress.Web`. Next free port: **8084**.

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
  -e SLOTSMITH_SQL="Server=oceanswimmer_sqlserver,1433;Database=SlotSmith;User Id=sa;Password=<password>;TrustServerCertificate=True" \
  -e Calendar__Google__ClientId="<...>" \
  -e Calendar__Google__ClientSecret="<...>" \
  -e Calendar__Microsoft__ClientId="<...>" \
  -e Calendar__Microsoft__ClientSecret="<...>" \
  slotsmith-web
```

Reverse-proxy it as its own subdomain rather than a path under mihoknows — Google/Microsoft's
OAuth redirect URI matching is more reliable against a subdomain than a proxied sub-path, and
it keeps the door open to pointing `booking.mihoknows.com.au`'s DNS at a different box entirely
later without touching the mihoknows site. New server block,
`/etc/nginx/sites-available/booking.mihoknows`:

```nginx
server {
    server_name booking.mihoknows.com.au;

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
sudo ln -s /etc/nginx/sites-available/booking.mihoknows /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
sudo certbot --nginx -d booking.mihoknows.com.au
```

Add an A record for `booking.mihoknows.com.au` → `170.64.145.69` before running certbot,
same as any other subdomain.

## Next steps

- Get ATSI's real service menu and pricing from the owner before demoing
- Decide on payment / no-show handling before pitching this as a serious Fresha replacement
- Harden the "no preference" staff assignment against double-booking races
- Add SMS/email confirmation (currently the front-end just claims one was sent)
