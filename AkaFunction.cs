// AkaFunction.cs
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace Aka;

public class AkaFunction
{
    private readonly ILogger<AkaFunction> _logger;
    private static readonly string? AuthorizationKey = Environment.GetEnvironmentVariable("X_Authorization"); // Use updated env var name

    public AkaFunction(ILogger<AkaFunction> logger)
    {
        _logger = logger;
    }

    [Function("aka")] // Matches the original function name/route segment
    // Output binding for Table Storage, used for creating/updating entries
    [TableOutput("Aka", Connection = "AzureWebJobsStorage")]
    public async Task<AkaOutput?> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", Route = "aka/{alias}")] HttpRequestData req,
        FunctionContext executionContext,
        // Input binding for Table Storage, attempts to read existing entry based on route alias
        [TableInput("Aka", "aka", "{alias}", Connection = "AzureWebJobsStorage")] AkaEntity? inputEntity,
        string alias)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request. Alias: {Alias}", alias);

        if (string.IsNullOrEmpty(alias))
        {
            _logger.LogWarning("Alias is null or empty.");
            var badReqResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            // Return null for output binding as we are not writing anything
            return new AkaOutput { HttpResponse = badReqResponse };
        }

        // Handle Create/Update (POST/PUT) with Authorization
        if ((req.Method == HttpMethod.Post.Method || req.Method == HttpMethod.Put.Method))
        {
            if (!req.Headers.TryGetValues("X-Authorization", out var headers) || headers.FirstOrDefault() != AuthorizationKey)
            {
                _logger.LogWarning("Unauthorized attempt to modify alias: {Alias}", alias);
                var unauthResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                return new AkaOutput { HttpResponse = unauthResponse };
            }

            _logger.LogInformation("Authorized request received for alias: {Alias}", alias);
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            if (!Uri.TryCreate(requestBody, UriKind.Absolute, out var redirectUri))
            {
                _logger.LogWarning("Invalid URL provided in body: {Url}", requestBody);
                var badReqBodyResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badReqBodyResponse.WriteStringAsync("Request body must be a valid absolute URL.");
                return new AkaOutput { HttpResponse = badReqBodyResponse };
            }

            var entityToUpsert = inputEntity ?? new AkaEntity { RowKey = alias };
            entityToUpsert.Url = requestBody;

            // Prepare response: Redirect to the newly set URL
            var createUpdateResponse = req.CreateResponse(HttpStatusCode.Redirect);
            createUpdateResponse.Headers.Add("Location", entityToUpsert.Url);

            // Return the entity to be written to table storage AND the HTTP response
            return new AkaOutput { AkaEntity = entityToUpsert, HttpResponse = createUpdateResponse };
        }

        // Handle Get/Redirect
        if (inputEntity == null || string.IsNullOrEmpty(inputEntity.Url))
        {
            _logger.LogWarning("Alias not found: {Alias}", alias);
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            return new AkaOutput { HttpResponse = notFoundResponse };
        }

        _logger.LogInformation("Redirecting alias {Alias} to {Url}", alias, inputEntity.Url);
        var response = req.CreateResponse(HttpStatusCode.Redirect);

        // Construct the final redirect URL, preserving query string
        var finalUrl = inputEntity.Url;
        var queryString = req.Url.Query;
        if (!string.IsNullOrEmpty(queryString))
        {
            // Append query string carefully
            var baseUri = new Uri(inputEntity.Url);
            if (string.IsNullOrEmpty(baseUri.Query))
            {
                finalUrl += queryString; // Append ?key=value directly
            }
            else
            {
                // Append &key=value if query already exists
                finalUrl += "&" + queryString.TrimStart('?');
            }
        }

        response.Headers.Add("Location", finalUrl);

        // Return only the HTTP response, no table output needed for GET
        return new AkaOutput { HttpResponse = response };
    }

    // Helper class to allow returning both table output and HTTP response
    public class AkaOutput
    {
        // This property will be used by the TableOutput binding implicitly
        public AkaEntity? AkaEntity { get; set; }
        public HttpResponseData? HttpResponse { get; set; }
    }
}