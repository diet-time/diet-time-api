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

New orders enqueue the configured Twilio content template for `OperationsNumber`
only after the order transaction commits. The API returns the confirmed order without
waiting for Twilio, so provider delays, retries and failures cannot block checkout or
the dashboard refresh. A background worker uses its own dependency-injection scope
and a 60-second send deadline. Failures are logged, and idempotent order replays do
not enqueue another message.

Notifications remain best effort: the bounded queue holds up to 1,024 pending order IDs
in memory, and pending messages do not survive an API restart. A full or stopping queue
logs an error without changing the confirmed order. No database migration is required.

Example request body:

```json
{
  "to": "+97474452435",
  "contentSid": "HXb5b62575e6e4ff6129ad7c8efe1f983e",
  "contentVariables": { "1": "12/1", "2": "3pm" }
}
```

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
