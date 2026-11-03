# Diet Time APIs

`DietTime.Meal.Api` is the .NET 8 API over the existing PostgreSQL meal catalogue and meal-plan schema. It returns localized Flutter-ready projections, never translation collections or image binaries. The solution is organized to accommodate additional hosts such as `DietTime.Auth.Api`, `DietTime.Customer.Api`, and `DietTime.Admin.Api` under `src/Apis`.

## Projects

- `src/Apis/DietTime.Meal.Api`: versioned meal controllers, JWT, middleware, Swagger, rate limiting, CORS, and health checks.
- `src/BuildingBlocks`: reusable application, contracts, domain, infrastructure, and persistence projects shared by API hosts.
- `tests/DietTime.Meal.Api.IntegrationTests`: integration coverage for the meal API host.
- `DietTime.Application`: use-case interfaces, validation, localization, availability, calendar, selection, and pricing rules.
- `DietTime.Domain`: meal and plan entities matching the supplied schema.
- `DietTime.Persistence`: one `DietTimeDbContext`, Fluent mappings, projected queries, and transactional admin writes.
- `DietTime.Infrastructure`: JWT/refresh-token issuance and server-side S3-compatible object storage integration.
- `DietTime.Contracts`: request, response, envelope, pagination, and error contracts.

## Configuration

Required Railway variables:

```text
ASPNETCORE_ENVIRONMENT=Production
PORT=8080
ConnectionStrings__DefaultConnection=Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true
DTDBCONNECTION=Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true
Storage__PublicBaseUrl=<public bucket/CDN base URL>
Storage__MaxUploadSizeBytes=10485760
Api__PublicBaseUrl=https://your-api-domain
AWS_ENDPOINT_URL=<S3-compatible endpoint>
AWS_S3_BUCKET_NAME=<bucket>
AWS_ACCESS_KEY_ID=<secret>
AWS_SECRET_ACCESS_KEY=<secret>
AWS_DEFAULT_REGION=auto
AWS_S3_URL_STYLE=virtual
Cors__AllowedOrigins__0=https://your-flutter-web-origin.example
```

JWT bearer validation is enabled outside Development. Development keeps the temporary admin/dietitian/content-manager handler for catalogue work, while `/auth/me` explicitly validates bearer tokens so the real login flow can also be tested locally.

Do not use the checked-in development placeholders as credentials. Secret values belong in Railway variables or .NET user secrets.

Authentication uses one rotating session contract:

```text
POST /api/v1/auth/register
POST /api/v1/auth/login
POST /api/v1/auth/phone-otp
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
GET  /api/v1/auth/me
```

`DTDBCONNECTION` takes precedence when both database variables are present. It accepts either an Npgsql connection string or a `postgres://`/`postgresql://` URI. Keep only one database variable configured in each deployed environment when possible.

Login, registration, and refresh return an `ApiResponse<AuthSessionResponse>`. Refresh tokens are hashed in PostgreSQL, rotated on every refresh, and revoked on logout. Browser clients receive the refresh token in an HttpOnly cookie (`Secure` outside Development); native clients store the returned refresh token in platform secure storage. Access tokens remain short-lived and are sent as bearer tokens. The default refresh-session lifetime is 3,650 days so native sessions survive normal app restarts; deleting the app removes its securely stored token. Native clients must call `POST /api/v1/auth/refresh` before expiry or after an access-token 401 and persist the newly rotated refresh token returned by that call.

Temporary phone login is available through `POST /api/v1/auth/phone-otp`. Send an E.164 number such as `+97455555555` with the configured test OTP; the first successful login creates the Identity user and customer profile. Development uses `123456`. In other environments, explicitly set `PhoneOtp__Enabled=true` and provide `PhoneOtp__TestCode` through secrets. Keep it disabled in production until `IPhoneOtpVerifier` is replaced with the Twilio implementation.

Apply `20260730000000_AddRefreshSessions` to environments that do not already have the `refresh_tokens` table before deploying this session flow.

## Database assumptions

The supplied meal tables, indexes, `pgcrypto`/`gen_random_uuid()`, and `set_updated_at()` trigger function already exist. Entity mappings preserve their names and constraints.

## PostgreSQL scripts

PostgreSQL database scripts are stored under [`database/scripts`](database/scripts). The current script is:

- [`database/scripts/pg_dump.sql`](database/scripts/pg_dump.sql): schema-only snapshot containing the public tables, primary and foreign keys, indexes, checks, Identity tables, and sequences used by the API. It does not contain seed or customer data.

Restore the snapshot into a new, empty PostgreSQL database from the repository root:

```powershell
psql --set ON_ERROR_STOP=on --dbname "postgresql://postgres:<password>@localhost:5432/diet_time" --file "database/scripts/pg_dump.sql"
```

The dump is not idempotent: running it against a database that already contains these objects will fail with duplicate-object errors. It also contains `OWNER TO postgres` statements, so restore it as the `postgres` role or adjust the ownership statements for the target environment. Ensure the target database provides `gen_random_uuid()` before restoring.

Treat the dump as a database snapshot rather than an EF Core migration. Review and back up an existing environment before applying database changes; do not run the full dump over production data.

Meal-plan pricing uses `meal_plan_price_packages.code` as the duration identifier. The corresponding code is stored in `meal_plan_prices.package_code`; there is no separate `durations` table in this schema.

Recurring menu configuration uses the existing meal-plan template editor and the `meal_plan_template_days`, `meal_plan_template_slots`, and `meal_plan_slot_options` tables. The schema does not use separate `meal_plan_weekdays` or `meal_plan_day_items` tables.

## Public and customer endpoints

```text
GET  /api/v1/meal-plan-categories
GET  /api/v1/meal-plans/{planId}
GET  /api/v1/meal-plans/{planId}/calendar
GET  /api/v1/meal-plans/{planId}/meals
GET  /api/v1/meals/{mealItemId}
GET  /api/v1/meal-types
GET  /api/v1/meals/search
POST /api/v1/meal-selections/validate        (JWT)
POST /api/v1/auth/register
POST /api/v1/auth/login
POST /api/v1/auth/phone-otp
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
GET  /api/v1/auth/me
POST /api/v1/customer-profiles/{customerProfileId}/addresses        (JWT, profile owner)
GET  /api/v1/customer-profiles/{customerProfileId}/addresses         (JWT, profile owner)
GET  /api/v1/customer-profiles/{customerProfileId}/addresses/{id}    (JWT, profile owner)
PUT  /api/v1/customer-profiles/{customerProfileId}/addresses/{id}    (JWT, profile owner)
DELETE /api/v1/customer-profiles/{customerProfileId}/addresses/{id} (JWT, profile owner)
PATCH /api/v1/customer-profiles/{customerProfileId}/addresses/{id}/default (JWT, profile owner)
GET  /api/v1/delivery-time-slots
```

Admin endpoints from the brief are under `/api/v1/admin` and require `Admin`, `Dietitian`, or `ContentManager`. Meal images are uploaded as multipart form data to `POST /api/v1/admin/meals/{mealId}/media/upload`; send `mediaType=IMAGE` for the original or `mediaType=THUMBNAIL` for its thumbnail. An original image must exist before its thumbnail is uploaded. The persisted `public_url` and `thumbnail_url` point to the public API media route (`GET /api/v1/media/{objectKey}`), while storage credentials and object URLs remain server-side. Set `Api__PublicBaseUrl` to the externally reachable API origin in deployed environments.

Meal types support `GET /api/v1/admin/meal-types`, `POST /api/v1/admin/meal-types`
and `PUT /api/v1/admin/meal-types/{id}` using the existing meal-type tables.
Create accepts `code`, `nameEn`, optional `nameAr`, `displayOrder` (default `0`)
and `isActive` (default `true`). Codes are normalized to uppercase; names allow
up to 100 characters. Creation returns `201` with the ID, or `409` for an existing
code. Deploy both the API and admin SPA for the Add Meal Type form fix; no schema
change is required.

Swagger is available outside Production at `/swagger`; liveness/database health is `/health`.

## Twilio WhatsApp messages

Twilio WhatsApp content templates are supported through the Admin-only
`POST /api/admin/integrations/whatsapp/twilio/messages` endpoint. Configure provider
credentials through deployment secrets (never commit the auth token):

```text
TwilioWhatsApp__Enabled=true
TwilioWhatsApp__AccountSid=<Twilio account SID>
TwilioWhatsApp__AuthToken=<Twilio auth token>
TwilioWhatsApp__FromNumber=+14155238886
TwilioWhatsApp__OperationsNumber=+97474452435
TwilioWhatsApp__NewOrderContentSid=<approved new-order Content SID beginning with HX>
```

New orders return `PENDING_APPROVAL` without waiting for Twilio. Notification events
are saved in the same PostgreSQL transaction as the order or staff action. The bounded
in-memory channel only wakes the background worker; its five-second database poll
recovers work after a restart or a full channel. Provider failures cannot change order
status or block checkout and dashboard refresh. The worker claims events with leases,
uses a separate dependency-injection scope, and has a 60-second send deadline.

Configure approved customer templates independently (none are supplied by default):

```text
OrderWhatsAppTemplates__ReceivedContentSid=HX...
OrderWhatsAppTemplates__ApprovedContentSid=HX...
OrderWhatsAppTemplates__RejectedContentSid=HX...
OrderWhatsAppTemplates__OnHoldContentSid=HX...
OrderWhatsAppTemplates__PaymentReceivedContentSid=HX...
```

Each customer template uses variables `1` order number, `2` plan name, `3` event label,
and `4` message or staff reason. The existing operations template retains its eleven
variables. Missing configuration leaves durable events in `WAITING_CONFIGURATION`,
checked again after five minutes. No free-form WhatsApp fallback is sent. Transient
provider failures are retried; permanent failures are logged and retained as `FAILED`.
`SENT` means Twilio accepted the message, not that WhatsApp delivered it. Unique
history/audience keys prevent duplicate events on action retries, but a network failure
after provider acceptance can still cause duplicate delivery on transport retry.

Example request body:

```json
{
  "to": "+97474452435",
  "contentSid": "HXb5b62575e6e4ff6129ad7c8efe1f983e",
  "contentVariables": { "1": "12/1", "2": "3pm" }
}
```

## Staff order approval

Apply the EF migration `20261030020000_StaffOrderApproval` as the schema owner
before deploying the API and clients if these schema changes have not already been
applied. The standalone SQL script has been removed.
It extends the existing orders and status-history tables and adds a durable notification
outbox. Existing status, payment, price and service records are not rewritten and no
historical notification events are generated. Legacy `PENDING` orders remain eligible
for explicit review; other legacy statuses retain their records and require operational
review where no supported transition exists.

New orders start in `PENDING_APPROVAL`; their purchased schedule is saved, but service
days and default meals are initialized only after approval and payment eligibility.
The React Admin Orders screen supports review, reasons, history, payment recording,
explicit date revision and concurrency conflicts. Flutter shows Order Submitted,
Pending Confirmation, hold/rejection reasons and separate payment status, and refreshes
orders when returning to the app and periodically while viewing them.

For staff approval, sign in with an **Admin** account and open **Daily Orders →
Review / Approve → Approve → Confirm**. The separate `/orders/daily` screen lists
orders by **meal service date**, defaults to today in Qatar, and includes all order
statuses. **All dates** removes the date filter, and **All pending approvals** shows
the pending backlog across every date. Date filtering uses the purchased service
period and weekdays, so pending orders appear before fulfillment records exist.
The sidebar and approval controls use the existing `/orders` screen permission.
For older databases without an Orders menu definition, the API supplies that
permission to existing Admin accounts without writing configuration or requiring
SQL. Explicitly configured inactive Orders entries remain inactive; non-Admin
permissions are unchanged. Deploy the API and SPA, then sign out and back in to
refresh permissions. The dashboard's
**Review pending orders** shortcut opens the same screen, and its review count now
defaults to `PENDING_APPROVAL` even with the older empty status configuration.

The React dashboard and Delivery Calendar display a compact orange **Pending
confirmation orders** banner only when the live pending count is greater than zero.
It contains the count and links to Orders, with details and approval actions inside
the Orders screen. The count refreshes every 30 seconds, on window focus and after
staff decisions, independently of calendar filters. No banner appears while loading,
when there are no pending orders, or without Orders access; load failures have a
separate retryable error message.

To record a received payment, open **Daily Orders → Review → Record Payment Status**,
select **PAID**, enter the receipt/payment reference, and **Confirm**. This records
the verified payment without collecting funds or approving a pending order. These
actions currently require an Admin account with Orders write access.

Admin-only endpoints under `/api/admin/orders`:

- `GET /` lists orders with optional `status`, `serviceDate` (`yyyy-MM-dd`),
  `pageNumber` and `pageSize`. Service-date filtering happens before pagination.
- `GET /{id}` returns the purchased order snapshot and audit history.
- `POST /{id}/approve`, `/reject`
  accept `{ "expectedRowVersion": 1, "reason": "..." }`.
- `POST /{id}/payment` also accepts `paymentStatus`; a verification reference/reason
  is mandatory. This records manual payment/refund status without moving funds.
- `POST /{id}/revise-dates` accepts `expectedRowVersion`, `startDate`, `serviceWeekdays` and
  `reason`. It preserves purchased duration and requires explicit agreement; approval
  never silently moves dates.

Customer `POST /api/v1/orders/{id}/cancel` checks ownership and accepts the current
row version and reason. Cancellation is allowed during review or before confirmed
service/preparation starts; later cancellation requires contacting Diet Time.

Staff decisions are limited to pending review → confirmed/rejected. React shows only
Approve and Reject as status actions, with a separate Record Payment Status button
in the fixed order-dialog footer alongside Close. Removed admin routes (`hold`, `resume`, `cancel`, `activate`,
`complete`) are unavailable, and the service rejects unsupported direct calls.
Customer cancellation retains its existing ownership, date and preparation safeguards.
The existing background worker automatically activates confirmed, payment-eligible
orders whose purchased service period has started and has not ended, with an initialized
service schedule. It checks Qatar dates on startup and every 15 seconds; activation
is row-locked, versioned and audited once, including across concurrent workers.
Approval and activation never rewrite service dates. Completed, cancelled, rejected,
held and other legacy statuses remain unchanged. No schema migration is needed.
Explicit date revision remains an operational API for resolving agreed schedule
conflicts; it is separate from approval and is not offered as a workflow button.
Reasons remain required for rejection, cancellation, manual payments and revised dates.
Row locks and version checks return HTTP 409 for conflicting or repeated actions
without duplicating service records. Flutter keeps pending cards amber and confirmed/
active cards dark green, uses Pending Approval, and explains rejected orders.
Payment status is independent: approval never marks an order paid. Paid rejected or
cancelled orders become `REFUND_PENDING`; refunds require the existing manual process.

`Orders__RequiresPrepayment=false` preserves the manual/offline fulfillment policy.
Set it to `true` to require recorded payment before fulfillment for newly placed orders;
the policy is snapshotted on each order. Confirmed prepaid orders can await payment.
Late payment before service initialization blocks fulfillment until explicit date revision.
Pending, held, rejected, cancelled and payment-blocked orders cannot enter kitchen,
dispatch or meal confirmation flows. Takeaway orders remain excluded from dispatch.
Overlapping orders retain independent schedules, prices and status histories.

Implementation files:

- API: `StaffOrdersController.cs`, `OrdersController.cs`, `Program.cs` and
  `OrderNotificationWorker.cs` expose review/cancellation and run outbox processing.
- Domain/application/contracts: `Entities.cs`, `OrderNotification.cs`,
  `OrderWorkflowPolicy.cs`, `WhatsApp.cs`, `ApiContracts.cs` and
  `OrderApprovalContracts.cs` define status policy, audit, payment and response data.
- Persistence: `StaffOrderService.cs`, `OrderService.cs`, `OrderConfigurations.cs`,
  `DietTimeDbContext.cs`, `OrderNotificationEvents.cs` and
  `OrderWhatsAppNotificationSender.cs` implement transactional review and notifications.
  `UpcomingMealService.cs`, `TomorrowMealService.cs`, `AdminUpcomingMealsController.cs`,
  `OperationsDashboardService.cs` and `AdminDeliveryCalendarService.cs` gate fulfillment.
- Admin SPA: `staffOrdersApi.ts`, `features/orders/OrdersPage.tsx` and its test,
  `app/router.tsx` and `layouts/Sidebar.tsx` provide the staff workflow.
- Flutter: `orders_repository.dart`, `order_models.dart`, `order_status.dart`,
  `order_placed_screen.dart`, `plan_summary_screen.dart`,
  `customer_dashboard_screen.dart`, `order_details_screen.dart` and
  `authenticated_landing_screen.dart` handle submission, review and refresh.
- Tests: `StaffOrderApprovalTests.cs`, `StaffApprovalMigrationTests.cs` and
  `OrderWorkflowPolicyTests.cs` cover the new policy. Existing order notification,
  package selection/delivery, questionnaire, menu image, dashboard and checkout tests
  were updated for explicit approval rather than automatic confirmation.

Validation: 210 .NET unit tests, 58 selected PostgreSQL-backed API regression tests,
318 Flutter tests and 98 admin tests passed. Five workflow tests were rerun after
expanding their edge-case assertions and passed. Admin type checking/lint and Flutter
analysis passed. Three older integration tests still reference previously removed
standalone SQL files (`20261026_package_discounted_price.sql`,
`tomorrow_menu_diagnostics.sql`, `20261026_address_delivery_slots.sql`); those tests
were excluded from the 58-test regression run. No production database was changed
and live WhatsApp delivery was not tested.

## Checkout availability diagnostics

The checkout previously labelled almost every HTTP 409 from `POST /api/v1/orders`
as an unavailable plan, including database and concurrency failures. Summary preparation
also skipped revalidation whenever a price ID was already present. Price metadata did
not retain the API's separate plan/package identities, and an older asynchronous
configuration response could replace a newer selection. The shared price predicate
used UTC's date while order validation used Qatar's date, causing disagreement around
midnight for date-limited plans. These confirmed code defects are fixed.

Read-only inspection of the Flutter-configured staging API on 2026-10-30 found one
available plan, five meal configurations and 15 distinct prices. The reported option
was returned as active/available, with these separate identifiers:

| Record | Identifier/value |
| --- | --- |
| Complete Balance template (`PLN_003`) | `81012dd5-6453-4dc9-87ae-d2c243e5bdcf` |
| 3 Meals + 1 Snack package option | `d8967227-085d-43b7-839c-c70591d05a82` |
| Six-day price | `2ff05101-2f41-41cf-a3dc-9589c75501ae` |
| Duration code / amount / delivery | `SIX_DAYS` / QAR 699 / disabled |

Both code-based and ID-based purchase-options requests resolved to that same plan.
The subsequent PostgreSQL log supplied on 2026-10-31 identified the save failure:
SQLSTATE `23514`, table `order_status_history`, constraint
`ck_order_status_history_status`. The legacy history constraint accepts only
`PENDING`, `CONFIRMED`, `ACTIVE`, `PAUSED`, `COMPLETED` and `CANCELLED`.
Order placement now inserts `PENDING_APPROVAL`, so the entire order transaction rolls
back even when the selected plan and price are valid. The earlier approval migration
extended the order constraint but omitted the history constraint.

The history constraint fix was applied directly to the database. The standalone script
and redundant EF migration were removed; no additional migration is required for this
fix. The corrected check permits the approval workflow and legacy statuses while
preserving all order/history rows. `NOT VALID` avoids a historical-table scan while
enforcing the constraint on all new writes. Keep PostgreSQL error-detail redaction enabled.
Database metadata logs now include SQLSTATE, schema, table, column and
constraint in Production as well as Development, without logging row values.

The history-constraint regression recreates the legacy check, reproduces SQLSTATE
`23514` and verifies that the failed purchase rolls back without notification events.
After applying the schema correction twice, submission, rejection and approval
succeed and historical records remain unchanged. All six focused migration/approval
tests passed. The test retains the correction as an isolated database fixture and does
not depend on migration files. The agent has not modified the live database.

Flutter now refreshes the exact selected plan/package/price, retains explicit API
identities and ignores stale responses after switching plans or packages. Unavailable
configurations disable submission with Retry and context-aware package/plan review.
Valid refreshes retain customer, address, branch and schedule selections. Unknown
database/network failures no longer imply an inactive plan. Recovery messages are
available in English and Arabic; loading labels fit narrow screens.

The existing order endpoint returns specific codes including `plan_not_found`,
`plan_inactive`, `plan_not_published`, `plan_not_effective`, `plan_version_changed`,
`package_not_available`, `pricing_not_available` and `invalid_plan_configuration`.
Database failures retain their HTTP status with `database_conflict` or
`concurrency_conflict`. Structured validation logs include plan ID, resolved package
ID, price ID, duration and the failed condition; database logs also identify
the PostgreSQL constraint. Flutter debug logs include selected IDs, response status
and error code without tokens, customer addresses or credentials.

Changes are in `OrderService.cs`, `OrdersController.cs`, `ApiMiddleware.cs`,
`PurchasablePlanPrices.cs`, the order result in `Abstractions.cs`, Flutter's checkout
controller/state/summary, package model, order request logging, button loading layout
and localization resources. Tests cover relationship failures, midnight availability,
switching/returning, stale responses, independent IDs, recovery and legitimate inactive
configuration rejection. The history-constraint correction above resolves the reported
`23514` failure; no price update or order approval policy change is
required. Rebuild/deploy the API and Flutter app to use the checkout changes.

Validation: 340 Flutter tests, 210 .NET unit tests and 80 selected API/availability
tests passed, with clean Dart analysis. The PostgreSQL matrix submitted 12 valid fixture
combinations across two plans, two packages, three durations and both takeaway branches;
existing delivery eligibility and approval regressions also passed. The three older
tests referencing previously removed SQL files remain excluded.

## Customer nutrition, menu navigation and language

The dashboard's Explore Menu shortcut pushes the existing `/menu` route with a
dashboard origin. The same menu screen keeps its plan, date, week and ordering
behavior. Back to Dashboard appears only for an authenticated dashboard-origin
visit with an existing return stack; it pops that stack. Guest and other entry
points keep their original back behavior and Start Plan action.

Existing tomorrow-menu and daily-menu responses now include optional `nutrition`
using the existing `NutritionResponse`, and `quantity` on each dish. The existing
selection schema assigns one serving per dish; nutrition values are already stored
per configured serving in the meal editor. No 100 g scaling is assumed. Calories,
macros, serving size and available additional nutrients come from existing tables.
Available, selected and assigned dishes reuse the existing meal detail viewer for
ingredients and allergens, including when selection is locked. Daily totals use
only the current order/day's selected servings and update with unsaved draft changes.
Missing values remain unknown rather than zero; incomplete nutrient totals are
labelled unavailable. Genuine recorded zero values are retained.

The dashboard English/Arabic control uses the existing language controller, shared
preferences, ARB resources and generated Flutter localizations. Static labels across
customer screens, status messages, navigation and dates follow the selected locale;
Arabic uses the existing RTL support. Local preference survives app restarts and
logout. Authenticated changes also best-effort synchronize the existing profile field
through `PATCH /api/v1/customer/profile/preferred-language` with `{ "language": "ar" }`
(or `en`). This endpoint updates only the authenticated customer's existing profile
language and audit fields, without replacing questionnaire answers. Backend meal
translations are reused with English fallback. Stored order snapshots and customer
or staff-entered content are not automatically translated.

API files: `UpcomingMealContracts.cs`, `UpcomingMealService.cs`, `ApiContracts.cs`,
`Abstractions.cs`, `CustomerProfileController.cs` and `CustomerProfileService.cs`.
Flutter changes cover the existing router/menu/detail viewer, daily and tomorrow meal
cards, dashboard shortcut, language controller/control, nutrition models/widgets and
customer presentation labels through `customer_copy.dart` and the existing ARB files.
No database migration or new menu endpoint is required. Order approval, payment,
booking, cutoff and meal-selection policies are unchanged.

Validation: all 330 Flutter tests passed, followed by a nine-test check of the final
dashboard language and address-label changes; Dart analysis reports no issues.
All 210 .NET unit tests and 60 selected PostgreSQL-backed API regression tests passed.
The two new API tests also passed after adding per-field English description fallback.
Coverage includes guest/authenticated/dashboard-origin navigation, unchanged Start Plan
behavior, back-stack return, Arabic RTL and saved locale restoration, real serving
quantities, incomplete totals, draft total updates, locked meal details, confirmed
nutrition and profile ownership. The same three older tests referencing previously
removed SQL files remain excluded, as noted in the staff workflow validation above.
Tests use a disposable local database; no production data was changed.

## Calendar behavior

Meal-plan template days use stable uppercase weekday codes in `day_of_week` and are ordered by `display_order`. Menu lookup always selects the weekday matching the actual delivery date; numbered rolling-day progression is not supported.

The delivery preparation summary is available at
`GET /api/v1/admin/delivery-calendar/{date}/preparation-summary` (with an unversioned
alias for the operations SPA). The current schema validates menu choices but does not
persist a per-order, per-service-date selection. Preparation therefore uses only the
configured default menu item for each active meal-plan weekday slot; it does not guess
among non-default options.

## Response example

```json
{
  "data": [{
    "slotOptionId": "00000000-0000-0000-0000-000000000014",
    "slotId": "00000000-0000-0000-0000-000000000013",
    "mealItemId": "00000000-0000-0000-0000-000000000006",
    "mealType": { "id": "00000000-0000-0000-0000-000000000002", "code": "BREAKFAST", "name": "Breakfast", "displayOrder": 1 },
    "name": "Coconut Chia Pudding",
    "thumbnailUrl": "https://cdn.example/meals/coconut-chia-thumb.webp",
    "caloriesKcal": 324,
    "proteinGrams": 9,
    "carbohydratesGrams": 22,
    "fatGrams": 24,
    "additionalPrice": 0,
    "currencyCode": "QAR",
    "isDefault": true,
    "isAvailable": true,
    "allergenCodes": ["TREE_NUTS"]
  }],
  "meta": { "page": 1, "pageSize": 20, "totalCount": 1, "totalPages": 1 },
  "errors": []
}
```

## Build, test, and deploy

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
docker build -t diet-time-api .
```

PostgreSQL integration tests use Testcontainers and are opt-in because Docker is not present in every build agent: set `RUN_INTEGRATION_TESTS=true` before `dotnet test`. Railway deployment needs this repository, the `Dockerfile`, the variables above, and a health-check path of `/health`.

## Known schema limitations

- No customer subscription, delivery-date, preference, health-record, or allergen-profile tables were supplied. Validation is stateless and returns an allergen-profile warning; it does not persist a subscription.
- A future subscription schema needs subscription header/status, customer/plan/price references, service dates, daily selections, snapshotted prices/currency, cutoff/audit timestamps, and uniqueness/idempotency constraints.
- There is no plan-media table, so plan categories temporarily use a primary meal image.
- Admin MFA is ready through Identity token providers and `two_factor_enabled`, but enrollment/challenge endpoints are not included.
- Micronutrients beyond the nutrition columns in the supplied schema cannot be returned until a micronutrient table or JSON contract is added.
## Operations dashboard data limitations

`GET /api/admin/dashboard/operations` derives scheduled deliveries from each order's
stored service-date range and configured delivery weekdays. The current schema has no
delivery execution record or completion status, so `completedDeliveries` is returned as
`null`. It also has no route, driver, or per-delivery assignment data from which a genuine
scheduling conflict can be identified, so `deliveryConflicts` is returned as `0`.

## Package delivery and fulfillment

Customer order detail and list responses include nullable `mealCount` and `snackCount`.
These aggregate purchased `order_meals` quantities using meal-type codes, independent
of localized names and subsequent price edits. Existing meal rows and identifiers remain
unchanged; no database migration is needed for these response fields.

The customer UI uses aggregate meal/snack labels, separate calendar date/count indicators,
and the existing inline checkout calendar and branch selection. Meal-plan selection omits
daily prices and calorie estimates; package prices and discounts remain backend-driven.
The wellness BMI scale uses proportional adult category segments and a shared numeric
domain for its marker. Youth profiles show an age/sex-specific assessment note rather
than adult categories; no pediatric percentiles or missing nutrition targets are invented.

Delivery availability belongs to the existing duration master, `public.meal_plan_price_packages`,
not the meal/snack quantity options. The duration Add/Edit form exposes **Enable Delivery**.
New packages default to enabled. Customer purchase options and duration APIs return `isDeliveryEnabled`.

Apply the EF migration `20261030000000_EnablePackageDelivery` as the schema owner before
deploying the API and clients against the intended database. The migration uses bounded lock
and statement timeouts; retry during a quieter period if a timeout rolls it back. It seeds the
existing 6-day duration as takeaway only and leaves other durations, including 12 and 24 days,
enabled. Rerunning it preserves subsequent administrator choices.

Order requests accept `fulfillmentMethod: "TAKEAWAY"` or `"HOME_DELIVERY"`. Omitting it retains
Home Delivery behavior for older clients. Takeaway accepts null/omitted `customerAddressId` and
`deliveryTimeSlotId`; Home Delivery still requires both. `deliveryDays` remains the service-day
schedule for either method. The API validates the selected package's current setting and stores
fulfillment on the order. Order details and summaries return that snapshot; takeaway details
have no delivery address or slot. Package edits never recalculate confirmed orders.

The dashboard and order calendar show both fulfillment methods. Kitchen preparation includes
both; `/api/admin/dashboard/operations/deliveries` returns Home Delivery orders only. Existing
orders retain Home Delivery fulfillment. Migration rollback is intentionally a reviewed forward
change, so fulfillment history cannot be discarded by an automatic EF downgrade.

Takeaway branch checkout requires the additional EF migration
`20261030010000_AddTakeawayBranches` before deploying the API to a database without
the branch schema. It seeds Izghawa and Azizia idempotently, leaves unknown
addresses null, and preserve existing orders and any subsequent branch edits.
Administrators can configure `address`, `is_active`, `updated_at` and `updated_by` in
`public.takeaway_branches`; inactive branches are not offered for new orders.

Authenticated `GET /api/v1/takeaway-branches` returns active branches. New takeaway orders
must send an active `takeawayBranchId`; Home Delivery ignores this field. The order stores
the branch ID and a name/address snapshot, returned as `takeawayBranch` in order details
and summaries. Calendar, operations and kitchen reports also expose the saved branch.
An existing takeaway order without a branch remains readable; its branch is not inferred.

Checkout selects dates in the existing calendar bottom sheet on Your Plan Details.
Requests can include `serviceDates`; the API verifies them against the purchased duration,
start date and service weekdays and persists the schedule in `subscription_service_days`.
Older clients may omit this array; the server still generates the authoritative schedule.

Delivery regression tests are in `DeliveryEligibilityTests.cs` and `PackageDeliveryIntegrationTests.cs`.
To run the PostgreSQL tests, set `UPCOMING_MEALS_TEST_CONNECTION` to a local test server whose
user can create databases, then filter `dotnet test` to `Package_delivery_is_authoritative`,
`Delivery_migration_seeds_once`, or `Duration_create_update`. Each test creates and drops its
own isolated database.

## Questionnaire after login

Guests can browse meal plans and select a package without a profile. Flutter keeps the
selected package in its checkout draft while OTP login and questionnaire screens run.
Checkout loads the authenticated customer's saved profile, restores partial answers, and
requests the questionnaire only when required information is incomplete. A cached complete
profile skips this loading step. Start-date and fulfillment selection follow completion.

The existing `GET` and `PUT /api/v1/customer/profile` endpoints remain responsible for
questionnaire persistence. Ownership comes from the authenticated user's claims; a supplied
customer ID cannot select another customer's profile. Responses now include
`isQuestionnaireComplete`, `nextStepCode`, and `shouldShowOnboarding`. Completion requires
valid mandatory answers and completed onboarding status. Older completed profiles retain
compatibility with the questionnaire confirmation flags introduced later.

Partial updates preserve omitted scalar answers and omitted preference/allergen collections.
Explicitly supplied collections replace the corresponding saved selections, including empty
arrays for no selections. Existing profile locking and the unique user mapping prevent
duplicate profiles. No database migration or additional questionnaire tables are required.

New order submissions with incomplete questionnaires return HTTP 400 with code
`questionnaire_incomplete`. Existing order reads and idempotent replays continue to work.
Ordinary and restored login open the dashboard, including customers with no current
meal plans. Subscription browsing stays available from the dashboard. Explicit checkout intent continues the selected order, allowing additional
orders without repeating completed questionnaires. Profile exposes questionnaire editing
as **Adjust My Plan**. **Not now** in that Profile flow returns to Profile without
creating a deferred draft or marking an incomplete questionnaire complete. Initial
onboarding retains its separate browsing/checkout navigation behavior.

The customer app hides order IDs/references on dashboard cards, meal cards, the
calendar, order details, confirmation and support messages. IDs remain in models,
API requests and internal staff workflows. The dashboard has one New Plan quick
action; My Plan opens the single plan's details (including pending plans), or the
existing order list when several plans are present.

Regression coverage is in `QuestionnaireCompletionTests.cs`,
`QuestionnaireFlowIntegrationTests.cs`, and Flutter's `questionnaire_checkout_flow_test.dart`
and updated journey tests.

## API audit trail

The audit SQL setup has already been applied and its manual script removed.
The API requires `audit_events` and `audit_event_targets` in the target database;
existing meal-item audit logs and order-status history are preserved. There is
no backfill of actions that occurred before deployment.

Every completed request under `/api` is recorded, including reads, automatic
refreshes, mutations and rejected/failed requests. CORS preflight, `/health` and
non-API assets are excluded. Screen navigation and unsaved edits are not tracked.
Records include the authenticated user ID/name, server UTC timestamp, application,
controller/action, HTTP result, safe affected-record identifiers and request trace.
Successful login/refresh responses identify the authenticated user; unauthenticated
attempts remain anonymous. User/resource deletion does not cascade into audit history.

The updated Flutter and React clients send `X-Client-App` labels. These labels are
informational, not authorization credentials; actor identity comes from validated
authentication. Older clients still produce audit records labelled `API`.
Passwords, OTPs, access/refresh/reset tokens, request/response bodies, query values,
questionnaire answers and before/after field contents are not copied into audit tables.
This is an API activity history, not an audit of direct database changes or workers.

Admins can use **Audit History** to filter by user name/ID, application, area,
action, result and date. UTC timestamps and inclusive date filters are displayed
and interpreted in Qatar local time. The read-only endpoint is
`GET /api/v1/admin/audit` (also `/api/admin/audit`), with `page`, `pageSize`,
`entityType`, `action`, `user`, `userId`, `source`, `outcome`, `from`, and `until`.
There are no audit update/delete endpoints or automatic history deletion.

Audit persistence uses an independent scope with a three-second timeout. If storage
fails, `AUDIT_WRITE_FAILED` is logged with safe metadata and the business response is
preserved. Monitor these errors: failed audit inserts remain in application logs,
not in the audit tables, and are not automatically replayed. Audit HTTP outcomes
describe request results, not a field-by-field database transaction history.

Deploy the API, React SPA and Flutter updates with the audit tables available. Existing
Admin accounts receive an Audit History navigation fallback when an older menu
catalogue has no audit entry; explicitly inactive entries remain inactive.

### Menu browsing calendar and rotation

Menu browsing uses consecutive seven-day weeks from the selected template's
`ValidFrom`, falling back to `MenuRotation:AnchorDate` only when `ValidFrom` is
unset. The menu week is `1 + (whole weeks since that date % CycleWeeks)`; it
repeats after the existing configured cycle length, without resetting monthly or
using the customer's subscription start. The November 2026 examples in
`MenuBrowsingCalendarTests.cs` and `MenuBrowsingMappingTests.cs` explicitly seed a
November 1 start; they do not assert that this is the deployed business anchor.

The existing guest home/menu and public plan calendar/menu endpoints share this
browsing mapping. All dates in the browsing window are selectable, including
unconfigured days. `isAvailable` on browsing calendar entries means selectable,
not delivery eligibility. Missing week/day configurations, or dates outside the
template validity window, return empty menus without substituting another day.
Unconfigured public calendar dates have no `templateDayId`.

Meal-type tabs and API slots follow `meal_types.display_order`, with the stable
meal-type code breaking ties, then slot/option display order. Translated names
and meal-type IDs do not determine the order. Flutter retains a selected type
while it remains present; otherwise it selects the first configured type. The
existing All filter remains available. Database weekday values remain named
strings such as `SUNDAY` and `SATURDAY`, with explicit .NET date conversion.
Subscription service-day rotation and Friday delivery restrictions are unchanged.
No database schema change is needed; deploy the API and Flutter changes together.

### Customer app tour

The manual customer app tour SQL script has been removed. The API requires
`customer_profiles.is_app_tour_enabled` with a database default of `FALSE`,
retaining existing customers' opt-out. New
profiles explicitly start enabled in both existing profile-creation paths. Login,
profile reads and questionnaire updates do not reset this preference.

The authenticated profile GET includes `isAppTourEnabled`.
`PATCH /api/v1/customer/profile/app-tour` accepts `{ "isAppTourEnabled": false }`
and returns the saved value, timestamp and row version. Identity comes from the
access token; this operation updates only that customer's preference and standard
update metadata. Missing values are rejected.

Flutter starts the spotlight walkthrough once the authenticated dashboard is ready.
It highlights existing widgets and navigates through existing routes/tabs. The
welcome step offers optional detailed guidance; features unavailable to that
customer are skipped. Individual dashboard steps highlight Menu, language,
View Details, Choose Plan/Order Another Plan, the available tomorrow-meal button,
and each bottom navigation button. Tooltips include feature icons, progress and
explicit hints when Next opens another screen. Customers without plan cards or
active plans skip the related order/meal steps before the walkthrough starts.
The tour blocks interactions with underlying controls and
never places orders, edits questionnaire answers or confirms meal selections.
Checkout is explained from the existing plan list without creating a purchase draft.
Tour text supports English and Arabic with the current application locale and RTL.

Profile's **Enable App Tour** switch saves the customer preference; return to Home
after enabling it to replay. Skip and Finish immediately dismiss the overlay and
return to the same dashboard, then save `false`. On a failed save, the previous
saved switch value remains visible and a retry action is shown. There is no shared
device completion flag. Account changes cancel the tour and discard old in-flight
save results. Existing customers remain opted out until they enable the switch.

Regression coverage: `test/app_tour_test.dart` in the Flutter repository and
`CustomerAppTourTests.cs` in the API integration tests. The latter uses
`UPCOMING_MEALS_TEST_CONNECTION` against an isolated PostgreSQL database and
verifies ownership, profile creation and preference updates.

## Staff notification inbox

Persistent, recipient-scoped order notifications back the existing React Admin header bell. No separate notification page is added. The React Admin project README documents the header, dropdown and 45-second client polling.

**Events and recipients**

| Event | Admin | Existing navigation |
| --- | --- | --- |
| New order, Pending Confirmation | Yes | Order review/details |
| Order approved | Yes | Order details |
| Order rejected or cancelled | Yes | Order details |
| Delivery overdue | Yes | Calendar date and service-day row |
| Delivered | Yes | Calendar date and service-day row |
| Collected | Yes | Calendar date and service-day row |

The existing durable WhatsApp queue has no per-recipient inbox/read state. The implementation reuses the existing order-status history and service-day fulfillment audits as event sources, leaving that queue unchanged. A SaveChanges capture adds only the specified order/fulfillment events to a small durable outbox in the same database transaction as the source change. Unrelated updates, payment-only history and automatic activation do not emit inbox events. No placement, approval-validation, payment or delivery-transition rules are changed.

An ASP.NET Core background worker runs at startup and every 30 seconds, independent of browsers. It projects outbox events to currently assigned staff recipients and detects overdue home-delivery days using the existing DailyFulfillmentTracking eligibility/deadline rules and Asia/Qatar timezone. Takeaway has no configured collection deadline and is not treated as overdue. Future or already fulfilled days do not produce overdue alerts.

Outbox source keys and per-recipient delivery keys are unique. Projection uses transactional row locks with SKIP LOCKED; notification inserts use ON CONFLICT DO NOTHING. A crash or multiple API instances cannot create duplicate recipient notifications. Repeated mark-read requests preserve the original read time. Notifications are ordered by event creation time, then ID, newest first.

Notification list responses include an optional customerName derived from the linked order's customer profile and user profile. Titles/messages are enriched at read time with that customer's name, rather than the notification recipient's name. Names are batch-loaded for the current page; missing names retain generic customer wording. Existing notification rows, event times and read timestamps are not rewritten, and no schema change is required. The client refreshes notifications, calendar and order data in place without a page-wide loading overlay.

The customer-specific copy update passed 14 notification-rule unit tests and the API build. The corresponding React update passed 50 targeted tests. Database-backed customer-name lookup still requires the existing opt-in PostgreSQL test environment.

**API**

Both /api/admin and /api/v1/admin route prefixes are supported:

- GET /api/admin/notifications?page=1&pageSize=20 — recipient-scoped items, page metadata, total count; maximum page size 50.
- GET /api/admin/notifications/unread-count — `{ "data": { "unreadCount": 4 } }`.
- PATCH /api/admin/notifications/{id}/read — 204; 404 for another recipient's or an inaccessible notification.
- PATCH /api/admin/notifications/read-all — 204; affects only the authenticated recipient's permitted events.

Only authenticated Admin users can call these endpoints. Identity comes from validated claims; no recipient ID is accepted from requests. Every inbox and read action is scoped to that authenticated recipient. Customer and other non-Admin accounts are rejected by API authorization. No additional role is defined.

Order links reuse /orders?orderId={id} and the existing Admin-only order details screen. Approval, rejection, payment and date-change endpoints retain their existing Admin authorization. Personal notification read receipts are exempted from the client screen-write guard; order mutations remain guarded and API-authorized. No roles or screen permissions are created for this feature.

Service-day links reuse /operations/delivery-calendar?date={yyyy-MM-dd}&serviceDayId={id}. Navigation hooks select the corresponding month/date, reopen the existing drawer and scroll to the row. Calendar layout, fulfillment actions and Preparation Summary are unchanged by this feature.

**Database and deployment**

The API requires the existing staff_notification_events outbox and staff_notifications inbox tables, their deduplication and recipient/unread indexes, and the fulfillment audit fields described in the EF mappings. No migration or standalone SQL script is included for these features; schema setup is managed separately. No live database changes were applied during this work.

Newly occurring order/fulfillment events are captured after deployment; historical order events are not replayed at installation. Existing overdue service days are detected on the next worker pass. If no staff recipients are assigned when an event is dispatched, it is not later backfilled to newly assigned staff.

**Feature files**

- src/BuildingBlocks/DietTime.Domain/StaffNotification.cs
- src/BuildingBlocks/DietTime.Application/StaffNotificationRules.cs
- src/BuildingBlocks/DietTime.Contracts/StaffNotificationContracts.cs
- src/BuildingBlocks/DietTime.Persistence/DietTimeDbContext.cs
- src/BuildingBlocks/DietTime.Persistence/StaffNotificationCapture.cs
- src/BuildingBlocks/DietTime.Persistence/StaffNotificationService.cs
- src/Apis/DietTime.Meal.Api/Controllers/StaffNotificationsController.cs
- src/Apis/DietTime.Meal.Api/Controllers/StaffOrdersController.cs — existing Admin access retained.
- src/Apis/DietTime.Meal.Api/StaffNotificationWorker.cs
- src/Apis/DietTime.Meal.Api/Program.cs
- tests/DietTime.UnitTests/StaffNotificationRulesTests.cs
- tests/DietTime.Meal.Api.IntegrationTests/StaffNotificationTests.cs
- README.md

No Flutter, login/OTP, questionnaire, order-placement, meal-assignment, payment-processing, delivery-confirmation logic or Preparation Summary files were changed for this notification feature. Earlier work in this conversation remains in the working tree.

**Validation recorded for this implementation**

- Backend unit run: 30 passed, including event selection/content/role routing and the existing Qatar deadline regressions.
- API/capture run: 7 passed, including capture deduplication, unrelated-update exclusion, role policies, and actual anonymous/customer rejection for all four notification endpoints.
- PostgreSQL scenarios: 2 skipped because UPCOMING_MEALS_TEST_CONNECTION is unset. These cover recipient isolation, read timestamp persistence, duplicate projection, overdue generation and fulfillment projection. These database behaviors remain unverified against a running PostgreSQL instance.
- API build passed with existing unrelated compiler warnings.

To run the database scenarios, set UPCOMING_MEALS_TEST_CONNECTION to an isolated PostgreSQL server where the test account may create/drop test databases, then run the integration test project with filter FullyQualifiedName~Staff_inbox. The fixture creates a unique disposable database; do not point it at a production application database.

## Business dashboard insights

The existing authenticated Admin/Operations `GET /api/v1/admin/dashboard/operations?date=YYYY-MM-DD` endpoint now adds an `insights` object. Existing response fields and routes remain compatible. No new endpoint, schema, migration, SQL script or role is introduced.

`insights` contains `activeSubscriptions`, `pendingConfirmations`, `expiringSubscriptions`, `fulfillment`, `workload`, `popularPackages`, `deliverySlots` and `scheduledOrders`. Fulfillment includes total, pending, overdue, delivered, collected, fulfilled, homeDelivery, takeaway, mealItems and nullable fulfillmentRate. Seven workload entries cover the selected date through date + 6. All totals are calculated before the configured dashboard row limit is applied.

Service-day counts, deadlines, eligibility and meal demand reuse the existing Delivery Calendar read model. Package demand includes snacks and is not a replacement for eligible meal-selection preparation quantities. Active subscriptions count distinct eligible Confirmed/Active order plans spanning the selected date. Popular packages group these by daily meal/snack quantities. Renewal opportunities use their last scheduled service date within the selected seven-day window. Pending confirmations count both current and legacy pending order statuses across all dates. Slot progress uses actual configured windows or saved snapshots, excludes takeaway, and reports unknown windows without inventing capacities. Fulfillment rate is `(Delivered + Collected) / total * 100`, rounded to one decimal; no scheduled service days yields null.

The SPA polls quietly every 60 seconds and preserves cached data on refresh failures. No order approval, fulfillment, payment or subscription mutation is performed by these reads. No Flutter changes are included.

Implementation: `DashboardInsightsBuilder.cs` (Application), `DashboardInsightsContracts.cs` and the additive property in `OperationsDashboardContracts.cs` (Contracts), `OperationsDashboardService.cs` (Persistence), and `DashboardInsightsTests.cs` (UnitTests).

Validation: 9 dashboard unit tests passed, including service-day totals independent of table limits, delivery/takeaway slot isolation, package grouping, renewal boundaries, seven-day zero filling and unknown-window handling. API compilation passed. Frontend: 10 targeted tests, TypeScript, ESLint and production build passed. Desktop/mobile previews used fixture data. Live PostgreSQL aggregation was not exercised.

## Recent notifications, fulfillment dates and package restart dates

The Admin notification dropdown requests only the newest 50 inbox items using the existing pageSize parameter. Older notifications remain stored; the unread badge and read-all endpoint still cover the recipient's entire authorized inbox.

The existing fulfillment PATCH endpoint rejects a new Delivered/Collected transition when the service date is after today's Asia/Qatar date: HTTP 409, code `service_day_in_future`. The UI also hides those future-date actions. Same-status retries preserve existing completion audit metadata.

The customer pause UI chooses one restart date. Preview and confirmation requests set `moveRemaining: true` and submit the system's next eligible future date as pauseStartDate, moving all remaining uncommitted service days. Omitting moveRemaining retains the existing two-date contract. Restart dates may extend past the original package end but must be within six calendar months of the current Qatar date. The existing next-day cutoff still determines the earliest eligible restart. The API calculates the end date from the remaining purchased service days, respecting delivery weekdays and preserving committed deliveries.

The existing `POST /api/v1/orders/{orderId}/resume` request additionally accepts optional `resumeDate` (YYYY-MM-DD). The app supplies the selected restart date when removing a pause. Requests without that field retain the previous earliest-resume behavior. Command retries with a conflicting restart date return 409. The requested date is recorded in the existing schedule-change JSON; no database column, migration or SQL file is added. When an earlier restart precedes the original order start, the order start bound is extended to include it. Payment and approval statuses remain independent.

The customer dashboard shows only the package pause status on the existing plan card. Pause/resume controls remain on the existing order-details screen. No new screen or role is introduced. Deploy the API before the updated customer app so the selected resumeDate is honored.

Validation for this update: 34 Admin frontend tests, 44 Flutter tests, 62 backend unit tests and 20 API integration tests passed. The integration suite used a disposable local PostgreSQL 18 database with no skipped tests. Coverage includes actual future-date rejection, delivery/collection HTTP calls, single restart-date scheduling from tomorrow, six-month limits, pause/resume persistence, retries, entitlement preservation, authorization and existing preparation/PDF behavior. TypeScript, targeted ESLint, Flutter static analysis and the SPA production build passed. Existing compiler/Vite advisories remain. No existing application database was modified.

## Cancelled-order payment protection

The existing Admin order payment endpoint rejects all manual payment-status changes for cancelled orders with HTTP 409 and code `cancelled_order_payment_locked`. This check runs inside the existing order transaction/row lock before payment assignment, history creation, version increment or service-day initialization. Pending and confirmed order payment behavior and cancellation's existing automatic refund-pending handling are preserved.

The Admin UI hides Record Payment Status on cancelled order details. Its Daily Orders table now groups order/created time, customer, package, service period and fulfillment information with readable Qatar dates and status badges. Activity history omits the internal actor/history ID disclosure; stored audit records remain unchanged. No new endpoint, database field, migration or SQL script is included. Deploy the API change with the Admin SPA.

Validation: 37 distinct targeted React tests passed, plus 36 workflow-policy unit tests and 3 PostgreSQL integration tests (zero skipped). The integration tests cover cancelled-payment rejection without payment/version/history changes and existing payment/approval/cancellation behavior. TypeScript, targeted ESLint and the SPA production build passed, with existing compiler/Vite advisories. Browser previews used fixture data to check the updated order list, cancelled-order action visibility and activity history. A disposable PostgreSQL 18 cluster was used; no application database was changed.

## Admin customer directory

The read-only Admin Customers screen uses existing customer_profiles, UserProfiles/identity users, orders and subscription pause records. No entity/schema changes, migrations, SQL scripts or role definitions are introduced.

Both `/api/admin/customers` and `/api/v1/admin/customers` support:

- GET list with `search`, `pageNumber` (default 1), `pageSize` (default 20; max 100). Search matches name, phone or email case-insensitively, up to 200 characters. Returns items, totalCount, pageNumber and pageSize.
- GET `/{customerId}` for the saved profile/contact summary; 404 when absent.
- GET `/{customerId}/orders` with pageNumber/pageSize, returning the existing staff order-list contract filtered to that exact customer profile. Missing profiles return 404; existing profiles without orders return an empty page.

All three endpoints require the existing Admin role. Each customer summary includes ID, resolved name, phone/email when recorded, profile creation timestamp, profile-active flag, preferred language, total orders, booked plans, active plans, paused plans, pending/cancelled/rejected counts and latest order timestamp. Counts aggregate orders directly, without joining meals or service days. Customers with no orders and legacy profiles without identity links are retained. Page contacts and count queries are batched; no per-customer database loop is used.

Booked plans are Confirmed/Active/Completed order records. Pending/legacy Pending, Cancelled and Rejected orders are excluded. Active/paused now use the existing fulfillment eligibility policy, the order date range and today's Qatar date; a current confirmed pause moves the eligible plan from active to paused. Future pauses do not yet count as paused. Latest order includes all order statuses. No metric automatically changes approval, payment or fulfillment.

An Admin-only virtual read-only Customers menu is returned when an explicit menu does not exist, following the existing Orders/Audit fallback convention. Explicit configured menus, including inactive entries, are respected. No menu or permission records are written. The SPA reuses the existing order-details screen for order actions and contains no customer edit, financial mutation or health/questionnaire UI.

Feature files: AdminCustomerContracts.cs (Contracts), AdminCustomerService.cs (Persistence), AdminCustomersController.cs (API), plus the additive scoped filter in StaffOrderService, DI registration and virtual menu in AccessControlService. Deploy the API and Admin SPA together; no Flutter update is needed.

Customer directory validation: 12 targeted React tests, 3 API/PostgreSQL integration tests and 2 desktop/mobile Chromium tests passed. PostgreSQL tests cover counts independent of service-day/meal joins, contact search, pagination, paused/active and prepaid eligibility, zero-booking profiles, customer-scoped HTTP history, missing/invalid requests and virtual menu behavior without menu writes. Authorization tests issue actual anonymous/non-Admin requests to all three endpoints. Frontend tests cover role gating, search, independent history pagination, missing data, error retry and existing order-detail navigation. TypeScript, targeted ESLint and production SPA/API builds passed. Browser fixtures are synthetic and the PostgreSQL cluster was disposable; no application database was modified.

Preparation warning display fix: AdminDeliveryCalendarService now projects the existing OrderNumber into its preparation read model and uses it for meal-assignment, legacy-lock and missing-service-day warnings. The same OperationalExceptions strings feed the Preparation Summary and PDF. Missing numbers use 'Order number not recorded'; internal IDs remain available to the application but are not substituted as warning labels. No stored review reason, preparation eligibility/quantity, schema or migration is changed. Deploy the API for the correction to appear in Admin.
Validation: 18 preparation/PDF unit tests and 1 PostgreSQL integration test passed. The database test covers all three warning branches and the missing-number fallback, and verifies raw review text is preserved. The PDF test verifies the order number appears in extracted report text. The PostgreSQL cluster was disposable and no application database was modified.

## Kitchen Preparation PDF

The existing Download PDF action produces an A4 landscape report using the Admin palette: dark green (#00684F), mint, white and subtle grey. Every page has the embedded Diet Time logo, report title, service date, Qatar generation timestamp, page number and footer. The overview counts orders, unique customers, meal items, deliveries and takeaways for the selected date.

Customer sections group orders by customer ID and keep them adjacent, with prominent customer names, order numbers, alternating subtle backgrounds and separate delivery/collection location blocks per order. Small groups stay together when they fit; larger groups and long orders paginate with repeated customer/location/allergen headings. Meals retain their configured categories, quantities, food preferences and allergen notes. Allergens are red, and selection status/source codes are omitted.

Home delivery uses the historical order address snapshot, including building, street, unit, zone, area, directions and coordinates. Only when the snapshot is completely absent can the explicitly linked, customer-owned address be used. The current default address is never substituted. Generic map labels are discarded; incomplete addresses show "Address verification required" while retaining known location details. Valid coordinates include an Open map link. Takeaway uses saved branch details, with a linked branch fallback for missing snapshot fields.

Deploy the API to apply the PDF changes; existing endpoints and preparation eligibility remain unchanged. No migration, SQL script or Flutter change is needed. Validation: 27 report/preparation/location unit tests and 2 isolated PostgreSQL integration tests passed. The synthetic seven-page report covers five customers, six orders, deliveries, takeaways, long addresses, allergens, customer grouping and map links. Rendered overview, delivery, takeaway and missing-address pages were visually reviewed. The sample is available at artifacts/kitchen-preparation-customer-report.pdf.

Implementation: PreparationOrderLocation.cs (Application); DeliveryPreparationContracts.cs (additive delivery/takeaway counts, address verification flag and coordinates); AdminDeliveryCalendar.cs (fulfillment method projection); AdminDeliveryCalendarService.cs (batched explicit address/branch lookups and snapshot mapping); KitchenPreparationPdfReportGenerator.cs, .BrandHeader.cs and .CustomerOrders.cs (MigraDoc layout). Tests: KitchenPreparationReportTests.cs, PreparationPremiumReportTests.cs, PreparationLocationTests.cs, and PreparationAddressTests.cs. PostgreSQL checks cover actual snapshot projections, partial-address handling, no unrelated default fallback, preserved branch snapshots, missing branch snapshot fallback, correct delivery/takeaway counts and existing warning order numbers. The temporary test cluster does not use application data.


## Dashboard customer insights

The dashboard replaces Orders by day with a top-five customer booking chart and counters for customers with bookings, repeat customers, new customers and total booked orders. Customer links reuse /customers?customerId={id} and appear only with Customers read permission. Existing operational cards and scheduled orders remain available; minute polling refreshes the new panel with the dashboard.

Each confirmed, active or completed order counts as one booked plan; service days and meals never multiply the count. Pending, rejected and cancelled orders are excluded, independently of payment state. Totals and ranks include purchases placed before the end of the selected Qatar date. Repeat customers have at least two counted bookings; repeat share uses all customers with counted bookings. New customers means first counted booking in the inclusive 30-day period ending on the selected date. Ranks use booked count descending, most recent booking, then customer ID for deterministic ties. These are current approval states filtered by placement date, not reconstructed historical approval states.

The existing GET /api/v1/admin/dashboard/operations response adds customerInsights. Existing Admin/Operations API authorization is preserved. Analytics are aggregated in PostgreSQL; only the five leading customer names are fetched in one batch. No schema, migration, revenue information or customer-app change is required. Deploy API and SPA together; an older API shows an unavailable state instead of invented counts.

Validation: 12 targeted frontend tests, 8 backend dashboard unit tests, 2 real PostgreSQL integration tests and 2 desktop/mobile Chromium tests passed. TypeScript, targeted ESLint and the production SPA build passed. Checks cover purchase counts, excluded statuses, repeat share, first-booking cohorts, Qatar midnight boundaries, empty states, existing dashboard behavior, customer actions and responsive layout. Browser examples use synthetic data; the isolated PostgreSQL cluster uses no application data.

Backend files: OperationsDashboardService.cs and OperationsDashboardService.Customers.cs; OperationsDashboardContracts.cs and DashboardCustomerInsightsContracts.cs. DashboardCustomerTests.cs covers actual PostgreSQL aggregation and selected-date boundaries. Existing routes and mutation workflows are unchanged.


## Master-data export, customer sorting and meal filters

Use **Export English / Arabic** on categories, ingredients, allergens, meal types, Meals or Meal Plans. Each file contains only that screen's dataset. The CSV has exactly two columns: English and Arabic. Names and existing descriptive text are paired on successive rows; unused optional fields are omitted, while missing translations remain blank. IDs, codes, statuses, versions, field labels and review columns are excluded. Meals exports current catalogue revisions; Meal Plans exports the selected template. Arabic remains Excel-compatible UTF-8, with safe CSV quoting for commas, quotes, multiline text and formula-like values.

The authenticated GET /api/v1/admin/master-data/export (also /api/admin/master-data/export) now requires resource=meal-categories|ingredients|allergens|meal-types|meals|meal-plans. Meal Plans supplies planId for the selected template. Missing or unsupported resources return 400; there is no combined all-master export. Existing Admin, Dietitian and ContentManager access remains unchanged. No customer, order, payment or health data is exported, and no schema or migration changes are required.

Dashboard now appears first in the sidebar. The Customers screen adds server-side sorting by name, most booked plans, latest order and newest customer, preserving search and resetting pagination when sorting changes. The existing customer endpoint accepts sort=name_asc|name_desc|booked_desc|latest_order_desc|newest_desc. Booked-plan sorting uses confirmed, active and completed orders; rejected/cancelled/pending orders are excluded. Null latest-order dates sort last and customer IDs provide stable pagination ties.

Meals now pass the status query through to the database before counting or pagination. Draft, Active, Inactive and Archived match their saved lifecycle states; availability remains separate. Search and status work together, clearing status restores the full list, and invalid API status values return 400. The status control also has an associated accessible label.

Validation: 24 targeted React/API-adapter/sidebar tests, 7 CSV unit tests, 4 API integration tests (3 with isolated PostgreSQL, 1 authorization test) and 4 desktop/mobile Chromium tests passed. API build, TypeScript, targeted ESLint and the production SPA build passed. Checks cover complete exports beyond 100 records, inactive records, actual Arabic text/BOM, missing translations, CSV escaping, read-only behavior, unauthorized callers, global customer sorting and filtered meal counts/pagination. Test data and the sample CSV are synthetic; no application database was used. Deploy API and SPA together. No Flutter changes are required.

Backend implementation: MasterDataExportController.cs, MasterDataTranslationExport.cs; AdminCustomersController.cs and AdminCustomerService.cs (sorting); AdminController.cs, AdminMealService.cs and the IAdminMealService signature in Abstractions.cs (status filter). Existing callers can omit the new optional parameters. Tests: MasterDataExportTests.cs, CatalogueVerificationTests.cs and MasterDataExportAuthorizationTests.cs. No standalone SQL or migration scripts were added.

Export scope follow-up validation: 6 targeted frontend tests, 7 CSV unit tests, 2 API integration tests (including real PostgreSQL resource isolation and selected-template checks) and 2 desktop/mobile Chromium tests passed. CSV parsing verifies exactly English/Arabic columns even with multiline content. TypeScript, targeted ESLint and production build passed. The synthetic sample CSV was regenerated in artifacts/master-data-translations-sample.csv. Deploy API and SPA together; the endpoint now requires the screen resource.
