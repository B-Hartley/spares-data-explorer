using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClickSpares.SparesExplorer.Models;
using ClickSpares.SparesExplorer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClickSpares.SparesExplorer.IntegrationTests;

internal static class IntegrationRunner
{
    private static int checks;
    private static readonly string[] Files = ImportService.RequiredFiles;

    public static async Task Main()
    {
        var repository = FindRepository();
        var samplePath = Path.Combine(repository, "SampleData");
        var temporary = Path.Combine(Path.GetTempPath(), "spares-http-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, "Data", "Imports"));
        Directory.CreateDirectory(Path.Combine(temporary, "Data", "Auth"));
        Directory.CreateDirectory(Path.Combine(temporary, "wwwroot"));
        var allowlist = Path.Combine(temporary, "Data", "Auth", "admin_allowed_ips.txt");
        await File.WriteAllTextAsync(allowlist, "127.0.0.1\n");
        foreach (var file in Files)
            File.Copy(Path.Combine(samplePath, file), Path.Combine(temporary, "Data", "Imports", file));

        // Mvc.Testing consults this setting before its optional solution-file
        // discovery. This repository deliberately does not require a .sln.
        const string contentRootSetting = "ASPNETCORE_TEST_CONTENTROOT_CLICKSPARES_SPARESEXPLORER";
        var previousContentRoot = Environment.GetEnvironmentVariable(contentRootSetting);
        Environment.SetEnvironmentVariable(contentRootSetting, temporary);
        var factory = new ExplorerFactory(temporary);
        try
        {
            Check(factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath == temporary,
                "The HTTP host uses an isolated temporary content root");
            var users = factory.Services.GetRequiredService<UserStore>();
            const string adminEmail = "integration-admin@clickspares.co.uk";
            const string staffEmail = "integration-user@clickspares.co.uk";
            const string externalEmail = "integration-user@external.example";
            var adminPassword = RandomPassword();
            var staffPassword = RandomPassword();
            var replacementPassword = RandomPassword();
            await users.CreateFirstAdministratorAsync(adminEmail, adminPassword);

            using var anonymous = Client(factory);
            using (var response = await anonymous.GetAsync("/api/metadata"))
            {
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous data access returns 401");
                Check(response.Content.Headers.ContentType?.MediaType == "application/json" && response.Headers.Location is null,
                    "Anonymous API access returns JSON without a login redirect");
            }
            using (var response = await anonymous.PostAsJsonAsync("/api/explore", new ExploreRequest()))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous POST data access is rejected before the query runs");
            using (var response = await anonymous.GetAsync("/"))
                Check(IsRedirect(response) && response.Headers.Location?.OriginalString.Contains("/Login", StringComparison.Ordinal) == true,
                    "Anonymous Razor navigation redirects to sign-in");

            using var admin = Client(factory);
            await SignIn(admin, adminEmail, adminPassword, expectedSuccess: true);
            var session = await Read<SessionResponse>(admin, "/api/session");
            Check(session.Email == adminEmail && session.IsAdmin && session.CanManage, "A real administrator cookie passes the staff and IP gates");
            Check(!string.IsNullOrWhiteSpace(session.CsrfToken), "The authenticated session returns an antiforgery token");
            var baseline = await Read<Metadata>(admin, "/api/metadata");
            Check(baseline.HasData && baseline.RequestCount > 0, "The server loads the synthetic sample dataset");
            var initial = await Post<ExploreResponse>(admin, "/api/explore", new ExploreRequest(), session.CsrfToken);
            Check(initial.Summary.Requests == baseline.RequestCount, "Authenticated exploration reconciles to the loaded synthetic requests");
            Check(initial.Rows.Count > 0 && initial.Rows.All(row => row.Label.StartsWith("Demo ", StringComparison.Ordinal)),
                "The HTTP test contains synthetic factory labels only");
            using (var response = await admin.GetAsync("/Users"))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "An approved administrator can open user management");
                Check(response.Headers.TryGetValues("Content-Security-Policy", out var policies) &&
                    policies.Single().Contains("default-src 'self'", StringComparison.Ordinal), "Security headers are present on authenticated HTML");
            }
            using (var response = await admin.PostAsJsonAsync("/api/explore", new ExploreRequest()))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Authenticated JSON POST without antiforgery is rejected");
            using (var request = JsonRequest("/api/explore", new ExploreRequest(), "invalid-token"))
            using (var response = await admin.SendAsync(request))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "An invalid JSON antiforgery token is rejected");

            await File.WriteAllTextAsync(allowlist, "192.0.2.45\n");
            using (var response = await admin.GetAsync("/Users"))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "The actual connection IP gates administrator pages");
            using (var response = await admin.GetAsync("/api/imports/status"))
                Check(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Location is null,
                    "The actual connection IP gates administrator APIs with JSON 403");
            using (var response = await admin.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.OK, "The admin network restriction does not remove ordinary staff data access");
            await File.WriteAllTextAsync(allowlist, "127.0.0.1\n");

            using (var response = await Form(admin, "/Users?handler=Create", session.CsrfToken,
                new() { ["email"] = staffEmail, ["password"] = staffPassword }))
                Check(response.StatusCode == HttpStatusCode.OK, "The genuine admin form creates an ordinary staff user");
            Check(await users.AuthenticateAsync(staffEmail, staffPassword) is { IsAdmin: false }, "The form-created account has no administrator role");
            using var staff = Client(factory);
            await SignIn(staff, staffEmail, staffPassword, expectedSuccess: true);
            var staffSession = await Read<SessionResponse>(staff, "/api/session");
            Check(!staffSession.CanManage && !staffSession.IsAdmin, "Ordinary staff receive no administration capabilities");
            using (var response = await staff.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.OK, "Ordinary staff can view authorized business data");
            foreach (var route in new[] { "/Users", "/Usage", "/Imports", "/api/imports/status" })
            {
                using var response = await staff.GetAsync(route);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "Ordinary staff cannot access " + route);
            }
            using (var request = JsonRequest("/api/reload", new { }, staffSession.CsrfToken))
            using (var response = await staff.SendAsync(request))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "Ordinary staff cannot reload server data");

            using (var response = await Form(admin, "/Users?handler=Create", session.CsrfToken,
                new() { ["email"] = externalEmail, ["password"] = staffPassword, ["isAdmin"] = "true" }))
                Check(response.StatusCode == HttpStatusCode.OK, "An external-account creation attempt returns a controlled form response");
            Check(!(await users.ListForAdminAsync(adminEmail)).Any(user => user.Email == externalEmail),
                "The administration form cannot create an external account");
            using var external = Client(factory);
            await SignIn(external, externalEmail, staffPassword, expectedSuccess: false);
            using (var response = await external.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "External sign-in attempts cannot obtain business data");

            await VerifyImports(admin, session.CsrfToken, samplePath, baseline);

            using (var response = await Form(admin, "/Users?handler=Password", session.CsrfToken,
                new() { ["email"] = staffEmail, ["newPassword"] = replacementPassword }))
                Check(response.StatusCode == HttpStatusCode.OK, "The genuine password-reset form succeeds");
            using (var response = await staff.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Password reset revokes the existing HTTP authentication cookie");
            await SignIn(staff, staffEmail, replacementPassword, expectedSuccess: true);
            using (var response = await staff.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.OK, "The replacement password creates a valid new HTTP session");
            using (var response = await Form(admin, "/Users?handler=Toggle", session.CsrfToken,
                new() { ["email"] = staffEmail, ["enabled"] = "false" }))
                Check(response.StatusCode == HttpStatusCode.OK, "The genuine account-disable form succeeds");
            using (var response = await staff.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Disabling an account revokes its HTTP authentication cookie");
            await SignIn(staff, staffEmail, replacementPassword, expectedSuccess: false);

            foreach (var route in new[] { "/Data/Auth/users.json", "/Data/Imports/CustomerRequests.csv", "/Data/Auth/Keys" })
            {
                using var response = await admin.GetAsync(route);
                Check(response.StatusCode == HttpStatusCode.NotFound, "Private runtime paths cannot be downloaded: " + route);
            }
            using (var response = await Form(admin, "/Logout", session.CsrfToken, new()))
                Check(IsRedirect(response), "The antiforgery-protected sign-out form succeeds");
            using (var response = await admin.GetAsync("/api/metadata"))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "The signed-out client cannot read business data");

            Console.WriteLine($"PASS: {checks} in-memory HTTP integration checks; no network listener or real business data used.");
        }
        finally
        {
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable(contentRootSetting, previousContentRoot);
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task VerifyImports(HttpClient admin, string csrf, string samplePath, Metadata baseline)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports/preview") { Content = Multipart(samplePath) })
        using (var response = await admin.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Multipart import without antiforgery is rejected");
        using (var request = UploadRequest(samplePath, csrf, omitLast: true))
        using (var response = await admin.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "An incomplete seven-file snapshot is rejected");
        using (var request = UploadRequest(samplePath, csrf, corruptProducts: true))
        using (var response = await admin.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "A complete but malformed CSV snapshot is rejected");
        var afterFailure = await Read<Metadata>(admin, "/api/metadata");
        Check(afterFailure.Version == baseline.Version && afterFailure.RequestCount == baseline.RequestCount,
            "Invalid imports do not change the live dataset");

        ImportPreview preview;
        using (var request = UploadRequest(samplePath, csrf, addSyntheticRequest: true))
        using (var response = await admin.SendAsync(request))
        {
            Check(response.StatusCode == HttpStatusCode.OK, "All seven synthetic CSVs can be previewed through HTTP");
            preview = await response.Content.ReadFromJsonAsync<ImportPreview>() ?? throw new Exception("Missing preview response.");
        }
        Check(preview.Metadata.RequestCount == baseline.RequestCount + 1 && preview.Metadata.Version != baseline.Version,
            "The preview describes the changed synthetic candidate");
        var beforeApply = await Read<Metadata>(admin, "/api/metadata");
        Check(beforeApply.Version == baseline.Version && beforeApply.RequestCount == baseline.RequestCount,
            "A validated preview does not activate its candidate before apply");
        var applied = await Post<Metadata>(admin, "/api/imports/apply", new ApplyImportRequest(preview.Token), csrf);
        Check(applied.Version == preview.Metadata.Version && applied.RequestCount == baseline.RequestCount + 1,
            "Applying a validated token atomically activates the candidate snapshot");
        var explored = await Post<ExploreResponse>(admin, "/api/explore", new ExploreRequest(), csrf);
        Check(explored.Summary.Requests == applied.RequestCount, "Subsequent HTTP queries use the applied snapshot");
        using (var request = JsonRequest("/api/imports/apply", new ApplyImportRequest(preview.Token), csrf))
        using (var response = await admin.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "An already-applied preview token cannot be reused");
        var afterReuse = await Read<Metadata>(admin, "/api/metadata");
        Check(afterReuse.Version == applied.Version, "A rejected token reuse does not change the active snapshot");
    }

    private static async Task SignIn(HttpClient client, string email, string password, bool expectedSuccess)
    {
        using var login = await client.GetAsync("/Login");
        Check(login.StatusCode == HttpStatusCode.OK, "The sign-in form is available");
        var token = HiddenToken(await login.Content.ReadAsStringAsync());
        using var response = await Form(client, "/Login", token,
            new() { ["Input.Email"] = email, ["Input.Password"] = password });
        if (expectedSuccess)
        {
            Check(IsRedirect(response), "Valid credentials and a real antiforgery token establish a session");
            Check(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie =>
                cookie.StartsWith("ClickSpares.SparesExplorer.Auth=", StringComparison.Ordinal) &&
                cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
                cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)),
                "Authentication cookies are HTTP-only and SameSite Strict");
        }
        else
        {
            Check(response.StatusCode == HttpStatusCode.OK && !IsRedirect(response), "Invalid or disabled credentials do not establish a session");
        }
    }

    private static string HiddenToken(string html)
    {
        foreach (Match input in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (!Regex.IsMatch(input.Value, "name=\"__RequestVerificationToken\"", RegexOptions.IgnoreCase)) continue;
            var value = Regex.Match(input.Value, "value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (value.Success) return WebUtility.HtmlDecode(value.Groups[1].Value);
        }
        throw new Exception("The login form did not contain an antiforgery token.");
    }

    private static async Task<T> Read<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.OK, "GET " + path + " succeeds");
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new Exception("Missing JSON response.");
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body, string csrf)
    {
        using var request = JsonRequest(path, body, csrf);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "POST " + path + " succeeds");
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new Exception("Missing JSON response.");
    }

    private static HttpRequestMessage JsonRequest(string path, object value, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static Task<HttpResponseMessage> Form(HttpClient client, string path, string csrf, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = csrf;
        return client.PostAsync(path, new FormUrlEncodedContent(values));
    }

    private static HttpRequestMessage UploadRequest(string samplePath, string csrf, bool omitLast = false,
        bool corruptProducts = false, bool addSyntheticRequest = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports/preview")
            { Content = Multipart(samplePath, omitLast, corruptProducts, addSyntheticRequest) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static MultipartFormDataContent Multipart(string samplePath, bool omitLast = false,
        bool corruptProducts = false, bool addSyntheticRequest = false)
    {
        var form = new MultipartFormDataContent();
        foreach (var file in Files.Take(omitLast ? Files.Length - 1 : Files.Length))
        {
            var bytes = File.ReadAllBytes(Path.Combine(samplePath, file));
            if (corruptProducts && file == "products.csv") bytes = Encoding.UTF8.GetBytes("BadHeader\nBadRow\n");
            if (addSyntheticRequest && file == "CustomerRequests.csv")
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).TrimEnd() +
                    "\r\n\"04/11/2026\",\"DEMO-HTTP-EXTRA\",\"DEMO-P001\",\"Satisfied\",\"04/11/2026\",\"DEMO\"\r\n");
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            form.Add(content, "files", file);
        }
        return form;
    }

    private static HttpClient Client(ExplorerFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("http://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static bool IsRedirect(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect;

    private static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ClickSpares.SparesExplorer.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the source repository for synthetic sample data.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        checks++;
    }

    private sealed record SessionResponse(string Email, bool IsAdmin, bool CanManage, string CsrfToken);
}

internal sealed class ExplorerFactory(string contentRoot) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseContentRoot(contentRoot);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackConnectionStartupFilter>());
    }
}

/// <summary>TestServer has no socket peer. Supply transport metadata, never authentication.</summary>
internal sealed class LoopbackConnectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, continuation) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return continuation();
        });
        next(app);
    };
}
