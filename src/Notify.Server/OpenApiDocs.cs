using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Notify;

// The REST reference is generated from the real route table. This file supplies the
// prose, scopes, parameters and actual response matrix that minimal APIs cannot infer.
internal static class OpenApiDocs
{
    private const string JsonMedia = "application/json";

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer(DocumentAsync);
        options.AddDocumentTransformer(ComponentsAsync);
        options.AddOperationTransformer(OperationAsync);
        options.AddSchemaTransformer(SchemaAsync);
    }

    private static Task DocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo
        {
            Title = "ALXPRGS Notify API",
            Version = "v1",
            Description = """
                HTTP JSON API v1 для личного сервиса Notify: сообщения, подтверждения, формы, голосовые сообщения и живые звонки.
                Токены интеграций и устройств передаются в `Authorization: Bearer`. Веб-кабинет использует HttpOnly cookie
                `notify.session` (в HTTPS — `__Host-notify.session`) и для POST — заголовок `X-CSRF-Token` с точным `Origin`.
                Ошибки возвращаются как `{ "code": "..." }`. Ответы без тела (401 от middleware, 204) не содержат JSON.
                WebSocket-маршруты (`/api/v1/agents/connect`, `/api/v1/calls/{id}/audio`, `/api/v1/agents/calls/{id}/audio`)
                и MCP `/mcp` намеренно не входят в REST-справочник: их протоколы описаны в руководстве «Контракт API, MCP и Python SDK».
                """,
        };
        document.Tags = new HashSet<OpenApiTag>
        {
            new() { Name = "Служебные", Description = "Проверки процесса, конфигурация клиентов и App Links." },
            new() { Name = "Вход и привязка", Description = "SSO-вход кабинета и привязка Android без скрытого пароля." },
            new() { Name = "Сессия", Description = "Текущая учётная запись, CSRF и выход из веб-кабинета." },
            new() { Name = "Уведомления", Description = "Сообщения, подтверждения, формы и получение результата." },
            new() { Name = "Решения", Description = "Ответы устройства, receipt и подтверждения доставки." },
            new() { Name = "Голос", Description = "Двухфазная загрузка WAV и голосовые ответы агенту." },
            new() { Name = "Звонки", Description = "Метаданные живых звонков и elevated-challenge. Медиа идёт по WebSocket." },
            new() { Name = "Синхронизация", Description = "Инкрементальная синхронизация Android и сведения об устройствах." },
            new() { Name = "Управление", Description = "Источники, агенты и изменения, подтверждаемые телефоном." },
            new() { Name = "Иконки", Description = "Ограниченные PNG/WebP без произвольных внешних URL." },
        };
        document.Components ??= new OpenApiComponents();
        var settings = context.ApplicationServices.GetRequiredService<Settings>();
        document.Servers = [new OpenApiServer { Url = settings.PublicUrl, Description = "Публичный origin Notify" }];
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Токен интеграции (`nt_…`, 90 дней) или токен устройства (`nt_…`, 30 дней). Отдельного `sourceId` в запросе нет: источник определяется токеном. Для `POST /api/v1/device-enrollments` вместо него принимается SSO Access Token.",
            },
            ["SsoAccessToken"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "SSO Access Token (RS256, `token_use=access_token`, audience публичного Android-клиента). ID Token не принимается. Используется только для первичной привязки устройства.",
            },
            ["WebSession"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "notify.session",
                Description = "HttpOnly cookie веб-сессии после входа через SSO. В HTTPS-профиле имя — `__Host-notify.session`; срок 8 часов без sliding expiration. Передавайте cookie целиком, не копируйте значение в localStorage.",
            },
            ["WebCsrf"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-CSRF-Token",
                Description = "Анти-CSRF токен из `GET /api/v1/session` (`csrfToken`) и cookie `notify.csrf`. Обязателен для POST от веб-сессии вместе с `Origin`, точно равным `Notify__PublicUrl`.",
            },
        };
        return Task.CompletedTask;
    }

    // Runs after the operations/schemas are generated: remove schemas that only described
    // anonymous return types and apply the authoritative field metadata by component name.
    private static Task ComponentsAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var schemas = document.Components?.Schemas;
        if (schemas == null) return Task.CompletedTask;
        foreach (var key in schemas.Keys.Where(key => key.StartsWith("AnonymousType", StringComparison.Ordinal)).ToList()) schemas.Remove(key);
        foreach (var (name, value) in schemas)
        {
            if (value is not OpenApiSchema schema) continue;
            if (TypeDescriptions.TryGetValue(name, out var description)) schema.Description = description;
            if (schema.Properties == null) continue;
            foreach (var (property, propertySchema) in schema.Properties)
                ApplySchemaMetadata(name + "." + property, propertySchema);
        }
        return Task.CompletedTask;
    }

    private static void ApplySchemaMetadata(string key, IOpenApiSchema value)
    {
        var hasDescription = Descriptions.TryGetValue(key, out var description);
        if (value is OpenApiSchemaReference reference)
        {
            if (hasDescription) reference.Description = description;
            return;
        }
        if (value is not OpenApiSchema schema) return;
        if (hasDescription) schema.Description = description;
        if (Enums.TryGetValue(key, out var values))
        {
            schema.Enum = values.Select(item => (JsonNode)JsonValue.Create(item)!).ToList();
            if (schema.Type?.HasFlag(JsonSchemaType.Null) == true) schema.Enum = null;
        }
        if (Constraints.TryGetValue(key, out var constraint))
        {
            if (constraint.MinLength is { } min) schema.MinLength = min;
            if (constraint.MaxLength is { } max) schema.MaxLength = max;
            if (constraint.Pattern is { } pattern) schema.Pattern = pattern;
            if (constraint.Min is { } minimum) schema.Minimum = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (constraint.Max is { } maximum) schema.Maximum = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static async Task OperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var method = context.Description.HttpMethod?.ToUpperInvariant() ?? "";
        var path = "/" + (context.Description.RelativePath ?? "").TrimStart('/');
        if (!Operations.TryGetValue(method + " " + path, out var doc))
            throw new InvalidOperationException($"OpenAPI description is missing for {method} {path}");

        var document = context.Document ?? throw new InvalidOperationException("OpenAPI document is unavailable");
        operation.OperationId = doc.OperationId;
        operation.Summary = doc.Summary;
        operation.Description = doc.Description;
        operation.Tags = new HashSet<OpenApiTagReference> { new(doc.Tag, document) };
        operation.Security = SecurityFor(doc.Auth, document);

        operation.Parameters ??= new List<IOpenApiParameter>();
        foreach (var parameter in operation.Parameters)
        {
            if (parameter.Name == null) continue;
            if ((doc.Parameters ?? []).TryGetValue(parameter.Name, out var description)) parameter.Description = description;
            if (parameter is OpenApiParameter concrete && (doc.ParameterExamples ?? []).TryGetValue(parameter.Name, out var example)) concrete.Example = JsonNode.Parse(example);
        }
        foreach (var header in doc.Headers ?? [])
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header.Name,
                In = header.In,
                Required = header.Required,
                Description = header.Description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        await ApplyRequestBody(operation, doc, context, ct);

        var responses = new OpenApiResponses();
        foreach (var response in doc.Responses)
        {
            var value = new OpenApiResponse { Description = response.Description };
            if (response.Headers is { Length: > 0 })
            {
                value.Headers ??= new Dictionary<string, IOpenApiHeader>();
                foreach (var (name, description) in response.Headers)
                    value.Headers[name] = new OpenApiHeader { Description = description, Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            }

            if (response.Status != 204 && response.Status != 302 && !response.EmptyBody)
            {
                IOpenApiSchema schema;
                if (response.Binary)
                    schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
                else if (response.OneOf is { Length: > 0 })
                {
                    var choices = new List<IOpenApiSchema>();
                    foreach (var type in response.OneOf) choices.Add(await context.GetOrCreateSchemaAsync(type, null, ct));
                    schema = new OpenApiSchema { OneOf = choices };
                }
                else if (response.Schema != null)
                {
                    schema = response.Array
                        ? new OpenApiSchema { Type = JsonSchemaType.Array, Items = await context.GetOrCreateSchemaAsync(response.Schema, null, ct) }
                        : await context.GetOrCreateSchemaAsync(response.Schema, null, ct);
                }
                else schema = new OpenApiSchema();

                var mediaTypes = response.MediaTypes ?? [JsonMedia];
                value.Content ??= new Dictionary<string, OpenApiMediaType>();
                foreach (var mediaType in mediaTypes)
                    value.Content[mediaType] = new OpenApiMediaType { Schema = schema, Example = response.Example == null ? null : JsonNode.Parse(response.Example) };
            }
            responses[response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = value;
        }
        operation.Responses = responses;
    }

    private static async Task ApplyRequestBody(OpenApiOperation operation, OperationDoc doc, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        switch (doc.Body)
        {
            case RequestBodyKind.None:
                operation.RequestBody = null;
                return;
            case RequestBodyKind.Audio:
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = doc.RequestDescription,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["audio/wav"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                        ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                    },
                };
                return;
            case RequestBodyKind.Icon:
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = doc.RequestDescription,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["image/png"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                        ["image/webp"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                    },
                };
                return;
            default:
                if (operation.RequestBody?.Content == null) return;
                operation.RequestBody.Description = doc.RequestDescription;
                if (doc.RequestExample != null)
                {
                    foreach (var media in operation.RequestBody.Content.Values)
                    {
                        media.Example = JsonNode.Parse(doc.RequestExample);
                        break;
                    }
                }
                return;
        }
    }

    private static IList<OpenApiSecurityRequirement> SecurityFor(ApiAuth auth, OpenApiDocument document) => auth switch
    {
        ApiAuth.None => [],
        ApiAuth.Bearer => [Requirement(document, ("Bearer", []))],
        ApiAuth.Sso => [Requirement(document, ("SsoAccessToken", []))],
        ApiAuth.BearerOrWeb => [Requirement(document, ("Bearer", [])), Requirement(document, ("WebSession", []))],
        ApiAuth.WebPost => [Requirement(document, ("WebSession", []), ("WebCsrf", []))],
        ApiAuth.BearerOrWebPost => [Requirement(document, ("Bearer", [])), Requirement(document, ("WebSession", []), ("WebCsrf", []))],
        _ => [],
    };

    private static OpenApiSecurityRequirement Requirement(OpenApiDocument document, params (string Scheme, List<string> Scopes)[] schemes)
    {
        var requirement = new OpenApiSecurityRequirement();
        foreach (var (scheme, scopes) in schemes) requirement[new OpenApiSecuritySchemeReference(scheme, document)] = scopes;
        return requirement;
    }

    private enum ApiAuth { None, Bearer, Sso, BearerOrWeb, WebPost, BearerOrWebPost }
    private enum RequestBodyKind { None, Json, Audio, Icon }

    private sealed record HeaderDoc(string Name, bool Required, string Description, ParameterLocation In = ParameterLocation.Header);
    private sealed record ApiResponse(int Status, string Description, Type? Schema = null, bool Array = false, Type[]? OneOf = null,
        string[]? MediaTypes = null, string? Example = null, bool Binary = false, bool EmptyBody = false, (string Name, string Description)[]? Headers = null);
    private sealed record OperationDoc(string OperationId, string Tag, string Summary, string Description, ApiAuth Auth, ApiResponse[] Responses,
        RequestBodyKind Body = RequestBodyKind.None, string? RequestDescription = null, string? RequestExample = null,
        Dictionary<string, string>? Parameters = null, Dictionary<string, string>? ParameterExamples = null, HeaderDoc[]? Headers = null);

    private static ApiResponse Ok<T>(string description, string? example = null, (string, string)[]? headers = null) => new(200, description, typeof(T), Example: example, Headers: headers);
    private static ApiResponse OkArray<T>(string description, string? example = null) => new(200, description, typeof(T), Array: true, Example: example);
    private static ApiResponse OneOf(string description, params Type[] types) => new(200, description, OneOf: types);
    private static ApiResponse Html(string description) => new(200, description, MediaTypes: ["text/html"]);
    private static ApiResponse Binary(string description, params string[] mediaTypes) => new(200, description, MediaTypes: mediaTypes, Binary: true);
    private static ApiResponse NoContent(string description) => new(204, description);
    private static ApiResponse Error(int status, string description, string? example = null) => new(status, description, typeof(ErrorResponse), Example: example);
    private static ApiResponse Empty(int status, string description) => new(status, description, EmptyBody: true);

    private static readonly ApiResponse Unauthorized = Error(401,
        "Учётные данные отсутствуют или недействительны. Middleware отвечает пустым телом; отозванное устройство — `device_revoked`.",
        """{"code":"device_revoked"}""");
    private static readonly ApiResponse RateLimited = Empty(429,
        "Общий лимит 300 HTTP-запросов в минуту на IP (пустое тело). Операции с собственной квотой возвращают JSON-код, указанный в их описании.");
    private static readonly ApiResponse InternalError = Error(500, "Непредвиденная ошибка сервера; тело — `{\"code\":\"internal_error\"}`.", """{"code":"internal_error"}""");

    private static readonly Dictionary<string, OperationDoc> Operations = BuildOperations();

    private static Dictionary<string, OperationDoc> BuildOperations()
    {
        var operations = new Dictionary<string, OperationDoc>(StringComparer.Ordinal);
        void Add(string method, string path, OperationDoc doc) => operations[method + " " + path] = doc;

        // ---- service ----
        Add("GET", "/health/live", new("getHealthLive", "Служебные", "Проверка жизни процесса",
            "Возвращает `alive` без обращения к базе, SSO и Firebase. Используется Docker HEALTHCHECK; не заменяет readiness.",
            ApiAuth.None, [Ok<HealthLiveResponse>("Процесс отвечает.", """{"status":"alive"}"""), RateLimited, InternalError]));

        Add("GET", "/health/ready", new("getHealthReady", "Служебные", "Проверка готовности конфигурации",
            "Готовность требует настроенного SSO (`OwnerSub` и web client secret) и существующего Firebase credential. Пока конфигурация неполная, сервер осознанно отвечает 503: защищённые функции не получают обходного входа.",
            ApiAuth.None, [
                Ok<HealthReadyResponse>("SSO и Firebase настроены.", """{"status":"ready"}"""),
                new(503, "Конфигурация неполная; поля `sso`/`fcm` показывают, что именно отсутствует.", typeof(HealthReadyResponse), Example: """{"status":"configuration_required","sso":false,"fcm":false}"""),
                RateLimited, InternalError]));

        Add("GET", "/api/config", new("getConfig", "Служебные", "Возможности и версии клиента",
            "Публичная конфигурация для Android и интеграций. `capabilities` описывает только реализованные функции; `protocols` — точные идентификаторы подписываемых байтов и протоколов; `minClientVersion` — минимальные версии клиента для каждой функции.",
            ApiAuth.None, [Ok<ConfigResponse>("Текущая конфигурация.", """{"version":"0.5.0","ssoIssuer":"https://auth.example.com","androidClientId":"notify-android","publicUrl":"https://notify.example.com","ssoConfigured":true,"capabilities":{"message":true,"confirmation":true,"elevated":true,"forms":true,"voiceMessages":true,"calls":true,"agents":true},"protocols":{"answer":"alxprgs.notify.answer.v1","form":"alxprgs.notify.form.v1","audio":"alxprgs.notify.audio.v1","agent":"alxprgs.notify.agent.v1","call":"alxprgs.notify.call.v2"},"minClientVersion":{"forms":"0.2.0","voiceMessages":"0.3.0","calls":"0.5.0"}}"""), RateLimited, InternalError]));

        Add("GET", "/.well-known/assetlinks.json", new("getAssetLinks", "Служебные", "Digital Asset Links для Android App Link",
            "Публикует package `tech.alxprgs.notify` и SHA-256 сертификатов подписи из `Notify__AndroidSigningFingerprints`. Без настроенных отпечатков возвращает 503 с пустым массивом, а не поддельный сертификат.",
            ApiAuth.None, [
                OkArray<AssetLink>("Список связей для проверки Android.", """[{"relation":["delegate_permission/common.handle_all_urls"],"target":{"namespace":"android_app","package_name":"tech.alxprgs.notify","sha256_cert_fingerprints":["AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99"]}}]"""),
                new(503, "Отпечатки подписи не настроены; тело — пустой массив `[]`.", typeof(AssetLink), Array: true, Example: "[]"),
                RateLimited, InternalError]));

        // ---- login ----
        Add("GET", "/auth/login", new("startWebLogin", "Вход и привязка", "Начать вход в веб-кабинет",
            "Запускает OIDC Authorization Code + PKCE через SSO и перенаправляет браузер. Notify не принимает пароль и не хранит SSO-секреты. Ответ 302 содержит `Location` на SSO; при выключенном SSO возвращается ProblemDetails 503.",
            ApiAuth.None, [
                new(302, "Редирект на SSO; заголовок `Location` ведёт на authorization endpoint.", Headers: [("Location", "URL SSO authorization endpoint.")]),
                new(503, "SSO не настроен (`Results.Problem`).", typeof(ProblemResponse), MediaTypes: ["application/problem+json"], Example: """{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"SSO not configured","status":503}"""),
                RateLimited, InternalError]));

        Add("GET", "/auth/step-up/callback", new("completeStepUp", "Вход и привязка", "Callback внешнего step-up SSO",
            "Браузерный callback привязки/восстановления устройства. Проверяет `iss`, одноразовый `state`, срок, PKCE и подписанный proof SSO (действие, payload hash, nonce, assurance `reauth_password_mfa`, pwd + otp/hwk). Успех — HTML-страница возврата в приложение; ошибки — JSON `{code}`. Эндпоинт не предназначен для прямых вызовов интегратором.",
            ApiAuth.None, [
                Html("Устройство привязано; HTML-страница «Вернитесь в приложение Notify»."),
                Error(400, "`iss` не совпал или параметры слишком длинные (`invalid_sso_callback`); неизвестный state (`invalid_step_up_state`); отсутствующий параметр (`invalid_request`).", """{"code":"invalid_sso_callback"}"""),
                Error(401, "Подпись/срок SSO-токена не прошли проверку (`invalid_sso_token`) или сменился signing key (`sso_signing_key_changed_retry`).", """{"code":"invalid_sso_token"}"""),
                Error(403, "SSO отклонил redemption (`step_up_redemption_failed`) или proof не совпал с действием/hash/nonce/assurance (`step_up_binding_mismatch`).", """{"code":"step_up_binding_mismatch"}"""),
                Error(404, "Enrollment не найден (`enrollment_not_found`).", """{"code":"enrollment_not_found"}"""),
                Error(409, "State уже погашен (`step_up_replayed`).", """{"code":"step_up_replayed"}"""),
                Error(410, "Истёк state или enrollment (`step_up_expired`).", """{"code":"step_up_expired"}"""),
                Error(502, "SSO вернул некорректный ответ (`invalid_sso_response`) или authorization URL чужого origin (`invalid_sso_authorization_origin`).", """{"code":"invalid_sso_response"}"""),
                Error(503, "SSO не настроен (`sso_not_configured`), metadata недоступна (`sso_metadata_unavailable`) или step-up недоступен (`sso_step_up_unavailable`).", """{"code":"sso_metadata_unavailable"}"""),
                RateLimited, InternalError],
            Parameters: new() {
                ["state"] = "Одноразовый state, выданный Notify при создании step-up; максимум 128 символов.",
                ["code"] = "Код авторизации/redemption SSO; максимум 2048 символов.",
                ["iss"] = "Issuer SSO; должен точно совпадать с `Notify__SsoIssuer`.",
            }));

        Add("POST", "/api/v1/device-enrollments", new("startDeviceEnrollment", "Вход и привязка", "Начать привязку Android-устройства",
            "Первый шаг привязки. Принимает SSO Access Token в `Authorization: Bearer`, проверяет RS256, issuer/JWKS, audience Android-клиента, срок, `token_use=access_token` и sub владельца. ID Token отклоняется. Возвращает URL подтверждения; credential устройства ещё не выдаётся. Recovery в той же транзакции отзывает прежние устройства после подтверждения.",
            ApiAuth.Sso, [
                Ok<EnrollmentStartResponse>("Enrollment создан; откройте `authorizationUrl` в системном браузере.", """{"enrollmentId":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","authorizationUrl":"https://auth.example.com/step-up?state=EXAMPLE","expiresAt":1767225600000}"""),
                Error(400, "Некорректное имя/ключ/FCM (`invalid_device`, `invalid_device_key`) или тело (`invalid_request`).", """{"code":"invalid_device_key"}"""),
                Error(401, "Нет Bearer-заголовка (`sso_access_token_required`), токен недействителен (`invalid_sso_token`) или сменился ключ (`sso_signing_key_changed_retry`).", """{"code":"sso_access_token_required"}"""),
                Error(403, "Токен принадлежит другому владельцу или имеет неверный `token_use` (`sso_owner_or_profile_mismatch`).", """{"code":"sso_owner_or_profile_mismatch"}"""),
                new(502, "Некорректный ответ SSO (`invalid_sso_response`) или чужой origin authorization URL (`invalid_sso_authorization_origin`).", typeof(ErrorResponse), Example: """{"code":"invalid_sso_response"}"""),
                Error(503, "SSO не настроен/недоступен (`sso_not_configured`, `sso_metadata_unavailable`, `sso_step_up_unavailable`).", """{"code":"sso_not_configured"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json,
            "Параметры устройства и его P-256 публичный ключ (SubjectPublicKeyInfo, Base64). `recovery=true` отзывает прежние устройства после подтверждения.",
            """{"name":"Мой телефон","publicKey":"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...EXAMPLE","fcmToken":null,"recovery":false}"""));

        Add("GET", "/api/v1/device-enrollments/{id}", new("getDeviceEnrollment", "Вход и привязка", "Одноразовый результат привязки",
            "Публичный опрос по секретному `enrollmentId` (43 символа). Пока подтверждение не завершено — `status=pending`; после выдачи credential — `status=consumed`. Готовый результат отдаётся ровно один раз и содержит `deviceId`, `token`, `expiresAt`; повторная выдача секрета невозможна.",
            ApiAuth.None, [
                OneOf("Статус ожидания или одноразовые учётные данные устройства.", typeof(EnrollmentStateResponse), typeof(EnrollmentCredentialResponse)),
                Error(404, "ID неверной длины или enrollment не найден (`enrollment_not_found`).", """{"code":"enrollment_not_found"}"""),
                Error(410, "Срок enrollment истёк (`enrollment_expired`).", """{"code":"enrollment_expired"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Секретный enrollment id длиной ровно 43 символа." }));

        // ---- session ----
        Add("GET", "/api/v1/session", new("getSession", "Сессия", "Текущая сессия, actor и CSRF",
            """
            Возвращает сведения о текущей учётной записи и анти-CSRF токен. Тела, path- и query-параметров нет.

            **Способы аутентификации.** `Authorization: Bearer <токен>` — токен интеграции или устройства. Либо HttpOnly cookie веб-сессии
            `notify.session` (в HTTPS — `__Host-notify.session`), полученная после `/auth/login`. Cookie не читается из JavaScript.

            **`actor`.** `kind` — `integration`, `device` или `web`; `id` — id источника, устройства или SSO `sub`;
            `scopes` — права источника (у устройства и веб-сессии пусто); `sessionId` — идентификатор веб-сессии (иначе `null`);
            `isOwner` — вычисляемый признак владельца для `device`/`web`.

            **`csrfToken`.** Выдаётся при каждом успешном вызове вместе с cookie `notify.csrf` (HttpOnly). Нужен только веб-сессии
            для небезопасных методов: передавайте его в `X-CSRF-Token`; для POST также требуется `Origin`, точно равный
            `Notify__PublicUrl`. Bearer-клиентам CSRF-токен не нужен, но его наличие не является ошибкой.

            Успешный вызов с действующей сессией — 200; без учётных данных или с недействительным токеном — 401 с пустым телом.
            На запрос распространяется общий лимит 300 запросов/мин на IP (429).
            """,
            ApiAuth.BearerOrWeb, [
                Ok<SessionResponse>("Сессия действительна.", """{"actor":{"kind":"integration","id":"0123456789abcdef0123456789abcdef","scopes":["notify:send","requests:create"],"sessionId":null,"isOwner":false},"csrfToken":"EXAMPLE_CSRF_TOKEN"}""", [("Set-Cookie", "Устанавливает antiforgery cookie `notify.csrf`; HttpOnly.")]),
                Empty(401, "Нет или недействительны Bearer-токен либо cookie веб-сессии. Middleware отвечает пустым телом."), RateLimited, InternalError],
            Headers: [new("notify.session", false, "HttpOnly cookie веб-сессии; в HTTPS-профиле имя `__Host-notify.session`. Не обязательна для Bearer.", ParameterLocation.Cookie), new("notify.csrf", false, "Antiforgery cookie, устанавливаемая этим ответом для веб-сессии; в HTTPS-профиле — `__Host-notify.csrf`.", ParameterLocation.Cookie)]));
        Add("POST", "/api/v1/logout", new("logout", "Сессия", "Выйти из веб-кабинета",
            "Завершает только веб-сессию и удаляет cookie. Bearer-токены не отзываются: для интеграции используйте подтверждённый `integration.revoke`, для устройства — `device.revoke`. Для cookie-запроса обязательны `X-CSRF-Token` и точный `Origin`.",
            ApiAuth.WebPost, [
                NoContent("Сессия завершена."),
                Unauthorized,
                Error(403, "Операция только для веб-сессии (`web_required`); неверный Origin (`invalid_origin`) или CSRF (`invalid_csrf`).", """{"code":"web_required"}"""),
                RateLimited, InternalError],
            Headers: [new("Origin", true, "Точный `Notify__PublicUrl`; проверяется до обработчика."), new("X-CSRF-Token", true, "Токен из `GET /api/v1/session`.")]));

        // ---- notifications ----
        var notificationResponses = new ApiResponse[]
        {
            Ok<Notification>("Уведомление создано или возвращено по идемпотентному ключу.", """{"id":"0123456789abcdef0123456789abcdef","sourceId":"abcdef0123456789abcdef0123456789","content":{"kind":"confirmation","title":"Перезапустить тестовый процесс?","body":"Процесс будет остановлен и запущен заново.","securityLevel":"elevated","expiresAt":"2030-01-01T12:00:00+00:00","allowCustom":true,"iconId":null,"correlationId":"task-42","form":null,"voiceUpload":null,"voice":null},"createdAt":1767225600000,"status":"pending","answer":null,"answeredAt":null,"managementId":null,"previewOnly":false,"formAnswer":null}"""),
            Error(400, "`invalid_idempotency_key` (8–128, без control-символов), `invalid_notification` (kind/level/сроки/длины), `invalid_form` (спецификация формы) или `invalid_request` (JSON).", """{"code":"invalid_notification"}"""),
            Error(403, "Нет scope для этого kind (`scope_required`) или источник отозван/права изменились (`source_revoked_or_scope_changed`).", """{"code":"scope_required"}"""),
            Error(404, "`icon_not_found` или `audio_upload_not_found`.", """{"code":"icon_not_found"}"""),
            Error(409, "Тот же ключ с другим содержимым (`idempotency_conflict`) или повторное использование загрузки (`audio_upload_used`).", """{"code":"idempotency_conflict"}"""),
            Error(410, "Временная аудиозагрузка истекла (`audio_upload_expired`).", """{"code":"audio_upload_expired"}"""),
            Error(429, "Дневная квота источника (`source_daily_quota_exceeded`); повторы с тем же ключом её не расходуют. Общий IP-лимит даёт пустой 429.", """{"code":"source_daily_quota_exceeded"}"""),
            Unauthorized, InternalError,
        };
        Add("POST", "/api/v1/notifications", new("createNotification", "Уведомления", "Создать уведомление, подтверждение, форму или голосовое сообщение",
            """
            Создаёт событие от имени источника, определённого токеном. `kind=message` — информационное сообщение (`notify:send`).
            `confirmation` — решение да/нет/свой вариант (`requests:create`), `securityLevel=standard|elevated`, обязательный будущий `expiresAt` (до 30 дней).
            `form` — структурированный ответ и обязательный `form.fields` (`requests:create`).
            `voice` — голосовое вложение и обязательные `voiceUpload.uploadId` + `expiresAt` (`notify:send`).
            Повтор с тем же `Idempotency-Key` и тем же содержимым возвращает прежнее событие; изменённое содержимое — 409.
            Сохраняйте ключ между сетевыми повторами. Ответ — полная запись, не preview.
            """,
            ApiAuth.Bearer, notificationResponses,
            RequestBodyKind.Json,
            "Содержимое события. Для `voice` поле `voice` заполняется сервером и должно быть `null` во входе.",
            """{"kind":"confirmation","title":"Перезапустить тестовый процесс?","body":"Процесс будет остановлен и запущен заново.","securityLevel":"elevated","expiresAt":"2030-01-01T12:00:00Z","allowCustom":true,"iconId":null,"correlationId":"task-42"}""",
            new() { ["id"] = "Серверный id уведомления (32 hex-символа)." },
            Headers: [new("Idempotency-Key", true, "Стабильный ключ 8–128 символов без control-символов; обязателен.")]));

        Add("GET", "/api/v1/notifications", new("listNotifications", "Уведомления", "История уведомлений",
            "Страница истории по убыванию времени. Владелец (`web`/`device`) видит всё; интеграция с `requests:read` — только свои события. Список всегда `previewOnly=true`: body сокращён до 1 000 символов, текст ответа — до 512, значения формы скрыты. Полный текст получайте через `GET /notifications/{id}` перед решением. `next` содержит `before` и `beforeId` для следующей страницы.",
            ApiAuth.BearerOrWeb, [
                Ok<NotificationPage>("Страница истории.", """{"items":[{"id":"0123456789abcdef0123456789abcdef","sourceId":"abcdef0123456789abcdef0123456789","content":{"kind":"message","title":"Сборка завершена","body":"Артефакты готовы.","securityLevel":"standard","expiresAt":null,"allowCustom":true,"iconId":null,"correlationId":null,"form":null,"voiceUpload":null,"voice":null},"createdAt":1767225600000,"status":"active","answer":null,"answeredAt":null,"managementId":null,"previewOnly":true,"formAnswer":null}],"next":{"before":1767225600000,"beforeId":"0123456789abcdef0123456789abcdef"}}"""),
                Error(400, "`limit` вне 1–200 или `beforeId` длиннее 64 (`invalid_pagination`).", """{"code":"invalid_pagination"}"""),
                Error(403, "Интеграции нужен scope `requests:read` (`scope_required`).", """{"code":"scope_required"}"""),
                Unauthorized, RateLimited, InternalError],
            Parameters: new() {
                ["before"] = "Unix milliseconds; по умолчанию `long.MaxValue`. Передайте `next.before` для следующей страницы.",
                ["beforeId"] = "Id последнего элемента предыдущей страницы; по умолчанию `~`. Передайте `next.beforeId`.",
                ["limit"] = "Размер страницы 1–200; по умолчанию 50.",
            }, ParameterExamples: new() { ["limit"] = "50" }));

        Add("GET", "/api/v1/notifications/{id}", new("getNotification", "Уведомления", "Получить уведомление",
            "Полная запись без preview-сокращений: тело, ответ и значения формы. Доступна владельцу и интеграции-источнику с `requests:read`. Интеграция без scope получает 404, чтобы не раскрывать чужие id. Перед подтверждением Android обязан заново получить полную запись.",
            ApiAuth.BearerOrWeb, [
                Ok<Notification>("Полная запись."),
                Unauthorized,
                Error(404, "Событие не найдено или недоступно вызывающему (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id уведомления." }));

        Add("GET", "/api/v1/notifications/{id}/wait", new("waitNotification", "Уведомления", "Ожидание решения (long polling)",
            "Опрашивает событие до `seconds` секунд. Если решение не пришло, возвращает текущее `pending`, а не согласие. Только интеграция с `requests:read`; владелец устройства или веб-сессии получает 403 `scope_required`. `seconds` по умолчанию 20, допустимо 0–25.",
            ApiAuth.Bearer, [
                Ok<Notification>("Текущее состояние; при таймауте — `pending`."),
                Error(400, "`seconds` вне 0–25 (`invalid_wait_seconds`).", """{"code":"invalid_wait_seconds"}"""),
                Error(403, "Нужен `requests:read` (`scope_required`).", """{"code":"scope_required"}"""),
                Unauthorized,
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id уведомления.", ["seconds"] = "Секунды ожидания 0–25; по умолчанию 20." }));

        Add("POST", "/api/v1/notifications/{id}/challenge", new("createAnswerChallenge", "Решения", "Challenge для ответа да/нет",
            "Первый шаг elevated-ответа. Только доверенное устройство и только `confirmation` в состоянии `pending`. Сервер возвращает точные UTF-8 байты протокола `alxprgs.notify.answer.v1` в Base64; Android подписывает их P-256/SHA-256/DER после системной биометрии или PIN. Challenge живёт не более 90 секунд и не дольше самого запроса; предыдущий challenge этого устройства для этого события удаляется.",
            ApiAuth.Bearer, [
                Ok<Challenge>("Challenge создан.", """{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","notificationId":"0123456789abcdef0123456789abcdef","deviceId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","payload":"eyJwcm90b2NvbCI6ImFsc3ByZ3Mubm90aWZ5LmFuc3dlci52MS...","expiresAt":1767225690000,"answer":{"decision":"yes","text":null},"form":null,"audioId":null}"""),
                Error(400, "`confirmation` ожидает `decision=yes|no|custom`; для этого kind `invalid_answer`.", """{"code":"invalid_answer"}"""),
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Unauthorized,
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Событие уже не `pending` (`request_not_pending`).", """{"code":"request_not_pending"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Предполагаемый ответ; сервер включает его в подписываемые байты.",
            """{"decision":"yes"}""",
            new() { ["id"] = "Id подтверждения." }));

        Add("POST", "/api/v1/notifications/{id}/form-challenge", new("createFormChallenge", "Решения", "Challenge для формы",
            "Первый шаг elevated-формы. Только устройство, только `form` в состоянии `pending`. Сервер проверяет типы, обязательность, `min/max/maxLength`, неизвестные поля и варианты, затем отдаёт Base64 точных байтов `alxprgs.notify.form.v1`; Android подписывает весь набор значений. Challenge привязан к устройству, событию и конкретным значениям.",
            ApiAuth.Bearer, [
                Ok<Challenge>("Challenge создан."),
                Error(400, "`invalid_form` (kind не form), `invalid_form_answer` (тип/значение/неизвестное поле) или `form_required_missing`.", """{"code":"invalid_form_answer"}"""),
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Unauthorized,
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Событие уже не `pending` (`request_not_pending`).", """{"code":"request_not_pending"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Значения формы; объект `values` должен содержать только известные поля формы.",
            """{"values":{"env":"staging","count":3,"dry":true}}""",
            new() { ["id"] = "Id формы." }));

        Add("POST", "/api/v1/notifications/{id}/audio-challenge", new("createAudioChallenge", "Голос", "Challenge для прослушивания elevated-голоса",
            "Только устройство и только `voice`-уведомление с `securityLevel=elevated`. Возвращает Base64 точных байтов `alxprgs.notify.audio.v1`; подпись разрешает только выдачу grant на этот файл, но не выполнение задачи. Для обычного голосового сообщения возвращается 409.",
            ApiAuth.Bearer, [
                Ok<Challenge>("Challenge создан."),
                Error(400, "Событие не голосовое (`invalid_audio`).", """{"code":"invalid_audio"}"""),
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Unauthorized,
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Подтверждение не требуется (`audio_confirmation_not_required`).", """{"code":"audio_confirmation_not_required"}"""),
                Error(410, "Срок голосового вложения истёк (`audio_expired`).", """{"code":"audio_expired"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id голосового уведомления." }));

        Add("POST", "/api/v1/notifications/{id}/audio-grant", new("createAudioGrant", "Голос", "Одноразовый grant на скачивание",
            "Проверяет подпись challenge устройством и выдаёт grant на 60 секунд. Grant передаётся в `X-Notify-Audio-Grant` при `GET /api/v1/audio/{audioId}` и расходуется один раз. Повтор/истечение/чужое устройство отклоняются.",
            ApiAuth.Bearer, [
                Ok<AudioGrantResponse>("Grant выдан.", """{"grant":"EXAMPLE_ONE_TIME_GRANT","expiresAt":1767225660000}"""),
                Error(400, "Событие не голосовое (`invalid_audio`).", """{"code":"invalid_audio"}"""),
                Error(401, "Устройство отозвано (`device_revoked`).", """{"code":"device_revoked"}"""),
                Error(403, "`device_required`, `invalid_challenge` или `invalid_signature`.", """{"code":"invalid_challenge"}"""),
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Подтверждение не требуется (`audio_confirmation_not_required`).", """{"code":"audio_confirmation_not_required"}"""),
                Error(410, "Вложение истекло (`audio_expired`).", """{"code":"audio_expired"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Challenge и подпись его точных байтов.",
            """{"challengeId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","signature":"MEUCIQ...EXAMPLE"}""",
            new() { ["id"] = "Id голосового уведомления." }));

        // ---- audio / voice ----
        Add("POST", "/api/v1/audio/uploads", new("uploadAudio", "Голос", "Первая фаза загрузки WAV",
            "Принимает сырые байты WAV (`audio/wav` или `application/octet-stream`) и проверяет реальный RIFF/WAVE: PCM, mono, 16 кГц, 16 бит, корректный data chunk, до 10 МиБ и 5 минут. Доступно устройству или интеграции с `notify:send`. Возвращает временный `uploadId` на 15 минут. Файл не становится доступным, пока его не подтвердит создание уведомления или голосового ответа. Ошибка проверки удаляет `.part`.",
            ApiAuth.Bearer, [
                Ok<VoiceUploadResponse>("WAV принят.", """{"uploadId":"cccccccccccccccccccccccccccccccc","durationMs":1200,"sizeBytes":38528,"expiresAt":1767226500000}"""),
                Error(400, "`invalid_audio` (заголовок/формат/размер chunk) или `audio_too_long` (> 5 минут).", """{"code":"invalid_audio"}"""),
                Error(401, "Нет/недействителен токен.", null),
                Error(403, "Нужен токен устройства или интеграции (`device_or_integration_required`).", """{"code":"device_or_integration_required"}"""),
                Error(413, "`audio_too_large` (> 10 МиБ) или `audio_quota_exceeded` (общая квота диска).", """{"code":"audio_too_large"}"""),
                Error(429, "Две загрузки уже выполняются (`audio_upload_busy`); общий IP-лимит даёт пустой 429.", """{"code":"audio_upload_busy"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Audio, "Сырые байты WAV. Content-Length > 10 МиБ отклоняется до чтения тела."));

        Add("GET", "/api/v1/audio/{id}", new("downloadAudio", "Голос", "Скачать WAV",
            "Отдаёт файл владельцу-устройству или интеграции-источнику. Для elevated-уведомления нужен заголовок `X-Notify-Audio-Grant`, полученный после подписи; без него — 403. Поддерживается Range (206) для частичной загрузки. Файл существует только пока не истёк TTL; путь нельзя задать снаружи.",
            ApiAuth.Bearer, [
                Binary("Полный WAV-файл.", "audio/wav"),
                new(206, "Часть файла по заголовку `Range`.", Binary: true, MediaTypes: ["audio/wav"]),
                Empty(416, "Запрошенный диапазон `Range` вне размера файла."),
                Unauthorized,
                Error(403, "`audio_confirmation_required` (нет grant), `audio_grant_required` (grant неверен/использован) или `device_required`.", """{"code":"audio_confirmation_required"}"""),
                Error(404, "Файл/событие не найдены или недоступны (`audio_not_found`).", """{"code":"audio_not_found"}"""),
                Error(410, "Срок хранения истёк (`audio_expired`).", """{"code":"audio_expired"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id аудиоассета (32 hex-символа)." },
            Headers: [new("X-Notify-Audio-Grant", false, "Одноразовый grant для elevated-вложения; действует 60 секунд.")]));

        Add("POST", "/api/v1/voice-messages", new("sendVoiceMessage", "Голос", "Голосовой ответ Android → агент",
            "Превращает подтверждённую загрузку в durable-команду `voice_message` агенту источника. `notificationId` — голосовое уведомление, на которое отвечает владелец; `expiresAt` по умолчанию `AudioMessageTtlDays` (7 дней), максимум 30 дней. Команда ждёт подключения агента до TTL; статус после ack — `delivered`.",
            ApiAuth.Bearer, [
                Ok<VoiceCreatedResponse>("Команда создана.", """{"id":"dddddddddddddddddddddddddddddddd","status":"queued","expiresAt":1767830400000,"inReplyTo":"0123456789abcdef0123456789abcdef"}"""),
                Error(400, "Нет цели ответа (`voice_reply_target_required`) или неверный TTL (`invalid_voice_message`).", """{"code":"voice_reply_target_required"}"""),
                Error(401, "Нет/недействителен токен.", null),
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "`notification_not_found` или `audio_upload_not_found`.", """{"code":"notification_not_found"}"""),
                Error(409, "У источника нет агента (`agent_not_registered`) или загрузка уже использована (`audio_upload_used`).", """{"code":"agent_not_registered"}"""),
                Error(410, "Временная загрузка истекла (`audio_upload_expired`).", """{"code":"audio_upload_expired"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Подтверждённая загрузка и цель ответа.",
            """{"uploadId":"cccccccccccccccccccccccccccccccc","notificationId":"0123456789abcdef0123456789abcdef"}"""));

        Add("GET", "/api/v1/voice-messages/{id}", new("getVoiceMessage", "Голос", "Статус голосового ответа",
            "Доступно устройству-отправителю и интеграции-источнику. `status`: `queued`, `delivered` (агент подтвердил), `expired`, `failed`; `audioAvailable` показывает, существует ли файл. Веб-сессия получает 403.",
            ApiAuth.Bearer, [
                Ok<VoiceStatusResponse>("Текущий статус.", """{"id":"dddddddddddddddddddddddddddddddd","status":"queued","createdAt":1767225600000,"expiresAt":1767830400000,"durationMs":1200,"notificationId":"0123456789abcdef0123456789abcdef","audioAvailable":true}"""),
                Unauthorized,
                Error(403, "Нужен токен устройства или интеграции (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Голосовое сообщение не найдено (`voice_message_not_found`).", """{"code":"voice_message_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id голосового сообщения." }));

        Add("DELETE", "/api/v1/voice-messages/{id}", new("deleteVoiceMessage", "Голос", "Удалить голосовой ответ",
            "Удаляет метаданные и файл своего голосового ответа, а также неподтверждённую команду. Чужое сообщение неотличимо от отсутствующего (404).",
            ApiAuth.Bearer, [
                NoContent("Сообщение и файл удалены."),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Сообщение не найдено (`voice_message_not_found`).", """{"code":"voice_message_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id голосового сообщения." }));

        // ---- calls ----
        var callErrors = new ApiResponse[]
        {
            Error(400, "Неверный запрос: `call_verification_not_required` (challenge не для elevated), `invalid_call`, `invalid_session`.", """{"code":"invalid_call"}"""),
            Unauthorized,
            Error(403, "`device_required`; для elevated — `call_verification_required`, `invalid_challenge` или `invalid_signature`.", """{"code":"call_verification_required"}"""),
            Error(404, "Агент не зарегистрирован (`agent_not_registered`).", """{"code":"agent_not_registered"}"""),
            Error(409, "`agent_offline`, `agent_unsupported` (нет `realtime_audio`), `call_protocol_unsupported` или `busy`.", """{"code":"agent_offline"}"""),
            RateLimited, InternalError,
        };
        Add("POST", "/api/v1/calls/challenge", new("createCallChallenge", "Звонки", "Challenge исходящего elevated-звонка",
            "Только для `securityLevel=elevated`. Проверяет, что агент источника online, заявил `realtime_audio` и общий call-протокол v2, а `sessionId` (если задан) присутствует в hello. Возвращает Base64 точных байтов `alxprgs.notify.call.v2` для подписи после биометрии/PIN. Без подписи elevated-звонок не создаётся.",
            ApiAuth.Bearer, [Ok<CallChallengeResponse>("Challenge создан."), .. callErrors],
            RequestBodyKind.Json, "Агент и необязательная задача; только elevated.",
            """{"sourceId":"abcdef0123456789abcdef0123456789","sessionId":"task-1","securityLevel":"elevated"}"""));

        Add("POST", "/api/v1/calls", new("startCall", "Звонки", "Начать исходящий звонок",
            "Создаёт исходящий звонок владельца агенту. Одновременно допускается один звонок; второй получает 409 `busy`. Elevated требует `challengeId` и `signature`; для standard эти поля должны отсутствовать. Сервер отправляет агенту `call.invite`; звонок переходит в `connecting` после `call.accept` агента. Медиа — отдельный WebSocket.",
            ApiAuth.Bearer, [Ok<CallViewResponse>("Звонок создан и приглашение отправлено агенту."), .. callErrors],
            RequestBodyKind.Json, "Параметры исходящего звонка.",
            """{"sourceId":"abcdef0123456789abcdef0123456789","sessionId":"task-1","securityLevel":"standard"}"""));

        Add("GET", "/api/v1/calls", new("listCalls", "Звонки", "История звонков",
            "Только владелец (`web`/`device`). Возвращает до 100 последних записей с метаданными: direction, deviceId, reason, durationMs. Аудио и расшифровок нет.",
            ApiAuth.BearerOrWeb, [
                OkArray<CallRecord>("История звонков.", """[{"id":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","sourceId":"abcdef0123456789abcdef0123456789","agentId":"ffffffffffffffffffffffffffffffff","deviceId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sessionId":"task-1","securityLevel":"standard","startedAt":1767225600000,"endedAt":1767225660000,"status":"ended","reason":"ended","durationMs":60000,"direction":"outgoing"}]"""),
                Unauthorized,
                Error(403, "Интеграции недоступно (`owner_required`).", """{"code":"owner_required"}"""),
                RateLimited, InternalError]));

        Add("GET", "/api/v1/calls/incoming", new("getIncomingCall", "Звонки", "Текущий входящий звонок",
            "Только устройство. Возвращает актуальный входящий звонок в состоянии `ringing`; если приглашения нет — 404. Приглашение не включает микрофон: приложение показывает карточку и ждёт явного accept. Этот опрос — резервный путь, если FCM/full-screen intent недоступны.",
            ApiAuth.Bearer, [
                Ok<CallViewResponse>("Есть входящий ringing-звонок."),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Нет входящего звонка (`no_incoming_call`).", """{"code":"no_incoming_call"}"""),
                RateLimited, InternalError]));

        Add("GET", "/api/v1/calls/{id}", new("getCall", "Звонки", "Состояние звонка",
            "Активный звонок возвращается как `CallViewResponse` (поля приглашения/медиа), завершённый — как `CallRecord` (durationMs, reason). Участник: владелец-веб, устройство (своё или любой входящий) либо интеграция-источник. Остальным — 404.",
            ApiAuth.BearerOrWeb, [
                OneOf("Активный звонок или историческая запись.", typeof(CallViewResponse), typeof(CallRecord)),
                Unauthorized,
                Error(404, "Звонок не найден или недоступен (`call_not_found`).", """{"code":"call_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id звонка (32 hex-символа)." }));

        Add("POST", "/api/v1/calls/{id}/challenge", new("createIncomingCallChallenge", "Звонки", "Challenge принятия входящего elevated-звонка",
            "Только elevated-звонок в состоянии `ringing`, ещё не принятый другим устройством. Возвращает точные байты `alxprgs.notify.call.v2` с `direction=incoming`, `action=accept`, `callId` и `inviteId`. Приём на нескольких телефонах: первый окончательный ответ побеждает.",
            ApiAuth.Bearer, [
                Ok<CallChallengeResponse>("Challenge создан."),
                Error(400, "Звонок не входящий (`invalid_call`).", """{"code":"invalid_call"}"""),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Звонок не найден (`call_not_found`).", """{"code":"call_not_found"}"""),
                Error(409, "`call_taken` (уже принят/завершён) или `call_verification_not_required`.", """{"code":"call_taken"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id входящего звонка." }));

        Add("POST", "/api/v1/calls/{id}/accept", new("acceptCall", "Звонки", "Принять входящий звонок",
            "Только входящий `ringing`. Первый успешный accept закрепляет `deviceId`, переводит звонок в `connecting` и рассылает актуальное состояние; остальные получают 409 `call_taken`. По истечении 45 секунд — 409 `call_missed`. Elevated требует `challengeId` и `signature`; звук и микрофон не включаются этим запросом.",
            ApiAuth.Bearer, [
                Ok<CallViewResponse>("Звонок принят."),
                Error(400, "Звонок не входящий или для standard переданы challenge-поля (`invalid_call`).", """{"code":"invalid_call"}"""),
                Error(401, "Устройство отозвано (`device_revoked`).", """{"code":"device_revoked"}"""),
                Error(403, "`device_required`, `call_verification_required`, `invalid_challenge`, `invalid_signature`.", """{"code":"call_verification_required"}"""),
                Error(404, "Звонок не найден (`call_not_found`).", """{"code":"call_not_found"}"""),
                Error(409, "`call_taken` или `call_missed`.", """{"code":"call_taken"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Для standard — пустой объект; для elevated — challenge и подпись.",
            """{"challengeId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","signature":"MEUCIQ...EXAMPLE"}""",
            new() { ["id"] = "Id входящего звонка." }));

        Add("POST", "/api/v1/calls/{id}/reject", new("rejectCall", "Звонки", "Отклонить входящий звонок",
            "Завершает `ringing`-звонок с причиной `rejected`. Если звонок уже принят или завершён — 409 `call_taken`.",
            ApiAuth.Bearer, [
                NoContent("Звонок отклонён."),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Звонок не найден (`call_not_found`).", """{"code":"call_not_found"}"""),
                Error(409, "Звонок уже принят/завершён (`call_taken`).", """{"code":"call_taken"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id входящего звонка." }));

        Add("POST", "/api/v1/calls/{id}/end", new("endCall", "Звонки", "Завершить звонок",
            "Завершает звонок, закреплённый за этим устройством, с причиной `ended`. Идемпотентно: повторное завершение не создаёт второй записи. При зависшем агенте/телефоне завершение ограничено таймаутами и не блокирует следующий звонок.",
            ApiAuth.Bearer, [
                NoContent("Звонок завершён."),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Звонок не найден или закреплён за другим устройством (`call_not_found`).", """{"code":"call_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id звонка." }));

        // ---- responses / deliveries ----
        Add("POST", "/api/v1/notifications/{id}/response", new("submitResponse", "Решения", "Отправить окончательный ответ",
            "Только доверенное устройство. Один окончательный ответ: `pending → answered`; повтор и ответ на expired/cancelled — 409. Для `confirmation` передайте `answer` (`yes|no|custom`, текст ≤ 4 000); для `form` — `form.values`. Для elevated обязательны `challengeId` и `signature` точных серверных байтов; подпись проверяется до сохранения. Только `answered + yes` разрешает описанное действие; `no`, `pending`, `expired`, `cancelled` — не согласие.",
            ApiAuth.Bearer, [
                Ok<Notification>("Ответ сохранён; статус answered."),
                Error(400, "`invalid_answer` (decision/текст/смешение answer и form), `invalid_form_answer` или `form_required_missing`.", """{"code":"invalid_answer"}"""),
                Error(401, "Устройство отозвано (`device_revoked`).", """{"code":"device_revoked"}"""),
                Error(403, "`device_required`, `invalid_challenge` или `invalid_signature`.", """{"code":"invalid_challenge"}"""),
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Событие уже не `pending` (`request_not_pending`).", """{"code":"request_not_pending"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Ответ да/нет или значения формы. Ровно одно из `answer`/`form`.",
            """{"answer":{"decision":"yes"},"challengeId":null,"signature":null,"form":null}""",
            new() { ["id"] = "Id уведомления." }));

        Add("POST", "/api/v1/notifications/{id}/cancel", new("cancelNotification", "Уведомления", "Отменить ожидающий запрос",
            "Только интеграция с `requests:cancel`. Переводит `pending` в `cancelled`; повторная отмена и отмена завершённого — 409. Отмена не является ответом и не разрешает действие.",
            ApiAuth.Bearer, [
                Ok<Notification>("Запрос отменён."),
                Unauthorized,
                Error(403, "Нужен scope `requests:cancel` (`scope_required`).", """{"code":"scope_required"}"""),
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                Error(409, "Событие уже не `pending` (`request_not_pending`).", """{"code":"request_not_pending"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id уведомления." }));

        Add("POST", "/api/v1/notifications/{id}/receipt", new("reportReceipt", "Решения", "Подтвердить получение/открытие",
            "Только устройство. `device_received` и `opened` — состояния доставки, а не решение и не согласие. `opened` не понижается обратно в `device_received`.",
            ApiAuth.Bearer, [
                NoContent("Receipt принят."),
                Error(400, "Недопустимый статус (`invalid_receipt`).", """{"code":"invalid_receipt"}"""),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "Состояние доставки.",
            """{"status":"opened"}""",
            new() { ["id"] = "Id уведомления." }));

        Add("GET", "/api/v1/notifications/{id}/deliveries", new("listDeliveries", "Решения", "Подтверждения доставки",
            "Владелец или интеграция-источник с `requests:read`. Возвращает записи устройств: `device_received`/`opened` и время. FCM acceptance не является решением.",
            ApiAuth.BearerOrWeb, [
                OkArray<Delivery>("Доставки события."),
                Unauthorized,
                Error(403, "Интеграции нужен `requests:read` (`scope_required`).", """{"code":"scope_required"}"""),
                Error(404, "Событие не найдено (`notification_not_found`).", """{"code":"notification_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id уведомления." }));

        // ---- sync / devices ----
        Add("GET", "/api/v1/sync", new("sync", "Синхронизация", "Инкрементальная синхронизация Android",
            "Только устройство. `after=0` или устаревший cursor даёт snapshot последних 1 000 событий с `reset=true`; очистите прежний кэш. Иначе — до 200 изменений, `deleted` и `hasMore`. Сначала атомарно сохраните данные вместе с `cursor`, затем переходите к следующей странице. Элементы — preview (`previewOnly=true`).",
            ApiAuth.Bearer, [
                Ok<SyncResponse>("Изменения или snapshot.", """{"cursor":42,"items":[],"deleted":["0123456789abcdef0123456789abcdef"],"hasMore":false,"reset":false}"""),
                Error(400, "`after` отрицателен (`invalid_cursor`).", """{"code":"invalid_cursor"}"""),
                Unauthorized,
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["after"] = "Последний сохранённый cursor; 0 — запросить snapshot. По умолчанию 0." }));

        Add("GET", "/api/v1/devices", new("listDevices", "Синхронизация", "Доверенные устройства",
            "Только владелец (`web`/`device`). Возвращает устройства без FCM-токена (`fcmToken=null`), включая отозванные. Интеграция получает 403.",
            ApiAuth.BearerOrWeb, [
                OkArray<Device>("Список устройств."),
                Unauthorized,
                Error(403, "Интеграции недоступно (`owner_required`).", """{"code":"owner_required"}"""),
                RateLimited, InternalError]));

        Add("POST", "/api/v1/devices/fcm", new("setFcmToken", "Синхронизация", "Обновить FCM-токен устройства",
            "Только устройство. Токен длиной 10–4096 сохраняется для data-only push; при отзыве устройства он очищается. FCM содержит только id события, а для звонка — `call_id`/состояние.",
            ApiAuth.Bearer, [
                NoContent("Токен сохранён."),
                Error(400, "Длина токена вне 10–4096 (`invalid_fcm_token`).", """{"code":"invalid_fcm_token"}"""),
                Error(401, "Устройство отозвано (`device_revoked`).", """{"code":"device_revoked"}"""),
                Error(403, "Нужен токен устройства (`device_required`).", """{"code":"device_required"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Json, "FCM registration token.",
            """{"token":"EXAMPLE_FCM_REGISTRATION_TOKEN"}"""));

        // ---- agents / sources / management ----
        Add("GET", "/api/v1/agents", new("listAgents", "Управление", "Зарегистрированные агенты",
            "Только владелец. Возвращает агентов, их online-состояние, lastSeenAt, заявленные возможности, согласованный call-протокол и текущие задачи. Offline-агент остаётся видимым; звонки ему недоступны.",
            ApiAuth.BearerOrWeb, [
                OkArray<AgentViewResponse>("Агенты и их состояние.", """[{"id":"ffffffffffffffffffffffffffffffff","sourceId":"abcdef0123456789abcdef0123456789","name":"Мой компьютер","capabilities":["text_tasks","realtime_audio"],"callProtocols":["alxprgs.notify.call.v2"],"callProtocol":"alxprgs.notify.call.v2","revoked":false,"online":true,"lastSeenAt":1767225600000,"sessions":[{"id":"task-1","name":"Сборка"}]}]"""),
                Unauthorized,
                Error(403, "Интеграции недоступно (`owner_required`).", """{"code":"owner_required"}"""),
                RateLimited, InternalError]));

        Add("GET", "/api/v1/sources", new("listSources", "Управление", "Источники и их права",
            "Владелец видит все источники, интеграция с `requests:read` — только себя. `protectedWebhookSecret` никогда не возвращается; webhook secret показывается один раз при создании интеграции.",
            ApiAuth.BearerOrWeb, [
                OkArray<SourceViewResponse>("Источники без секретов.", """[{"id":"abcdef0123456789abcdef0123456789","name":"Агент разработки","scopes":["notify:send","requests:create","agents:connect"],"preview":"hidden","iconId":null,"webhookUrl":null,"revoked":false}]"""),
                Unauthorized,
                Error(403, "Интеграции нужен `requests:read` (`scope_required`).", """{"code":"scope_required"}"""),
                RateLimited, InternalError]));

        Add("POST", "/api/v1/management", new("beginManagement", "Управление", "Начать изменение, подтверждаемое телефоном",
            "Только владелец. Создаёт отдельный elevated-запрос `_system`; само изменение выполняется лишь после подписанного ответа доверенного телефона. Нужен хотя бы один неотозванный device. Действия: `integration.create` (нужны `name` и `scopes`), `integration.revoke`/`device.revoke`/`source.update`/`agent.register` (нужен `targetId`). Одновременно допускается до 20 ожидающих запросов. Действие `chatgpt.link` создаёт выделенный источник ChatGPT и выдаёт OAuth-реквизиты. Секреты выдаются один раз через `GET /management/{id}`.",
            ApiAuth.BearerOrWebPost, [
                Ok<Management>("Запрос управления создан; ожидает ответа телефона."),
                Error(400, "`invalid_management_action`, `invalid_integration`, `invalid_agent`, `target_required`; для webhook — `webhook_not_allowlisted_https`/`webhook_private_address`; тело — `invalid_request`.", """{"code":"invalid_management_action"}"""),
                Unauthorized,
                Error(403, "Только владелец (`owner_required`); для cookie-запроса — `invalid_origin`/`invalid_csrf`.", """{"code":"owner_required"}"""),
                Error(404, "`source_not_found` или `icon_not_found`.", """{"code":"source_not_found"}"""),
                Error(409, "`trusted_device_required`, `agent_scope_required`, `agent_already_registered`, `agent_registration_pending`.", """{"code":"trusted_device_required"}"""),
                Error(429, "Слишком много ожидающих запросов (`too_many_pending_management_requests`); общий IP-лимит даёт пустой 429.", """{"code":"too_many_pending_management_requests"}"""),
                InternalError],
            RequestBodyKind.Json, "Действие и его параметры. Поля, не относящиеся к действию, отклоняются.",
            """{"action":"integration.create","name":"Новый источник","scopes":["notify:send","requests:create"],"preview":"hidden"}""",
            Headers: [new("Origin", false, "Для cookie-сессии — точный `Notify__PublicUrl`."), new("X-CSRF-Token", false, "Для cookie-сессии — токен из `GET /api/v1/session`.")]));

        Add("GET", "/api/v1/management/{id}", new("getManagement", "Управление", "Результат изменения",
            "Только владелец и только сессия, создавшая запрос. Пока телефон не ответил — `{id,status}`; после одобрения — одноразовый `result` (новый токен/webhook secret или созданный id). Потерянный результат не выдаётся повторно: создайте новую интеграцию и отзовите прежнюю.",
            ApiAuth.BearerOrWeb, [
                Ok<ManagementStatusResponse>("Статус и, после одобрения, одноразовый результат.", """{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"approved","result":{"sourceId":"abcdef0123456789abcdef0123456789","token":"nt_EXAMPLE","webhookSecret":null}}"""),
                Unauthorized,
                Error(403, "Только владелец (`owner_required`).", """{"code":"owner_required"}"""),
                Error(404, "Запрос не найден или создан другой сессией (`management_not_found`).", """{"code":"management_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id запроса управления." }));

        // ---- icons ----
        Add("POST", "/api/v1/icons", new("uploadIcon", "Иконки", "Загрузить PNG/WebP",
            "Владелец или интеграция с `icons:write` загружает сырые байты PNG/WebP до 256 КиБ и 512×512. Формат определяется по сигнатуре, а не по Content-Type; анимированный WebP отклоняется. Повтор одинаковых байтов от того же источника возвращает тот же id. Всего допускается 256 иконок. Произвольные внешние URL не принимаются.",
            ApiAuth.BearerOrWebPost, [
                Ok<IconResponse>("Иконка сохранена.", """{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""),
                Error(400, "`icon_size_invalid`, `icon_format_invalid`, `icon_dimensions_invalid`, `animated_icon_not_supported`, `invalid_webp`.", """{"code":"icon_format_invalid"}"""),
                Unauthorized,
                Error(403, "Интеграции нужен `icons:write` (`scope_required`); для cookie-сессии — `invalid_origin`/`invalid_csrf`.", """{"code":"scope_required"}"""),
                Error(409, "Исчерпан лимит 256 иконок (`icon_quota_exceeded`).", """{"code":"icon_quota_exceeded"}"""),
                RateLimited, InternalError],
            RequestBodyKind.Icon, "Сырые байты PNG или WebP; максимум 262 144 байта."));

        Add("GET", "/api/v1/icons/{id}", new("getIcon", "Иконки", "Получить иконку",
            "Любая аутентифицированная учётная запись. Чужая иконка интеграции неотличима от отсутствующей (404). Возвращает сохранённый Content-Type (`image/png` или `image/webp`).",
            ApiAuth.BearerOrWeb, [
                Binary("Изображение.", "image/png", "image/webp"),
                Unauthorized,
                Error(404, "Иконка не найдена (`icon_not_found`).", """{"code":"icon_not_found"}"""),
                RateLimited, InternalError],
            Parameters: new() { ["id"] = "Id иконки." }));

        return operations;
    }

    private static Task SchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonPropertyInfo?.DeclaringType ?? context.JsonTypeInfo?.Type;
        var property = context.JsonPropertyInfo?.Name;
        if (type == null) return Task.CompletedTask;

        if (property == null)
        {
            if (TypeDescriptions.TryGetValue(type.Name, out var description)) schema.Description = description;
            return Task.CompletedTask;
        }
        var key = type.Name + "." + property;
        ApplySchemaMetadata(key, schema);
        if (context.JsonPropertyInfo?.IsGetNullable == true && schema.Type is { } schemaType && !schemaType.HasFlag(JsonSchemaType.Null))
        {
            schema.Type = schemaType | JsonSchemaType.Null;
            schema.Enum = null;
        }
        if (schema.Items is OpenApiSchema items && ItemEnums.TryGetValue(key, out var itemValues))
            items.Enum = itemValues.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        if (key == "ManagementStatusResponse.result")
        {
            schema.Type = JsonSchemaType.Object;
            schema.AdditionalPropertiesAllowed = true;
        }
        return Task.CompletedTask;
    }

    private static readonly Dictionary<string, string> TypeDescriptions = new(StringComparer.Ordinal)
    {
        ["CreateNotification"] = "Содержимое события: тип, заголовок, текст, уровень безопасности, срок и вложения.",
        ["Notification"] = "Уведомление или запрос и его окончательное состояние.",
        ["Answer"] = "Ответ да/нет/свой вариант.",
        ["SubmitAnswer"] = "Окончательный ответ устройства, включая необязательную подпись elevated-ответа.",
        ["Challenge"] = "Серверный challenge: точные байты для подписи и его срок.",
        ["FormSpec"] = "Спецификация формы из 1–20 полей.",
        ["FormField"] = "Описание одного поля формы.",
        ["FormOption"] = "Вариант choice-поля.",
        ["FormValues"] = "Значения формы по id полей.",
        ["FormAnswer"] = "Сохранённый окончательный набор значений формы.",
        ["VoiceInput"] = "Ссылка на временную WAV-загрузку.",
        ["VoiceAttachment"] = "Готовое голосовое вложение уведомления.",
        ["VoiceSend"] = "Голосовой ответ владельца агенту.",
        ["EnrollDevice"] = "Параметры первичной привязки Android-устройства.",
        ["CallStart"] = "Параметры исходящего звонка.",
        ["CallChallengeInput"] = "Параметры elevated-challenge исходящего звонка.",
        ["CallAcceptInput"] = "Параметры принятия входящего звонка.",
        ["CallRecord"] = "Историческая запись завершённого звонка без аудио и расшифровок.",
        ["Device"] = "Доверенное устройство владельца.",
        ["Delivery"] = "Подтверждение доставки события устройством.",
        ["ManagementInput"] = "Действие управления и его параметры.",
        ["Management"] = "Запрос управления, подтверждаемый телефоном.",
        ["Actor"] = "Учётная запись, от имени которой выполнен запрос.",
        ["ReceiptInput"] = "Состояние доставки события.",
        ["FcmInput"] = "FCM registration token устройства.",
        ["ErrorResponse"] = "Машинная ошибка API.",
        ["Agent"] = "Зарегистрированный агент источника.",
        ["AgentSession"] = "Задача/сессия, заявленная агентом.",
        ["Source"] = "Источник уведомлений и его права.",
        ["Icon"] = "Сохранённая иконка.",
    };

    private sealed record SchemaConstraint(int? MinLength = null, int? MaxLength = null, string? Pattern = null, double? Min = null, double? Max = null);

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        // request/response records
        ["CreateNotification.kind"] = "Тип события: `message`, `confirmation`, `form` или `voice`.",
        ["CreateNotification.title"] = "Короткий заголовок; 1–160 символов, без control-символов. HTML не исполняется.",
        ["CreateNotification.body"] = "Текст до 16 000 символов; для формы/голоса может быть пустым. HTML не исполняется.",
        ["CreateNotification.securityLevel"] = "`standard` или `elevated`; для `message` допустим только `standard`. Elevated требует подписи устройства.",
        ["CreateNotification.expiresAt"] = "UTC ISO 8601. Обязателен для `confirmation`, `form` и `voice`: в будущем, не более 30 дней.",
        ["CreateNotification.allowCustom"] = "Разрешить ответ `custom` с текстом (до 4 000). Для `form`/`voice` принудительно false.",
        ["CreateNotification.iconId"] = "Необязательный id ранее загруженной иконки; должен принадлежать источнику или владельцу.",
        ["CreateNotification.correlationId"] = "Необязательная метка задачи/сессии до 128 символов; возвращается в событии и webhook.",
        ["CreateNotification.form"] = "Спецификация формы; обязательна и допустима только для `kind=form`.",
        ["CreateNotification.voiceUpload"] = "Ссылка на подтверждённую WAV-загрузку; обязательна и допустима только для `kind=voice`.",
        ["CreateNotification.voice"] = "Заполняется сервером после promotion загрузки; во входе должно быть `null`.",
        ["Answer.decision"] = "`yes`, `no` или `custom`.",
        ["Answer.text"] = "Текст собственного ответа; обязателен только для `custom` и не длиннее 4 000 символов.",
        ["SubmitAnswer.answer"] = "Ответ да/нет для `confirmation`.",
        ["SubmitAnswer.challengeId"] = "Id challenge; обязателен для elevated и запрещён для standard.",
        ["SubmitAnswer.signature"] = "Base64 DER-подпись P-256/SHA-256 точных серверных байтов; обязательна для elevated.",
        ["SubmitAnswer.form"] = "Значения формы для `kind=form`.",
        ["Notification.id"] = "Серверный id (32 hex-символа).",
        ["Notification.sourceId"] = "Источник события; `_system` — служебные запросы управления.",
        ["Notification.content"] = "Полное исходное содержимое события.",
        ["Notification.createdAt"] = "Unix milliseconds создания.",
        ["Notification.status"] = "`pending`, `answered`, `expired`, `cancelled` или `active` для сообщений.",
        ["Notification.answer"] = "Окончательный ответ или `null`.",
        ["Notification.answeredAt"] = "Unix milliseconds ответа; `null`, пока ответа нет.",
        ["Notification.managementId"] = "Id запроса управления для служебного `_system`-события.",
        ["Notification.previewOnly"] = "`true` в списках/sync: тело и ответ сокращены, значения формы скрыты. Полный текст — только GET по id.",
        ["Notification.formAnswer"] = "Значения формы; в preview-ответах `values` пуст.",
        ["FormField.id"] = "Стабильный id поля: 1–64 ASCII, начинается с буквы/цифры, далее буквы/цифры/`_`/`-`; уникален.",
        ["FormField.label"] = "Подпись 1–160 символов без control-символов.",
        ["FormField.type"] = "`text`, `boolean`, `number`, `single_choice` или `multi_choice`.",
        ["FormField.required"] = "Если true, поле должно присутствовать и быть непустым.",
        ["FormField.description"] = "Пояснение до 1 000 символов; допускаются переводы строк и табуляция.",
        ["FormField.maxLength"] = "Для `text` — 1–4 000; по умолчанию 1 000. Для других типов запрещён.",
        ["FormField.min"] = "Для `number` — необязательная нижняя граница (конечное число).",
        ["FormField.max"] = "Для `number` — необязательная верхняя граница; должна быть ≥ `min`.",
        ["FormField.options"] = "Для choice-типов: 1–20 уникальных вариантов.",
        ["FormOption.id"] = "Id варианта: 1–64 ASCII, уникален в поле.",
        ["FormOption.label"] = "Подпись варианта 1–160 символов.",
        ["FormSpec.fields"] = "1–20 полей формы.",
        ["FormValues.values"] = "Объект `id поля → значение`. Типы не приводятся: строка не становится boolean/number.",
        ["FormAnswer.values"] = "Сохранённые значения формы.",
        ["VoiceAttachment.audioId"] = "Id приватного WAV-ассета.",
        ["VoiceAttachment.durationMs"] = "Длительность WAV в миллисекундах (≤ 300 000).",
        ["VoiceAttachment.sizeBytes"] = "Размер WAV в байтах (≤ 10 МиБ).",
        ["VoiceAttachment.expiresAt"] = "Unix milliseconds истечения вложения.",
        ["VoiceInput.uploadId"] = "Id временной загрузки, полученный от `POST /api/v1/audio/uploads`.",
        ["VoiceSend.uploadId"] = "Id подтверждённой временной загрузки WAV.",
        ["VoiceSend.notificationId"] = "Голосовое уведомление, на которое отвечает владелец.",
        ["VoiceSend.expiresAt"] = "Необязательный TTL команды; по умолчанию 7 дней, максимум 30 дней.",
        ["EnrollDevice.name"] = "Понятное имя устройства; 1–80 символов без control-символов.",
        ["EnrollDevice.publicKey"] = "Base64 SubjectPublicKeyInfo P-256 (secp256r1); ключ создаётся в Android Keystore.",
        ["EnrollDevice.fcmToken"] = "Необязательный FCM registration token; можно обновить позже через `/devices/fcm`.",
        ["EnrollDevice.recovery"] = "Если true, после подтверждения отзываются все прежние устройства и их credentials.",
        ["Challenge.id"] = "Id challenge.",
        ["Challenge.notificationId"] = "Id связанного уведомления.",
        ["Challenge.deviceId"] = "Устройство, для которого выпущен challenge.",
        ["Challenge.payload"] = "Base64 точных UTF-8 байтов, которые подписывает Android. Сервер не канонизирует JSON клиента.",
        ["Challenge.expiresAt"] = "Unix milliseconds; не более 90 секунд и не дольше самого запроса.",
        ["Challenge.answer"] = "Предполагаемый ответ, включённый в подписываемые байты.",
        ["Challenge.form"] = "Предполагаемые значения формы, включённые в подписываемые байты.",
        ["Challenge.audioId"] = "Аудиоассет для elevated-прослушивания.",
        ["CallStart.sourceId"] = "Id источника, к агенту которого выполняется звонок.",
        ["CallStart.sessionId"] = "Необязательная задача/сессия из hello агента.",
        ["CallStart.securityLevel"] = "`standard` или `elevated`.",
        ["CallStart.challengeId"] = "Challenge исходящего elevated-звонка; обязателен только для elevated.",
        ["CallStart.signature"] = "Base64 DER-подпись точных байтов call-proof; обязательна только для elevated.",
        ["CallChallengeInput.sourceId"] = "Id источника агента.",
        ["CallChallengeInput.sessionId"] = "Необязательная задача из hello агента.",
        ["CallChallengeInput.securityLevel"] = "Только `elevated`: для ordinary challenge возвращается 400.",
        ["CallAcceptInput.challengeId"] = "Challenge входящего elevated-звонка; обязателен только для elevated.",
        ["CallAcceptInput.signature"] = "Base64 DER-подпись call-proof; обязательна только для elevated.",
        ["CallRecord.id"] = "Id звонка.",
        ["CallRecord.sourceId"] = "Источник агента.",
        ["CallRecord.agentId"] = "Агент, участвовавший в звонке.",
        ["CallRecord.deviceId"] = "Победившее устройство или `null`.",
        ["CallRecord.sessionId"] = "Задача/сессия агента или `null`.",
        ["CallRecord.securityLevel"] = "`standard` или `elevated`.",
        ["CallRecord.startedAt"] = "Unix milliseconds начала.",
        ["CallRecord.endedAt"] = "Unix milliseconds завершения.",
        ["CallRecord.status"] = "Историческая запись всегда `ended`.",
        ["CallRecord.reason"] = "Причина завершения: `rejected`, `missed`, `timeout`, `disconnected`, `unsupported`, `busy`, `invalid_audio`, `degraded`, `revoked`, `ended`.",
        ["CallRecord.durationMs"] = "Длительность активной части разговора.",
        ["CallRecord.direction"] = "`outgoing` (телефон → агент) или `incoming` (агент → телефон).",
        ["ReceiptInput.status"] = "`device_received` или `opened`. Это доставка, не решение.",
        ["FcmInput.token"] = "FCM registration token длиной 10–4096; хранится только для доверенного устройства.",
        ["AudioGrantInput.challengeId"] = "Id audio-challenge, выданного для этого elevated-вложения.",
        ["AudioGrantInput.signature"] = "Base64 DER-подпись P-256/SHA-256 точных байтов audio-challenge.",
        ["ManagementInput.action"] = "`integration.create`, `integration.revoke`, `device.revoke`, `source.update`, `agent.register` или `chatgpt.link`.",
        ["ManagementInput.name"] = "Имя нового источника/агента; обязательно для create/register, ≤ 80 символов.",
        ["ManagementInput.targetId"] = "Id источника/устройства; обязателен для всех действий, кроме `integration.create`.",
        ["ManagementInput.scopes"] = "Права нового источника; обязательны для `integration.create`.",
        ["ManagementInput.preview"] = "`hidden` или `full` — показывать ли содержание на экране блокировки.",
        ["ManagementInput.iconId"] = "Иконка источника (владельца или своя).",
        ["ManagementInput.webhookUrl"] = "Публичный HTTPS URL из allowlist `Notify__WebhookHosts`; секрет выдаётся один раз.",
        ["ManagementInput.capabilities"] = "Возможности агента: `text_tasks`, `voice_messages`, `realtime_audio`.",
        ["Management.id"] = "Id запроса управления.",
        ["Management.sessionId"] = "Веб-сессия, создавшая запрос; результат доступен только ей.",
        ["Management.input"] = "Исходное действие.",
        ["Management.input"] = "Исходное действие управления и его параметры.",
        ["Management.expiresAt"] = "Unix milliseconds; запрос живёт 5 минут.",
        ["Management.status"] = "`pending`, `approved`, `denied` или `expired`.",
        ["Management.protectedResult"] = "Зашифрованный одноразовый результат; в ответе всегда `null`, результат выдаётся через GET.",
        ["Device.id"] = "Id устройства.",
        ["Device.name"] = "Имя, заданное при привязке.",
        ["Device.publicKey"] = "P-256 SubjectPublicKeyInfo в Base64.",
        ["Device.fcmToken"] = "FCM-токен; в списке устройств всегда `null`.",
        ["Device.revoked"] = "Отозванное устройство не аутентифицируется.",
        ["Delivery.notificationId"] = "Id события.",
        ["Delivery.deviceId"] = "Устройство, сообщившее доставку.",
        ["Delivery.status"] = "`device_received` или `opened`.",
        ["Delivery.updatedAt"] = "Unix milliseconds последнего receipt.",
        ["SourceViewResponse.id"] = "Id источника (он же actor.id интеграции).",
        ["SourceViewResponse.name"] = "Имя источника.",
        ["SourceViewResponse.scopes"] = "Выданные права.",
        ["SourceViewResponse.preview"] = "`hidden` или `full`.",
        ["SourceViewResponse.iconId"] = "Иконка источника по умолчанию.",
        ["SourceViewResponse.webhookUrl"] = "URL webhook или `null`.",
        ["SourceViewResponse.revoked"] = "Отозванный источник не может отправлять и читать.",
        ["Actor.kind"] = "`integration`, `device` или `web`.",
        ["Actor.id"] = "Id источника, устройства или SSO sub владельца.",
        ["Actor.scopes"] = "Права источника; у устройства и веб-сессии пусто.",
        ["Actor.sessionId"] = "Идентификатор веб-сессии; для Bearer — `null`.",
        ["Actor.isOwner"] = "Вычисляемый признак владельца (`device` или `web`).",
        ["SessionResponse.actor"] = "Текущая учётная запись.",
        ["SessionResponse.csrfToken"] = "Анти-CSRF токен; выдаётся при каждом успешном вызове. Для веб-POST передавайте в `X-CSRF-Token`; Bearer-клиенты могут игнорировать.",
        ["EnrollmentStartResponse.enrollmentId"] = "Секретный id для одноразового результата.",
        ["EnrollmentStartResponse.authorizationUrl"] = "URL подтверждения SSO; origin должен совпадать с issuer.",
        ["EnrollmentStartResponse.expiresAt"] = "Unix milliseconds истечения enrollment (5 минут).",
        ["EnrollmentStateResponse.status"] = "`pending` или `consumed`.",
        ["EnrollmentCredentialResponse.deviceId"] = "Созданный id устройства.",
        ["EnrollmentCredentialResponse.token"] = "Одноразово выданный токен устройства; сохраните в Keystore.",
        ["EnrollmentCredentialResponse.expiresAt"] = "Unix milliseconds истечения токена устройства (30 дней).",
        ["NotificationPage.items"] = "Страница событий в preview-виде.",
        ["NotificationPage.next"] = "Курсор следующей страницы или `null`.",
        ["PageCursor.before"] = "Значение `createdAt` последнего элемента.",
        ["PageCursor.beforeId"] = "Id последнего элемента.",
        ["SyncResponse.cursor"] = "Новый cursor; сохраните его вместе с данными.",
        ["SyncResponse.items"] = "Изменённые события в preview-виде.",
        ["SyncResponse.deleted"] = "Id удалённых событий.",
        ["SyncResponse.hasMore"] = "Есть ли ещё изменения после этого cursor.",
        ["SyncResponse.reset"] = "`true` — это snapshot; очистите прежний кэш.",
        ["VoiceUploadResponse.uploadId"] = "Временный id загрузки; живёт 15 минут.",
        ["VoiceUploadResponse.durationMs"] = "Проверенная длительность WAV.",
        ["VoiceUploadResponse.sizeBytes"] = "Размер файла в байтах.",
        ["VoiceUploadResponse.expiresAt"] = "Unix milliseconds истечения временной загрузки.",
        ["VoiceCreatedResponse.id"] = "Id голосового сообщения и durable-команды.",
        ["VoiceCreatedResponse.status"] = "Начальный статус `queued`.",
        ["VoiceCreatedResponse.expiresAt"] = "Unix milliseconds TTL команды.",
        ["VoiceCreatedResponse.inReplyTo"] = "Id голосового уведомления.",
        ["VoiceStatusResponse.id"] = "Id голосового сообщения.",
        ["VoiceStatusResponse.status"] = "`queued`, `delivered`, `expired` или `failed`.",
        ["VoiceStatusResponse.createdAt"] = "Unix milliseconds создания.",
        ["VoiceStatusResponse.expiresAt"] = "Unix milliseconds TTL.",
        ["VoiceStatusResponse.durationMs"] = "Длительность WAV.",
        ["VoiceStatusResponse.notificationId"] = "Связанное голосовое уведомление.",
        ["VoiceStatusResponse.audioAvailable"] = "Существует ли файл и имеет ли он статус ready.",
        ["AudioGrantResponse.grant"] = "Одноразовый grant для `X-Notify-Audio-Grant`.",
        ["AudioGrantResponse.expiresAt"] = "Unix milliseconds истечения grant (60 секунд).",
        ["CallChallengeResponse.id"] = "Id challenge.",
        ["CallChallengeResponse.payload"] = "Base64 точных UTF-8 байтов `alxprgs.notify.call.v2`.",
        ["CallChallengeResponse.expiresAt"] = "Unix milliseconds истечения (90 секунд).",
        ["CallViewResponse.id"] = "Id звонка.",
        ["CallViewResponse.inviteId"] = "Id приглашения, связывающий сигнализацию и proof.",
        ["CallViewResponse.direction"] = "`outgoing` или `incoming`.",
        ["CallViewResponse.status"] = "`ringing`, `connecting`, `active` или `ended`.",
        ["CallViewResponse.reason"] = "Причина завершения или `null`, пока звонок идёт.",
        ["CallViewResponse.sourceId"] = "Источник агента.",
        ["CallViewResponse.agentId"] = "Агент.",
        ["CallViewResponse.agentName"] = "Имя агента из hello.",
        ["CallViewResponse.sessionId"] = "Задача агента или `null`.",
        ["CallViewResponse.securityLevel"] = "`standard` или `elevated`.",
        ["CallViewResponse.deviceId"] = "Устройство, принявшее/начавшее звонок, или `null`.",
        ["CallViewResponse.startedAt"] = "Unix milliseconds начала.",
        ["CallViewResponse.activeAt"] = "Unix milliseconds перехода в active или `null`.",
        ["CallViewResponse.endedAt"] = "Unix milliseconds завершения или `null`.",
        ["CallViewResponse.title"] = "Заголовок экрана вызова.",
        ["CallViewResponse.iconId"] = "Иконка звонка или `null`.",
        ["CallViewResponse.expiresAt"] = "Unix milliseconds deadline ringing.",
        ["AgentViewResponse.id"] = "Id агента.",
        ["AgentViewResponse.sourceId"] = "Источник агента.",
        ["AgentViewResponse.name"] = "Имя из последнего hello.",
        ["AgentViewResponse.capabilities"] = "Заявленные возможности: `text_tasks`, `voice_messages`, `realtime_audio`.",
        ["AgentViewResponse.callProtocols"] = "Call-протоколы из hello.",
        ["AgentViewResponse.callProtocol"] = "Согласованный сервером протокол или `null`.",
        ["AgentViewResponse.revoked"] = "Агент отозван вместе с источником.",
        ["AgentViewResponse.online"] = "Есть ли активное control-соединение.",
        ["AgentViewResponse.lastSeenAt"] = "Unix milliseconds последнего hello/heartbeat.",
        ["AgentViewResponse.sessions"] = "Текущие задачи/сессии агента (до 50).",
        ["ManagementStatusResponse.id"] = "Id запроса.",
        ["AgentSession.id"] = "Стабильный id задачи/сессии (1–64 символа).",
        ["AgentSession.name"] = "Короткое имя задачи (1–160 символов).",
        ["Agent.id"] = "Id агента.",
        ["Agent.sourceId"] = "Источник агента.",
        ["Agent.name"] = "Имя из последнего hello.",
        ["Agent.capabilities"] = "Заявленные возможности.",
        ["Agent.createdAt"] = "Unix milliseconds регистрации.",
        ["Agent.lastSeenAt"] = "Unix milliseconds последней активности.",
        ["Agent.revoked"] = "Агент отозван.",
        ["Agent.callProtocols"] = "Объявленные call-протоколы.",
        ["Agent.callProtocol"] = "Согласованный call-протокол.",
        ["Source.id"] = "Id источника.",
        ["Source.name"] = "Имя источника.",
        ["Source.scopes"] = "Выданные права.",
        ["Source.preview"] = "`hidden` или `full`.",
        ["Source.iconId"] = "Иконка источника.",
        ["Source.webhookUrl"] = "URL webhook.",
        ["Source.protectedWebhookSecret"] = "Зашифрованный webhook secret; никогда не возвращается API.",
        ["Source.revoked"] = "Источник отозван.",
        ["Icon.id"] = "Id иконки.",
        ["Icon.sourceId"] = "Владелец иконки или `null` для иконки владельца.",
        ["Icon.contentType"] = "`image/png` или `image/webp`.",
        ["Icon.fileName"] = "Серверное имя файла; снаружи не задаётся.",
        ["ManagementStatusResponse.status"] = "`pending`, `approved`, `denied` или `expired`.",
        ["ManagementStatusResponse.result"] = "Одноразовый результат одобренного действия; отсутствует, пока запрос не одобрен.",
        ["IconResponse.id"] = "Id сохранённой иконки.",
        ["ErrorResponse.code"] = "Машинный код ошибки; стабилен для обработки клиентом.",
        ["ConfigResponse.version"] = "Версия продукта.",
        ["ConfigResponse.ssoIssuer"] = "Issuer SSO.",
        ["ConfigResponse.androidClientId"] = "Публичный OIDC client Android.",
        ["ConfigResponse.publicUrl"] = "Публичный origin; должен совпадать с Origin веб-POST.",
        ["ConfigResponse.ssoConfigured"] = "Настроен ли SSO владельца.",
        ["ConfigResponse.capabilities"] = "Реализованные функции.",
        ["ConfigResponse.protocols"] = "Идентификаторы протоколов подписи/агента/звонка.",
        ["ConfigResponse.minClientVersion"] = "Минимальные версии Android для функций.",
        ["CapabilitiesResponse.message"] = "Информационные сообщения.",
        ["CapabilitiesResponse.confirmation"] = "Подтверждения да/нет/свой вариант.",
        ["CapabilitiesResponse.elevated"] = "Повышенные ответы с подписью.",
        ["CapabilitiesResponse.forms"] = "Структурированные формы.",
        ["CapabilitiesResponse.voiceMessages"] = "Голосовые сообщения.",
        ["CapabilitiesResponse.calls"] = "Живые звонки.",
        ["CapabilitiesResponse.agents"] = "Исходящие агентские подключения.",
        ["ProtocolVersionsResponse.answer"] = "Протокол answer-proof.",
        ["ProtocolVersionsResponse.form"] = "Протокол form-proof.",
        ["ProtocolVersionsResponse.audio"] = "Протокол audio-proof.",
        ["ProtocolVersionsResponse.agent"] = "Протокол control WebSocket.",
        ["ProtocolVersionsResponse.call"] = "Протокол call-proof и медиа.",
        ["MinClientVersionResponse.forms"] = "Минимальная версия клиента для форм.",
        ["MinClientVersionResponse.voiceMessages"] = "Минимальная версия клиента для голоса.",
        ["MinClientVersionResponse.calls"] = "Минимальная версия клиента для звонков.",
        ["HealthLiveResponse.status"] = "`alive`.",
        ["HealthReadyResponse.status"] = "`ready` или `configuration_required`.",
        ["HealthReadyResponse.sso"] = "Настроен ли SSO (только в 503).",
        ["HealthReadyResponse.fcm"] = "Найден ли Firebase credential (только в 503).",
        ["AssetLink.relation"] = "Всегда `delegate_permission/common.handle_all_urls`.",
        ["AssetLink.target"] = "Описание Android-приложения.",
        ["AssetLinkTarget.namespace"] = "`android_app`.",
        ["AssetLinkTarget.packageName"] = "`tech.alxprgs.notify`.",
        ["AssetLinkTarget.sha256CertFingerprints"] = "SHA-256 отпечатки сертификатов подписи APK.",
        ["ProblemResponse.type"] = "URI типа ProblemDetails.",
        ["ProblemResponse.title"] = "Человекочитаемое описание.",
        ["ProblemResponse.status"] = "HTTP-статус.",
    };

    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.Ordinal)
    {
        ["CreateNotification.kind"] = ["message", "confirmation", "form", "voice"],
        ["CreateNotification.securityLevel"] = ["standard", "elevated"],
        ["Answer.decision"] = ["yes", "no", "custom"],
        ["Notification.status"] = ["pending", "answered", "expired", "cancelled", "active"],
        ["FormField.type"] = ["text", "boolean", "number", "single_choice", "multi_choice"],
        ["CallStart.securityLevel"] = ["standard", "elevated"],
        ["CallChallengeInput.securityLevel"] = ["elevated"],
        ["CallRecord.securityLevel"] = ["standard", "elevated"],
        ["CallRecord.status"] = ["ended"],
        ["CallRecord.direction"] = ["outgoing", "incoming"],
        ["CallRecord.reason"] = ["rejected", "missed", "timeout", "disconnected", "unsupported", "busy", "invalid_audio", "degraded", "revoked", "ended"],
        ["CallViewResponse.direction"] = ["outgoing", "incoming"],
        ["CallViewResponse.status"] = ["ringing", "connecting", "active", "ended"],
        ["CallViewResponse.securityLevel"] = ["standard", "elevated"],
        ["CallViewResponse.reason"] = ["rejected", "missed", "timeout", "disconnected", "unsupported", "busy", "invalid_audio", "degraded", "revoked", "ended"],
        ["ReceiptInput.status"] = ["device_received", "opened"],
        ["ManagementInput.action"] = ["integration.create", "integration.revoke", "device.revoke", "source.update", "agent.register", "chatgpt.link"],
        ["ManagementInput.preview"] = ["hidden", "full"],
        ["Management.status"] = ["pending", "approved", "denied", "expired"],
        ["ManagementStatusResponse.status"] = ["pending", "approved", "denied", "expired"],
        ["Delivery.status"] = ["device_received", "opened"],
        ["SourceViewResponse.preview"] = ["hidden", "full"],
        ["Actor.kind"] = ["integration", "device", "web"],
        ["EnrollmentStateResponse.status"] = ["pending", "consumed"],
        ["VoiceCreatedResponse.status"] = ["queued"],
        ["VoiceStatusResponse.status"] = ["queued", "delivered", "expired", "failed"],
        ["AgentViewResponse.callProtocols"] = ["alxprgs.notify.call.v2"],
        ["HealthReadyResponse.status"] = ["ready", "configuration_required"],
    };

    private static readonly Dictionary<string, string[]> ItemEnums = new(StringComparer.Ordinal)
    {
        ["ManagementInput.scopes"] = ["notify:send", "requests:create", "requests:read", "requests:cancel", "icons:write", "agents:connect"],
        ["ManagementInput.capabilities"] = ["text_tasks", "voice_messages", "realtime_audio"],
        ["AgentViewResponse.capabilities"] = ["text_tasks", "voice_messages", "realtime_audio"],
        ["AgentViewResponse.callProtocols"] = ["alxprgs.notify.call.v2"],
        ["SourceViewResponse.scopes"] = ["notify:send", "requests:create", "requests:read", "requests:cancel", "icons:write", "agents:connect"],
        ["Actor.scopes"] = ["notify:send", "requests:create", "requests:read", "requests:cancel", "icons:write", "agents:connect"],
    };

    private static readonly Dictionary<string, SchemaConstraint> Constraints = new(StringComparer.Ordinal)
    {
        ["CreateNotification.title"] = new(1, 160),
        ["CreateNotification.body"] = new(0, 16000),
        ["CreateNotification.correlationId"] = new(0, 128),
        ["CreateNotification.iconId"] = new(0, 64),
        ["Answer.text"] = new(0, 4000),
        ["FormField.id"] = new(1, 64, "^[A-Za-z0-9][A-Za-z0-9_-]*$"),
        ["FormField.label"] = new(1, 160),
        ["FormField.description"] = new(0, 1000),
        ["FormField.maxLength"] = new(1, 4000),
        ["FormOption.id"] = new(1, 64, "^[A-Za-z0-9][A-Za-z0-9_-]*$"),
        ["FormOption.label"] = new(1, 160),
        ["VoiceAttachment.durationMs"] = new(1, 300000),
        ["VoiceAttachment.sizeBytes"] = new(44, 10485760),
        ["EnrollDevice.name"] = new(1, 80),
        ["EnrollDevice.publicKey"] = new(1, 1024),
        ["EnrollDevice.fcmToken"] = new(0, 4096),
        ["CallStart.sourceId"] = new(1, 64),
        ["CallStart.sessionId"] = new(0, 64),
        ["CallChallengeInput.sourceId"] = new(1, 64),
        ["CallChallengeInput.sessionId"] = new(0, 64),
        ["FcmInput.token"] = new(10, 4096),
        ["ManagementInput.name"] = new(0, 80),
        ["ManagementInput.targetId"] = new(0, 64),
        ["ManagementInput.iconId"] = new(0, 64),
        ["ManagementInput.webhookUrl"] = new(0, 2048),
        ["FormSpec.fields"] = new(1, 20),
        ["FormField.options"] = new(1, 20),
        ["Device.name"] = new(1, 80),
    };
}
