# Diet Time APIs

`DietTime.Meal.Api` is the .NET 8 API over the existing PostgreSQL meal catalogue and meal-plan schema. It returns localized Flutter-ready projections, never translation collections or image binaries. The solution is organized to accommodate additional hosts such as `DietTime.Auth.Api`, `DietTime.Customer.Api`, and `DietTime.Admin.Api` under `src/Apis`.

## Projects

- `src/Apis/DietTime.Meal.Api`: versioned meal controllers, JWT, middleware, Swagger, rate limiting, CORS, and health checks.
- `src/BuildingBlocks`: reusable application, contracts, domain, infrastructure, and persistence projects shared by API hosts.
- `tests/DietTime.Meal.Api.IntegrationTests`: integration coverage for the meal API host.
- `DietTime.Application`: use-case interfaces, validation, localization, availability, calendar, selection, and pricing rules.
- `DietTime.Domain`: meal and plan entities matching the supplied schema.
- `DietTime.Persistence`: one `DietTimeDbContext`, Fluent mappings, projected queries, transactional admin writes, development seed, and identity migration.
- `DietTime.Infrastructure`: JWT/refresh-token issuance and server-side S3-compatible object storage integration.
- `DietTime.Contracts`: request, response, envelope, pagination, and error contracts.

## Configuration

Required Railway variables:

```text
ASPNETCORE_ENVIRONMENT=Production
PORT=8080
ConnectionStrings__DefaultConnection=Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true
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

JWT bearer validation is temporarily disabled in all environments. Requests are authenticated by the temporary development handler with the admin, dietitian, and content-manager roles. Do not treat the deployed API as access-controlled until JWT authentication is restored.

Do not use the checked-in development placeholders as credentials. Secret values belong in Railway variables or .NET user secrets.

## Database assumptions

The supplied meal tables, indexes, `pgcrypto`/`gen_random_uuid()`, and `set_updated_at()` trigger function already exist. Entity mappings preserve their names and constraints. The only included migration creates ASP.NET Core Identity and hashed refresh-token storage:

```powershell
dotnet ef database update --project src/BuildingBlocks/DietTime.Persistence --startup-project src/Apis/DietTime.Meal.Api
```

Run that migration after the supplied meal schema has been installed. It deliberately does not create or alter meal tables. Development seeding is opt-in with `DevelopmentSeed__Enabled=true`, exits when lookup data exists, and must never be enabled in production.

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
POST /api/v1/auth/refresh
```

Admin endpoints from the brief are under `/api/v1/admin` and require `Admin`, `Dietitian`, or `ContentManager`. Meal images are uploaded as multipart form data to `POST /api/v1/admin/meals/{mealId}/media/upload`. The persisted `public_url` points to the public API media route (`GET /api/v1/media/{objectKey}`), while storage credentials and object URLs remain server-side. Set `Api__PublicBaseUrl` to the externally reachable API origin in deployed environments.

Swagger is available outside Production at `/swagger`; liveness/database health is `/health`.

## Calendar behavior

Meal-plan template days use stable uppercase weekday codes in `day_of_week` and are ordered by `display_order`. Menu lookup always selects the weekday matching the actual delivery date; numbered rolling-day progression is not supported.

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
