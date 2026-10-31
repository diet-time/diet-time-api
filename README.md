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

For staff approval, sign in with an **Admin** account and open **Orders → Review →
Approve → Confirm**. The sidebar and approval controls use the existing screen
permissions. If Orders is missing, check that the existing `/orders` menu is active
and the account has Admin access, then sign out and back in to refresh permissions.
Deploy the current admin SPA if the menu is still absent despite an active `/orders`
permission. The dashboard's
**Review pending orders** shortcut opens the same screen, and its review count now
defaults to `PENDING_APPROVAL` even with the older empty status configuration.

The React dashboard and Delivery Calendar also display a prominent **Pending
confirmation orders** section above operational data. It shows the live pending
count, previews up to five purchased orders, and opens each order's existing review
dialog. The queue refreshes every 30 seconds, on window focus and after staff
decisions. It is independent of the calendar month and service-date filters, and
shows loading, failure or missing-access messages rather than hiding the section.

Admin-only endpoints under `/api/admin/orders`:

- `GET /` lists orders with optional `status`, `pageNumber` and `pageSize`.
- `GET /{id}` returns the purchased order snapshot and audit history.
- `POST /{id}/approve`, `/reject`, `/hold`, `/resume`, `/cancel`, `/activate`, `/complete`
  accept `{ "expectedRowVersion": 1, "reason": "..." }`.
- `POST /{id}/payment` also accepts `paymentStatus`; a verification reference/reason
  is mandatory. This records manual payment/refund status without moving funds.
- `POST /{id}/revise-dates` accepts `expectedRowVersion`, `startDate`, `serviceWeekdays` and
  `reason`. It preserves purchased duration and requires explicit agreement; approval
  never silently moves dates.

Customer `POST /api/v1/orders/{id}/cancel` checks ownership and accepts the current
row version and reason. Cancellation is allowed during review or before confirmed
service/preparation starts; later cancellation requires contacting Diet Time.

Transitions are centralized: pending review → confirmed/rejected/on hold/cancelled;
on hold → pending review/cancelled; confirmed → active/cancelled; active → completed
only after every purchased service day is delivered. Reasons are required for rejection,
hold, cancellation, manual payments and revised dates. Row locks and version checks
return HTTP 409 for conflicting or repeated actions without duplicating service records.
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
After applying the schema correction twice, submission, hold/resume, rejection and approval
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
Ordinary login sends customers with current orders to the dashboard and other customers to
plan browsing. Explicit checkout intent continues the selected order, allowing additional
orders without repeating completed questionnaires. Profile still provides questionnaire
editing; **Not Now** returns to browsing without marking an incomplete questionnaire complete.

Regression coverage is in `QuestionnaireCompletionTests.cs`,
`QuestionnaireFlowIntegrationTests.cs`, and Flutter's `questionnaire_checkout_flow_test.dart`
and updated journey tests.
