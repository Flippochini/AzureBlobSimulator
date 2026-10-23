using Adliance.AzureBlobSimulator.Services;

namespace Adliance.AzureBlobSimulator.Middleware;

public class SasAuthenticationMiddleware(RequestDelegate next, SasValidatorService validator)
{
    public async Task Invoke(HttpContext context)
    {
        // Validate SAS token if present
        if (context.Request.Query.ContainsKey("sig"))
        {
            // Request.Path is already URL-decoded; decoding it again would corrupt names containing "+" or "%"
            var path = context.Request.Path.Value;
            var pathSegments = path?.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (pathSegments is { Length: >= 1 })
            {
                var account = pathSegments[0]; // the first segment is the account
                context.Items["account"] = account; // save it in HttpContext
                context.Request.Path = new PathString("/" + string.Join('/', pathSegments.Skip(1))); // remove account from the path (implicit string conversion would URL-decode again)
            }

            var result = validator.Validate(context.Request);

            if (!result.IsSuccess)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/xml";

                await context.Response.WriteAsync($"""
                                                   <?xml version="1.0" encoding="utf-8"?>
                                                   <Error>
                                                     <Code>{result.ErrorCode}</Code>
                                                     <Message>{result.ErrorMessage}</Message>
                                                   </Error>
                                                   """);
                return;
            }
        }

        await next(context);
    }
}
