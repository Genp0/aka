using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Aka.Function
{
    public static class UrlShortener
    {
        // Read shared key from environment variable (same as before)
        static readonly string? authorization = Environment.GetEnvironmentVariable("X_Authorization");

        public class MultiOutput
        {
            [TableOutput("Aka", Connection = "AzureWebJobsStorage")]
            public Aka? TableOutput { get; set; }

            public HttpResponseData? HttpResponse { get; set; }
        }

        [Function("UrlShortener")]
        public static async Task<MultiOutput> RunAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", Route = "aka/{alias}")] HttpRequestData req,
            [TableInput("Aka", "aka", "{alias}", Connection = "AzureWebJobsStorage")] Aka? aka, // Input binding - alias comes from route parameter
            string alias,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("UrlShortener");
            logger.LogInformation($"C# HTTP trigger function processed a request. alias={alias}, Method={req.Method}");

            var response = req.CreateResponse();
            var multiOutput = new MultiOutput { HttpResponse = response };

            if (string.IsNullOrEmpty(alias) || alias == "400") // "400" was the default value in .csx, handle it explicitly
            {
                logger.LogWarning("Invalid or missing alias.");
                response.StatusCode = HttpStatusCode.BadRequest;
                return multiOutput;
            }

            // Create or Update via POST/PUT requires authorization
            if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || req.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                if (req.Headers.TryGetValues("X-Authorization", out var values) &&
                    values.FirstOrDefault() == authorization)
                {
                    logger.LogInformation("Authorization successful for update/create.");
                    string targetUrl = string.Empty;
                    using (var reader = new StreamReader(req.Body))
                    {
                        targetUrl = await reader.ReadToEndAsync();
                    }

                    if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var redirectUri))
                    {
                        logger.LogWarning($"Invalid body cannot be parsed as a URL: {targetUrl}");
                        response.StatusCode = HttpStatusCode.BadRequest;
                        return multiOutput;
                    }

                    if (aka != null) // Update existing
                    {
                        aka.Url = targetUrl;
                    }
                    else // Create new
                    {
                        aka = new Aka { RowKey = alias, Url = targetUrl };
                    }

                    // Set the output binding to save to table storage
                    multiOutput.TableOutput = aka;

                    // Redirect to the target URL after creation/update
                    response.StatusCode = HttpStatusCode.Redirect; // 302 Found
                    response.Headers.Add("Location", targetUrl);
                    return multiOutput;
                }
                else
                {
                    logger.LogWarning("Authorization failed or missing for update/create.");
                    response.StatusCode = HttpStatusCode.Unauthorized;
                    return multiOutput;
                }
            }

            // Handle GET request (or unauthorized POST/PUT)
            if (aka == null)
            {
                logger.LogInformation($"Alias '{alias}' not found.");
                response.StatusCode = HttpStatusCode.NotFound;
                return multiOutput;
            }

            // Redirect to the found URL
            logger.LogInformation($"Redirecting alias '{alias}' to '{aka.Url}'.");
            response.StatusCode = HttpStatusCode.Redirect; // 302 Found

            // Append query string if present
            var queryString = req.Url.Query;
            var redirectUrl = aka.Url;
            if (!string.IsNullOrEmpty(queryString))
            {
                // Avoid double '?' if the original URL already has query parameters
                if (redirectUrl.Contains('?'))
                    redirectUrl += "&" + queryString.TrimStart('?');
                else
                    redirectUrl += queryString;
            }

            response.Headers.Add("Location", redirectUrl);
            return multiOutput;
        }
    }

    // Data model for Azure Table Storage
    public class Aka
    {
        // PartitionKey is fixed for this simple scenario
        public string PartitionKey { get; set; } = "aka";
        public string RowKey { get; set; } = default!;
        public string Url { get; set; } = default!;
        // ETag is needed for updates using output bindings
        public Azure.ETag ETag { get; set; } = Azure.ETag.All;
    }
}